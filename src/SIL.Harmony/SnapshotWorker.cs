using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger _logger;
    /// <summary>each entity's newest snapshot so far in this replay</summary>
    private readonly Dictionary<Guid, ObjectSnapshot> _latestSnapshots = [];
    /// <summary>superseded snapshots the policy says must still be persisted</summary>
    private readonly List<ObjectSnapshot> _keptSupersededSnapshots = [];

    /// <param name="baseline">the snapshots the commits are applied on top of</param>
    private SnapshotWorker(ISnapshotView baseline, SortedSet<Commit> commits, HarmonyConfig crdtConfig, ILogger logger)
    {
        _logger = logger;
        _batchCommits = commits;
        _policy = new SnapshotCheckpointPolicy(commits, crdtConfig.MaxChangesBetweenSnapshotCheckpoints);
        _baseline = baseline;
        _crdtConfig = crdtConfig;
    }

    /// <summary>
    /// The snapshots as of the last of <paramref name="commits"/>, applied on top of <paramref name="baseline"/>
    /// without persisting anything.
    /// </summary>
    internal static async Task<ISnapshotView> ReplayCommits(ISnapshotView baseline, SortedSet<Commit> commits, HarmonyConfig crdtConfig, ILogger logger)
    {
        if (commits.Count == 0) return baseline;
        var worker = new SnapshotWorker(baseline, commits, crdtConfig, logger);
        await worker.ApplyCommitChanges();
        return worker;
    }

    /// <summary>
    /// The snapshots to persist, and which of the batch commits a later replay can resume from.
    /// Persisting both is the caller's job.
    /// </summary>
    internal static async Task<(IReadOnlyList<ObjectSnapshot> Snapshots, CheckpointFlag[] CheckpointFlags)> ComputeNewSnapshotsAndCheckpoints(
        ISnapshotView baseline, SortedSet<Commit> commits, HarmonyConfig crdtConfig, ILogger logger)
    {
        var worker = new SnapshotWorker(baseline, commits, crdtConfig, logger);
        await worker.ApplyCommitChanges();
        IReadOnlyList<ObjectSnapshot> snapshots = [.. worker._keptSupersededSnapshots, .. worker._latestSnapshots.Values];
        return (snapshots, worker._policy.CheckpointFlags());
    }

    private async ValueTask ApplyCommitChanges()
    {
        foreach (var commit in _batchCommits)
        {
            foreach (var commitChange in commit.ChangeEntities.OrderBy(c => c.Index))
            {
                var prevSnapshot = await GetAsync(commitChange.EntityId);
                var changeContext = new ChangeContext(commit, this, _crdtConfig);

                IObjectBase? entity;
                try
                {
                    entity = await ApplyChange(commitChange.Change, prevSnapshot, changeContext);
                }
                catch (Exception e) when (commitChange.AuthoredModelVersion > _crdtConfig.CurrentModelVersion)
                {
                    SkipFutureChange(commitChange, commit, e);
                    continue;
                }

                if (entity is null) continue;
                if (prevSnapshot is { EntityIsDeleted: false } && entity.DeletedAt.HasValue)
                {
                    await MarkDeleted(entity.Id, changeContext);
                }

                await RecordEntitySnapshot(entity, prevSnapshot, changeContext);
            }
        }
    }

    /// <returns>the entity's new state, or null when the change doesn't apply to it</returns>
    private static async ValueTask<IObjectBase?> ApplyChange(IChange change, ObjectSnapshot? prevSnapshot, ChangeContext changeContext)
    {
        if (prevSnapshot is null)
        {
            if (change is OpaqueChange)
            {
                // Keep unknown changes in history until this client understands how to apply them.
                return null;
            }

            // create brand new entity - this will (and should) throw if the change doesn't support NewEntity
            return await change.NewEntity(changeContext.Commit, changeContext);
        }

        if (prevSnapshot.EntityIsDeleted && change.SupportsNewEntity())
        {
            // revive deleted entity
            return await change.NewEntity(changeContext.Commit, changeContext);
        }

        if (change.SupportsApplyChange())
        {
            var entity = prevSnapshot.Entity.Copy();
            await change.ApplyChange(entity, changeContext);
            return entity;
        }

        // Entity already exists (and is not deleted)
        // and change does not support updating existing entities,
        // so do nothing.
        return null;
    }

    /// <summary>
    /// A newer app authored the change, so this app's code may not be able to apply it, e.g. a patch of a property
    /// this model doesn't have. Skipped like an <see cref="OpaqueChange"/>: listing the type in the model version
    /// that changed it replays the change after an upgrade, and a throw then is a bug and isn't caught.
    /// </summary>
    private void SkipFutureChange(ChangeEntity<IChange> commitChange, Commit commit, Exception exception)
    {
        var changeType = _crdtConfig.ChangeTypeName(commitChange.Change);
        _logger.LogWarning(exception,
            "Skipped change {ChangeType} at index {Index} of commit {CommitId}: authored with model version {AuthoredModelVersion}, this app is on {CurrentModelVersion} and failed to apply it",
            changeType, commitChange.Index, commit.Id, commitChange.AuthoredModelVersion, _crdtConfig.CurrentModelVersion);
        Activity.Current?.AddEvent(new ActivityEvent("harmony.change.skipped", tags: new ActivityTagsCollection
        {
            ["harmony.change.type"] = changeType,
            ["harmony.change.index"] = commitChange.Index,
            ["harmony.commit.id"] = commit.Id.ToString(),
            ["harmony.change.authored_model_version"] = commitChange.AuthoredModelVersion,
            ["harmony.current_model_version"] = _crdtConfig.CurrentModelVersion,
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = exception.Message
        }));
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

        if (_latestSnapshots.TryGetValue(newSnapshot.EntityId, out var superseded) && _policy.Supersede(superseded, by: newSnapshot))
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
