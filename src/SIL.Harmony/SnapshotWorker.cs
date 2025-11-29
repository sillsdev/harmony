using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// helper service to update snapshots and apply commits to them, has mutable state, don't reuse
/// </summary>
internal class SnapshotWorker
{
    private readonly Dictionary<Guid, Guid?> _snapshotLookup;
    private readonly ICrdtRepository _crdtRepository;
    private readonly CrdtConfig _crdtConfig;
    private readonly Dictionary<Guid, ObjectSnapshot> _pendingSnapshots  = [];
    private readonly Dictionary<Guid, ObjectSnapshot> _rootSnapshots = [];
    private readonly List<ObjectSnapshot> _newIntermediateSnapshots = [];

    private SnapshotWorker(Dictionary<Guid, ObjectSnapshot> snapshots,
        Dictionary<Guid, Guid?> snapshotLookup,
        ICrdtRepository crdtRepository,
        CrdtConfig crdtConfig)
    {
        _pendingSnapshots = snapshots;
        _crdtRepository = crdtRepository;
        _snapshotLookup = snapshotLookup;
        _crdtConfig = crdtConfig;
    }

    internal static async Task<Dictionary<Guid, ObjectSnapshot>> ApplyCommitsToSnapshots(
        Dictionary<Guid, ObjectSnapshot> snapshots,
        ICrdtRepository crdtRepository,
        ICollection<Commit> commits,
        CrdtConfig crdtConfig)
    {
        //we need to pass in the snapshots because we expect it to be modified, this is intended.
        //if the constructor makes a copy in the future this will need to be updated
        await new SnapshotWorker(snapshots, [], crdtRepository, crdtConfig).ApplyCommitChanges(commits, false, null);
        return snapshots;
    }

    /// <param name="snapshotLookup">a dictionary of entity id to latest snapshot id</param>
    /// <param name="crdtRepository"></param>
    /// <param name="crdtConfig"></param>
    internal SnapshotWorker(Dictionary<Guid, Guid?> snapshotLookup,
        ICrdtRepository crdtRepository,
        CrdtConfig crdtConfig): this([], snapshotLookup, crdtRepository, crdtConfig)
    {
    }

    public async Task UpdateSnapshots(Commit oldestAddedCommit, Commit[] newCommits)
    {
        var previousCommit = await _crdtRepository.FindPreviousCommit(oldestAddedCommit);
        var commits = await _crdtRepository.GetCommitsAfter(previousCommit);
        await ApplyCommitChanges(commits.UnionBy(newCommits, c => c.Id), true, previousCommit?.Hash ?? CommitBase.NullParentHash);

        await _crdtRepository.AddSnapshots([
            .._rootSnapshots.Values,
            .._newIntermediateSnapshots,
            .._pendingSnapshots.Values
        ]);
    }

    private async ValueTask ApplyCommitChanges(IEnumerable<Commit> commits, bool updateCommitHash, string? previousCommitHash)
    {
        var intermediateSnapshots = new Dictionary<Guid, ObjectSnapshot>();
        var commitIndex = 0;
        foreach (var commit in commits.DefaultOrder())
        {
            if (updateCommitHash && previousCommitHash is not null)
            {
                //we're rewriting history, so we need to update the previous commit hash
                if (commit.SetParentHash(previousCommitHash))
                    await _crdtRepository.UpdateCommitHash(commit.Id, hash: commit.Hash, parentHash: commit.ParentHash);
            }

            previousCommitHash = commit.Hash;
            commitIndex++;
            foreach (var commitChange in commit.ChangeEntities.OrderBy(c => c.Index))
            {
                IObjectBase entity;
                var prevSnapshot = await GetSnapshot(commitChange.EntityId);
                var changeContext = new ChangeContext(commit, commitIndex, intermediateSnapshots, this, _crdtConfig);

                if (prevSnapshot is null)
                {
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

        if (_snapshotLookup.TryGetValue(entityId, out var snapshotId))
        {
            if (snapshotId is null) return null;
            return await _crdtRepository.FindSnapshot(snapshotId.Value, true);
        }

        snapshot = await _crdtRepository.GetCurrentSnapshotByObjectId(entityId, true);
        _snapshotLookup[entityId] = snapshot?.Id;

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
        //to get the state in a point in time we would have to find a snapshot before that time, then apply any commits that came after that snapshot but still before the point in time.
        //we would probably want the most recent snapshot to always follow current, so we might need to track the number of changes a given snapshot represents so we can
        //decide when to create a new snapshot instead of replacing one inline. This would be done by using the current snapshots parent, instead of the snapshot itself.
        // s0 -> s1 -> sCurrent
        // if always taking snapshots would become
        // s0 -> s1 -> sCurrent -> sNew
        //but but to not snapshot every change we could do this instead
        // s0 -> s1 -> sNew

        //when both snapshots are for the same commit we don't want to keep the previous, therefore the new snapshot should be root
        var isRoot = prevSnapshot is null || (prevSnapshot.IsRoot && prevSnapshot.CommitId == context.Commit.Id);
        var newSnapshot = new ObjectSnapshot(entity, context.Commit, isRoot);
        //if both snapshots are for the same commit then we don't want to keep the previous snapshot
        if (prevSnapshot is null || prevSnapshot.CommitId == context.Commit.Id)
        {
            //do nothing, will cause prevSnapshot to be overriden in _pendingSnapshots if it exists
        }
        else if (context.CommitIndex % 2 == 0 && !prevSnapshot.IsRoot && IsNew(prevSnapshot))
        {
            context.IntermediateSnapshots[prevSnapshot.Entity.Id] = prevSnapshot;
        }

        await _crdtConfig.BeforeSaveObject.Invoke(entity.DbObject, newSnapshot);

        AddSnapshot(newSnapshot);
    }

    private void AddSnapshot(ObjectSnapshot snapshot)
    {
        if (snapshot.IsRoot)
        {
            _rootSnapshots[snapshot.Entity.Id] = snapshot;
        }
        else
        {
            //if there was already a pending snapshot there's no need to store it as both may point to the same commit
            _pendingSnapshots[snapshot.Entity.Id] = snapshot;
        }
    }

    /// <summary>
    /// snapshot is not from the database
    /// </summary>
    private bool IsNew(ObjectSnapshot snapshot)
    {
        var entityId = snapshot.EntityId;
        if (_pendingSnapshots.TryGetValue(entityId, out var pendingSnapshot))
        {
            return pendingSnapshot.Id == snapshot.Id;
        }
        if (_rootSnapshots.TryGetValue(entityId, out var rootSnapshot))
        {
            return rootSnapshot.Id == snapshot.Id;
        }
        return false;
    }
}
