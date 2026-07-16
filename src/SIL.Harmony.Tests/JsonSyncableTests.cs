using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;

namespace SIL.Harmony.Tests;

public class JsonSyncableTests
{
    private static JsonSerializerOptions SerializerOptions { get; } = new ServiceCollection()
        .AddCrdtDataSample(":memory:")
        .BuildServiceProvider()
        .GetRequiredService<JsonSerializerOptions>();

    [Fact]
    public async Task WritesPerClientFile()
    {
        var dir = Directory.CreateTempSubdirectory("harmony-json-syncable-test-");
        try
        {
            var syncable = new JsonSyncable(dir, SerializerOptions, NullLogger<JsonSyncable>.Instance);
            var clientId = Guid.NewGuid();
            var commit = new Commit
            {
                ClientId = clientId,
                HybridDateTime = new HybridDateTime(DateTimeOffset.UtcNow, 0),
                ChangeEntities =
                {
                    new ChangeEntity<IChange>
                    {
                        Change = new SetWordTextChange(Guid.NewGuid(), "hello"),
                        Index = 0,
                        CommitId = Guid.Empty,
                        EntityId = Guid.Empty
                    }
                }
            };

            await syncable.AddRangeFromSync([commit]);

            var file = Path.Combine(dir.FullName, $"{JsonSyncable.FilenamePrefix}{clientId}{JsonSyncable.FilenameExtension}");
            File.Exists(file).Should().BeTrue();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotDuplicateFileLines()
    {
        var dir = Directory.CreateTempSubdirectory("harmony-json-syncable-test-");
        try
        {
            var syncable = new JsonSyncable(dir, SerializerOptions, NullLogger<JsonSyncable>.Instance);
            var commit = new Commit
            {
                ClientId = Guid.NewGuid(),
                HybridDateTime = new HybridDateTime(DateTimeOffset.UtcNow, 0)
            };

            await syncable.AddRangeFromSync([commit]);
            await syncable.AddRangeFromSync([commit]);

            var file = new FileInfo(Path.Combine(dir.FullName,
                $"{JsonSyncable.FilenamePrefix}{commit.ClientId}{JsonSyncable.FilenameExtension}"));
            var lines = await File.ReadAllLinesAsync(file.FullName, TestContext.Current.CancellationToken);
            lines.Should().HaveCount(1);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
