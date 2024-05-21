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
    private IQueryable<ObjectSnapshot> Snapshots => _dbContext.Snapshots.AsNoTracking();
    public Task<IDbContextTransaction> BeginTransactionAsync()
    {
        return _dbContext.Database.BeginTransactionAsync();
    }

    public bool IsInTransaction => _dbContext.Database.CurrentTransaction is not null;


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
        //this is a performance optimization to avoid deleting snapshots where there are none to delete
        var mostRecentCommit = await Snapshots.MaxAsync(s => (DateTimeOffset?)s.Commit.HybridDateTime.DateTime);
        if (mostRecentCommit < oldestChange.HybridDateTime.DateTime) return;
        await Snapshots
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
        var ignoreDate = ignoreChangesAfter?.UtcDateTime;
        return _dbContext.Snapshots.FromSql(
$"""
WITH LatestSnapshots AS (SELECT first_value(s1.Id)
    OVER (
    PARTITION BY "s1"."EntityId"
    ORDER BY "c"."DateTime" DESC, "c"."Counter" DESC, "c"."Id" DESC
    ) AS "LatestSnapshotId"
                         FROM "Snapshots" AS "s1"
                                  INNER JOIN "Commits" AS "c" ON "s1"."CommitId" = "c"."Id"
                         WHERE "c"."DateTime" < {ignoreDate} OR {ignoreDate} IS NULL)
SELECT *
FROM "Snapshots" AS "s"
         INNER JOIN LatestSnapshots AS "ls" ON "s"."Id" = "ls"."LatestSnapshotId"
GROUP BY s.EntityId
""").AsNoTracking();
    }

    public IAsyncEnumerable<SimpleSnapshot> CurrenSimpleSnapshots(bool includeDeleted = false)
    {
        var queryable = CurrentSnapshots();
        if (!includeDeleted) queryable = queryable.Where(s => !s.EntityIsDeleted);
        var snapshots = queryable.Select(s =>
            new SimpleSnapshot(s.Id,
                s.TypeName,
                s.EntityId,
                s.CommitId,
                s.IsRoot,
                s.Commit.HybridDateTime,
                s.Commit.Hash,
                s.EntityIsDeleted))
            .AsNoTracking()
            .AsAsyncEnumerable();
        return snapshots;
    }

    private IQueryable<Guid> CurrentSnapshotIds()
    {
        return Snapshots.GroupBy(s => s.EntityId,
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

    public async Task<ObjectSnapshot?> FindSnapshot(Guid id, bool tracking = false)
    {        
        return await Snapshots
            .AsTracking(tracking)
            .Include(s => s.Commit)
            .SingleOrDefaultAsync(s => s.Id == id);
    }

    public async Task<ObjectSnapshot?> GetCurrentSnapshotByObjectId(Guid objectId, bool tracking = false)
    {
        return await Snapshots.AsTracking(tracking).Include(s => s.Commit)
            .DefaultOrder()
            .LastOrDefaultAsync(s => s.EntityId == objectId && (ignoreChangesAfter == null || s.Commit.DateTime <= ignoreChangesAfter));
    }

    public async Task<T> GetObjectBySnapshotId<T>(Guid snapshotId)
    {
        var entity = await Snapshots
                         .Where(s => s.Id == snapshotId)
                         .Select(s => s.Entity)
                         .SingleOrDefaultAsync()
                     ?? throw new ArgumentException($"unable to find snapshot with id {snapshotId}");
        return (T) entity;
    }

    public async Task<T?> GetCurrent<T>(Guid objectId) where T: class
    {
        var snapshot = await Snapshots
            .DefaultOrder()
            .LastOrDefaultAsync(s => s.EntityId == objectId && (ignoreChangesAfter == null || s.Commit.DateTime <= ignoreChangesAfter));
        return (T?) snapshot?.Entity.DbObject;
    }

    public IQueryable<T> GetCurrentObjects<T>() where T : class
    {
        if (crdtConfig.Value.EnableProjectedTables)
        {
            return _dbContext.Set<T>();
        }
        throw new NotSupportedException("GetCurrentObjects is not supported when not using projected tables");
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
            
            if (_dbContext.Snapshots.Local.FindEntry(snapshot.Id) is not null) continue;
            _dbContext.Add(snapshot);
            await SnapshotAdded(snapshot);
        }

        await _dbContext.SaveChangesAsync();
    }

    private async ValueTask SnapshotAdded(ObjectSnapshot objectSnapshot)
    {
        if (!crdtConfig.Value.EnableProjectedTables) return;
        if (objectSnapshot.IsRoot && objectSnapshot.EntityIsDeleted) return;
        //need to check if an entry exists already, even if this is the root commit it may have already been added to the db
        var existingEntry = await GetEntityEntry(objectSnapshot.Entity.DbObject.GetType(), objectSnapshot.EntityId);
        if (existingEntry is null && objectSnapshot.IsRoot)
        {
            //if we don't make a copy first then the entity will be tracked by the context and be modified
            //by future changes in the same session
            _dbContext.Add((object)objectSnapshot.Entity.Copy().DbObject)
                .Property(ObjectSnapshot.ShadowRefName).CurrentValue = objectSnapshot.Id;
            return;
        }

        if (existingEntry is null) return;
        if (objectSnapshot.EntityIsDeleted)
        {
            _dbContext.Remove(existingEntry.Entity);
            return;
        }

        existingEntry.CurrentValues.SetValues(objectSnapshot.Entity.DbObject);
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


    public async Task AddLocalResource(LocalResource localResource)
    {
        _dbContext.Set<LocalResource>().Add(localResource);
        await _dbContext.SaveChangesAsync();
    }

    public IAsyncEnumerable<LocalResource> LocalResourcesByIds(IEnumerable<Guid> resourceIds)
    {
        return _dbContext.Set<LocalResource>().Where(r => resourceIds.Contains(r.Id)).AsAsyncEnumerable();
    }

    /// <summary>
    /// primarily for filtering other queries
    /// </summary>
    public IQueryable<Guid> LocalResourceIds()
    {
        return _dbContext.Set<LocalResource>().Select(r => r.Id);
    }

    public async Task<LocalResource?> GetLocalResource(Guid resourceId)
    {
        return await _dbContext.Set<LocalResource>().FindAsync(resourceId);
    }
}
