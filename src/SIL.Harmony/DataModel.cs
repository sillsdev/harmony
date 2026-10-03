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

    //constructor must be internal because CrdtRepository is internal
    internal DataModel(CrdtRepositoryFactory crdtRepositoryFactory,
        JsonSerializerOptions serializerOptions,
        IHybridDateTimeProvider timeProvider,
        IOptions<HarmonyConfig> crdtConfig,
        ILogger<DataModel> logger)
    {
        _crdtRepositoryFactory = crdtRepositoryFactory;
        _serializerOptions = serializerOptions;
        _timeProvider = timeProvider;
        _crdtConfig = crdtConfig;
        _logger = logger;
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
        commit.ChangeEntities.AddRange(changes.Select((c, i) => ToChangeEntity(c, i, commit.Id)));
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

    internal static ChangeEntity<IChange> ToChangeEntity(IChange change, int index, Guid commitId)
    {
        return new ChangeEntity<IChange>()
        {
            Change = change,
            CommitId = commitId,
            EntityId = change.EntityId,
            Index = index
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

            await using var transaction = await repo.BeginTransactionAsync();
            var replayWindow = await repo.AddCommits(newCommits);
            await UpdateSnapshots(repo, replayWindow);
            if (AlwaysValidate) await ValidateCommits(repo);
            await transaction.CommitAsync();
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

    /// <summary>
    /// Rebuilds every snapshot the window covers by replaying its commits onto the state it resumes from.
    /// A window resuming from nothing rebuilds all of history.
    /// </summary>
    /// <returns>the snapshots the replay made</returns>
    private async Task<IReadOnlyList<ObjectSnapshot>> UpdateSnapshots(CrdtRepository repo, CrdtRepository.ReplayWindow window)
    {
        var (checkpoint, commitsToApply) = window;
        if (commitsToApply.Count == 0) return [];

        ISnapshotView baseline;
        // A database with no checkpoints replays all of history,
        // which is what we want, because it will trigger creating checkpoints
        if (checkpoint is null)
        {
            await repo.DeleteSnapshotsAndProjectedTables();
            //the delete left the table empty, so there's nothing to query
            baseline = EmptySnapshotView.Instance;
        }
        else
        {
            await repo.DeleteSnapshotsAfter(checkpoint.Commit);
            //the current table is the state at the checkpoint, because the delete above just made it so
            baseline = repo.CurrentSnapshotView();
        }

        await PreloadTouched(baseline, commitsToApply);
        var (newSnapshots, checkpoints) = await SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(baseline, commitsToApply, _crdtConfig.Value);
        await repo.AddSnapshots(newSnapshots, checkpoints);
        return newSnapshots;
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
        await using var transaction = await repo.BeginTransactionAsync();
        var wholeHistory = await repo.WholeHistory();
        //Replay does nothing without commits, which would leave snapshots with no history behind them in place
        if (wholeHistory.Commits.Count == 0) await repo.DeleteSnapshotsAndProjectedTables();
        else await UpdateSnapshots(repo, wholeHistory);
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Detects change types that were added to or removed from the config since the last call, for example after an app update,
    /// and rebuilds the snapshots from the oldest commit that uses one of them.
    /// Changes of an unknown type are stored as <see cref="OpaqueChange"/> and skipped, so once the type is known
    /// they must be replayed. A removed type is the reverse: its changes are applied in the snapshots, but are now opaque.
    /// Call this when opening a database, before using it. Querying changes by type is SQLite only.
    /// </summary>
    /// <param name="missingConfig">what to do when no config was stored yet, see <see cref="MissingConfigBehavior"/></param>
    public async Task<ConfigReconcileResult> ReconcileConfigChanges(
        MissingConfigBehavior missingConfig = MissingConfigBehavior.ReplayAll)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        using var locked = await repo.Lock();
        repo.ClearChangeTracker();
        await using var transaction = await repo.BeginTransactionAsync();
        var currentConfig = StoredHarmonyConfig.From(_crdtConfig.Value);
        var storedConfig = await repo.GetLocalState<StoredHarmonyConfig>(StoredHarmonyConfig.LocalStateKey);
        if (storedConfig is not null && storedConfig.SameAs(currentConfig)) return ConfigReconcileResult.Unchanged;

        string[] added = [];
        string[] removed = [];
        string[] affected;
        if (storedConfig is null)
        {
            affected = missingConfig == MissingConfigBehavior.ReplayAll ? currentConfig.ChangeTypes : [];
        }
        else
        {
            added = currentConfig.ChangeTypes.Except(storedConfig.ChangeTypes, StringComparer.Ordinal).ToArray();
            removed = storedConfig.ChangeTypes.Except(currentConfig.ChangeTypes, StringComparer.Ordinal).ToArray();
            affected = [..added, ..removed];
        }

        var replayFrom = await repo.FindOldestCommitWithChangeTypes(affected);
        if (replayFrom is not null)
        {
            _logger.LogInformation("Change types changed (added: {Added}, removed: {Removed}), replaying from commit {CommitId}",
                added, removed, replayFrom.Id);
            var window = await repo.ReplayWindowFrom(replayFrom);
            var replacedSnapshots = await repo.SnapshotsAfter(window.ResumeFrom);
            var newSnapshots = await UpdateSnapshots(repo, window);
            //a removed change type can leave entities with fewer snapshots than before, which the replay doesn't project
            await repo.ReprojectEntitiesWithoutNewSnapshots(replacedSnapshots, newSnapshots);
        }

        await repo.SetLocalState(StoredHarmonyConfig.LocalStateKey, currentConfig);
        await transaction.CommitAsync();
        return new ConfigReconcileResult(true, added, removed, replayFrom);
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
        await _crdtRepositoryFactory.Execute(repo => repo.SetLocalState(key, value));
    }

    /// <summary>
    /// Removes a value from the local state, does nothing when the key doesn't exist.
    /// </summary>
    /// <inheritdoc cref="GetLocalState{T}"/>
    public async Task RemoveLocalState(string key)
    {
        await _crdtRepositoryFactory.Execute(repo => repo.RemoveLocalState(key));
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
        var (baseline, commitsToReplay) = await ResumeFromCheckpoint(commit, repo);
        await PreloadTouched(baseline, commitsToReplay);
        return await (await SnapshotWorker.ReplayCommits(baseline, commitsToReplay, _crdtConfig.Value)).GetAsync(entityId);
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

    /// <summary>one query for every entity the commits touch beats a lookup per entity only for large batches</summary>
    private async Task PreloadTouched(ISnapshotView baseline, IEnumerable<Commit> commits)
    {
        var entityIds = commits.SelectMany(c => c.ChangeEntities.Select(ce => ce.EntityId)).ToHashSet();
        if (entityIds.Count > _crdtConfig.Value.PrefetchSnapshotsBreakpoint) await baseline.PreloadAsync(entityIds);
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
        return await SyncHelper.SyncWith(this, remoteModel, _serializerOptions);
    }

    public async Task SyncMany(ISyncable[] remotes)
    {
        await SyncHelper.SyncMany(this, remotes, _serializerOptions);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
