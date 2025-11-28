using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using SIL.Harmony.Changes;
using SIL.Harmony.Resource;

namespace SIL.Harmony.Db;

internal class CrdtRepositoryFactory(IServiceProvider serviceProvider, ICrdtDbContextFactory dbContextFactory)
{
    public async Task<CrdtRepository> CreateRepository()
    {
        return ActivatorUtilities.CreateInstance<CrdtRepository>(serviceProvider, await dbContextFactory.CreateDbContextAsync());
    }

    public CrdtRepository CreateRepositorySync()
    {
        return ActivatorUtilities.CreateInstance<CrdtRepository>(serviceProvider, dbContextFactory.CreateDbContext());
    }

    public async Task<T> Execute<T>(Func<CrdtRepository, Task<T>> func)
    {
        await using var repo = await CreateRepository();
        return await func(repo);
    }
    public async Task Execute(Func<CrdtRepository, Task> func)
    {
        await using var repo = await CreateRepository();
        await func(repo);
    }

    public async ValueTask<T> Execute<T>(Func<CrdtRepository, ValueTask<T>> func)
    {
        await using var repo = await CreateRepository();
        return await func(repo);
    }
}

internal class CrdtRepository : IDisposable, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, AsyncLock> Locks = new();

    private readonly AsyncLock _lock;
    private readonly ICrdtDbContext _dbContext;
    private readonly IOptions<CrdtConfig> _crdtConfig;
    private readonly ILogger<CrdtRepository> _logger;

    public CrdtRepository(ICrdtDbContext dbContext, IOptions<CrdtConfig> crdtConfig,
        ILogger<CrdtRepository> logger,
        Commit? ignoreChangesAfter = null)
    {
        _crdtConfig = crdtConfig;
        _dbContext = ignoreChangesAfter is not null ? new ScopedDbContext(dbContext, ignoreChangesAfter) : dbContext;
        _logger = logger;
        //we can't use the scoped db context is it prevents access to the DbSet for the Snapshots,
        //but since we're using a custom query, we can use it directly and apply the scoped filters manually
        _currentSnapshotsQueryable = MakeCurrentSnapshotsQuery(dbContext, ignoreChangesAfter);
        _lock = Locks.GetOrAdd(DatabaseIdentifier, _ => new AsyncLock());
    }

    public AwaitableDisposable<IDisposable> Lock()
    {
        return _lock.LockAsync();
    }

    /// <summary>
    /// used to ensure that multiple instances of the same database don't try to access the same lock
    /// may be the connection string so it could contain sensitive information
    /// if it's in memory we'll just use a random guid
    /// </summary>
    private string DatabaseIdentifier
    {
        get
        {
            var connection = _dbContext.Database.GetDbConnection();
            if (connection.ConnectionString is ":memory:") return Guid.NewGuid().ToString();
            return connection.ConnectionString;
        }
    }

    //doesn't really do anything when using a dbcontext factory since it will likely just have been created
    //but when not using the factory it is still useful
    internal void ClearChangeTracker()
    {
        _dbContext.ChangeTracker.Clear();
    }

    private IQueryable<ObjectSnapshot> Snapshots => _dbContext.Snapshots.AsNoTracking();

    private IQueryable<Commit> Commits => _dbContext.Commits;

    public Task<IDbContextTransaction> BeginTransactionAsync()
    {
        return _dbContext.Database.BeginTransactionAsync();
    }

    public bool IsInTransaction => _dbContext.Database.CurrentTransaction is not null;


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

    public async Task DeleteSnapshotsAndProjectedTables()
    {
        if (_crdtConfig.Value.EnableProjectedTables)
        {
            foreach (var objectType in _crdtConfig.Value.ObjectTypes)
            {
                deleteProjectedTableMethod.MakeGenericMethod(objectType).Invoke(null, [_dbContext]);
            }
        }
        await Snapshots.ExecuteDeleteAsync();
    }

    private static readonly MethodInfo deleteProjectedTableMethod = new Func<ICrdtDbContext, Task>(DeleteProjectedTable<object>).Method.GetGenericMethodDefinition();

    private static async Task DeleteProjectedTable<T>(ICrdtDbContext dbContext) where T : class
    {
        await dbContext.Set<T>().ExecuteDeleteAsync();
    }

    public IQueryable<Commit> CurrentCommits()
    {
        return Commits.DefaultOrder();
    }

    private static IQueryable<ObjectSnapshot> MakeCurrentSnapshotsQuery(ICrdtDbContext dbContext, Commit? ignoreChangesAfter)
    {
        var ignoreAfterDate = ignoreChangesAfter?.HybridDateTime.DateTime.UtcDateTime;
        var ignoreAfterCounter = ignoreChangesAfter?.HybridDateTime.Counter;
        var ignoreAfterCommitId = ignoreChangesAfter?.Id;
        return dbContext.Set<ObjectSnapshot>().FromSql(
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

    private readonly IQueryable<ObjectSnapshot> _currentSnapshotsQueryable;
    public IQueryable<ObjectSnapshot> CurrentSnapshots()
    {
        return _currentSnapshotsQueryable;
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
        return await Snapshots
            .AsTracking(tracking)
            .Include(s => s.Commit)
            .SingleOrDefaultAsync(s => s.Id == id);
    }

    public async Task<ObjectSnapshot?> GetCurrentSnapshotByObjectId(Guid objectId, bool tracking = false)
    {
        return await Snapshots
            .AsTracking(tracking)
            .Include(s => s.Commit)
            .DefaultOrder()
            .LastOrDefaultAsync(s => s.EntityId == objectId);
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
        var snapshot = await GetCurrentSnapshotByObjectId(objectId);
        return (T?) snapshot?.Entity.DbObject;
    }

    public IQueryable<T> GetCurrentObjects<T>() where T : class
    {
        if (_crdtConfig.Value.EnableProjectedTables)
        {
            return _dbContext.Set<T>().AsNoTracking();
        }
        throw new NotSupportedException("GetCurrentObjects is not supported when not using projected tables");
    }

    public async Task<SyncState> GetCurrentSyncState()
    {
        return await Commits.GetSyncState();
    }

    public async Task<ChangesResult<Commit>> GetChanges(SyncState remoteState)
    {
        return await _dbContext.Commits.GetChanges<Commit, IChange>(remoteState);
    }

    public async Task AddSnapshots(IEnumerable<ObjectSnapshot> snapshots)
    {
        var latestProjectByEntityId = new Dictionary<Guid, (DateTimeOffset, long, Guid)>();
        foreach (var grouping in snapshots.GroupBy(s => s.EntityIsDeleted).OrderByDescending(g => g.Key))//execute deletes first
        {
            foreach (var snapshot in grouping.DefaultOrderDescending())
            {
                _dbContext.Add(snapshot);
                if (latestProjectByEntityId.TryGetValue(snapshot.EntityId, out var latestProjected))
                {
                    // there might be a deleted and un-deleted snapshot for the same entity in the same batch
                    // in that case there's only a 50% chance that they're in the right order, so we need to explicitly only project the latest one
                    if (snapshot.Commit.CompareKey.CompareTo(latestProjected) < 0)
                    {
                        continue;
                    }
                }
                latestProjectByEntityId[snapshot.EntityId] = snapshot.Commit.CompareKey;

                await ProjectSnapshot(snapshot);
            }

            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException e)
            {
                var entries = string.Join(Environment.NewLine, e.Entries.Select(entry => entry.ToString()));
                var message = $"Error saving snapshots: {e.Message}{Environment.NewLine}{entries}";
                _logger.LogError(e, message);
                throw new DbUpdateException(message, e);
            }
        }

        // this extra try/catch was added as a quick way to get the NewEntityOnExistingEntityIsNoOp test to pass
        // it will be removed again in a larger refactor in https://github.com/sillsdev/harmony/pull/56
        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException e)
        {
            var entries = string.Join(Environment.NewLine, e.Entries.Select(entry => entry.ToString()));
            var message = $"Error saving snapshots: {e.Message}{Environment.NewLine}{entries}";
            _logger.LogError(e, message);
            throw new DbUpdateException(message, e);
        }
    }

    private async ValueTask ProjectSnapshot(ObjectSnapshot objectSnapshot)
    {
        if (!_crdtConfig.Value.EnableProjectedTables) return;

        //need to check if an entry exists already, even if this is the root commit it may have already been added to the db
        var existingEntry = await GetEntityEntry(objectSnapshot.Entity.DbObject.GetType(), objectSnapshot.EntityId);
        if (existingEntry is null && objectSnapshot.EntityIsDeleted) return;

        if (existingEntry is null) // add
        {
            // this is a new entity even though it might not be a root snapshot, because we only project the latest snapshot of each entity per sync

            //if we don't make a copy first then the entity will be tracked by the context and be modified
            //by future changes in the same session
            var entity = objectSnapshot.Entity.Copy().DbObject;

            var newEntry = _dbContext.Entry(entity);
            // only mark this single entry as added, rather than the whole graph (this matches the update behaviour below)
            newEntry.State = EntityState.Added;
            newEntry.Property(ObjectSnapshot.ShadowRefName).CurrentValue = objectSnapshot.Id;
        }
        else if (objectSnapshot.EntityIsDeleted) // delete
        {
            _dbContext.Remove(existingEntry.Entity);
        }
        else // update
        {
            var entity = objectSnapshot.Entity.DbObject;
            existingEntry.CurrentValues.SetValues(entity);
            existingEntry.Property(ObjectSnapshot.ShadowRefName).CurrentValue = objectSnapshot.Id;
        }
    }

    private async ValueTask<EntityEntry?> GetEntityEntry(Type entityType, Guid entityId)
    {
        if (!_crdtConfig.Value.EnableProjectedTables) return null;
        var entity = await _dbContext.FindAsync(entityType, entityId);
        return entity is not null ? _dbContext.Entry(entity) : null;
    }

    public CrdtRepository GetScopedRepository(Commit excludeChangesAfterCommit)
    {
        return new CrdtRepository(_dbContext, _crdtConfig, _logger, excludeChangesAfterCommit);
    }

    public async Task AddCommit(Commit commit)
    {
        _dbContext.Add(commit);
        await _dbContext.SaveChangesAsync();
    }

    public async Task AddCommits(IEnumerable<Commit> commits, bool save = true)
    {
        _dbContext.AddRange(commits);
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


    public async Task AddLocalResource(LocalResource localResource)
    {
        _dbContext.Set<LocalResource>().Add(localResource);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteLocalResource(Guid id)
    {
        await _dbContext.Set<LocalResource>().Where(r => r.Id == id).ExecuteDeleteAsync();
    }

    public IAsyncEnumerable<LocalResource> LocalResourcesByIds(IEnumerable<Guid> resourceIds)
    {
        return _dbContext.Set<LocalResource>().Where(r => resourceIds.Contains(r.Id)).AsAsyncEnumerable();
    }
    public IAsyncEnumerable<LocalResource> LocalResources()
    {
        return _dbContext.Set<LocalResource>().AsAsyncEnumerable();
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

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
    }
}

internal class ScopedDbContext(ICrdtDbContext inner, Commit ignoreChangesAfter) : ICrdtDbContext
{
    public IQueryable<Commit> Commits => inner.Commits.WhereBefore(ignoreChangesAfter, inclusive: true);

    public IQueryable<ObjectSnapshot> Snapshots => inner.Snapshots.WhereBefore(ignoreChangesAfter, inclusive: true);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return inner.SaveChangesAsync(cancellationToken);
    }

    public ValueTask<object?> FindAsync(Type entityType, params object?[]? keyValues)
    {
        throw new NotSupportedException("can not support FindAsync when using scoped db context");
    }

    public DbSet<TEntity> Set<TEntity>() where TEntity : class
    {
        throw new NotSupportedException("can not support Set<T> when using scoped db context");
    }

    public DatabaseFacade Database => inner.Database;
    public ChangeTracker ChangeTracker => inner.ChangeTracker;

    public EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class
    {
        return inner.Entry(entity);
    }

    public EntityEntry Entry(object entity)
    {
        return inner.Entry(entity);
    }

    public EntityEntry Add(object entity)
    {
        return inner.Add(entity);
    }

    public void AddRange(IEnumerable<object> entities)
    {
        inner.AddRange(entities);
    }

    public EntityEntry Remove(object entity)
    {
        return inner.Remove(entity);
    }

    public void Dispose()
    {
        inner.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return inner.DisposeAsync();
    }
}
