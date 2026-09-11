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
    private readonly SnapshotCheckpointPolicy _policy;
    private readonly Dictionary<Guid, ObjectSnapshot> _pendingSnapshots = [];
    private readonly Dictionary<Guid, ObjectSnapshot> _rootSnapshots = [];
    private readonly List<ObjectSnapshot> _newIntermediateSnapshots = [];
    /// <summary>batch position of each entity's newest droppable (non-root, this-run) snapshot; see KeepOrDrop</summary>
    private readonly Dictionary<Guid, int> _droppableSnapshotCommitIndex = [];
    /// <summary>half-open [from, to) batch position ranges left unsafe by a dropped snapshot; the input to checkpoint discovery</summary>
    private readonly List<(int From, int ToExclusive)> _holes = [];

    private SnapshotWorker(SortedSet<Commit> commits,
        Dictionary<Guid, ObjectSnapshot> snapshots,
        Dictionary<Guid, ObjectSnapshot?> snapshotCache,
        CrdtRepository crdtRepository,
        HarmonyConfig crdtConfig)
    {
        _batchCommits = [.. commits];
        _policy = new SnapshotCheckpointPolicy(
            _batchCommits.Select(c => c.ChangeEntities.Count),
            crdtConfig.MaxChangesBetweenSnapshotCheckpoints);
        _pendingSnapshots = snapshots;
        _crdtRepository = crdtRepository;
        _snapshotCache = snapshotCache;
        _crdtConfig = crdtConfig;
    }

    internal static async Task<Dictionary<Guid, ObjectSnapshot>> ApplyCommitsToSnapshots(
        Dictionary<Guid, ObjectSnapshot> snapshots,
        CrdtRepository crdtRepository,
        SortedSet<Commit> commits,
        HarmonyConfig crdtConfig)
    {
        //we need to pass in the snapshots because we expect it to be modified, this is intended.
        //if the constructor makes a copy in the future this will need to be updated
        var worker = new SnapshotWorker(commits, snapshots, [], crdtRepository, crdtConfig);
        await worker.ApplyCommitChanges();
        foreach (var (entityId, rootSnapshot) in worker._rootSnapshots)
        {
            //entities created during the replay only exist as roots, and a caller asking for state at a commit wants them too
            snapshots.TryAdd(entityId, rootSnapshot);
        }

        return snapshots;
    }

    /// <param name="snapshotCache">a dictionary of entity id to its latest snapshot, or null when it has none</param>
    internal SnapshotWorker(SortedSet<Commit> commits,
        Dictionary<Guid, ObjectSnapshot?> snapshotCache,
        CrdtRepository crdtRepository,
        HarmonyConfig crdtConfig) : this(commits, [], snapshotCache, crdtRepository, crdtConfig)
    {
    }

    public async Task UpdateSnapshots()
    {
        await ApplyCommitChanges();
        await _crdtRepository.AddSnapshots([
            .._rootSnapshots.Values,
            .._newIntermediateSnapshots,
            .._pendingSnapshots.Values
        ]);
        //flag checkpoints after the replay: the holes that say where a resume is safe are only known once it has finished
        await _crdtRepository.SetCheckpoints(_batchCommits, _policy.DiscoverCheckpoints(_holes));
    }

    /// <summary>
    /// Applies the commits to snapshots the same way <see cref="UpdateSnapshots"/> does, but returns the full list
    /// of snapshots that would be persisted instead of writing them. Used by benchmarks to isolate the
    /// <see cref="CrdtRepository.AddSnapshots"/> step from commit application.
    /// </summary>
    internal async Task<IReadOnlyList<ObjectSnapshot>> ComputeSnapshotsToPersist()
    {
        await ApplyCommitChanges();
        return [.. _rootSnapshots.Values, .. _newIntermediateSnapshots, .. _pendingSnapshots.Values];
    }

    private async ValueTask ApplyCommitChanges()
    {
        var intermediateSnapshots = new Dictionary<Guid, ObjectSnapshot>();
        var commitIndex = 0;
        foreach (var commit in _batchCommits)
        {
            commitIndex++;
            foreach (var commitChange in commit.ChangeEntities.OrderBy(c => c.Index))
            {
                IObjectBase entity;
                var prevSnapshot = await GetSnapshot(commitChange.EntityId);
                var changeContext = new ChangeContext(commit, commitIndex, intermediateSnapshots, this, _crdtConfig);

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
            _newIntermediateSnapshots.AddRange(intermediateSnapshots.Values);
            intermediateSnapshots.Clear();
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
        if (_pendingSnapshots.TryGetValue(entityId, out var snapshot))
        {
            return snapshot;
        }

        if (_rootSnapshots.TryGetValue(entityId, out var rootSnapshot))
        {
            return rootSnapshot;
        }

        if (_snapshotCache.TryGetValue(entityId, out snapshot))
        {
            return snapshot;
        }

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

        // foreaches ordered by most to least up-to-date, so we don't return snapshots that are out of date
        foreach (var snapshot in _pendingSnapshots.Values
            .Where(predicate))
        {
            yield return snapshot;
        }

        foreach (var snapshot in _rootSnapshots.Values
            .Where(predicate)
            .Where(s => !_pendingSnapshots.ContainsKey(s.EntityId)))
        {
            yield return snapshot;
        }

        await foreach (var snapshot in _crdtRepository.CurrentSnapshots()
            .Where(predicateExpression)
            .AsAsyncEnumerable())
        {
            if (_pendingSnapshots.ContainsKey(snapshot.EntityId) || _rootSnapshots.ContainsKey(snapshot.EntityId))
                continue;
            yield return snapshot;
        }
    }

    private async Task GenerateSnapshotForEntity(IObjectBase entity, ObjectSnapshot? prevSnapshot, ChangeContext context)
    {
        //when both snapshots are for the same commit we don't want to keep the previous, therefore the new snapshot should be root
        var isRoot = prevSnapshot is null || (prevSnapshot.IsRoot && prevSnapshot.CommitId == context.Commit.Id);
        var newSnapshot = new ObjectSnapshot(entity, context.Commit, isRoot);
        //a previous snapshot at this same commit is just replaced in _pendingSnapshots; an earlier one is kept or holed
        if (prevSnapshot is not null && prevSnapshot.CommitId != context.Commit.Id)
            KeepOrDrop(prevSnapshot, context);

        await _crdtConfig.BeforeSaveObject.Invoke(entity.DbObject, newSnapshot);

        AddSnapshot(newSnapshot, context.CommitIndex);
    }

    private void KeepOrDrop(ObjectSnapshot prevSnapshot, ChangeContext context)
    {
        //only a non-root snapshot from this run is droppable; a root or a pre-batch snapshot stays and covers its gap
        if (!_droppableSnapshotCommitIndex.TryGetValue(prevSnapshot.EntityId, out var prevCommitIndex)) return;
        if (_policy.MustKeepSnapshot(prevCommitIndex, context.CommitIndex))
            context.IntermediateSnapshots[prevSnapshot.EntityId] = prevSnapshot;
        else
            _holes.Add((prevCommitIndex, context.CommitIndex));
    }

    private void AddSnapshot(ObjectSnapshot snapshot, int commitIndex)
    {
        if (snapshot.IsRoot)
        {
            _rootSnapshots[snapshot.Entity.Id] = snapshot;
        }
        else
        {
            //if there was already a pending snapshot there's no need to store it as both may point to the same commit
            _pendingSnapshots[snapshot.Entity.Id] = snapshot;
            _droppableSnapshotCommitIndex[snapshot.EntityId] = commitIndex;
        }
    }
}
