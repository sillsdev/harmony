using System.Linq.Expressions;
using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Crdt.Db;

public class CrdtRepository(CrdtDbContext _dbContext, IOptions<CrdtConfig> crdtConfig, DateTimeOffset? currentTime = null)
{
    public Task<IDbContextTransaction> BeginTransactionAsync()
    {
        return _dbContext.Database.BeginTransactionAsync();
    }


    public async Task<bool> HasCommit(Guid commitId)
    {
        return await _dbContext.Commits.AnyAsync(c => c.Id == commitId);
    }

    public async Task<(Commit? oldestChange, Commit[] newCommits)> FilterExistingCommits(ICollection<Commit> commits)
    {
        Commit? oldestChange = null;
        var commitIdsToExclude = await _dbContext.Commits.Where(c => commits.Select(c => c.Id).Contains(c.Id))
            .Select(c => c.Id).ToArrayAsync();
        var newCommits = commits.ExceptBy(commitIdsToExclude, c => c.Id).Select(commit =>
        {
            if (oldestChange is null || commit.CompareKey.CompareTo(oldestChange.CompareKey) < 0) oldestChange = commit;
            return commit;
        }).ToArray(); //need to use ToArray because the select has side effects that must trigger before this method returns
        return (oldestChange, newCommits);
    }

    public async Task DeleteStaleSnapshots(Commit oldestChange)
    {
        //use the oldest commit added to clear any snapshots that are based on a now incomplete history
        await _dbContext.Snapshots
            .Where(s => s.Commit.HybridDateTime.DateTime > oldestChange.DateTime
                        || (s.Commit.HybridDateTime.DateTime == oldestChange.DateTime &&
                            s.Commit.HybridDateTime.Counter > oldestChange.HybridDateTime.Counter)
                        || (s.Commit.HybridDateTime.DateTime == oldestChange.DateTime &&
                            s.Commit.HybridDateTime.Counter == oldestChange.HybridDateTime.Counter &&
                            s.CommitId > oldestChange.Id)
            )
            .ExecuteDeleteAsync();
    }

    public IQueryable<Commit> CurrentCommits()
    {
        return _dbContext.Commits.DefaultOrder().Where(c => currentTime == null || c.HybridDateTime.DateTime <= currentTime);
    }

    public IQueryable<ObjectSnapshot> CurrentSnapshots()
    {
        return _dbContext.Snapshots.Where(snapshot => CurrentSnapshotIds().Contains(snapshot.Id));
    }

    private IQueryable<Guid> CurrentSnapshotIds()
    {
        return _dbContext.Snapshots.GroupBy(s => s.EntityId,
            (entityId, snapshots) => snapshots
                .OrderByDescending(s => s.Commit.HybridDateTime.DateTime)
                .ThenBy(s => s.Commit.HybridDateTime.Counter)
                .ThenBy(s => s.CommitId)
                .First(s => currentTime == null || s.Commit.HybridDateTime.DateTime <= currentTime).Id);
    }

    public async Task<(Dictionary<Guid, ObjectSnapshot> currentSnapshots, Commit[] pendingCommits)> GetCurrentSnapshotsAndPendingCommits()
    {
        var snapshots = await CurrentSnapshots().ToDictionaryAsync(s => s.EntityId);

        if (snapshots.Count == 0) return (snapshots, []);
        var lastCommit = snapshots.Values.Select(s => s.Commit).MaxBy(c => (c.DateTime, c.Id));
        ArgumentNullException.ThrowIfNull(lastCommit);
        var newCommits = await CurrentCommits()
            .Include(c => c.ChangeEntities)
            .Where(c => lastCommit.HybridDateTime.DateTime < c.HybridDateTime.DateTime
                        || (lastCommit.HybridDateTime.DateTime == c.HybridDateTime.DateTime && lastCommit.HybridDateTime.Counter < c.HybridDateTime.Counter))
            .ToArrayAsync();
        return (snapshots, newCommits);
    }

    public async Task<Commit?> FindCommitByHash(string hash)
    {
        return await _dbContext.Commits.SingleOrDefaultAsync(c => c.Hash == hash);
    }

    public async Task<Commit?> FindPreviousCommit(Commit commit)
    {
        if (!string.IsNullOrWhiteSpace(commit.ParentHash)) return await FindCommitByHash(commit.ParentHash);
        return await _dbContext.Commits.Where(c => c.HybridDateTime.DateTime < commit.HybridDateTime.DateTime
                                                   || c.HybridDateTime.DateTime == commit.HybridDateTime.DateTime &&
                                                   c.HybridDateTime.Counter < commit.HybridDateTime.Counter
                                                   || c.HybridDateTime.DateTime == commit.HybridDateTime.DateTime &&
                                                   c.HybridDateTime.Counter == commit.HybridDateTime.Counter &&
                                                   c.Id < commit.Id)
            .OrderByDescending(c => c.HybridDateTime.DateTime)
            .ThenByDescending(c => c.HybridDateTime.Counter)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<Commit[]> GetCommitsAfter(Commit? commit)
    {
        var dbContextCommits = _dbContext.Commits.Include(c => c.ChangeEntities);
        if (commit is null) return await dbContextCommits.DefaultOrder().ToArrayAsync();
        return await dbContextCommits
            .Where(c => c.HybridDateTime.DateTime > commit.HybridDateTime.DateTime
                        || (c.HybridDateTime.DateTime == commit.HybridDateTime.DateTime &&
                            c.HybridDateTime.Counter > commit.HybridDateTime.Counter)
                        || (c.HybridDateTime.DateTime == commit.HybridDateTime.DateTime &&
                            c.HybridDateTime.Counter == commit.HybridDateTime.Counter &&
                            c.Id > commit.Id))
            .DefaultOrder()
            .ToArrayAsync();
    }

    public async Task<ObjectSnapshot?> FindSnapshot(Guid id)
    {
        return await _dbContext.Snapshots.Include(s => s.Commit).SingleOrDefaultAsync(s => s.Id == id);
    }

    public async Task<ObjectSnapshot> GetCurrentSnapshotByObjectId(Guid objectId)
    {
        return await _dbContext.Snapshots.Include(s => s.Commit)
            .DefaultOrder()
            .LastAsync(s => s.EntityId == objectId && (currentTime == null || s.Commit.DateTime <= currentTime));
    }

    public async Task<IObjectBase> GetObjectBySnapshotId(Guid snapshotId)
    {
        var entity = await _dbContext.Snapshots
                         .Where(s => s.Id == snapshotId)
                         .Select(s => s.Entity)
                         .SingleOrDefaultAsync()
                     ?? throw new ArgumentException($"unable to find snapshot with id {snapshotId}");
        return entity;
    }

    public async Task<T?> GetCurrent<T>(Guid objectId) where T: class, IObjectBase
    {
        var snapshot = await _dbContext.Snapshots
            .DefaultOrder()
            .LastOrDefaultAsync(s => s.EntityId == objectId && (currentTime == null || s.Commit.DateTime <= currentTime));
        return snapshot?.Entity.Is<T>();
    }

    public IQueryable<T> GetCurrentObjects<T>(Expression<Func<ObjectSnapshot, bool>>? predicate = null) where T : class, IObjectBase
    {
        if (crdtConfig.Value.EnableProjectedTables && predicate is null)
        {
            return _dbContext.Set<T>().Where(e => CurrentSnapshotIds().Contains(EF.Property<Guid>(e, ObjectSnapshot.ShadowRefName)));
        }
        var typeName = DerivedTypeHelper.GetEntityDiscriminator<T>();
        var queryable = CurrentSnapshots().Where(s => s.TypeName == typeName && !s.EntityIsDeleted);
        if (predicate is not null) queryable = queryable.Where(predicate);
        return queryable.Select(s => (T)s.Entity);
    }

    public async Task<SyncState> GetCurrentSyncState()
    {
        return await _dbContext.Commits
            .Where(c => currentTime == null || c.HybridDateTime.DateTime <= currentTime)
            .GetSyncState();
    }

    public async Task<ChangesResult<Commit>> GetChanges(SyncState remoteState)
    {
        var dbContextCommits = _dbContext.Commits;
        return await dbContextCommits.GetChanges<Commit, IChange>(remoteState);
    }

    public async Task AddSnapshots(IEnumerable<ObjectSnapshot> snapshots)
    {
        foreach (var objectSnapshot in snapshots)
        {
            _dbContext.Snapshots.Add(objectSnapshot);
            await SnapshotAdded(objectSnapshot);
        }

        await _dbContext.SaveChangesAsync();
    }

    public async ValueTask AddIfNew(IEnumerable<ObjectSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (_dbContext.Snapshots.Local.Contains(snapshot)) continue;
            _dbContext.Snapshots.Add(snapshot);
            await SnapshotAdded(snapshot);
        }

        await _dbContext.SaveChangesAsync();
    }

    private async ValueTask SnapshotAdded(ObjectSnapshot objectSnapshot)
    {
        if (!crdtConfig.Value.EnableProjectedTables) return;
        if (objectSnapshot.IsRoot && objectSnapshot.EntityIsDeleted) return;
        //need to check if an entry exists already, even if this is the root commit it may have already been added to the db
        var existingEntry = await GetEntityEntry(objectSnapshot.Entity.GetType(), objectSnapshot.EntityId);
        if (existingEntry is null && objectSnapshot.IsRoot)
        {
            //if we don't make a copy first then the entity will be tracked by the context and be modified
            //by future changes in the same session
            _dbContext.Add((object)objectSnapshot.Entity.Copy())
                .Property(ObjectSnapshot.ShadowRefName).CurrentValue = objectSnapshot.Id;
            return;
        }

        if (existingEntry is null) return;
        if (objectSnapshot.EntityIsDeleted)
        {
            _dbContext.Remove(existingEntry.Entity);
            return;
        }

        existingEntry.CurrentValues.SetValues(objectSnapshot.Entity);
        existingEntry.Property(ObjectSnapshot.ShadowRefName).CurrentValue = objectSnapshot.Id;
    }

    private async ValueTask<EntityEntry?> GetEntityEntry(Type entityType, Guid entityId)
    {
        if (!crdtConfig.Value.EnableProjectedTables) return null;
        var entity = await _dbContext.FindAsync(entityType, entityId);
        return entity is not null ? _dbContext.Entry(entity) : null;
    }

    public CrdtRepository GetScopedRepository(DateTimeOffset newCurrentTime)
    {
        return new CrdtRepository(_dbContext, crdtConfig, newCurrentTime);
    }

    public async Task AddCommit(Commit commit)
    {
        _dbContext.Commits.Add(commit);
        await _dbContext.SaveChangesAsync();
    }

    public async Task AddCommits(IEnumerable<Commit> commits)
    {
        _dbContext.Commits.AddRange(commits);
        await _dbContext.SaveChangesAsync();
    }

    public HybridDateTime? GetLatestDateTime()
    {
        return _dbContext.Commits
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ThenByDescending(c => c.HybridDateTime.Counter)
            .AsNoTracking()
            .Select(c => c.HybridDateTime)
            .FirstOrDefault();
    }
}
