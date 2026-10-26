using System.Runtime.ExceptionServices;
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
        var (missingFromLocal, remoteSyncState) = await remoteModel.GetChanges(localSyncState);
        //todo abort if local and remote heads are the same
        var (missingFromRemote, _) = await localModel.GetChanges(remoteSyncState);
        if (localModel is DataModel && remoteModel is DataModel)
        {
            //cloning just to simulate the objects going over the wire
            missingFromLocal = Clone(missingFromLocal, serializerOptions);
            missingFromRemote = Clone(missingFromRemote, serializerOptions);
        }
        var pull = TryAddRange(localModel, missingFromLocal);
        var push = TryAddRange(remoteModel, missingFromRemote);
        var pullError = await pull;
        var pushError = await push;
        ThrowIfAny([pullError, pushError]);
        return new SyncResults(missingFromLocal, missingFromRemote, true);
    }
    internal static async Task SyncMany(ISyncable localModel, ISyncable[] remotes, JsonSerializerOptions serializerOptions)
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
                missingFromLocal = Clone(missingFromLocal, serializerOptions);
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
                missingFromRemote = Clone(missingFromRemote, serializerOptions);
            }
            errors.Add(await TryAddRange(remote, missingFromRemote));
        }
        ThrowIfAny(errors);
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

    private static void ThrowIfAny(IEnumerable<Exception?> errors)
    {
        Exception[] thrown = [.. errors.OfType<Exception>()];
        if (thrown.Length == 1) ExceptionDispatchInfo.Throw(thrown[0]);
        if (thrown.Length > 1) throw new AggregateException(thrown);
    }
    private static T Clone<T>(this T source, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        var json = JsonSerializer.Serialize(source, options);
        var clone = JsonSerializer.Deserialize<T>(json, options);
        return clone ?? throw new NullReferenceException("unable to clone object type " + typeof(T));
    }
}

