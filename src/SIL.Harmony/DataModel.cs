using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony;

public record SyncResults(Commit[] MissingFromLocal, Commit[] MissingFromRemote, bool IsSynced);

public class DataModel : ISyncable, IAsyncDisposable
{
    /// <summary>
    /// after adding any commit validate the commit history, not great for performance but good for testing.
    /// </summary>
    private bool AlwaysValidate => _crdtConfig.Value.AlwaysValidateCommits;

    private readonly CrdtRepositoryFactory _crdtRepositoryFactory;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly IHybridDateTimeProvider _timeProvider;
    private readonly IOptions<HarmonyConfig> _crdtConfig;
    private readonly ILogger<DataModel> _logger;
    private readonly ModelVersionService _modelVersionService;
    private readonly SyncService _syncService;

    //constructor must be internal because CrdtRepository is internal
    internal DataModel(CrdtRepositoryFactory crdtRepositoryFactory,
        JsonSerializerOptions serializerOptions,
        IHybridDateTimeProvider timeProvider,
        IOptions<HarmonyConfig> crdtConfig,
        ILogger<DataModel> logger,
        ModelVersionService modelVersionService,
        SyncService syncService)
    {
        _crdtRepositoryFactory = crdtRepositoryFactory;
        _serializerOptions = serializerOptions;
        _timeProvider = timeProvider;
        _crdtConfig = crdtConfig;
        _logger = logger;
        _modelVersionService = modelVersionService;
        _syncService = syncService;
    }


    /// <summary>
    /// add a change to the model, snapshots will be updated
    /// </summary>
    /// <param name="clientId">
    /// Unique identifier for the client, used to determine what changes need to be synced, for a single install it should always author commits with the same client id
    /// if the client id changes too much it could slow down the sync process
    /// </param>
    /// <param name="change">change to be applied to the model</param>
    /// <param name="commitMetadata">used to store metadata on the commit, for example app version or author id</param>
    /// <returns>the newly created commit</returns>
    public async Task<Commit> AddChange(
        Guid clientId,
        IChange change,
        CommitMetadata? commitMetadata = null)
    {
        return await AddChanges(clientId, [change], commitMetadata);
    }

    public async Task AddManyChanges(Guid clientId,
        IEnumerable<IChange> changes,
        Func<CommitMetadata?> commitMetadata,
        int changesPerCommitMax = 100)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var commits = changes
            .Chunk(changesPerCommitMax)
            .Select(chunk => NewCommit(clientId, commitMetadata(), chunk))
            .ToArray();
        if (commits is []) return;
        using var locked = await repo.Lock();
        repo.ClearChangeTracker();

        await using var transaction = await repo.BeginTransactionAsync();
        var replayWindow = await repo.AddCommits(commits);
        await UpdateSnapshots(repo, replayWindow);
        if (AlwaysValidate) await ValidateCommits(repo);
        await transaction.CommitAsync();
    }

    /// <inheritdoc cref="AddChange"/>
    public async Task<Commit> AddChanges(
        Guid clientId,
        IEnumerable<IChange> changes,
        CommitMetadata? commitMetadata = null)
    {
        var commit = NewCommit(clientId, commitMetadata, changes);
        await Add(commit);
        return commit;
    }

    private Commit NewCommit(Guid clientId, CommitMetadata? commitMetadata, IEnumerable<IChange> changes)
    {
        var commit = new Commit
        {
            ClientId = clientId,
            HybridDateTime = _timeProvider.GetDateTime(),
            Metadata = commitMetadata ?? new()
        };
        commit.ChangeEntities.AddRange(changes.Select((c, i) => ToChangeEntity(c, i, commit.Id, _crdtConfig.Value)));
        return commit;
    }

    private async Task Add(Commit commit)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        using var locked = await repo.Lock();
        if (await repo.HasCommit(commit.Id)) return;
        repo.ClearChangeTracker();

        await using var transaction = repo.IsInTransaction ? null : await repo.BeginTransactionAsync();
        var replayWindow = await repo.AddCommit(commit);
        await UpdateSnapshots(repo, replayWindow);

        if (AlwaysValidate) await ValidateCommits(repo);


        if (transaction is not null) await transaction.CommitAsync();
    }

    internal static ChangeEntity<IChange> ToChangeEntity(IChange change, int index, Guid commitId, HarmonyConfig config)
    {
        return new ChangeEntity<IChange>()
        {
            Change = change,
            CommitId = commitId,
            EntityId = change.EntityId,
            Index = index,
            AuthoredModelVersion = config.CurrentModelVersion
        };
    }

    async Task ISyncable.AddRangeFromSync(IEnumerable<Commit> commits)
    {
        commits = commits.ToArray();
        try
        {
            await using var repo = await _crdtRepositoryFactory.CreateRepository();
            using var locked = await repo.Lock();
            repo.ClearChangeTracker();
            _timeProvider.TakeLatestTime(commits.Select(c => c.HybridDateTime));
            var (oldestChange, newCommits) = await repo.FilterExistingCommits(commits.ToArray());
            //no changes added
            if (oldestChange is null || newCommits is []) return;
            CrdtRepository.ReplayWindow replayWindow;
            await using (var transaction = await repo.BeginTransactionAsync())
            {
                replayWindow = await repo.AddCommits(newCommits);
                await UpdateSnapshots(repo, replayWindow);
                if (AlwaysValidate) await ValidateCommits(repo);
                await transaction.CommitAsync();
            }
            //a window resuming from nothing rebuilt every snapshot, the first sync into an empty database is the
            //big one, since it pulls the whole project. Any later full rebuild is small, so compacting after it is cheap
            if (replayWindow.ResumeFrom is null) await repo.CompactDatabase();
        }
        catch (DbUpdateException e)
        {
            _logger.LogError(e, "Failed to sync commits, check {FailedImportPath} for more details", _crdtConfig.Value.FailedSyncOutputPath);
            await DumpFailedSync(new
            {
                ExceptionMessage = e.ToString(),
                Commits = commits.DefaultOrder(),
                Objects = e.Entries.Select(entry => entry.Entity)
            });
            throw;
        }
    }

    private async Task DumpFailedSync(object data)
    {
        try
        {
            Directory.CreateDirectory(_crdtConfig.Value.FailedSyncOutputPath);
            await using var failedImport =
                File.Create(Path.Combine(_crdtConfig.Value.FailedSyncOutputPath, "last-failed-import.json"));
            await JsonSerializer.SerializeAsync(failedImport, data, _serializerOptions);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to dump failed import");
        }
    }

    ValueTask<bool> ISyncable.ShouldSync()
    {
        return ValueTask.FromResult(true);
    }

    private Task UpdateSnapshots(CrdtRepository repo, CrdtRepository.ReplayWindow window)
    {
        return SnapshotReplay.UpdateSnapshots(repo, window, _crdtConfig.Value);
    }

    private async Task ValidateCommits(CrdtRepository repo)
    {
        Commit? parentCommit = null;
        await foreach (var commit in repo.CurrentCommits().AsNoTracking().AsAsyncEnumerable())
        {
            var parentHash = parentCommit?.Hash ?? CommitBase.NullParentHash;
            var expectedHash = commit.GenerateHash(parentHash);
            if (commit.Hash == expectedHash && commit.ParentHash == parentHash)
            {
                parentCommit = commit;
                continue;
            }

            var actualParentCommit = await repo.FindCommitByHash(commit.ParentHash);
            var commitWithSnapshots = await repo.CurrentCommits().Include(c => c.Snapshots).SingleAsync(c => c.Id == commit.Id);
            throw new CommitValidationException(
                $"Commit {commit} does not match expected hash, parent hash [{commit.ParentHash}] !== [{parentHash}], expected parent {parentCommit?.ToString() ?? "null"} and actual parent {actualParentCommit?.ToString() ?? "null"}, with snapshots: {string.Join(", ", commitWithSnapshots.Snapshots.Select(s => s.Entity.DbObject))}");
        }
    }

    public async Task RegenerateSnapshots()
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        using var locked = await repo.Lock();
        repo.ClearChangeTracker();
        await using (var transaction = await repo.BeginTransactionAsync())
        {
            await SnapshotReplay.RegenerateAll(repo, _crdtConfig.Value);
            await transaction.CommitAsync();
        }
        //every snapshot and projected row was just deleted and rewritten, so the file is mostly free pages now.
        //Still under the lock so nothing writes between the rebuild and the cleanup
        await repo.CompactDatabase();
    }

    /// <summary>
    /// Reclaims the space a mass delete left behind and refreshes the query planner statistics, SQLite only.
    /// Runs on its own after <see cref="RegenerateSnapshots"/> and after the first sync into an empty database;
    /// call it after any other bulk delete the application does. Expensive: it rewrites the whole file and
    /// holds the write lock while it does, so not something to run after every sync
    /// </summary>
    public async Task CompactDatabase()
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        using var locked = await repo.Lock();
        await repo.CompactDatabase();
    }

    /// <inheritdoc cref="ModelVersionService.ReconcileModelVersions"/>
    public Task<ModelVersionReconcileResult> ReconcileModelVersions()
    {
        return _modelVersionService.ReconcileModelVersions();
    }

    /// <summary>
    /// Gets a value from the local state, which is stored in this database and never synced.
    /// Keys starting with <c>harmony:</c> are used by Harmony itself, apps should use their own prefix.
    /// </summary>
    /// <returns>the value, or default when the key doesn't exist</returns>
    public async Task<T?> GetLocalState<T>(string key)
    {
        return await _crdtRepositoryFactory.Execute(repo => repo.GetLocalState<T>(key));
    }

    /// <summary>
    /// Adds or replaces a value in the local state, the value is stored as json.
    /// </summary>
    /// <inheritdoc cref="GetLocalState{T}"/>
    public async Task SetLocalState<T>(string key, T value)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        //the write is read-then-add, so two first writes to a key would both add it
        using var locked = await repo.Lock();
        await repo.SetLocalState(key, value);
    }

    /// <summary>
    /// Removes a value from the local state, does nothing when the key doesn't exist.
    /// </summary>
    /// <inheritdoc cref="GetLocalState{T}"/>
    public async Task RemoveLocalState(string key)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        using var locked = await repo.Lock();
        await repo.RemoveLocalState(key);
    }

    public async Task<ObjectSnapshot> GetLatestSnapshotByObjectId(Guid entityId)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await repo.GetCurrentSnapshotByObjectId(entityId) ??
               throw new ArgumentException($"unable to find snapshot for entity {entityId}");
    }

    public async IAsyncEnumerable<ObjectSnapshot> GetLatestSnapshots()
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        await foreach (var snapshot in repo.CurrentSnapshots().AsAsyncEnumerable())
        {
            yield return snapshot;
        }
    }

    public async Task<T?> GetLatest<T>(Guid objectId) where T : class
    {
        return await _crdtRepositoryFactory.Execute(repo => repo.GetCurrent<T>(objectId));
    }


    public IAsyncEnumerable<T> QueryLatest<T>(Func<IQueryable<T>, IQueryable<T>>? apply = null)
        where T : class
    {
        return QueryLatest<T, T>(apply ?? (static q => q));
    }

    public async IAsyncEnumerable<TResult> QueryLatest<T, TResult>(Func<IQueryable<T>, IQueryable<TResult>> apply) where T : class
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var q = repo.GetCurrentObjects<T>();
        if (q is IQueryable<IOrderableCrdt>)
        {
            q = q.OrderBy(o => EF.Property<double>(o, nameof(IOrderableCrdt.Order)))
                .ThenBy(o => EF.Property<Guid>(o, nameof(IOrderableCrdt.Id)));
        }

        await foreach (var result in apply(q).AsAsyncEnumerable())
        {
            yield return result;
        }
    }

    public async Task<ModelSnapshot> GetProjectSnapshot(bool includeDeleted = false)
    {
        var snapshots = await _crdtRepositoryFactory.Execute(repo => repo.CurrenSimpleSnapshots(includeDeleted).ToArrayAsync());
        return new ModelSnapshot(snapshots);
    }

    public async Task<T> GetBySnapshotId<T>(Guid snapshotId)
    {
        return await _crdtRepositoryFactory.Execute(repo => repo.GetObjectBySnapshotId<T>(snapshotId));
    }

    public async Task<Dictionary<Guid, ObjectSnapshot>> GetSnapshotsAtCommit(Commit commit)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var (baseline, commitsToReplay) = await ResumeFromCheckpoint(commit, repo);
        //loading everything up front makes every lookup during the replay a cache hit
        await baseline.PreloadAllAsync();
        var updatedSnapshotView = await SnapshotWorker.ReplayCommits(baseline, commitsToReplay, _crdtConfig.Value);
        return await updatedSnapshotView.All()
            .ToDictionaryAsync(s => s.EntityId);
    }

    public async Task<T> GetAtTime<T>(DateTimeOffset time, Guid entityId)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var commitBefore = await repo.CurrentCommits().LastOrDefaultAsync(c => c.HybridDateTime.DateTime <= time);
        if (commitBefore is null) throw new ArgumentException("unable to find any commits");
        return await GetAtCommit<T>(commitBefore, entityId);
    }

    public async Task<T> GetAtCommit<T>(Guid commitId, Guid entityId)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var commit = await repo.CurrentCommits().SingleAsync(c => c.Id == commitId);
        return await GetAtCommit<T>(commit, entityId, repo);
    }

    public async Task<T> GetAtCommit<T>(Commit commit, Guid entityId)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await GetAtCommit<T>(commit, entityId, repo);
    }

    private async Task<T> GetAtCommit<T>(Commit commit, Guid entityId, CrdtRepository repo)
    {
        var snapshot = await GetSnapshotAtCommit(commit, entityId, repo);
        ArgumentNullException.ThrowIfNull(snapshot);
        return (T)snapshot.Entity.DbObject;
    }

    public async Task<T?> GetBeforeCommit<T>(Guid commitId, Guid entityId)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var commit = await repo.CurrentCommits().SingleAsync(c => c.Id == commitId);
        return await GetBeforeCommit<T>(commit, entityId, repo);
    }

    public async Task<T?> GetBeforeCommit<T>(Commit commit, Guid entityId)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await GetBeforeCommit<T>(commit, entityId, repo);
    }

    private async Task<T?> GetBeforeCommit<T>(Commit commit, Guid entityId, CrdtRepository repo)
    {
        var previousCommit = await repo.FindPreviousCommit(commit);
        //there's no state before the first commit
        if (previousCommit is null) return default;
        var snapshot = await GetSnapshotAtCommit(previousCommit, entityId, repo);
        //the entity did not exist before the given commit
        if (snapshot is null) return default;
        return (T)snapshot.Entity.DbObject;
    }

    private async Task<ObjectSnapshot?> GetSnapshotAtCommit(Commit commit, Guid entityId, CrdtRepository repo)
    {
        //fast path: every entity is complete at a checkpoint, so if the entity's newest snapshot as of the next checkpoint
        //is already at or before this commit, nothing touched it in between and that snapshot is its state here, no replay.
        var nextCheckpoint = await repo.FindCheckpointAtOrAfter(commit);
        if (nextCheckpoint is not null)
        {
            var newestByNextCheckpoint = await repo.SnapshotViewAsOf(nextCheckpoint).GetAsync(entityId);
            //no snapshot by the next checkpoint means the entity does not exist at the commit either (roots are never pruned)
            if (newestByNextCheckpoint is null) return null;
            if (newestByNextCheckpoint.Commit.CompareKey.CompareTo(commit.CompareKey) <= 0) return newestByNextCheckpoint;
        }

        //we don't have a persisted snapshot in the correct state, so rebuild it
        return await (await ReplayToCommit(commit, repo)).GetAsync(entityId);
    }

    /// <summary>
    /// Replays history up to <paramref name="commit"/> once and opens the snapshots as of it, for asking the same point
    /// in history several things. A single entity is cheaper through <see cref="GetAtCommit{T}(Commit, Guid)"/>, which can
    /// skip the replay. The view holds a database connection until disposed; <see cref="ObjectSnapshot"/>s read from it stay valid.
    /// </summary>
    public Task<SnapshotView> OpenViewAtCommit(Commit commit)
    {
        return OpenView(repo => ReplayToCommit(commit, repo));
    }

    /// <inheritdoc cref="OpenViewAtCommit(Commit)"/>
    public Task<SnapshotView> OpenViewAtCommit(Guid commitId)
    {
        return OpenView(async repo => await ReplayToCommit(await repo.CurrentCommits().SingleAsync(c => c.Id == commitId), repo));
    }

    /// <summary>
    /// <see cref="OpenViewAtCommit(Commit)"/> as of the commit before <paramref name="commit"/>; the view is empty before the first commit.
    /// </summary>
    public Task<SnapshotView> OpenViewBeforeCommit(Commit commit)
    {
        return OpenView(repo => ReplayToBefore(commit, repo));
    }

    /// <inheritdoc cref="OpenViewBeforeCommit(Commit)"/>
    public Task<SnapshotView> OpenViewBeforeCommit(Guid commitId)
    {
        return OpenView(async repo => await ReplayToBefore(await repo.CurrentCommits().SingleAsync(c => c.Id == commitId), repo));
    }

    private async Task<SnapshotView> OpenView(Func<CrdtRepository, Task<ISnapshotView>> replay)
    {
        var repo = await _crdtRepositoryFactory.CreateRepository();
        try
        {
            return new SnapshotView(repo, await replay(repo));
        }
        catch
        {
            await repo.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Starts a replay of the commits from <paramref name="from"/> to <paramref name="to"/>, both included; each
    /// <see cref="CommitReplay.ApplyNextCommit"/> applies one, the first applying <paramref name="from"/>.
    /// Costs one replay of every commit in the range, so it beats <see cref="OpenViewAtCommit(Commit)"/> or
    /// <see cref="GetAtCommit{T}(Commit, Guid)"/> per commit when the commits of interest are close together (a sync burst)
    /// and loses to them when they are spread out: in a benchmark it lost with 20 rows 100 commits apart and won with 100.
    /// </summary>
    public async Task<CommitReplay> StartCommitReplay(Commit from, Commit to)
    {
        var repo = await _crdtRepositoryFactory.CreateRepository();
        try
        {
            return await CommitReplay.Start(repo, from, to, _crdtConfig.Value);
        }
        catch
        {
            await repo.DisposeAsync();
            throw;
        }
    }

    private async Task<ISnapshotView> ReplayToBefore(Commit commit, CrdtRepository repo)
    {
        var previousCommit = await repo.FindPreviousCommit(commit);
        return previousCommit is null ? EmptySnapshotView.Instance : await ReplayToCommit(previousCommit, repo);
    }

    private async Task<ISnapshotView> ReplayToCommit(Commit commit, CrdtRepository repo)
    {
        var (baseline, commitsToReplay) = await ResumeFromCheckpoint(commit, repo);
        await SnapshotReplay.PreloadTouched(baseline, commitsToReplay, _crdtConfig.Value);
        return await SnapshotWorker.ReplayCommits(baseline, commitsToReplay, _crdtConfig.Value);
    }

    /// <summary>
    /// What a point-in-time read resumes from: the snapshots as of the newest checkpoint at or before
    /// <paramref name="commit"/>, and the commits to replay onto them to reach <paramref name="commit"/>.
    /// </summary>
    private static async Task<(ISnapshotView baseline, SortedSet<Commit> commitsToReplay)> ResumeFromCheckpoint(
        Commit commit,
        CrdtRepository repo)
    {
        var checkpoint = await repo.FindCheckpointAtOrBefore(commit);
        var commitsToReplay = await repo.GetCommitsBetween(afterExclusive: checkpoint, upToInclusive: commit);
        return (repo.SnapshotViewAsOf(checkpoint), commitsToReplay);
    }

    public async Task<SyncState> GetSyncState()
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await repo.GetCurrentSyncState();
    }

    public async Task<ChangesResult<Commit>> GetChanges(SyncState remoteState)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await repo.GetChanges(remoteState);
    }

    public async Task<SyncResults> SyncWith(ISyncable remoteModel)
    {
        return await _syncService.SyncWith(this, remoteModel);
    }

    public async Task SyncMany(ISyncable[] remotes)
    {
        await _syncService.SyncMany(this, remotes);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
