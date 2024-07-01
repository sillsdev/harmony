using System.Text.Json;
using Crdt.Changes;
using Crdt.Core;
using Crdt.Db;
using Crdt.Entities;
using Microsoft.EntityFrameworkCore;

namespace Crdt;

public record SyncResults(Commit[] MissingFromLocal, Commit[] MissingFromRemote, bool IsSynced);

public class DataModel : ISyncable, IAsyncDisposable
{
    /// <summary>
    /// after adding any commit validate the commit history, not great for performance but good for testing.
    /// </summary>
    private readonly bool _autoValidate = true;

    private readonly CrdtRepository _crdtRepository;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly IHybridDateTimeProvider _timeProvider;

    //constructor must be internal because CrdtRepository is internal
    internal DataModel(CrdtRepository crdtRepository, JsonSerializerOptions serializerOptions, IHybridDateTimeProvider timeProvider)
    {
        _crdtRepository = crdtRepository;
        _serializerOptions = serializerOptions;
        _timeProvider = timeProvider;
    }


    /// <summary>
    /// add a change to the model, snapshots will be updated
    /// </summary>
    /// <param name="clientId">
    /// Unique identifier for the client, used to determine what changes need to be synced, for a single install it should always author commits with the same client id
    /// if the client id changes too much it could slow down the sync process
    /// </param>
    /// <param name="change">change to be applied to the model</param>
    /// <param name="commitId">
    /// can be used by the application code to ensure a specific change is only applied once,
    /// for example a one time migration or update of pre seeded data in the model, a hard coded guid could be used
    /// which will ensure it's only applied once to the model, even if multiple clients update at the same time and all apply the same change.
    /// typical changes should not specify the commitId and let a new guid to be generated for each commit.
    /// This could also be useful if the application has a flaky connection with the DataModel and needs to retry the same change multiple times but ensure it's only applied once,
    /// then the guid would be generated by the application
    /// </param>
    /// <param name="commitMetadata">used to store metadata on the commit, for example app version or author id</param>
    /// <returns>the newly created commit</returns>
    public async Task<Commit> AddChange(
        Guid clientId,
        IChange change,
        Guid commitId = default,
        CommitMetadata? commitMetadata = null)
    {
        return await AddChanges(clientId, [change], commitId, commitMetadata);
    }

    /// <inheritdoc cref="AddChange"/>
    public async Task<Commit> AddChanges(
        Guid clientId,
        IEnumerable<IChange> changes,
        Guid commitId = default,
        CommitMetadata? commitMetadata = null,
        bool deferCommit = false)
    {
        commitId = commitId == default ? Guid.NewGuid() : commitId;
        var commit = new Commit(commitId)
        {
            ClientId = clientId,
            HybridDateTime = _timeProvider.GetDateTime(),
            ChangeEntities = [..changes.Select(ToChangeEntity)],
            Metadata = commitMetadata ?? new()
        };
        await Add(commit, deferCommit);
        return commit;
    } 

    private List<Commit> _deferredCommits = [];
    private async Task Add(Commit commit, bool deferSnapshotUpdates)
    {
        if (await _crdtRepository.HasCommit(commit.Id)) return;

        await using var transaction = await _crdtRepository.BeginTransactionAsync();
        await _crdtRepository.AddCommit(commit);
        if (!deferSnapshotUpdates)
        {
            //if there are deferred commits, update snapshots with them first
            if (_deferredCommits is not []) await UpdateSnapshotsByDeferredCommits();
            await UpdateSnapshots(commit, [commit]);
            if (_autoValidate) await ValidateCommits();
        }
        else
        {
            _deferredCommits.Add(commit);
        }
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_deferredCommits is []) return;
        await UpdateSnapshotsByDeferredCommits();
    }

    private async Task UpdateSnapshotsByDeferredCommits()
    {
        var commits = Interlocked.Exchange(ref _deferredCommits, []);
        var oldestChange = commits.MinBy(c => c.CompareKey);
        if (oldestChange is null) return;
        await UpdateSnapshots(oldestChange, commits.ToArray());
        if (_autoValidate) await ValidateCommits();
    }


    private static ChangeEntity<IChange> ToChangeEntity(IChange change, int index)
    {
        return new ChangeEntity<IChange>()
        {
            Change = change, CommitId = change.CommitId, EntityId = change.EntityId, Index = index
        };
    }

    async Task ISyncable.AddRangeFromSync(IEnumerable<Commit> commits)
    {
        commits = commits.ToArray();
        _timeProvider.TakeLatestTime(commits.Select(c => c.HybridDateTime));
        var (oldestChange, newCommits) = await _crdtRepository.FilterExistingCommits(commits.ToArray());
        //no changes added
        if (oldestChange is null || newCommits is []) return;

        await using var transaction = await _crdtRepository.BeginTransactionAsync();
        //if there are deferred commits, update snapshots with them first
        if (_deferredCommits is not []) await UpdateSnapshotsByDeferredCommits();
        //don't save since UpdateSnapshots will also modify newCommits with hashes, so changes will be saved once that's done
        await _crdtRepository.AddCommits(newCommits, false);
        await UpdateSnapshots(oldestChange, newCommits);
        await ValidateCommits();
        await transaction.CommitAsync();
    }

    ValueTask<bool> ISyncable.ShouldSync()
    {
        return ValueTask.FromResult(true);
    }

    private async Task UpdateSnapshots(Commit oldestAddedCommit, Commit[] newCommits)
    {
        await _crdtRepository.DeleteStaleSnapshots(oldestAddedCommit);
        var modelSnapshot = await GetProjectSnapshot(true);
        var snapshotWorker = new SnapshotWorker(modelSnapshot.Snapshots, _crdtRepository);
        await snapshotWorker.UpdateSnapshots(oldestAddedCommit, newCommits);
    }

    private async Task ValidateCommits()
    {
        Commit? parentCommit = null;
        await foreach (var commit in _crdtRepository.CurrentCommits().AsAsyncEnumerable())
        {
            var parentHash = parentCommit?.Hash ?? CommitBase.NullParentHash;
            var expectedHash = commit.GenerateHash(parentHash);
            if (commit.Hash == expectedHash && commit.ParentHash == parentHash)
            {
                parentCommit = commit;
                continue;
            }

            var actualParentCommit = await _crdtRepository.FindCommitByHash(commit.ParentHash);

            throw new CommitValidationException(
                $"Commit {commit} does not match expected hash, parent hash [{commit.ParentHash}] !== [{parentHash}], expected parent {parentCommit} and actual parent {actualParentCommit}");
        }
    }

    public async Task<ObjectSnapshot?> GetEntitySnapshotAtTime(DateTimeOffset time, Guid entityId)
    {
        var snapshots = await GetSnapshotsAt(time);
        return snapshots.GetValueOrDefault(entityId);
    }

    public async Task<ObjectSnapshot> GetLatestSnapshotByObjectId(Guid entityId)
    {
        return await _crdtRepository.GetCurrentSnapshotByObjectId(entityId);
    }

    public async Task<T?> GetLatest<T>(Guid objectId) where T : class, IObjectBase
    {
        return await _crdtRepository.GetCurrent<T>(objectId);
    }

    public async Task<ModelSnapshot> GetProjectSnapshot(bool includeDeleted = false)
    {
        return new ModelSnapshot(await GetEntitySnapshots(includeDeleted));
    }

    public IQueryable<T> GetLatestObjects<T>() where T : class, IObjectBase
    {
        var q = _crdtRepository.GetCurrentObjects<T>();
        if (q is IQueryable<IOrderableCrdt>)
        {
            q = q.OrderBy(o => EF.Property<double>(o, nameof(IOrderableCrdt.Order))).ThenBy(o => o.Id);
        }
        return q;
    }

    public async Task<IObjectBase> GetBySnapshotId(Guid snapshotId)
    {
        return await _crdtRepository.GetObjectBySnapshotId(snapshotId);
    }

    private async Task<SimpleSnapshot[]> GetEntitySnapshots(bool includeDeleted = false)
    {
        var queryable = _crdtRepository.CurrentSnapshots();
        if (!includeDeleted) queryable = queryable.Where(s => !s.EntityIsDeleted);
        var snapshots = await queryable.Select(s =>
            new SimpleSnapshot(s.Id,
                s.TypeName,
                s.EntityId,
                s.CommitId,
                s.IsRoot,
                s.Commit.HybridDateTime,
                s.Commit.Hash,
                s.EntityIsDeleted)).AsNoTracking().ToArrayAsync();
        return snapshots;
    }

    public async Task<Dictionary<Guid, ObjectSnapshot>> GetSnapshotsAt(DateTimeOffset dateTime)
    {
        var repository = _crdtRepository.GetScopedRepository(dateTime);
        var (snapshots, pendingCommits) = await repository.GetCurrentSnapshotsAndPendingCommits();

        if (pendingCommits.Length != 0)
        {
            snapshots = await SnapshotWorker.ApplyCommitsToSnapshots(snapshots, repository, pendingCommits);
        }

        return snapshots;
    }

    public async Task<SyncState> GetSyncState()
    {
        return await _crdtRepository.GetCurrentSyncState();
    }

    public async Task<ChangesResult<Commit>> GetChanges(SyncState remoteState)
    {
        return await _crdtRepository.GetChanges(remoteState);
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
