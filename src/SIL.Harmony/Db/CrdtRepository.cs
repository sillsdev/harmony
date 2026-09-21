using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
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
    private readonly IOptions<HarmonyConfig> _crdtConfig;
    private readonly ILogger<CrdtRepository> _logger;
    private readonly FastProjection _fastProjection;
    private readonly IProjectedEntityInterceptor[] _interceptors;

    public CrdtRepository(ICrdtDbContext dbContext, IOptions<HarmonyConfig> crdtConfig,
        ILogger<CrdtRepository> logger,
        FastProjection fastProjection,
        IEnumerable<IProjectedEntityInterceptor> interceptors)
    {
        _crdtConfig = crdtConfig;
        _dbContext = dbContext;
        _logger = logger;
        _fastProjection = fastProjection;
        _interceptors = interceptors as IProjectedEntityInterceptor[] ?? interceptors.ToArray();
        _lock = Locks.GetOrAdd(DatabaseIdentifier, _ => new AsyncLock());
    }

    /// <summary>
    /// A commit at which every entity's newest snapshot is its complete state, so the snapshots as of it are a sound
    /// base to replay from. Only the checkpoint lookups create one, and it is only as fresh as the query behind it.
    /// </summary>
    internal sealed record Checkpoint
    {
        private Checkpoint(Commit commit)
        {
            Commit = commit;
        }

        public Commit Commit { get; }

        internal static Checkpoint? From(Commit? commit) => commit is null ? null : new Checkpoint(commit);
    }

    /// <summary>
    /// Everything a replay needs: the state it resumes from and the commits to apply on top of it.
    /// Only this class builds one, so the resume point and the commits can never disagree.
    /// </summary>
    /// <param name="ResumeFrom">null is the state before the first commit, so <paramref name="Commits"/> is every commit there is</param>
    /// <param name="Commits">empty means there is nothing to replay, whatever <paramref name="ResumeFrom"/> says</param>
    internal readonly record struct ReplayWindow(Checkpoint? ResumeFrom, SortedSet<Commit> Commits);

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

    /// <summary>tracking on purpose: rewritten hashes and checkpoint flags are persisted by mutating the commits it loaded</summary>
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
        //EF.Parameter forces a single JSON parameter; without it EF 10+ emits one parameter per id and overflows SQLite's parameter limit
        var commitIds = commits.Select(c => c.Id);
        var commitIdsToExclude = await Commits
            .Where(c => EF.Parameter(commitIds).Contains(c.Id))
            .Select(c => c.Id)
            .ToArrayAsync();
        var newCommits = commits.ExceptBy(commitIdsToExclude, c => c.Id).Select(commit =>
        {
            if (oldestChange is null || commit.CompareKey.CompareTo(oldestChange.CompareKey) < 0) oldestChange = commit;
            return commit;
        }).ToArray(); //need to use ToArray because the select has side effects that must trigger before this method returns
        return (oldestChange, newCommits);
    }

    public async Task DeleteSnapshotsAfter(Commit commit)
    {
        await Snapshots.WhereAfter(commit).ExecuteDeleteAsync();
    }

    private IQueryable<Commit> Checkpoints => Commits.Where(c => c.IsSnapshotCheckpoint);

    public async Task<Checkpoint?> FindCheckpointBefore(Commit commit)
    {
        return Checkpoint.From(await Checkpoints
            .WhereBefore(commit, inclusive: false)
            .DefaultOrderDescending()
            .FirstOrDefaultAsync());
    }

    public async Task<Checkpoint?> FindCheckpointAtOrBefore(Commit commit)
    {
        return Checkpoint.From(await Checkpoints
            .WhereBefore(commit, inclusive: true)
            .DefaultOrderDescending()
            .FirstOrDefaultAsync());
    }

    public async Task<Checkpoint?> FindCheckpointAtOrAfter(Commit commit)
    {
        return Checkpoint.From(await Checkpoints
            .WhereAfter(commit, inclusive: true)
            .DefaultOrder()
            .FirstOrDefaultAsync());
    }

    /// <param name="checkpoint">null is the state before the first commit, i.e. no snapshots at all</param>
    public ISnapshotView SnapshotViewAsOf(Checkpoint? checkpoint)
    {
        return checkpoint is null ? EmptySnapshotView.Instance : new DbSnapshotView(_dbContext, checkpoint.Commit);
    }

    public ISnapshotView CurrentSnapshotView()
    {
        return new DbSnapshotView(_dbContext, null);
    }

    public async Task DeleteSnapshotsAndProjectedTables()
    {
        if (_crdtConfig.Value.EnableProjectedTables)
        {
            //dependents first: ExecuteDelete never sees EF's client side fixup, so only a database level cascade
            //saves a table deleted before the rows pointing at it. The insert order is principals first, so reverse it
            var orderedTypes = FastProjection.OrderTypesByDependency(_dbContext.Model, _crdtConfig.Value.ObjectTypes);
            orderedTypes.Reverse();
            foreach (var objectType in orderedTypes)
            {
                await (Task)deleteProjectedTableMethod.MakeGenericMethod(objectType)
                    .Invoke(null, [_dbContext])!;
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

    public IQueryable<ObjectSnapshot> CurrentSnapshots()
    {
        return DbSnapshotView.CurrentSnapshotsQuery(_dbContext, upToInclusive: null);
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

    /// <summary>The window that rebuilds every snapshot: all of history, resuming from nothing.</summary>
    public async Task<ReplayWindow> WholeHistory()
    {
        return new ReplayWindow(null, await GetCommitsAfter(null));
    }

    private Task<SortedSet<Commit>> GetCommitsAfter(Checkpoint? checkpoint)
    {
        return GetCommitsBetween(checkpoint?.Commit, upToInclusive: null);
    }

    /// <summary>The commits in <c>(afterExclusive, upToInclusive]</c>. Null <paramref name="afterExclusive"/> starts at the beginning.</summary>
    public Task<SortedSet<Commit>> GetCommitsBetween(Checkpoint? afterExclusive, Commit upToInclusive)
    {
        return GetCommitsBetween(afterExclusive?.Commit, upToInclusive);
    }

    /// <summary>The commits in <c>(afterExclusive, upToInclusive]</c>; a null bound is open.</summary>
    private async Task<SortedSet<Commit>> GetCommitsBetween(Commit? afterExclusive, Commit? upToInclusive)
    {
        IQueryable<Commit> commits = Commits.Include(c => c.ChangeEntities);
        if (afterExclusive is not null) commits = commits.WhereAfter(afterExclusive);
        if (upToInclusive is not null) commits = commits.WhereBefore(upToInclusive, inclusive: true);
        return await commits.ToSortedSetAsync();
    }

    public async Task<ObjectSnapshot?> GetCurrentSnapshotByObjectId(Guid objectId)
    {
        return await Snapshots
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
        return (T)entity;
    }

    public async Task<T?> GetCurrent<T>(Guid objectId) where T : class
    {
        var snapshot = await GetCurrentSnapshotByObjectId(objectId);
        return (T?)snapshot?.Entity.DbObject;
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

    /// <summary>
    /// Saves the snapshots and, with them, which commits a later replay can resume from.
    /// </summary>
    public Task AddSnapshots(IEnumerable<ObjectSnapshot> snapshots, IReadOnlyList<CheckpointFlag> checkpointFlags)
    {
        //the commits are tracked, so this rides along on the save below
        foreach (var (commit, isCheckpoint) in checkpointFlags)
        {
            commit.IsSnapshotCheckpoint = isCheckpoint;
        }
        var snapshotList = snapshots as IReadOnlyCollection<ObjectSnapshot> ?? snapshots.ToArray();
        var notify = ShouldNotifyProjectedChanges();
        return _fastProjection.AddSnapshotsRawAsync(
            _dbContext,
            snapshotList,
            notify ? NotifyProjectedChanges : null);
    }

    private bool ShouldNotifyProjectedChanges()
    {
        if (!_crdtConfig.Value.EnableProjectedTables) return false;
        if (_interceptors.Length > 0) return true;
        return !ReferenceEquals(
            _crdtConfig.Value.OnProjectedEntitiesChanged,
            HarmonyConfig.DefaultOnProjectedEntitiesChanged);
    }

    private async ValueTask NotifyProjectedChanges(IReadOnlyCollection<ObjectSnapshot> latest)
    {
        var changes = new List<ProjectedEntityChange>(latest.Count);
        foreach (var snapshot in latest)
        {
            var entity = snapshot.Entity.DbObject;
            changes.Add(new ProjectedEntityChange
            {
                Entity = entity,
                EntityId = snapshot.EntityId,
                ClrType = entity.GetType(),
                Kind = snapshot.EntityIsDeleted ? ProjectedChangeKind.Delete : ProjectedChangeKind.Upsert,
                Snapshot = snapshot
            });
        }

        var batch = new ProjectedEntityBatch { DbContext = _dbContext, Changes = changes };
        foreach (var interceptor in _interceptors)
            await interceptor.OnProjectedEntitiesChanged(batch);
        await _crdtConfig.Value.OnProjectedEntitiesChanged(batch);
    }

    /// <inheritdoc cref="AddCommits"/>
    public Task<ReplayWindow> AddCommit(Commit commit) => AddCommits([commit]);

    /// <summary>
    /// Adds commits to the database. If any of the new commits were authored before any commits that
    /// are already in the database, then history will be rewritten by updating those commit hashes.
    /// </summary>
    /// <returns>what the caller must replay to bring the snapshots back in line</returns>
    public async Task<ReplayWindow> AddCommits(IEnumerable<Commit> commits)
    {
        var newCommits = commits as IReadOnlyCollection<Commit> ?? commits.ToArray();
        if (newCommits.Count == 0) return new ReplayWindow(null, []);
        //resolving the resume point before loading is what keeps this to one query: the window always reaches
        //back past the oldest added commit, so it covers the commits needing a rehash too
        var resumeFrom = await FindCheckpointBefore(newCommits.MinBy(c => c.CompareKey)!);
        var commitsToApply = (await GetCommitsAfter(resumeFrom)).UnionBy(newCommits, c => c.Id).ToSortedSet();
        //a commit inserted in the past invalidates every hash after it. The window starts right after the resume
        //point, so re-linking all of it covers that; commits before the insert rehash to the value they already
        //hold, which costs a hash and no update
        UpdateCommitHashes(commitsToApply, resumeFrom?.Commit);
        _dbContext.AddRange(newCommits);
        await _dbContext.SaveChangesAsync();
        return new ReplayWindow(resumeFrom, commitsToApply);
    }

    private void UpdateCommitHashes(SortedSet<Commit> commits, Commit? parentCommit)
    {
        var previousCommitHash = parentCommit?.Hash ?? CommitBase.NullParentHash;
        foreach (var commit in commits)
        {
            commit.SetParentHash(previousCommitHash);
            previousCommitHash = commit.Hash;
        }
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
