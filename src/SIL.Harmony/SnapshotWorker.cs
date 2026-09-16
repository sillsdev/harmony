using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// helper service to update snapshots and apply commits to them, has mutable state, don't reuse
/// </summary>
internal class SnapshotWorker
{
    private readonly Dictionary<Guid, ObjectSnapshot?> _snapshotCache;
    private readonly CrdtRepository _crdtRepository;
    private readonly HarmonyConfig _crdtConfig;
    private readonly Commit[] _batchCommits;
    private readonly bool _snapshotTableIsEmpty;
    private readonly SnapshotCheckpointPolicy _policy;
    /// <summary>the state this run starts from, if the caller handed us one</summary>
    private readonly Dictionary<Guid, ObjectSnapshot> _initialSnapshots;
    /// <summary>each entity's newest snapshot so far in this run</summary>
    private readonly Dictionary<Guid, LatestSnapshot> _latestSnapshots = [];
    /// <summary>superseded snapshots we want to persist/retain: roots, and ones required by the checkpoint policy</summary>
    private readonly List<ObjectSnapshot> _retainedIntermediateSnapshots = [];

    /// <param name="CreatedAtIndex">the batch index of the commit the snapshot was made at</param>
    private readonly record struct LatestSnapshot(ObjectSnapshot Snapshot, int CreatedAtIndex);

    /// <param name="snapshotCache">a dictionary of entity id to its latest snapshot, or null when it has none</param>
    /// <param name="snapshotTableIsEmpty">the snapshot table was emptied and stays that way until this run persists, so snapshot reads are skipped</param>
    /// <param name="initialSnapshots">state this run starts from; we only read it, so holding the caller's dictionary is safe</param>
    internal SnapshotWorker(SortedSet<Commit> commits,
        Dictionary<Guid, ObjectSnapshot?> snapshotCache,
        CrdtRepository crdtRepository,
        HarmonyConfig crdtConfig,
        bool snapshotTableIsEmpty = false,
        Dictionary<Guid, ObjectSnapshot>? initialSnapshots = null)
    {
        _batchCommits = [.. commits];
        _policy = new SnapshotCheckpointPolicy(
            _batchCommits.Select(c => c.ChangeEntities.Count),
            crdtConfig.MaxChangesBetweenSnapshotCheckpoints);
        _initialSnapshots = initialSnapshots ?? [];
        _crdtRepository = crdtRepository;
        _snapshotCache = snapshotCache;
        _crdtConfig = crdtConfig;
        _snapshotTableIsEmpty = snapshotTableIsEmpty;
    }

    internal static async Task<Dictionary<Guid, ObjectSnapshot>> ApplyCommitsToSnapshots(
        Dictionary<Guid, ObjectSnapshot> snapshots,
        CrdtRepository crdtRepository,
        SortedSet<Commit> commits,
        HarmonyConfig crdtConfig)
    {
        var worker = new SnapshotWorker(commits, [], crdtRepository, crdtConfig, initialSnapshots: snapshots);
        await worker.ApplyCommitChanges();
        foreach (var (entityId, latest) in worker._latestSnapshots)
        {
            snapshots[entityId] = latest.Snapshot;
        }
        return snapshots;
    }

    public async Task UpdateSnapshots()
    {
        var snapshots = await ComputeSnapshotsToPersist();
        //must come before AddSnapshots, whose save is what persists the flags
        _policy.PopulateCheckpoints(_batchCommits);
        await _crdtRepository.AddSnapshots(snapshots);
    }

    /// <summary>
    /// Applies the commits to snapshots the same way <see cref="UpdateSnapshots"/> does, but returns the full list
    /// of snapshots that would be persisted instead of writing them. Used by benchmarks to isolate the
    /// <see cref="CrdtRepository.AddSnapshots"/> step from commit application.
    /// </summary>
    internal async Task<IReadOnlyList<ObjectSnapshot>> ComputeSnapshotsToPersist()
    {
        await ApplyCommitChanges();
        return [.. _retainedIntermediateSnapshots, .. _latestSnapshots.Values.Select(l => l.Snapshot)];
    }

    private async ValueTask ApplyCommitChanges()
    {
        for (var commitIndex = 0; commitIndex < _batchCommits.Length; commitIndex++)
        {
            var commit = _batchCommits[commitIndex];
            foreach (var commitChange in commit.ChangeEntities.OrderBy(c => c.Index))
            {
                IObjectBase entity;
                var prevSnapshot = await GetSnapshot(commitChange.EntityId);
                var changeContext = new ChangeContext(commit, commitIndex, this, _crdtConfig);

                if (prevSnapshot is null)
                {
                    if (commitChange.Change is OpaqueChange)
                    {
                        // Keep unknown changes in history until this client understands how to apply them.
                        continue;
                    }

                    // create brand new entity - this will (and should) throw if the change doesn't support NewEntity
                    entity = await commitChange.Change.NewEntity(commit, changeContext);
                }
                else if (prevSnapshot.EntityIsDeleted && commitChange.Change.SupportsNewEntity())
                {
                    // revive deleted entity
                    entity = await commitChange.Change.NewEntity(commit, changeContext);
                }
                else if (commitChange.Change.SupportsApplyChange())
                {
                    // update existing entity
                    entity = prevSnapshot.Entity.Copy();
                    var wasDeleted = prevSnapshot.EntityIsDeleted;
                    await commitChange.Change.ApplyChange(entity, changeContext);
                    var deletedByChange = !wasDeleted && entity.DeletedAt.HasValue;
                    if (deletedByChange)
                    {
                        await MarkDeleted(entity.Id, changeContext);
                    }
                }
                else
                {
                    // Entity already exists (and is not deleted)
                    // and change does not support updating existing entities,
                    // so do nothing.
                    continue;
                }

                await GenerateSnapshotForEntity(entity, prevSnapshot, changeContext);
            }
        }
    }

    /// <summary>
    /// responsible for removing references to the deleted entity from other entities
    /// </summary>
    /// <param name="deletedEntityId"></param>
    /// <param name="commit"></param>
    private async ValueTask MarkDeleted(Guid deletedEntityId, ChangeContext context)
    {
        // Including deleted shouldn't be necessary, because change objects are responsible for not adding references to deleted entities.
        // But maybe it's a good fallback.
        var toRemoveRefFrom = await GetSnapshotsReferencing(deletedEntityId, true)
            .ToArrayAsync();

        var commit = context.Commit;
        foreach (var snapshot in toRemoveRefFrom)
        {
            var updatedEntry = snapshot.Entity.Copy();
            var wasDeleted = updatedEntry.DeletedAt.HasValue;

            updatedEntry.RemoveReference(deletedEntityId, commit);
            var deletedByRemoveRef = !wasDeleted && updatedEntry.DeletedAt.HasValue;

            await GenerateSnapshotForEntity(updatedEntry, snapshot, context);

            //we need to do this after we add the snapshot above otherwise we might get stuck in a loop of deletions
            if (deletedByRemoveRef)
            {
                await MarkDeleted(updatedEntry.Id, context);
            }
        }
    }

    public async ValueTask<ObjectSnapshot?> GetSnapshot(Guid entityId)
    {
        if (_latestSnapshots.TryGetValue(entityId, out var latest))
        {
            return latest.Snapshot;
        }

        if (_initialSnapshots.TryGetValue(entityId, out var initialSnapshot))
        {
            return initialSnapshot;
        }

        if (_snapshotCache.TryGetValue(entityId, out var snapshot))
        {
            return snapshot;
        }

        if (_snapshotTableIsEmpty) return null;

        snapshot = await _crdtRepository.GetCurrentSnapshotByObjectId(entityId, true);
        _snapshotCache[entityId] = snapshot;

        return snapshot;
    }

    internal IAsyncEnumerable<ObjectSnapshot> GetSnapshotsReferencing(Guid entityId, bool includeDeleted = false)
    {
        return GetSnapshotsWhere(s => (includeDeleted || !s.EntityIsDeleted) && s.References.Contains(entityId));
    }

    internal async IAsyncEnumerable<ObjectSnapshot> GetSnapshotsWhere(Expression<Func<ObjectSnapshot, bool>> predicateExpression)
    {
        var predicate = predicateExpression.Compile();

        // this run's snapshots first, so we don't return an out of date copy of an entity we've already updated
        foreach (var latest in _latestSnapshots.Values.Where(l => predicate(l.Snapshot)))
        {
            yield return latest.Snapshot;
        }

        foreach (var snapshot in _initialSnapshots.Values.Where(predicate))
        {
            if (_latestSnapshots.ContainsKey(snapshot.EntityId)) continue;
            yield return snapshot;
        }

        if (_snapshotTableIsEmpty) yield break;

        await foreach (var snapshot in _crdtRepository.CurrentSnapshots()
            .Where(predicateExpression)
            .AsAsyncEnumerable())
        {
            if (_latestSnapshots.ContainsKey(snapshot.EntityId) || _initialSnapshots.ContainsKey(snapshot.EntityId))
                continue;
            yield return snapshot;
        }
    }

    private async Task GenerateSnapshotForEntity(IObjectBase entity, ObjectSnapshot? prevSnapshot, ChangeContext context)
    {
        //when both snapshots are for the same commit we don't want to keep the previous, therefore the new snapshot should be root
        var isRoot = prevSnapshot is null || (prevSnapshot.IsRoot && prevSnapshot.CommitId == context.Commit.Id);
        var newSnapshot = new ObjectSnapshot(entity, context.Commit, isRoot);

        await _crdtConfig.BeforeSaveObject.Invoke(entity.DbObject, newSnapshot);

        AddSnapshot(newSnapshot, context.BatchCommitIndex);
    }

    private void AddSnapshot(ObjectSnapshot newSnapshot, int currCommitIndex)
    {
        var prevSnapshot = _latestSnapshots.GetValueOrDefault(newSnapshot.EntityId);
        _latestSnapshots[newSnapshot.EntityId] = new LatestSnapshot(newSnapshot, currCommitIndex);

        // now evaluate what dropping this previous snapshot means

        if (prevSnapshot == default)
        {
            // we're not dropping anything
            return;
        }

        if (prevSnapshot.Snapshot.CommitId == newSnapshot.CommitId)
        {
            // we (can) only keep 1 snapshot per entity per commit, so the new one wins
            return;
        }

        var mustBeRescued = prevSnapshot.Snapshot.IsRoot // always keep root snapshots
            || _policy.MustKeepSnapshot(prevSnapshot.CreatedAtIndex, currCommitIndex);

        if (mustBeRescued)
            _retainedIntermediateSnapshots.Add(prevSnapshot.Snapshot);
        else
            _policy.SnapshotDropped(prevSnapshot.CreatedAtIndex, currCommitIndex);
    }
}
