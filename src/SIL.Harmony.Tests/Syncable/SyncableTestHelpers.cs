namespace SIL.Harmony.Tests.Syncable;

public static class SyncableTestHelpers
{
    public static ISyncableTestBackend[] Backends =>
    [
        new DataModelSyncBackend(),
        new JsonSyncableSyncBackend()
    ];

    public static IEnumerable<object[]> BackendData => Backends.Select(backend => new object[] { backend });

    public static IEnumerable<object[]> BackendPairData =>
        Backends.SelectMany(left => Backends, (left, right) => new object[] { left, right });

    public static async Task MirrorToReadModelAsync(this SyncableTestContext context, IEnumerable<Commit> commits)
    {
        if (context.SyncableUpdatesReadModel) return;

        var array = commits.ToArray();
        if (array.Length > 0)
            await ((ISyncable)context.ReadModel).AddRangeFromSync(array);
    }

    public static async Task MirrorToReadModelsAsync(
        SyncableTestContext local,
        SyncableTestContext remote,
        SyncResults results)
    {
        await local.MirrorToReadModelAsync(results.MissingFromLocal);
        await remote.MirrorToReadModelAsync(results.MissingFromRemote);
    }
}
