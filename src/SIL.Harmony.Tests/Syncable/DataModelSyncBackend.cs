namespace SIL.Harmony.Tests.Syncable;

public class DataModelSyncBackend : ISyncableTestBackend
{
    public string Name => "DataModel";
    public override string ToString() => Name;

    public async Task<SyncableTestContext> CreateAsync()
    {
        var testBase = new DataModelTestBase();
        await testBase.InitializeAsync();
        return new SyncableTestContext(
            testBase.DataModel,
            testBase.DataModel,
            Guid.NewGuid(),
            syncableUpdatesReadModel: true,
            () => testBase.DisposeAsync());
    }
}
