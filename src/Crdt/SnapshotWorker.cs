using System.Diagnostics;
using System.Linq.Expressions;
using Crdt.Changes;
using Crdt.Core;
using Crdt.Db;
using Crdt.Entities;
using Microsoft.EntityFrameworkCore;

namespace Crdt;

/// <summary>
/// helper service to update snapshots and apply commits to them, has mutable state, don't reuse
/// </summary>
public class SnapshotWorker
{
    private readonly IReadOnlyDictionary<Guid, SimpleSnapshot>? _snapshots;
    private readonly CrdtRepository _crdtRepository;
    private readonly Dictionary<Guid, ObjectSnapshot> _pendingSnapshots  = [];
    private readonly List<ObjectSnapshot> _newIntermediateSnapshots = [];

    private SnapshotWorker(Dictionary<Guid, ObjectSnapshot> snapshots, CrdtRepository crdtRepository)
    {
        _pendingSnapshots = snapshots;
        _crdtRepository = crdtRepository;
    }

    public static async Task<Dictionary<Guid, ObjectSnapshot>> ApplyCommitsToSnapshots(Dictionary<Guid, ObjectSnapshot> snapshots,
        CrdtRepository crdtRepository,
        ICollection<Commit> commits)
    {
        //we need to pass in the snapshots because we expect it to be modified, this is intended.
        //if the constructor makes a copy in the future this will need to be updated
        await new SnapshotWorker(snapshots, crdtRepository).ApplyCommitChanges(commits, false, null);
        return snapshots;
    }

    public SnapshotWorker(IReadOnlyDictionary<Guid, SimpleSnapshot> snapshots, CrdtRepository crdtRepository)
    {
        _snapshots = snapshots;
        _crdtRepository = crdtRepository;
    }

    public async Task UpdateSnapshots(Commit oldestAddedCommit, Commit[] newCommits)
    {
        var previousCommit = await _crdtRepository.FindPreviousCommit(oldestAddedCommit);
        var commits = await _crdtRepository.GetCommitsAfter(previousCommit);
        await ApplyCommitChanges(commits.UnionBy(newCommits, c => c.Id), true, previousCommit?.Hash ?? CommitBase.NullParentHash);

        //intermediate snapshots should be added first, as the last snapshot added for an entity will be used in the projected tables
        await _crdtRepository.AddIfNew(_newIntermediateSnapshots);
        await _crdtRepository.AddSnapshots(_pendingSnapshots.Values);
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
                var changeContext = new ChangeContext(commit, this, _crdtRepository);
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
                    await MarkDeleted(entity.Id, commit);
                }

                //to get the state in a point in time we would have to find a snapshot before that time, then apply any commits that came after that snapshot but still before the point in time.
                //we would probably want the most recent snapshot to always follow current, so we might need to track the number of changes a given snapshot represents so we can
                //decide when to create a new snapshot instead of replacing one inline. This would be done by using the current snapshots parent, instead of the snapshot itself.
                // s0 -> s1 -> sCurrent
                // if always taking snapshots would become
                // s0 -> s1 -> sCurrent -> sNew
                //but but to not snapshot every change we could do this instead
                // s0 -> s1 -> sNew

                //when both snapshots are for the same commit we don't want to keep the previous, therefore the new snapshot should be root
                var isRoot = prevSnapshot is null || (prevSnapshot.IsRoot && prevSnapshot.CommitId == commit.Id);
                var newSnapshot = new ObjectSnapshot(entity, commit, isRoot);
                //if both snapshots are for the same commit then we don't want to keep the previous snapshot
                if (prevSnapshot is null || prevSnapshot.CommitId == commit.Id)
                {
                    //do nothing, will cause prevSnapshot to be overriden in _pendingSnapshots if it exists
                }
                else if (commitIndex % 2 == 0 || prevSnapshot.IsRoot)
                {
                    intermediateSnapshots[prevSnapshot.Entity.Id] = prevSnapshot;
                }

                AddSnapshot(newSnapshot);
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
    private async ValueTask MarkDeleted(Guid deletedEntityId, Commit commit)
    {
        Expression<Func<ObjectSnapshot, bool>> predicateExpression =
            snapshot => snapshot.References.Contains(deletedEntityId);
        var predicate = predicateExpression.Compile();

        var toRemoveRefFromIds = new HashSet<Guid>(await _crdtRepository.CurrentSnapshots()
            .Where(predicateExpression)
            .Select(s => s.EntityId)
            .ToArrayAsync());
        //snapshots from the db might be out of date, we want to use the most up to date data in the worker as well
        toRemoveRefFromIds.UnionWith(_pendingSnapshots.Values.Where(predicate).Select(s => s.EntityId));
        foreach (var entityId in toRemoveRefFromIds)
        {
            var snapshot = await GetSnapshot(entityId);
            if (snapshot is null) throw new NullReferenceException("unable to find snapshot for entity " + entityId);
            //could be different from what's in the db if a previous change has already updated it
            if (!predicate(snapshot)) continue;
            var hasBeenApplied = snapshot.CommitId == commit.Id;
            var updatedEntry = snapshot.Entity.Copy();
            var wasDeleted = updatedEntry.DeletedAt.HasValue;

            updatedEntry.RemoveReference(deletedEntityId, commit);
            var deletedByRemoveRef = !wasDeleted && updatedEntry.DeletedAt.HasValue;

            //this snapshot has already been applied, we don't need to add it again
            //but we did need to run apply again because we may need to mark other entities as deleted
            if (!hasBeenApplied)
                AddSnapshot(new ObjectSnapshot(updatedEntry, commit, false));

            //we need to do this after we add the snapshot above otherwise we might get stuck in a loop of deletions
            if (deletedByRemoveRef)
            {
                await MarkDeleted(updatedEntry.Id, commit);
            }
        }
    }

    public async ValueTask<ObjectSnapshot?> GetSnapshot(Guid entityId)
    {
        if (_pendingSnapshots.TryGetValue(entityId, out var snapshot))
        {
            return snapshot;
        }

        if (_snapshots?.TryGetValue(entityId, out var simpleSnapshot) == true)
        {
            return await _crdtRepository.FindSnapshot(simpleSnapshot.Id);
        }

        return null;
    }

    private void AddSnapshot(ObjectSnapshot snapshot)
    {
        //if there was already a pending snapshot there's no need to store it as both may point to the same commit
        _pendingSnapshots[snapshot.Entity.Id] = snapshot;
    }
}
