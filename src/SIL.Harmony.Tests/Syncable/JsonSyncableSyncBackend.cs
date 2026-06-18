using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SIL.Harmony.Sample;

namespace SIL.Harmony.Tests.Syncable;

public class JsonSyncableSyncBackend : ISyncableTestBackend
{
    public string Name => "JsonSyncable";
    public override string ToString() => Name;

    private JsonSerializerOptions SerializerOptions { get; } = new ServiceCollection()
        .AddCrdtDataSample(":memory:")
        .BuildServiceProvider()
        .GetRequiredService<JsonSerializerOptions>();

    public async Task<SyncableTestContext> CreateAsync()
    {
        var testBase = new DataModelTestBase();
        await testBase.InitializeAsync();
        var tempDir = Directory.CreateTempSubdirectory("harmony-json-syncable-");
        var jsonSyncable = new JsonSyncable(tempDir, SerializerOptions, NullLogger<JsonSyncable>.Instance);
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
