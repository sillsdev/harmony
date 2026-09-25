using System.Linq.Expressions;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// Applies a batch of commits on top of a base state (the snapshots as of a checkpoint) and produces the snapshots
/// that result. Once <see cref="ReplayCommits"/> returns it is the <see cref="ISnapshotView"/> as of the last commit; before
/// that its reads show mid-batch state. Has mutable state, don't reuse.
/// </summary>
internal class SnapshotWorker : ISnapshotView
{
    private readonly ISnapshotView _baseline;
    private readonly HarmonyConfig _crdtConfig;
    private readonly SortedSet<Commit> _batchCommits;
    private readonly SnapshotCheckpointPolicy _policy;
    /// <summary>each entity's newest snapshot so far in this replay</summary>
    private readonly Dictionary<Guid, ObjectSnapshot> _latestSnapshots = [];
    /// <summary>superseded snapshots the policy says must still be persisted</summary>
    private readonly List<ObjectSnapshot> _keptSupersededSnapshots = [];

    /// <param name="baseline">the snapshots the commits are applied on top of</param>
    internal SnapshotWorker(SortedSet<Commit> commits, ISnapshotView baseline, HarmonyConfig crdtConfig)
    {
        _batchCommits = commits;
        _policy = new SnapshotCheckpointPolicy(commits, crdtConfig.MaxChangesBetweenSnapshotCheckpoints);
        _baseline = baseline;
        _crdtConfig = crdtConfig;
    }

    /// <summary>
    /// The snapshots as of the last of <paramref name="commits"/>, applied on top of <paramref name="baseline"/>
    /// without persisting anything.
    /// </summary>
    internal static async Task<ISnapshotView> ReplayCommits(ISnapshotView baseline, SortedSet<Commit> commits, HarmonyConfig crdtConfig)
    {
        if (commits.Count == 0) return baseline;
        var worker = new SnapshotWorker(commits, baseline, crdtConfig);
        await worker.ApplyCommitChanges();
        return worker;
    }

    /// <summary>
    /// The snapshots to persist, and which of the batch commits a later replay can resume from.
    /// Persisting both is the caller's job.
    /// </summary>
    internal async Task<(IReadOnlyList<ObjectSnapshot> Snapshots, CheckpointFlag[] CheckpointFlags)> ComputeSnapshotsAndCheckpoints()
    {
        await ApplyCommitChanges();
        IReadOnlyList<ObjectSnapshot> snapshots = [.. _keptSupersededSnapshots, .. _latestSnapshots.Values];
        return (snapshots, _policy.CheckpointFlags());
    }

    private async ValueTask ApplyCommitChanges()
    {
        foreach (var commit in _batchCommits)
        {
            foreach (var commitChange in commit.ChangeEntities.OrderBy(c => c.Index))
            {
                IObjectBase entity;
                var prevSnapshot = await GetAsync(commitChange.EntityId);
                var changeContext = new ChangeContext(commit, this, _crdtConfig);

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
    private async ValueTask MarkDeleted(Guid deletedEntityId, ChangeContext context)
    {
        // Including deleted shouldn't be necessary, because change objects are responsible for not adding references to deleted entities.
        // But maybe it's a good fallback.
        var toRemoveRefFrom = await this.WhereReferences(deletedEntityId, includeDeleted: true)
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

    private async Task GenerateSnapshotForEntity(IObjectBase entity, ObjectSnapshot? prevSnapshot, ChangeContext context)
    {
        //when both snapshots are for the same commit we don't want to keep the previous, therefore the new snapshot should be root
        var isRoot = prevSnapshot is null || (prevSnapshot.IsRoot && prevSnapshot.CommitId == context.Commit.Id);
        var newSnapshot = new ObjectSnapshot(entity, context.Commit, isRoot);

        await _crdtConfig.BeforeSaveObject.Invoke(entity.DbObject, newSnapshot);

        AddSnapshot(newSnapshot);
    }

    private void AddSnapshot(ObjectSnapshot newSnapshot)
    {
        if (_latestSnapshots.TryGetValue(newSnapshot.EntityId, out var superseded) && _policy.MustKeep(superseded, newSnapshot))
        {
            _keptSupersededSnapshots.Add(superseded);
        }

        _latestSnapshots[newSnapshot.EntityId] = newSnapshot;
    }

    public async ValueTask<ObjectSnapshot?> GetAsync(Guid entityId)
    {
        if (_latestSnapshots.TryGetValue(entityId, out var latest))
        {
            return latest;
        }

        return await _baseline.GetAsync(entityId);
    }

    public async IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicateExpression)
    {
        var predicate = predicateExpression.Compile();

        // this run's snapshots first, so we don't return an out of date copy of an entity we've already updated
        foreach (var snapshot in _latestSnapshots.Values.Where(predicate))
        {
            yield return snapshot;
        }

        await foreach (var snapshot in _baseline.Where(predicateExpression))
        {
            if (_latestSnapshots.ContainsKey(snapshot.EntityId)) continue;
            yield return snapshot;
        }
    }

    public async IAsyncEnumerable<ObjectSnapshot> All()
    {
        foreach (var snapshot in _latestSnapshots.Values)
        {
            yield return snapshot;
        }

        await foreach (var snapshot in _baseline.All())
        {
            if (_latestSnapshots.ContainsKey(snapshot.EntityId)) continue;
            yield return snapshot;
        }
    }

    public Task PreloadAsync(IReadOnlyCollection<Guid> entityIds) => _baseline.PreloadAsync(entityIds);

    public Task PreloadAllAsync() => _baseline.PreloadAllAsync();
}
