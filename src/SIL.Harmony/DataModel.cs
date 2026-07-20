using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using SIL.Harmony.Changes;
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
    private readonly IOptions<CrdtConfig> _crdtConfig;
    private readonly ILogger<DataModel> _logger;
    private readonly ICommitMaterializationFilter? _materializationFilter;
    private readonly IReadOnlyList<ICommitInterceptor> _commitInterceptors;
    private readonly Func<IEnumerable<ICommitAppliedListener>>? _commitAppliedListenersFactory;

    //constructor must be internal because CrdtRepository is internal
    internal DataModel(CrdtRepositoryFactory crdtRepositoryFactory,
        JsonSerializerOptions serializerOptions,
        IHybridDateTimeProvider timeProvider,
        IOptions<CrdtConfig> crdtConfig,
        ILogger<DataModel> logger,
        ICommitMaterializationFilter? materializationFilter = null,
        IEnumerable<ICommitInterceptor>? commitInterceptors = null,
        Func<IEnumerable<ICommitAppliedListener>>? commitAppliedListenersFactory = null)
    {
        _crdtRepositoryFactory = crdtRepositoryFactory;
        _serializerOptions = serializerOptions;
        _timeProvider = timeProvider;
        _crdtConfig = crdtConfig;
        _logger = logger;
        _materializationFilter = materializationFilter;
        _commitInterceptors = commitInterceptors?.ToArray() ?? [];
        // Listeners are resolved lazily (at apply time) rather than in the constructor: a listener's
        // roll-forward depends transitively on this DataModel, so eager resolution would form a
        // constructor cycle. The interceptor above has no such dependency and is resolved eagerly.
        _commitAppliedListenersFactory = commitAppliedListenersFactory;
    }

    private ICommitMaterializationFilter MaterializationFilter =>
        _materializationFilter ?? _crdtConfig.Value.CommitMaterializationFilter;


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
        Commit[] commits;
        await using (var repo = await _crdtRepositoryFactory.CreateRepository())
        {
            commits = changes
                .Chunk(changesPerCommitMax)
                .Select(chunk => NewCommit(clientId, commitMetadata(), chunk))
                .ToArray();
            if (commits is []) return;
            using var locked = await repo.Lock();
            repo.ClearChangeTracker();

            await using var transaction = await repo.BeginTransactionAsync();
            var updatedCommits = await repo.AddCommits(commits);
            await UpdateSnapshots(repo, updatedCommits);
            await ValidateCommits(repo);
            await transaction.CommitAsync();
        }
        // Notify after the repo (and its lock) is released: a listener's roll-forward opens its own
        // repository, and the apply lock is not reentrant, so notifying while holding it would deadlock
        // on a persistent database.
        await NotifyCommitsApplied(commits);
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
        // Single author choke point: let any registered interceptors (e.g. refs branch assignment)
        // stamp metadata or reject authoring before the commit is persisted. Metadata is not part
        // of the commit hash, so this is safe to do here. Sync-applied commits never pass through
        // NewCommit, so their assignment is left untouched.
        foreach (var interceptor in _commitInterceptors)
            interceptor.OnCommitAuthored(commit);
        return commit;
    }

    private async Task Add(Commit commit)
    {
        await using (var repo = await _crdtRepositoryFactory.CreateRepository())
        {
            using var locked = await repo.Lock();
            if (await repo.HasCommit(commit.Id)) return;
            repo.ClearChangeTracker();

            await using var transaction = repo.IsInTransaction ? null : await repo.BeginTransactionAsync();
            var updatedCommits = await repo.AddCommit(commit);
            await UpdateSnapshots(repo, updatedCommits);

            if (AlwaysValidate) await ValidateCommits(repo);

            // Nested apply: the caller owns the transaction and the post-apply notification, so a
            // listener never observes uncommitted state. Bail before notifying.
            if (transaction is null) return;
            await transaction.CommitAsync();
        }
        // See NotifyCommitsApplied / AddManyChanges: fire only after the lock is released.
        await NotifyCommitsApplied([commit]);
    }

    /// <summary>
    /// Fires registered <see cref="ICommitAppliedListener"/>s after an apply transaction commits and
    /// the repository lock is released. Only invoked from the genuine apply entrypoints (author +
    /// sync) — never from <see cref="RegenerateSnapshots"/> — so a listener that rematerializes
    /// cannot re-enter.
    /// </summary>
    private async Task NotifyCommitsApplied(IReadOnlyCollection<Commit> commits)
    {
        if (_commitAppliedListenersFactory is null || commits.Count == 0) return;
        foreach (var listener in _commitAppliedListenersFactory())
            await listener.OnCommitsAppliedAsync(commits);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static ChangeEntity<IChange> ToChangeEntity(IChange change, int index, Guid commitId)
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
            Commit[] newCommits;
            await using (var repo = await _crdtRepositoryFactory.CreateRepository())
            {
                using var locked = await repo.Lock();
                repo.ClearChangeTracker();
                _timeProvider.TakeLatestTime(commits.Select(c => c.HybridDateTime));
                Commit? oldestChange;
                (oldestChange, newCommits) = await repo.FilterExistingCommits(commits.ToArray());
                //no changes added
                if (oldestChange is null || newCommits is []) return;

                await using var transaction = await repo.BeginTransactionAsync();
                var updatedCommits = await repo.AddCommits(newCommits);
                await UpdateSnapshots(repo, updatedCommits);
                await ValidateCommits(repo);
                await transaction.CommitAsync();
            }
            // Fire after the lock is released so a listener's roll-forward can take it (see NotifyCommitsApplied).
            await NotifyCommitsApplied(newCommits);
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

    private async Task UpdateSnapshots(CrdtRepository repo, SortedSet<Commit> commitsToApply)
    {
        if (commitsToApply.Count == 0) return;
        var filter = MaterializationFilter;
        if (filter is IMaterializationApplyWindow applyWindow)
            commitsToApply = await applyWindow.PrepareApplyWindowAsync(repo, commitsToApply);

        var commitsToMaterialize = commitsToApply
            .Where(filter.Include)
            .ToSortedSet();
        if (commitsToMaterialize.Count == 0) return;

        // Stale boundary is the unfiltered apply-window head. If that head is excluded,
        // DeleteStaleSnapshots(filtered.First()) would not clear already-materialized later
        // commits (WhereAfter is strict), and re-applying them would duplicate snapshots.
        var oldestInApplyWindow = commitsToApply.First();
        await repo.DeleteStaleSnapshots(oldestInApplyWindow);
        Dictionary<Guid, Guid?> snapshotLookup = [];
        if (commitsToMaterialize.Count > 10)
        {
            // Bulk-load relevant snapshots to minimize DB queries
            var entityIds = commitsToMaterialize
                .SelectMany(c => c.ChangeEntities.Select(ce => ce.EntityId))
                .Distinct();

            //EF.Parameter forces a single JSON parameter; without it EF 10+ emits one parameter per id and overflows SQLite's parameter limit
            snapshotLookup = await repo.CurrentSnapshots()
                .Where(s => EF.Parameter(entityIds).Contains(s.EntityId))
                .Select(s => new KeyValuePair<Guid, Guid?>(s.EntityId, s.Id))
                .ToDictionaryAsync(s => s.Key, s => s.Value);
        }

        var snapshotWorker = new SnapshotWorker(snapshotLookup, repo, _crdtConfig.Value);
        await snapshotWorker.UpdateSnapshots(commitsToMaterialize);
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

    public async Task<Commit> GetCommit(Guid commitId)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await repo.CurrentCommits().AsNoTracking().SingleAsync(c => c.Id == commitId);
    }

    public async Task RegenerateSnapshots()
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        await repo.DeleteSnapshotsAndProjectedTables();
        repo.ClearChangeTracker();
        var allCommits = await repo.CurrentCommits()
            .Include(c => c.ChangeEntities)
            .ToSortedSetAsync();
        await UpdateSnapshots(repo, allCommits);
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
        var repository = repo.GetScopedRepository(commit);
        var (snapshots, pendingCommits) = await repository.GetCurrentSnapshotsAndPendingCommits();

        if (pendingCommits.Count != 0)
        {
            snapshots = await SnapshotWorker.ApplyCommitsToSnapshots(snapshots,
                repository,
                pendingCommits,
                _crdtConfig.Value);
        }

        return snapshots;
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
        var repository = repo.GetScopedRepository(commit);
        var snapshot = await repository.GetCurrentSnapshotByObjectId(entityId, false);
        if (snapshot is null) return null;
        var newCommits = await repository.CurrentCommits()
            .Include(c => c.ChangeEntities)
            .WhereAfter(snapshot.Commit)
            .ToSortedSetAsync();
        if (newCommits.Count > 0)
        {
            var snapshots = await SnapshotWorker.ApplyCommitsToSnapshots(
                new Dictionary<Guid, ObjectSnapshot>([
                    new KeyValuePair<Guid, ObjectSnapshot>(snapshot.EntityId, snapshot)
                ]),
                repository,
                newCommits,
                _crdtConfig.Value);
            snapshot = snapshots[snapshot.EntityId];
        }

        return snapshot;
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
}
