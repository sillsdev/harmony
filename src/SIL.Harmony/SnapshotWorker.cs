using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;

namespace SIL.Harmony;

internal class PendingSnapshots
{
    private readonly Dictionary<Guid, ObjectSnapshot> _entitySnapshots = [];
    private readonly List<ObjectSnapshot> _snapshots = [];


    internal PendingSnapshots(Dictionary<Guid, ObjectSnapshot> entitySnapshots)
    {
        _entitySnapshots = entitySnapshots;
        _snapshots = [.. entitySnapshots.Values.DefaultOrder()];
    }

    public void AddCurrentSnapshot(ObjectSnapshot snapshot)
    {
        // we only support one snapshot per entity per commit
        _snapshots.RemoveAll(s => s.EntityId == snapshot.EntityId && s.CommitId == snapshot.CommitId);
        _snapshots.Add(snapshot);
        _entitySnapshots[snapshot.EntityId] = snapshot;
    }

    public ObjectSnapshot? GetSnapshot(Guid entityId)
    {
        return _entitySnapshots.GetValueOrDefault(entityId);
    }


    public IEnumerable<ObjectSnapshot> GetSnapshots()
    {
        return _snapshots;
    }

    public IEnumerable<ObjectSnapshot> GetLatestSnapshots()
    {
        return _entitySnapshots.Values;
    }
}

/// <summary>
/// helper service to update snapshots and apply commits to them, has mutable state, don't reuse
/// </summary>
internal class SnapshotWorker
{
    private readonly Dictionary<Guid, Guid?> _snapshotLookup;
    private readonly CrdtRepository _crdtRepository;
    private readonly CrdtConfig _crdtConfig;
    private readonly PendingSnapshots _pendingSnapshots = new([]);

    private SnapshotWorker(Dictionary<Guid, ObjectSnapshot> snapshots,
        Dictionary<Guid, Guid?> snapshotLookup,
        CrdtRepository crdtRepository,
        CrdtConfig crdtConfig)
    {
        _pendingSnapshots = new PendingSnapshots(snapshots);
        _crdtRepository = crdtRepository;
        _snapshotLookup = snapshotLookup;
        _crdtConfig = crdtConfig;
    }

    internal static async Task<Dictionary<Guid, ObjectSnapshot>> ApplyCommitsToSnapshots(
        Dictionary<Guid, ObjectSnapshot> snapshots,
        CrdtRepository crdtRepository,
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
        CrdtRepository crdtRepository,
        CrdtConfig crdtConfig) : this([], snapshotLookup, crdtRepository, crdtConfig)
    {
    }

    public async Task UpdateSnapshots(Commit oldestAddedCommit, Commit[] newCommits)
    {
        var previousCommit = await _crdtRepository.FindPreviousCommit(oldestAddedCommit);
        var commits = await _crdtRepository.GetCommitsAfter(previousCommit);
        var allCommits = commits.UnionBy(newCommits, c => c.Id).DefaultOrder().ToArray();
        await ApplyCommitChanges(allCommits, true, previousCommit?.Hash ?? CommitBase.NullParentHash);

        var seenEntities = new HashSet<Guid>();
        var snapshots = _pendingSnapshots.GetSnapshots()
        .Reverse() // ensure we see the latest snapshots first
        .Where(s =>
        {
            if (!seenEntities.Contains(s.EntityId))
            {
                seenEntities.Add(s.EntityId);
                return true;
            }

            if (s.IsRoot) return true;

            var commitIndex = Array.IndexOf(allCommits, s.Commit) + 1;
            return commitIndex % 2 == 0;
        })
        // reverse back to original order, so snapshot data is more intuitive
        // (the repository sorts them as well, but only by commit. This reverse seems to keep snapshots within a single commit in the order they were made)
        .Reverse();

        await _crdtRepository.AddSnapshots(snapshots);
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
                commit.SetParentHash(previousCommitHash);
            }

            previousCommitHash = commit.Hash;
            commitIndex++;
            foreach (var commitChange in commit.ChangeEntities.OrderBy(c => c.Index))
            {
                IObjectBase entity;
                var prevSnapshot = await GetSnapshot(commitChange.EntityId);
                var changeContext = new ChangeContext(commit, this, _crdtConfig);
                bool wasDeleted;
                if (prevSnapshot is not null)
                {
                    entity = prevSnapshot.Entity.Copy();
                    wasDeleted = entity.DeletedAt.HasValue;
                }
                else
                {
                    entity = await commitChange.Change.NewEntity(commit, changeContext);
                    wasDeleted = false;
                }

                await commitChange.Change.ApplyChange(entity, changeContext);

                var deletedByChange = !wasDeleted && entity.DeletedAt.HasValue;
                if (deletedByChange)
                {
                    await MarkDeleted(entity.Id, changeContext);
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
        var snapshot = _pendingSnapshots.GetSnapshot(entityId);
        if (snapshot is not null) return snapshot;

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
        foreach (var snapshot in _pendingSnapshots.GetLatestSnapshots()
            .Where(predicate))
        {
            yield return snapshot;
        }

        await foreach (var snapshot in _crdtRepository.CurrentSnapshots()
            .Where(predicateExpression)
            .AsAsyncEnumerable())
        {
            if (_pendingSnapshots.GetSnapshot(snapshot.EntityId) is not null)
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

        await _crdtConfig.BeforeSaveObject.Invoke(entity.DbObject, newSnapshot);

        _pendingSnapshots.AddCurrentSnapshot(newSnapshot);
    }
}
