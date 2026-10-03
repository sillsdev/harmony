using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
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
    /// base to replay from. Exists so a plain commit can't be handed to <see cref="SnapshotViewAsOf"/> or a
    /// <see cref="ReplayWindow"/>; only as fresh as the query behind it.
    /// </summary>
    internal sealed record CheckpointCommit
    {
        private CheckpointCommit(Commit commit)
        {
            if (!commit.IsSnapshotCheckpoint) throw new ArgumentException($"Commit {commit.Id} is not a snapshot checkpoint", nameof(commit));
            Commit = commit;
        }

        public Commit Commit { get; }

        internal static CheckpointCommit? From(Commit? commit) => commit is null ? null : new CheckpointCommit(commit);
    }

    /// <summary>
    /// Everything a replay needs: the state it resumes from and the commits to apply on top of it.
    /// Only this class builds one, so the resume point and the commits can never disagree.
    /// </summary>
    /// <param name="ResumeFrom">null is the state before the first commit, so <paramref name="Commits"/> is every commit there is</param>
    /// <param name="Commits">empty means there is nothing to replay, whatever <paramref name="ResumeFrom"/> says</param>
    internal readonly record struct ReplayWindow(CheckpointCommit? ResumeFrom, SortedSet<Commit> Commits);

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

    private IQueryable<Commit> CheckpointCommits => Commits.Where(c => c.IsSnapshotCheckpoint);

    public async Task<CheckpointCommit?> FindCheckpointBefore(Commit commit)
    {
        return CheckpointCommit.From(await CheckpointCommits
            .WhereBefore(commit, inclusive: false)
            .DefaultOrderDescending()
            .FirstOrDefaultAsync());
    }

    public async Task<CheckpointCommit?> FindCheckpointAtOrBefore(Commit commit)
    {
        return CheckpointCommit.From(await CheckpointCommits
            .WhereBefore(commit, inclusive: true)
            .DefaultOrderDescending()
            .FirstOrDefaultAsync());
    }

    public async Task<CheckpointCommit?> FindCheckpointAtOrAfter(Commit commit)
    {
        return CheckpointCommit.From(await CheckpointCommits
            .WhereAfter(commit, inclusive: true)
            .DefaultOrder()
            .FirstOrDefaultAsync());
    }

    /// <param name="checkpoint">null is the state before the first commit, i.e. no snapshots at all</param>
    public ISnapshotView SnapshotViewAsOf(CheckpointCommit? checkpoint)
    {
        return checkpoint is null ? EmptySnapshotView.Instance : new DbSnapshotView(_dbContext, checkpoint.Commit);
    }

    public ISnapshotView CurrentSnapshotView()
    {
        return new DbSnapshotView(_dbContext, null);
    }

    public async Task DeleteSnapshotsAfter(Commit commit)
    {
        //going through the commits the snapshots hang off beats filtering the snapshots themselves: the sort key lives
        //on Commits, so the direct form has to join every snapshot row to its commit
        var staleCommitIds = Commits.WhereAfter(commit).Select(c => c.Id);
        await Snapshots.Where(s => staleCommitIds.Contains(s.CommitId)).ExecuteDeleteAsync();
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

    private Task<SortedSet<Commit>> GetCommitsAfter(CheckpointCommit? checkpoint)
    {
        return GetCommitsBetween(checkpoint?.Commit, upToInclusive: null);
    }

    /// <summary>The commits in <c>(afterExclusive, upToInclusive]</c>. Null <paramref name="afterExclusive"/> starts at the beginning.</summary>
    public Task<SortedSet<Commit>> GetCommitsBetween(CheckpointCommit? afterExclusive, Commit upToInclusive)
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

    private ValueTask NotifyProjectedChanges(IReadOnlyCollection<ObjectSnapshot> latest)
    {
        return NotifyProjectedChanges(latest, removed: []);
    }

    private async ValueTask NotifyProjectedChanges(IReadOnlyCollection<ObjectSnapshot> latest, IReadOnlyCollection<ObjectSnapshot> removed)
    {
        var changes = new List<ProjectedEntityChange>(latest.Count + removed.Count);
        foreach (var snapshot in latest)
        {
            changes.Add(ToProjectedChange(snapshot,
                snapshot.EntityIsDeleted ? ProjectedChangeKind.Delete : ProjectedChangeKind.Upsert));
        }
        foreach (var snapshot in removed)
        {
            changes.Add(ToProjectedChange(snapshot, ProjectedChangeKind.Delete));
        }

        var batch = new ProjectedEntityBatch { DbContext = _dbContext, Changes = changes };
        foreach (var interceptor in _interceptors)
            await interceptor.OnProjectedEntitiesChanged(batch);
        await _crdtConfig.Value.OnProjectedEntitiesChanged(batch);
    }

    private static ProjectedEntityChange ToProjectedChange(ObjectSnapshot snapshot, ProjectedChangeKind kind)
    {
        var entity = snapshot.Entity.DbObject;
        return new ProjectedEntityChange
        {
            Entity = entity,
            EntityId = snapshot.EntityId,
            ClrType = entity.GetType(),
            Kind = kind,
            Snapshot = snapshot
        };
    }

    /// <summary>
    /// The snapshots a replay resuming from <paramref name="checkpoint"/> deletes, so the caller can check which entities
    /// the replay didn't make new snapshots for, see <see cref="ReprojectEntitiesWithoutNewSnapshots"/>.
    /// </summary>
    public async Task<ObjectSnapshot[]> SnapshotsAfter(CheckpointCommit? checkpoint)
    {
        var snapshots = Snapshots;
        if (checkpoint is not null)
        {
            var commitIdsAfter = Commits.WhereAfter(checkpoint.Commit).Select(c => c.Id);
            snapshots = snapshots.Where(s => commitIdsAfter.Contains(s.CommitId));
        }
        return await snapshots.ToArrayAsync();
    }

    /// <summary>
    /// A replay only projects the snapshots it makes. Normally a replay makes snapshots for the same entities it deleted
    /// snapshots of, but not when a change type is no longer known. The projected rows of those entities go back to their
    /// current snapshot, or are deleted when the entity has no snapshot left.
    /// </summary>
    /// <param name="replacedSnapshots">from <see cref="SnapshotsAfter"/>, before the replay</param>
    /// <param name="newSnapshots">the snapshots the replay made</param>
    public async Task ReprojectEntitiesWithoutNewSnapshots(IReadOnlyCollection<ObjectSnapshot> replacedSnapshots,
        IReadOnlyCollection<ObjectSnapshot> newSnapshots)
    {
        if (!_crdtConfig.Value.EnableProjectedTables) return;
        var entitiesWithNewSnapshots = newSnapshots.Select(s => s.EntityId).ToHashSet();
        var currentView = CurrentSnapshotView();
        var current = new List<ObjectSnapshot>();
        var removed = new List<ObjectSnapshot>();
        foreach (var replaced in replacedSnapshots.DistinctBy(s => s.EntityId))
        {
            if (entitiesWithNewSnapshots.Contains(replaced.EntityId)) continue;
            if (await currentView.GetAsync(replaced.EntityId) is { } snapshot) current.Add(snapshot);
            else removed.Add(replaced);
        }

        await _fastProjection.ReprojectAsync(_dbContext, current, removed);
        if (ShouldNotifyProjectedChanges() && current.Count + removed.Count > 0)
            await NotifyProjectedChanges(current, removed);
    }

    /// <summary>The window that rebuilds every snapshot: all of history, resuming from nothing.</summary>
    public async Task<ReplayWindow> WholeHistory()
    {
        return new ReplayWindow(null, await GetCommitsAfter(null));
    }

    /// <summary>The window that rebuilds every snapshot from <paramref name="commit"/> onwards.</summary>
    public async Task<ReplayWindow> ReplayWindowFrom(Commit commit)
    {
        var resumeFrom = await FindCheckpointBefore(commit);
        return new ReplayWindow(resumeFrom, await GetCommitsAfter(resumeFrom));
    }

    /// <summary>
    /// The oldest commit with a change whose <c>$type</c> is one of <paramref name="changeTypes"/>.
    /// Reads every change, so it's slow, only use it for rare operations. SQLite only.
    /// </summary>
    public async Task<Commit?> FindOldestCommitWithChangeTypes(IReadOnlyCollection<string> changeTypes)
    {
        if (changeTypes.Count == 0) return null;
        if (!_dbContext.Database.IsSqlite())
            throw new NotSupportedException(
                $"Querying changes by type is only supported on SQLite, not {_dbContext.Database.ProviderName}");
        //the key must be quoted in the path, because $ has a special meaning in json paths
        var typeDiscriminatorPath = $"$.\"{CrdtConstants.ChangeDiscriminatorProperty}\"";
        //one json array parameter, so the parameter count doesn't depend on how many types there are
        var changeTypesJson = JsonSerializer.Serialize(changeTypes);
        var commitIds = await _dbContext.Database.SqlQuery<Guid>($"""
            SELECT c.Id AS Value FROM Commits c
            WHERE EXISTS (
                SELECT 1 FROM ChangeEntities ce
                WHERE ce.CommitId = c.Id
                  AND json_extract(ce.Change, {typeDiscriminatorPath}) IN (SELECT value FROM json_each({changeTypesJson}))
            )
            ORDER BY c.DateTime, c.Counter, c.Id
            LIMIT 1
            """).ToListAsync();
        if (commitIds is not [var commitId]) return null;
        return await Commits.SingleAsync(c => c.Id == commitId);
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
        //we're inserting commits in the past/rewriting history, so we need to update the previous commit hashes
        //(We unnecessarily rehash any non-new commits that come after the resume checkpoint. That's ok)
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


    private DbSet<LocalStateEntry> LocalState => _dbContext.Set<LocalStateEntry>();

    public async Task<T?> GetLocalState<T>(string key)
    {
        var value = await LocalState.AsNoTracking()
            .Where(e => e.Key == key)
            .Select(e => e.Value)
            .SingleOrDefaultAsync();
        return value is null ? default : JsonSerializer.Deserialize<T>(value);
    }

    public async Task SetLocalState<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        var entry = await LocalState.SingleOrDefaultAsync(e => e.Key == key);
        if (entry is null) LocalState.Add(new LocalStateEntry { Key = key, Value = json });
        else entry.Value = json;
        await _dbContext.SaveChangesAsync();
    }

    public async Task RemoveLocalState(string key)
    {
        await LocalState.Where(e => e.Key == key).ExecuteDeleteAsync();
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
