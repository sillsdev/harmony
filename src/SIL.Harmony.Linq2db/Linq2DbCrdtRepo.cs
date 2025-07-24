using System.Reflection;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Nito.AsyncEx;
using SIL.Harmony.Core;
using SIL.Harmony.Db;
using SIL.Harmony.Resource;

namespace SIL.Harmony.Linq2db;

public class Linq2DbCrdtRepoFactory(
    IServiceProvider serviceProvider,
    ICrdtDbContextFactory dbContextFactory,
    CrdtRepositoryFactory factory) : ICrdtRepositoryFactory
{
    public async Task<ICrdtRepository> CreateRepository()
    {
        return CreateInstance(await dbContextFactory.CreateDbContextAsync());
    }

    public ICrdtRepository CreateRepositorySync()
    {
        return CreateInstance(dbContextFactory.CreateDbContext());
    }

    private Linq2DbCrdtRepo CreateInstance(ICrdtDbContext dbContext)
    {
        return ActivatorUtilities.CreateInstance<Linq2DbCrdtRepo>(serviceProvider,
            factory.CreateInstance(dbContext),
            dbContext);
    }
}

public class Linq2DbCrdtRepo : ICrdtRepository
{
    private readonly ICrdtRepository _original;
    private readonly ICrdtDbContext _dbContext;

    public Linq2DbCrdtRepo(ICrdtRepository original, ICrdtDbContext dbContext)
    {
        _original = original;
        _dbContext = dbContext;
    }

    public ValueTask DisposeAsync()
    {
        return _original.DisposeAsync();
    }

    public void Dispose()
    {
        _original.Dispose();
    }

    public AwaitableDisposable<IDisposable> Lock()
    {
        return _original.Lock();
    }

    public void ClearChangeTracker()
    {
        _original.ClearChangeTracker();
    }

    public async Task AddSnapshots(IEnumerable<ObjectSnapshot> snapshots)
    {
        //save any pending commit changes
        await _dbContext.SaveChangesAsync();
        var projectedEntityIds = new HashSet<Guid>();
        var linqToDbTable = _dbContext.Set<ObjectSnapshot>().ToLinqToDBTable();
        var dataContext = linqToDbTable.DataContext;
        foreach (var grouping in snapshots.GroupBy(s => s.EntityIsDeleted)
                     .OrderByDescending(g => g.Key)) //execute deletes first
        {
            var objectSnapshots = grouping.ToArray();

            //delete existing snapshots before we bulk recreate them
            await _dbContext.Set<ObjectSnapshot>()
                .Where(s => objectSnapshots.Select(s => s.Id).Contains(s.Id))
                .ExecuteDeleteAsync();

            await linqToDbTable.BulkCopyAsync(objectSnapshots);

            //descending to insert the most recent snapshots first, only keep the last objects by ordering by descending
            //don't want to change the objectSnapshot order to preserve the order of the changes
            var snapshotsToProject = objectSnapshots.DefaultOrderDescending().DistinctBy(s => s.EntityId).Select(s => s.Id).ToHashSet();
            foreach (var objectSnapshot in objectSnapshots.IntersectBy(snapshotsToProject, s => s.Id))
            {
                //ensure we skip projecting the same entity multiple times
                if (!projectedEntityIds.Add(objectSnapshot.EntityId)) continue;
                try
                {
                    if (objectSnapshot.EntityIsDeleted)
                    {
                        await DeleteAsync(dataContext, objectSnapshot.Entity);
                    }
                    else
                    {
                        await InsertOrReplaceAsync(dataContext, objectSnapshot.Entity);
                    }
                }
                catch (Exception e)
                {
                    throw new Exception("error when projecting snapshot " + objectSnapshot, e);
                }
            }
        }
    }

    private static readonly MethodInfo InsertOrReplaceAsyncMethodGeneric =
        new Func<IDataContext, object, string?, string?, string?, string?, TableOptions, CancellationToken, Task<int>>(
            DataExtensions.InsertOrReplaceAsync).Method.GetGenericMethodDefinition();

    private Task InsertOrReplaceAsync(IDataContext dataContext, IObjectBase entity)
    {
        var result = InsertOrReplaceAsyncMethodGeneric.MakeGenericMethod(entity.GetType()).Invoke(null,
            [dataContext, entity, null, null, null, null, TableOptions.NotSet, CancellationToken.None]);
        ArgumentNullException.ThrowIfNull(result);
        return (Task)result;
    }

    private static readonly MethodInfo DeleteAsyncMethodGeneric =
        new Func<IDataContext, object, string?, string?, string?, string?, TableOptions, CancellationToken, Task<int>>(
            DataExtensions.DeleteAsync).Method.GetGenericMethodDefinition();

    private Task DeleteAsync(IDataContext dataContext, IObjectBase entity)
    {
        var result = DeleteAsyncMethodGeneric.MakeGenericMethod(entity.GetType()).Invoke(null,
            [dataContext, entity, null, null, null, null, TableOptions.NotSet, CancellationToken.None]);
        ArgumentNullException.ThrowIfNull(result);
        return (Task)result;
    }

    public Task AddCommit(Commit commit)
    {
        return _original.AddCommit(commit);
    }

    public Task AddCommits(IEnumerable<Commit> commits, bool save = true)
    {
        return _original.AddCommits(commits, save);
    }

    public Task UpdateCommitHash(Guid commitId, string hash, string parentHash)
    {
        return _original.UpdateCommitHash(commitId, hash, parentHash);
    }

    public Task<IDbContextTransaction> BeginTransactionAsync()
    {
        return _original.BeginTransactionAsync();
    }

    public bool IsInTransaction => _original.IsInTransaction;

    public Task<bool> HasCommit(Guid commitId)
    {
        return _original.HasCommit(commitId);
    }

    public Task<(Commit? oldestChange, Commit[] newCommits)> FilterExistingCommits(ICollection<Commit> commits)
    {
        return _original.FilterExistingCommits(commits);
    }

    public Task DeleteStaleSnapshots(Commit oldestChange)
    {
        return _original.DeleteStaleSnapshots(oldestChange);
    }

    public Task DeleteSnapshotsAndProjectedTables()
    {
        return _original.DeleteSnapshotsAndProjectedTables();
    }

    public IQueryable<Commit> CurrentCommits()
    {
        return _original.CurrentCommits();
    }

    public IQueryable<ObjectSnapshot> CurrentSnapshots()
    {
        return _original.CurrentSnapshots();
    }

    public IAsyncEnumerable<SimpleSnapshot> CurrenSimpleSnapshots(bool includeDeleted = false)
    {
        return _original.CurrenSimpleSnapshots(includeDeleted);
    }

    public Task<(Dictionary<Guid, ObjectSnapshot> currentSnapshots, Commit[] pendingCommits)>
        GetCurrentSnapshotsAndPendingCommits()
    {
        return _original.GetCurrentSnapshotsAndPendingCommits();
    }

    public Task<Commit?> FindCommitByHash(string hash)
    {
        return _original.FindCommitByHash(hash);
    }

    public Task<Commit?> FindPreviousCommit(Commit commit)
    {
        return _original.FindPreviousCommit(commit);
    }

    public Task<Commit[]> GetCommitsAfter(Commit? commit)
    {
        return _original.GetCommitsAfter(commit);
    }

    public Task<ObjectSnapshot?> FindSnapshot(Guid id, bool tracking = false)
    {
        return _original.FindSnapshot(id, tracking);
    }

    public Task<ObjectSnapshot?> GetCurrentSnapshotByObjectId(Guid objectId, bool tracking = false)
    {
        return _original.GetCurrentSnapshotByObjectId(objectId, tracking);
    }

    public Task<T> GetObjectBySnapshotId<T>(Guid snapshotId)
    {
        return _original.GetObjectBySnapshotId<T>(snapshotId);
    }

    public Task<T?> GetCurrent<T>(Guid objectId) where T : class
    {
        return _original.GetCurrent<T>(objectId);
    }

    public IQueryable<T> GetCurrentObjects<T>() where T : class
    {
        return _original.GetCurrentObjects<T>();
    }

    public Task<SyncState> GetCurrentSyncState()
    {
        return _original.GetCurrentSyncState();
    }

    public Task<ChangesResult<Commit>> GetChanges(SyncState remoteState)
    {
        return _original.GetChanges(remoteState);
    }

    public CrdtRepository GetScopedRepository(Commit excludeChangesAfterCommit)
    {
        return _original.GetScopedRepository(excludeChangesAfterCommit);
    }

    public HybridDateTime? GetLatestDateTime()
    {
        return _original.GetLatestDateTime();
    }

    public Task AddLocalResource(LocalResource localResource)
    {
        return _original.AddLocalResource(localResource);
    }

    public IAsyncEnumerable<LocalResource> LocalResourcesByIds(IEnumerable<Guid> resourceIds)
    {
        return _original.LocalResourcesByIds(resourceIds);
    }

    public IAsyncEnumerable<LocalResource> LocalResources()
    {
        return _original.LocalResources();
    }

    public IQueryable<Guid> LocalResourceIds()
    {
        return _original.LocalResourceIds();
    }

    public Task<LocalResource?> GetLocalResource(Guid resourceId)
    {
        return _original.GetLocalResource(resourceId);
    }
}