using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Sample;

namespace SIL.Harmony.Tests.Syncable;

public class JsonSyncableSyncBackend : ISyncableTestBackend
{
    public string Name => "JsonSyncable";
    public override string ToString() => Name;

    private IJsonSyncableFactory Factory { get; } = new ServiceCollection()
        .AddCrdtDataSample(":memory:")
        .BuildServiceProvider()
        .GetRequiredService<IJsonSyncableFactory>();

    public async Task<SyncableTestContext> CreateAsync()
    {
        var testBase = new DataModelTestBase();
        await testBase.InitializeAsync();
        var tempDir = Directory.CreateTempSubdirectory("harmony-json-syncable-");
        var jsonSyncable = Factory.Create(tempDir);
        return new SyncableTestContext(
            jsonSyncable,
            testBase.DataModel,
            Guid.NewGuid(),
            syncableUpdatesReadModel: false,
            async () =>
            {
                await testBase.DisposeAsync();
                tempDir.Delete(recursive: true);
            });
    }
}
