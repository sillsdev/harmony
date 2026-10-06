using System.Text.Json;
namespace SIL.Harmony;

internal static class SyncHelper
{
    public static async Task<SyncResults> SyncWithResourceUpload<TMetadata>(this DataModel localModel,
        ISyncable remoteModel,
        ResourceService<TMetadata> resourceService,
        IRemoteResourceService<TMetadata> remoteResourceService,
        Guid localClientId) where TMetadata : class
    {
        await resourceService.UploadPendingResources(localClientId, remoteResourceService);
        return await localModel.SyncWith(remoteModel);
    }
    /// <summary>
    /// simple sync example, each ISyncable could be over the wire or in memory
    /// prefer that remote is over the wire for the best performance, however they could both be remote
    /// </summary>
    /// <param name="localModel"></param>
    /// <param name="remoteModel"></param>
    /// <param name="serializerOptions"></param>
    internal static async Task<SyncResults> SyncWith(ISyncable localModel,
        ISyncable remoteModel,
        JsonSerializerOptions serializerOptions)
    {
        if (!await localModel.ShouldSync() || !await remoteModel.ShouldSync()) return new SyncResults([], [], false);
        var localSyncState = await localModel.GetSyncState();
        var (missingFromLocal, missingFromRemote) = await Exchange(localModel, localSyncState, remoteModel);
        if (localModel is DataModel && remoteModel is DataModel)
        {
            //cloning just to simulate the objects going over the wire
            missingFromLocal = Clone(missingFromLocal, serializerOptions);
            missingFromRemote = Clone(missingFromRemote, serializerOptions);
        }
        if (missingFromLocal.Length > 0)
            await localModel.AddRangeFromSync(missingFromLocal);
        if (missingFromRemote.Length > 0)
            await remoteModel.AddRangeFromSync(missingFromRemote);
        return new SyncResults(missingFromLocal, missingFromRemote, true);
    }

    internal static async Task SyncMany(ISyncable localModel, ISyncable[] remotes, JsonSerializerOptions serializerOptions)
    {
        var localSyncState = await localModel.GetSyncState();
        var exchanges = new (Commit[] missingFromLocal, Commit[] missingFromRemote)[remotes.Length];
        for (var i = 0; i < remotes.Length; i++)
        {
            var (missingFromLocal, missingFromRemote) = await Exchange(localModel, localSyncState, remotes[i]);
            if (localModel is DataModel && remotes[i] is DataModel)
            {
                //cloning just to simulate the objects going over the wire
                missingFromLocal = Clone(missingFromLocal, serializerOptions);
            }
            exchanges[i] = (missingFromLocal, missingFromRemote);
        }
        var pulled = exchanges.SelectMany(e => e.missingFromLocal).DistinctBy(c => c.Id).ToArray();
        await localModel.AddRangeFromSync(pulled);
        //each remote also gets what the others sent, so every remote ends up with the same commits as the local
        for (var i = 0; i < remotes.Length; i++)
        {
            var (missingFromLocal, missingFromRemote) = exchanges[i];
            missingFromRemote = [..missingFromRemote, ..pulled.ExceptBy(missingFromLocal.Select(c => c.Id), c => c.Id)];
            if (localModel is DataModel && remotes[i] is DataModel)
            {
                //cloning just to simulate the objects going over the wire
                missingFromRemote = Clone(missingFromRemote, serializerOptions);
            }
            await remotes[i].AddRangeFromSync(missingFromRemote);
        }
    }

    /// <summary>
    /// Works out what each side is missing so that one sync leaves both with the union of their commits.
    /// For a client where the remote has fewer commits it assumes we hold all of its and withholds them
    /// (QueryHelpers.PlanFor). What it sent and reported tells us whether that was true; if it may not
    /// have been, one more request fetches that client's commits in full.
    /// </summary>
    private static async Task<(Commit[] missingFromLocal, Commit[] missingFromRemote)> Exchange(ISyncable localModel,
        SyncState localSyncState,
        ISyncable remoteModel)
    {
        var (missingFromLocal, remoteSyncState) = await remoteModel.GetChanges(localSyncState);
        //todo abort if local and remote heads are the same
        var (missingFromRemote, _) = await localModel.GetChanges(remoteSyncState);

        var pulledState = missingFromLocal.GetSyncState();
        var pushedState = missingFromRemote.GetSyncState();
        HashSet<Guid> fullyPulled = [];
        HashSet<Guid> maybeWithheld = [];
        foreach (var local in localSyncState.ClientStates)
        {
            var remote = remoteSyncState.GetClientState(local.ClientId);
            if (remote is null || remote.OnlyHasTimestamp || remote == local) continue;
            var pulled = pulledState.GetClientState(local.ClientId);
            //a push after the remote's head is exact, so the remote should hold what it sent plus our commits up to its head
            var pushed = local.MaxTimestamp > remote.MaxTimestamp ? pushedState.GetClientState(local.ClientId) : null;
            if (local.Plus(pulled).HasSameCommits(remote.Plus(pushed))) continue;
            if (pulled?.HasSameCommits(remote) == true)
                fullyPulled.Add(local.ClientId);
            else
                maybeWithheld.Add(local.ClientId);
        }

        if (maybeWithheld.Count > 0)
        {
            //a client missing from the state we give the remote gets sent in full
            var (withheld, _) = await remoteModel.GetChanges(remoteSyncState.Without(maybeWithheld));
            missingFromLocal = missingFromLocal.UnionBy(withheld, c => c.Id).ToArray();
            fullyPulled.UnionWith(maybeWithheld);
        }
        if (fullyPulled.Count > 0)
        {
            //the remote holds exactly what it sent for these clients, so push exactly the rest of ours
            var (ours, _) = await localModel.GetChanges(localSyncState.Without(fullyPulled));
            missingFromRemote =
            [
                ..missingFromRemote.Where(c => !fullyPulled.Contains(c.ClientId)),
                ..ours.ExceptBy(missingFromLocal.Select(c => c.Id), c => c.Id)
            ];
        }
        return (missingFromLocal, missingFromRemote);
    }

    private static T Clone<T>(this T source, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        var json = JsonSerializer.Serialize(source, options);
        var clone = JsonSerializer.Deserialize<T>(json, options);
        return clone ?? throw new NullReferenceException("unable to clone object type " + typeof(T));
    }
}
