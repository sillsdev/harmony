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

    private static Commit MakeCommit(Guid clientId, int secondsOffset) => new()
    {
        ClientId = clientId,
        HybridDateTime = new HybridDateTime(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(secondsOffset), 0),
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

    private static string ClientFile(DirectoryInfo dir, Guid clientId) =>
        Path.Combine(dir.FullName, $"{JsonSyncable.FilenamePrefix}{clientId}{JsonSyncable.FilenameExtension}");

    private static string Serialize(Commit commit) => JsonSerializer.Serialize(commit, SerializerOptions);

    //simulates a write interrupted partway through a line, e.g. a USB drive pulled mid-sync
    private static string Truncated(Commit commit)
    {
        var json = Serialize(commit);
        return json[..(json.Length / 2)];
    }

    private static SyncState EmptySyncState => new(new Dictionary<Guid, long>());

    [Fact]
    public async Task ReadingIgnoresTruncatedLastLine()
    {
        var dir = Directory.CreateTempSubdirectory("harmony-json-syncable-test-");
        try
        {
            var syncable = new JsonSyncable(dir, SerializerOptions, NullLogger<JsonSyncable>.Instance);
            var clientId = Guid.NewGuid();
            Commit[] commits = [MakeCommit(clientId, 0), MakeCommit(clientId, 1)];
            await syncable.AddRangeFromSync(commits);
            await File.AppendAllTextAsync(ClientFile(dir, clientId), Truncated(MakeCommit(clientId, 2)),
                TestContext.Current.CancellationToken);

            var syncState = await syncable.GetSyncState();
            syncState.GetClientState(clientId)!.CommitCount.Should().Be(2);

            var changes = await syncable.GetChanges(EmptySyncState);
            changes.MissingFromClient.Select(c => c.Id).Should().BeEquivalentTo(commits.Select(c => c.Id));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task AppendingRemovesTruncatedLastLine(string newline)
    {
        var dir = Directory.CreateTempSubdirectory("harmony-json-syncable-test-");
        try
        {
            var syncable = new JsonSyncable(dir, SerializerOptions, NullLogger<JsonSyncable>.Instance);
            var clientId = Guid.NewGuid();
            var file = ClientFile(dir, clientId);
            var first = MakeCommit(clientId, 0);
            var dropped = MakeCommit(clientId, 1);
            await File.WriteAllTextAsync(file, Serialize(first) + newline + Truncated(dropped),
                TestContext.Current.CancellationToken);

            var next = MakeCommit(clientId, 2);
            await syncable.AddRangeFromSync([next]);

            var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
            lines.Should().Equal(Serialize(first), Serialize(next));

            //the device that wrote the dropped commit re-sends it on the next sync
            await syncable.AddRangeFromSync([dropped]);
            lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
            lines.Should().Equal(Serialize(first), Serialize(next), Serialize(dropped));

            var changes = await syncable.GetChanges(EmptySyncState);
            changes.MissingFromClient.Select(c => c.Id).Should()
                .BeEquivalentTo([first.Id, next.Id, dropped.Id]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AppendingRemovesFragmentWhenFileHasNoCompleteLines()
    {
        var dir = Directory.CreateTempSubdirectory("harmony-json-syncable-test-");
        try
        {
            var syncable = new JsonSyncable(dir, SerializerOptions, NullLogger<JsonSyncable>.Instance);
            var clientId = Guid.NewGuid();
            var file = ClientFile(dir, clientId);
            await File.WriteAllTextAsync(file, Truncated(MakeCommit(clientId, 0)), TestContext.Current.CancellationToken);

            (await syncable.GetSyncState()).ClientStates.Should().BeEmpty();

            var next = MakeCommit(clientId, 1);
            await syncable.AddRangeFromSync([next]);

            var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
            lines.Should().Equal(Serialize(next));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r")]
    public async Task UnterminatedCompleteLastLineIsKept(string trailing)
    {
        var dir = Directory.CreateTempSubdirectory("harmony-json-syncable-test-");
        try
        {
            var syncable = new JsonSyncable(dir, SerializerOptions, NullLogger<JsonSyncable>.Instance);
            var clientId = Guid.NewGuid();
            var file = ClientFile(dir, clientId);
            var first = MakeCommit(clientId, 0);
            var unterminated = MakeCommit(clientId, 1);
            await syncable.AddRangeFromSync([first]);
            await File.AppendAllTextAsync(file, Serialize(unterminated) + trailing, TestContext.Current.CancellationToken);

            (await syncable.GetSyncState()).GetClientState(clientId)!.CommitCount.Should().Be(2);

            var next = MakeCommit(clientId, 2);
            await syncable.AddRangeFromSync([next]);

            var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
            lines.Should().Equal(Serialize(first), Serialize(unterminated), Serialize(next));
            var changes = await syncable.GetChanges(EmptySyncState);
            changes.MissingFromClient.Select(c => c.Id).Should()
                .BeEquivalentTo([first.Id, unterminated.Id, next.Id]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task MalformedLineInTheMiddleThrows(string newline)
    {
        var dir = Directory.CreateTempSubdirectory("harmony-json-syncable-test-");
        try
        {
            var syncable = new JsonSyncable(dir, SerializerOptions, NullLogger<JsonSyncable>.Instance);
            var clientId = Guid.NewGuid();
            await File.WriteAllTextAsync(ClientFile(dir, clientId),
                Serialize(MakeCommit(clientId, 0)) + newline
                + Truncated(MakeCommit(clientId, 1)) + newline
                + Serialize(MakeCommit(clientId, 2)) + newline,
                TestContext.Current.CancellationToken);

            await syncable.Invoking(s => s.GetSyncState()).Should().ThrowAsync<JsonException>();
            await syncable.Invoking(s => s.GetChanges(EmptySyncState)).Should().ThrowAsync<JsonException>();
            await syncable.Invoking(s => s.AddRangeFromSync([MakeCommit(clientId, 3)])).Should().ThrowAsync<JsonException>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TerminatedMalformedLastLineThrows()
    {
        var dir = Directory.CreateTempSubdirectory("harmony-json-syncable-test-");
        try
        {
            var syncable = new JsonSyncable(dir, SerializerOptions, NullLogger<JsonSyncable>.Instance);
            var clientId = Guid.NewGuid();
            await File.WriteAllTextAsync(ClientFile(dir, clientId),
                Serialize(MakeCommit(clientId, 0)) + "\n" + Truncated(MakeCommit(clientId, 1)) + "\n",
                TestContext.Current.CancellationToken);

            await syncable.Invoking(s => s.GetSyncState()).Should().ThrowAsync<JsonException>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
