using SIL.Harmony.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Helpers;

namespace SIL.Harmony.Db;

internal class CrdtRepository(ICrdtDbContext _dbContext, IOptions<CrdtConfig> crdtConfig,
    Commit? ignoreChangesAfter = null
    // DateTimeOffset? ignoreChangesAfter = null
)
{
    private IQueryable<ObjectSnapshot> Snapshots => _dbContext.Snapshots.AsNoTracking();

    private IQueryable<ObjectSnapshot> SnapshotsFiltered
    {
        get
        {
            if (ignoreChangesAfter is not null)
            {
                return Snapshots.WhereBefore(ignoreChangesAfter, inclusive: true);
            }
            return Snapshots;
        }
    }
    private IQueryable<Commit> Commits
    {
        get
        {
            if (ignoreChangesAfter is not null)
            {
                return _dbContext.Commits.WhereBefore(ignoreChangesAfter, inclusive: true);
            }
            return _dbContext.Commits;
        }
    }

    public Task<IDbContextTransaction> BeginTransactionAsync()
    {
        return _dbContext.Database.BeginTransactionAsync();
    }

    public async Task<bool> HasCommit(Guid commitId)
    {
        return await Commits.AnyAsync(c => c.Id == commitId);
    }

    public async Task<(Commit? oldestChange, Commit[] newCommits)> FilterExistingCommits(ICollection<Commit> commits)
    {
        Commit? oldestChange = null;
        var commitIdsToExclude = await Commits
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
        return Commits.DefaultOrder();
    }

    public IQueryable<ObjectSnapshot> CurrentSnapshots()
    {
        var ignoreAfterDate = ignoreChangesAfter?.HybridDateTime.DateTime.UtcDateTime;
        var ignoreAfterCounter = ignoreChangesAfter?.HybridDateTime.Counter;
        var ignoreAfterCommitId = ignoreChangesAfter?.Id;
        return _dbContext.Snapshots.FromSql(
$"""
WITH LatestSnapshots AS (SELECT first_value(s1.Id)
    OVER (
    PARTITION BY "s1"."EntityId"
    ORDER BY "c"."DateTime" DESC, "c"."Counter" DESC, "c"."Id" DESC
    ) AS "LatestSnapshotId"
                         FROM "Snapshots" AS "s1"
                                  INNER JOIN "Commits" AS "c" ON "s1"."CommitId" = "c"."Id"
     WHERE {ignoreAfterDate} IS NULL
        OR ("c"."DateTime" < {ignoreAfterDate} OR ("c"."DateTime" = {ignoreAfterDate} AND "c"."Counter" < {ignoreAfterCounter}) OR
            ("c"."DateTime" = {ignoreAfterDate} AND "c"."Counter" = {ignoreAfterCounter} AND "c"."Id" < {ignoreAfterCommitId}) OR "c"."Id" = {ignoreAfterCommitId}))
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
        return await Commits.SingleOrDefaultAsync(c => c.Hash == hash);
    }

    public async Task<Commit?> FindPreviousCommit(Commit commit)
    {
        //can't trust the parentHash actually, so we can't do this.
        // if (!string.IsNullOrWhiteSpace(commit.ParentHash)) return await FindCommitByHash(commit.ParentHash);
        return await Commits.WhereBefore(commit)
            .DefaultOrderDescending()
            .FirstOrDefaultAsync();
    }

    public async Task<Commit[]> GetCommitsAfter(Commit? commit)
    {
        var dbContextCommits = Commits.Include(c => c.ChangeEntities);
        if (commit is null) return await dbContextCommits.DefaultOrder().ToArrayAsync();
        return await dbContextCommits
            .WhereAfter(commit)
            .DefaultOrder()
            .ToArrayAsync();
    }

    public async Task<ObjectSnapshot?> FindSnapshot(Guid id, bool tracking = false)
    {
        return await SnapshotsFiltered
            .AsTracking(tracking)
            .Include(s => s.Commit)
            .SingleOrDefaultAsync(s => s.Id == id);
    }

    public async Task<ObjectSnapshot?> GetCurrentSnapshotByObjectId(Guid objectId, bool tracking = false)
    {
        return await SnapshotsFiltered
            .AsTracking(tracking)
            .Include(s => s.Commit)
            .DefaultOrder()
            .LastOrDefaultAsync(s => s.EntityId == objectId);
    }

    public async Task<T> GetObjectBySnapshotId<T>(Guid snapshotId)
    {
        var entity = await SnapshotsFiltered
                         .Where(s => s.Id == snapshotId)
                         .Select(s => s.Entity)
                         .SingleOrDefaultAsync()
                     ?? throw new ArgumentException($"unable to find snapshot with id {snapshotId}");
        return (T) entity;
    }

    public async Task<T?> GetCurrent<T>(Guid objectId) where T: class
    {
        var snapshot = await GetCurrentSnapshotByObjectId(objectId);
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
        return await Commits.GetSyncState();
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
        object? entity;
        if (existingEntry is null && objectSnapshot.IsRoot)
        {
            //if we don't make a copy first then the entity will be tracked by the context and be modified
            //by future changes in the same session
            entity = objectSnapshot.Entity.Copy().DbObject;
            _dbContext.Add(entity)
                .Property(ObjectSnapshot.ShadowRefName).CurrentValue = objectSnapshot.Id;
            return;
        }

        if (existingEntry is null) return;
        if (objectSnapshot.EntityIsDeleted)
        {
            _dbContext.Remove(existingEntry.Entity);
            return;
        }

        entity = objectSnapshot.Entity.DbObject;
        existingEntry.CurrentValues.SetValues(entity);
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
        return GetScopedRepository(new Commit(Guid.Empty)
        {
            ClientId = Guid.Empty,
            HybridDateTime = new HybridDateTime(newCurrentTime, 0)
        });
    }

    public CrdtRepository GetScopedRepository(Commit excludeChangesAfterCommit)
    {
        return new CrdtRepository(_dbContext, crdtConfig, excludeChangesAfterCommit);
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
        return Commits
            .DefaultOrderDescending()
            .AsNoTracking()
            .Select(c => c.HybridDateTime)
            .FirstOrDefault();
    }
}
