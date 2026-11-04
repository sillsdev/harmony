using System.Text.Json;
namespace SIL.Harmony;

internal class SyncService(JsonSerializerOptions serializerOptions)
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
        var (missingFromRemote, _) = await localModel.GetChanges(remoteSyncState);
        if (localModel is DataModel && remoteModel is DataModel)
        {
            //cloning just to simulate the objects going over the wire
            missingFromLocal = Clone(missingFromLocal);
            missingFromRemote = Clone(missingFromRemote);
        }
        var pullTask = TryAddRange(localModel, missingFromLocal);
        var pushTask = TryAddRange(remoteModel, missingFromRemote);
        var pullError = await pullTask;
        var pushError = await pushTask;
        if (pullError is not null || pushError is not null)
            throw new SyncException(missingFromLocal, pullError, missingFromRemote, pushError);
        return new SyncResults(missingFromLocal, missingFromRemote, true);
    }
    public async Task SyncMany(ISyncable localModel, ISyncable[] remotes)
    {
        var localSyncState = await localModel.GetSyncState();
        var remoteSyncStates = new SyncState[remotes.Length];
        var pulls = new (Commit[] Commits, Exception? Error)[remotes.Length];
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
            pulls[i] = (missingFromLocal, await TryAddRange(localModel, missingFromLocal));
        }
        // Now the localModel has the changes it could apply from all remotes, so all remotes will get the changes from the localModel as well as all other remotes
        List<SyncException> failures = [];
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
            var pushError = await TryAddRange(remote, missingFromRemote);
            if (pulls[i].Error is not null || pushError is not null)
                failures.Add(new SyncException(pulls[i].Commits, pulls[i].Error, missingFromRemote, pushError));
        }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1) throw new AggregateException(failures);
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

    private T Clone<T>(T source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var json = JsonSerializer.Serialize(source, serializerOptions);
        var clone = JsonSerializer.Deserialize<T>(json, serializerOptions);
        return clone ?? throw new NullReferenceException("unable to clone object type " + typeof(T));
    }
}

