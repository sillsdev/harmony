using SIL.Harmony.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Helpers;

namespace SIL.Harmony.Db;

internal class CrdtRepository(ICrdtDbContext _dbContext, IOptions<CrdtConfig> crdtConfig, DateTimeOffset? ignoreChangesAfter = null)
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
        var commitIdsToExclude = await _dbContext.Commits
            .Where(c => commits.Select(c => c.Id).Contains(c.Id))
            .Select(c => c.Id)
            .ToArrayAsync();
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
            .WhereAfter(oldestChange)
            .ExecuteDeleteAsync();
    }

    public IQueryable<Commit> CurrentCommits()
    {
        var query = _dbContext.Commits.DefaultOrder();
        if (ignoreChangesAfter is not null) query = query.Where(c => c.HybridDateTime.DateTime <= ignoreChangesAfter);
        return query;
    }

    public IQueryable<ObjectSnapshot> CurrentSnapshots()
    {
        return _dbContext.Snapshots.Where(snapshot => CurrentSnapshotIds().Contains(snapshot.Id));
    }

    private IQueryable<Guid> CurrentSnapshotIds()
    {
        return _dbContext.Snapshots.GroupBy(s => s.EntityId,
            (entityId, snapshots) => snapshots
                    //unfortunately this can not be extracted into a helper because the whole thing is part of an expression
                .OrderByDescending(c => c.Commit.HybridDateTime.DateTime)
                .ThenByDescending(c => c.Commit.HybridDateTime.Counter)
                .ThenByDescending(c => c.Commit.Id)
                .First(s => ignoreChangesAfter == null || s.Commit.HybridDateTime.DateTime <= ignoreChangesAfter).Id);
    }

    public async Task<(Dictionary<Guid, ObjectSnapshot> currentSnapshots, Commit[] pendingCommits)> GetCurrentSnapshotsAndPendingCommits()
    {
        var snapshots = await CurrentSnapshots().Include(s => s.Commit).ToDictionaryAsync(s => s.EntityId);

        if (snapshots.Count == 0) return (snapshots, []);
        var lastCommit = snapshots.Values.Select(s => s.Commit).MaxBy(c => c.CompareKey);
        ArgumentNullException.ThrowIfNull(lastCommit);
        var newCommits = await CurrentCommits()
            .Include(c => c.ChangeEntities)
            .WhereAfter(lastCommit)
            .ToArrayAsync();
        return (snapshots, newCommits);
    }

    public async Task<Commit?> FindCommitByHash(string hash)
    {
        return await _dbContext.Commits.SingleOrDefaultAsync(c => c.Hash == hash);
    }

    public async Task<Commit?> FindPreviousCommit(Commit commit)
    {
        //can't trust the parentHash actually, so we can't do this.
        // if (!string.IsNullOrWhiteSpace(commit.ParentHash)) return await FindCommitByHash(commit.ParentHash);
        return await _dbContext.Commits.WhereBefore(commit)
            .DefaultOrderDescending()
            .FirstOrDefaultAsync();
    }

    public async Task<Commit[]> GetCommitsAfter(Commit? commit)
    {
        var dbContextCommits = _dbContext.Commits.Include(c => c.ChangeEntities);
        if (commit is null) return await dbContextCommits.DefaultOrder().ToArrayAsync();
        return await dbContextCommits
            .WhereAfter(commit)
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
            .LastAsync(s => s.EntityId == objectId && (ignoreChangesAfter == null || s.Commit.DateTime <= ignoreChangesAfter));
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
            .LastOrDefaultAsync(s => s.EntityId == objectId && (ignoreChangesAfter == null || s.Commit.DateTime <= ignoreChangesAfter));
        return snapshot?.Entity.Is<T>();
    }

    public IQueryable<T> GetCurrentObjects<T>() where T : class, IObjectBase
    {
        if (crdtConfig.Value.EnableProjectedTables)
        {
            return _dbContext.Set<T>();
        }
        var typeName = DerivedTypeHelper.GetEntityDiscriminator<T>();
        var queryable = CurrentSnapshots().Where(s => s.TypeName == typeName && !s.EntityIsDeleted);
        return queryable.Select(s => (T)s.Entity);
    }

    public async Task<SyncState> GetCurrentSyncState()
    {
        var queryable = _dbContext.Commits.AsQueryable();
        if (ignoreChangesAfter is not null)
            queryable = queryable.Where(c => c.HybridDateTime.DateTime <= ignoreChangesAfter);
        return await queryable.GetSyncState();
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

    public async Task AddCommits(IEnumerable<Commit> commits, bool save = true)
    {
        _dbContext.Commits.AddRange(commits);
        if (save) await _dbContext.SaveChangesAsync();
    }

    public HybridDateTime? GetLatestDateTime()
    {
        return _dbContext.Commits
            .DefaultOrderDescending()
            .AsNoTracking()
            .Select(c => c.HybridDateTime)
            .FirstOrDefault();
    }
}
