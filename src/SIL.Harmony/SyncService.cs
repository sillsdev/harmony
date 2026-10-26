using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static SIL.Harmony.Core.QueryHelpers;
namespace SIL.Harmony;

internal class SyncService(JsonSerializerOptions serializerOptions, ILogger<SyncService> logger)
{
    public async Task<SyncResults> SyncWithResourceUpload<TMetadata>(DataModel localModel,
        ISyncable remoteModel,
        ResourceService<TMetadata> resourceService,
        IRemoteResourceService<TMetadata> remoteResourceService,
        Guid localClientId) where TMetadata : class
    {
        await resourceService.UploadPendingResources(localClientId, remoteResourceService);
        return await localModel.SyncWith(remoteModel);
    }
    /// <summary>
    /// Syncs two ISyncables, each over the wire or in memory. Prefer the remote one to be over the wire for the best performance.
    /// </summary>
    public async Task<SyncResults> SyncWith(ISyncable localModel, ISyncable remoteModel)
    {
        if (!await localModel.ShouldSync() || !await remoteModel.ShouldSync()) return new SyncResults([], [], false);
        var localSyncState = await localModel.GetSyncState();
        var (missingFromLocal, remoteSyncState) = await remoteModel.GetChanges(localSyncState);
        //todo abort if local and remote heads are the same
        var (missingFromRemote, currentLocalSyncState) = await localModel.GetChanges(remoteSyncState);
        await WarnIfCommitsWillStillDiffer(currentLocalSyncState, remoteSyncState, missingFromLocal, missingFromRemote);
        if (localModel is DataModel && remoteModel is DataModel)
        {
            //cloning just to simulate the objects going over the wire
            missingFromLocal = Clone(missingFromLocal);
            missingFromRemote = Clone(missingFromRemote);
        }
        var pullTask = TryAddRange(localModel, missingFromLocal);
        var pushTask = TryAddRange(remoteModel, missingFromRemote);
        ThrowIfAnyErrors([await pullTask, await pushTask]);
        return new SyncResults(missingFromLocal, missingFromRemote, true);
    }
    public async Task SyncMany(ISyncable localModel, ISyncable[] remotes)
    {
        var localSyncState = await localModel.GetSyncState();
        var remoteSyncStates = new SyncState[remotes.Length];
        List<Exception?> errors = [];
        for (var i = 0; i < remotes.Length; i++)
        {
            var remote = remotes[i];
            var (missingFromLocal, remoteSyncState) = await remote.GetChanges(localSyncState);
            if (localModel is DataModel && remote is DataModel)
            {
                //cloning just to simulate the objects going over the wire
                missingFromLocal = Clone(missingFromLocal);
            }
            remoteSyncStates[i] = remoteSyncState;
            errors.Add(await TryAddRange(localModel, missingFromLocal));
        }
        // Now the localModel has the changes it could apply from all remotes, so all remotes will get the changes from the localModel as well as all other remotes
        for (var i = 0; i < remotes.Length; i++)
        {
            var remote = remotes[i];
            var remoteSyncState = remoteSyncStates[i];
            var (missingFromRemote, _) = await localModel.GetChanges(remoteSyncState);
            if (localModel is DataModel && remote is DataModel)
            {
                //cloning just to simulate the objects going over the wire
                missingFromRemote = Clone(missingFromRemote);
            }
            errors.Add(await TryAddRange(remote, missingFromRemote));
        }
        ThrowIfAnyErrors(errors);
    }

    //don't fail fast: if neither direction lands, the next sync sends the same commits and fails the same way
    private static async Task<Exception?> TryAddRange(ISyncable model, Commit[] commits)
    {
        if (commits.Length == 0) return null;
        try
        {
            await model.AddRangeFromSync(commits);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    //a clean sync sends each side only commits it lacks, leaving local + pulled == remote + pushed; a mismatch means a side withheld commits.
    //a side that sends all of a client's commits resends ones the other already has, so the sums don't apply and that client is skipped
    private async Task WarnIfCommitsWillStillDiffer(SyncState local, SyncState remote, Commit[] pulled, Commit[] pushed)
    {
        if (!logger.IsEnabled(LogLevel.Warning)) return;
        var pulledState = await StateOf(pulled);
        var pushedState = await StateOf(pushed);
        foreach (var localClient in local.ClientStates)
        {
            var remoteClient = remote.GetClientState(localClient.ClientId);
            if (remoteClient is null || remoteClient.OnlyHasTimestamp || localClient == remoteClient) continue;
            if (PlanFor(localClient, remoteClient) == SyncPlan.SendAll || PlanFor(remoteClient, localClient) == SyncPlan.SendAll) continue;
            var localAfter = localClient.Plus(pulledState.GetClientState(localClient.ClientId));
            var remoteAfter = remoteClient.Plus(pushedState.GetClientState(localClient.ClientId));
            if (!localAfter.HasSameCommits(remoteAfter))
                logger.LogWarning("Commits from client {ClientId} will still differ after this sync: local has {LocalCount} up to {LocalHead}, remote has {RemoteCount} up to {RemoteHead}",
                    localClient.ClientId, localClient.CommitCount, Head(localClient), remoteClient.CommitCount, Head(remoteClient));
        }
    }

    private static DateTimeOffset Head(ClientState state) => DateTimeOffset.FromUnixTimeMilliseconds(state.MaxTimestamp);

    private static async Task<SyncState> StateOf(Commit[] commits) =>
        new(await BuildSyncState(commits.Select(c => new SimpleCommit(c.ClientId, c.Id, c.HybridDateTime.DateTime)).ToAsyncEnumerable()));

    private static void ThrowIfAnyErrors(IEnumerable<Exception?> errors)
    {
        Exception[] thrown = [.. errors.OfType<Exception>()];
        if (thrown.Length == 1) ExceptionDispatchInfo.Throw(thrown[0]);
        if (thrown.Length > 1) throw new AggregateException(thrown);
    }
    private T Clone<T>(T source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var json = JsonSerializer.Serialize(source, serializerOptions);
        var clone = JsonSerializer.Deserialize<T>(json, serializerOptions);
        return clone ?? throw new NullReferenceException("unable to clone object type " + typeof(T));
    }
}

