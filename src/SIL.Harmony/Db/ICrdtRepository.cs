using Microsoft.EntityFrameworkCore.Storage;
using Nito.AsyncEx;
using SIL.Harmony.Resource;

namespace SIL.Harmony.Db;

public interface ICrdtRepository : IAsyncDisposable, IDisposable
{
    AwaitableDisposable<IDisposable> Lock();
    void ClearChangeTracker();
    Task<IDbContextTransaction> BeginTransactionAsync();
    bool IsInTransaction { get; }
    Task<bool> HasCommit(Guid commitId);
    Task<(Commit? oldestChange, Commit[] newCommits)> FilterExistingCommits(ICollection<Commit> commits);
    Task DeleteStaleSnapshots(Commit oldestChange);
    Task DeleteSnapshotsAndProjectedTables();
    IQueryable<Commit> CurrentCommits();
    IQueryable<ObjectSnapshot> CurrentSnapshots();
    IAsyncEnumerable<SimpleSnapshot> CurrenSimpleSnapshots(bool includeDeleted = false);
    Task<(Dictionary<Guid, ObjectSnapshot> currentSnapshots, Commit[] pendingCommits)> GetCurrentSnapshotsAndPendingCommits();
    Task<Commit?> FindCommitByHash(string hash);
    Task<Commit?> FindPreviousCommit(Commit commit);
    Task<Commit[]> GetCommitsAfter(Commit? commit);
    Task<ObjectSnapshot?> FindSnapshot(Guid id, bool tracking = false);
    Task<ObjectSnapshot?> GetCurrentSnapshotByObjectId(Guid objectId, bool tracking = false);
    Task<T> GetObjectBySnapshotId<T>(Guid snapshotId);
    Task<T?> GetCurrent<T>(Guid objectId) where T: class;
    IQueryable<T> GetCurrentObjects<T>() where T : class;
    Task<SyncState> GetCurrentSyncState();
    Task<ChangesResult<Commit>> GetChanges(SyncState remoteState);
    Task AddSnapshots(IEnumerable<ObjectSnapshot> snapshots);
    CrdtRepository GetScopedRepository(Commit excludeChangesAfterCommit);
    Task AddCommit(Commit commit);
    Task AddCommits(IEnumerable<Commit> commits, bool save = true);
    Task UpdateCommitHash(Guid commitId, string hash, string parentHash);
    HybridDateTime? GetLatestDateTime();
    Task AddLocalResource(LocalResource localResource);
    IAsyncEnumerable<LocalResource> LocalResourcesByIds(IEnumerable<Guid> resourceIds);
    IAsyncEnumerable<LocalResource> LocalResources();

    /// <summary>
    /// primarily for filtering other queries
    /// </summary>
    IQueryable<Guid> LocalResourceIds();

    Task<LocalResource?> GetLocalResource(Guid resourceId);
}