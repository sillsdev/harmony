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
    private readonly ReplayPolicy _replayPolicy;
    private readonly SnapshotCheckpointPolicy _checkpointPolicy;
    /// <summary>each entity's newest snapshot so far in this replay</summary>
    private readonly Dictionary<Guid, ObjectSnapshot> _latestSnapshots = [];
    /// <summary>superseded snapshots the policy says must still be persisted</summary>
    private readonly List<ObjectSnapshot> _keptSupersededSnapshots = [];

    /// <param name="baseline">the snapshots the commits are applied on top of</param>
    /// <param name="policy">which commits throw <see cref="CommitValidationException"/> when a change targets an
    /// entity id another type owns, instead of applying the replay rules</param>
    private SnapshotWorker(ISnapshotView baseline, SortedSet<Commit> commits, HarmonyConfig crdtConfig, ReplayPolicy policy)
    {
        _batchCommits = commits;
        _replayPolicy = policy;
        _checkpointPolicy = new SnapshotCheckpointPolicy(commits, crdtConfig.MaxChangesBetweenSnapshotCheckpoints);
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
        var worker = new SnapshotWorker(baseline, commits, crdtConfig, ReplayPolicy.Lenient);
        await worker.ApplyCommitChanges();
        return worker;
    }

    /// <summary>
    /// The snapshots to persist, and which of the batch commits a later replay can resume from.
    /// Persisting both is the caller's job.
    /// </summary>
    internal static async Task<(IReadOnlyList<ObjectSnapshot> Snapshots, CheckpointFlag[] CheckpointFlags)> ComputeNewSnapshotsAndCheckpoints(
        ISnapshotView baseline, SortedSet<Commit> commits, HarmonyConfig crdtConfig, ReplayPolicy policy = default)
    {
        var worker = new SnapshotWorker(baseline, commits, crdtConfig, policy);
        await worker.ApplyCommitChanges();
        IReadOnlyList<ObjectSnapshot> snapshots = [.. worker._keptSupersededSnapshots, .. worker._latestSnapshots.Values];
        return (snapshots, worker._checkpointPolicy.CheckpointFlags());
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
                if (prevSnapshot is not null && _replayPolicy.IsStrict(commit.Id))
                {
                    ThrowIfEntityTypeMismatch(commitChange.Change, prevSnapshot, commit);
                }

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

                if (prevSnapshot is not null && _replayPolicy.IsStrict(commit.Id))
                {
                    ThrowIfEntityTypeMismatch(commitChange.Change, prevSnapshot, entity, commit);
                }

                await RecordEntitySnapshot(entity, prevSnapshot, changeContext);
            }
        }
    }

    /// <summary>
    /// An entity id belongs to one type for its whole life, deleted or not: snapshots chain by entity id, so a change of
    /// another type on that id would corrupt the chain. Replay tolerates it (a create on a live id is skipped, a create
    /// on a deleted id revives it as the new type, see <see cref="ApplyCommitChanges"/>) because the commit is already
    /// in shared history; a strict commit is being authored on this client, where the caller can still fix the bug and
    /// the transaction rolls back. Checked before the change runs, from the type it declares, so a create on a live id
    /// is caught before replay skips it.
    /// </summary>
    private static void ThrowIfEntityTypeMismatch(IChange change, ObjectSnapshot prevSnapshot, Commit commit)
    {
        if (change.EntityType is not { } changeEntityType) return; // declares no type: checked on the entity it produces instead
        var existingType = prevSnapshot.Entity.DbObject.GetType();
        if (existingType.IsAssignableTo(changeEntityType)) return;
        throw new CommitValidationException(
            $"Entity {change.EntityId} is a {existingType.Name}, but {change.GetType().Name} targets a {changeEntityType.Name}. " +
            $"An entity id belongs to one type. CommitId: {commit.Id}");
    }

    /// <summary>
    /// The same rule as above, applied to the entity the change produced, which is the only type a change that declares
    /// no <see cref="IChange.EntityType"/> has.
    /// </summary>
    private static void ThrowIfEntityTypeMismatch(IChange change, ObjectSnapshot prevSnapshot, IObjectBase entity, Commit commit)
    {
        var existingType = prevSnapshot.Entity.DbObject.GetType();
        var producedType = entity.DbObject.GetType();
        if (existingType.IsAssignableTo(producedType) || producedType.IsAssignableTo(existingType)) return;
        throw new CommitValidationException(
            $"Entity {change.EntityId} is a {existingType.Name}, but {change.GetType().Name} produced a {producedType.Name}. " +
            $"An entity id belongs to one type. CommitId: {commit.Id}");
    }

    /// <summary>
    /// responsible for removing references to the deleted entity from other entities
    /// </summary>
    private async ValueTask MarkDeleted(Guid deletedEntityId, ChangeContext context)
    {
        // Including deleted shouldn't be necessary, because change objects are responsible for not adding references to deleted entities.
        // But maybe it's a good fallback.
        var toRemoveRefFrom = await ((ISnapshotView)this).WhereReferences(deletedEntityId, includeDeleted: true)
            .ToArrayAsync();

        var commit = context.Commit;
        foreach (var snapshot in toRemoveRefFrom)
        {
            var updatedEntry = snapshot.Entity.Copy();
            var wasDeleted = updatedEntry.DeletedAt.HasValue;

            updatedEntry.RemoveReference(deletedEntityId, commit);
            var deletedByRemoveRef = !wasDeleted && updatedEntry.DeletedAt.HasValue;

            await RecordEntitySnapshot(updatedEntry, snapshot, context);

            //we need to do this after we add the snapshot above otherwise we might get stuck in a loop of deletions
            if (deletedByRemoveRef)
            {
                await MarkDeleted(updatedEntry.Id, context);
            }
        }
    }

    private async Task RecordEntitySnapshot(IObjectBase entity, ObjectSnapshot? prevSnapshot, ChangeContext context)
    {
        //when both snapshots are for the same commit we don't want to keep the previous, therefore the new snapshot should be root
        var isRoot = prevSnapshot is null || (prevSnapshot.IsRoot && prevSnapshot.CommitId == context.Commit.Id);
        var newSnapshot = new ObjectSnapshot(entity, context.Commit, isRoot);

        await _crdtConfig.BeforeSaveObject.Invoke(entity.DbObject, newSnapshot);

        if (_latestSnapshots.TryGetValue(newSnapshot.EntityId, out var superseded) && _checkpointPolicy.Supersede(superseded, by: newSnapshot))
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
