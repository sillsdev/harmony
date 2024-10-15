using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class SyncTests : IAsyncLifetime
{
    private readonly DataModelTestBase _client1 = new();
    private readonly DataModelTestBase _client2 = new();

    public async Task InitializeAsync()
    {
        await _client1.InitializeAsync();
        await _client2.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _client1.DisposeAsync();
        await _client2.DisposeAsync();
    }

    [Fact]
    public async Task GetNewHistoryReturnsAddedChanges()
    {
        var entity1Id = Guid.NewGuid();
        var commit = await _client1.WriteNextChange(_client1.SetWord(entity1Id, "entity1"));
        var syncResults = await _client1.DataModel.SyncWith(_client2.DataModel);
        syncResults.MissingFromRemote.Should().ContainSingle(c => c.Id == commit.Id);
    }

    [Fact]
    public async Task CanSyncSimpleChange()
    {
        var entity1Id = Guid.NewGuid();
        var entity2Id = Guid.NewGuid();
        await _client1.WriteNextChange(_client1.SetWord(entity1Id, "entity1"));
        (await _client1.DataModel.GetLatest<Word>(entity1Id))!.Text.Should().Be("entity1");
        await _client2.WriteNextChange(_client2.SetWord(entity2Id, "entity2"));
        (await _client2.DataModel.GetLatest<Word>(entity2Id))!.Text.Should().Be("entity2");

        //act
        await _client1.DataModel.SyncWith(_client2.DataModel);

        _client2.DbContext.Snapshots.Should().NotBeEmpty();
        var client1Snapshot = await _client1.DataModel.GetProjectSnapshot();
        var client2Snapshot = await _client2.DataModel.GetProjectSnapshot();
        client1Snapshot.LastCommitHash.Should().Be(client2Snapshot.LastCommitHash);
        var client2Entity1 = await _client2.DataModel.GetBySnapshotId<Word>(client2Snapshot.Snapshots[entity1Id].Id);
        client2Entity1.Text.Should().Be("entity1");
        var client1Entity2 = await _client1.DataModel.GetBySnapshotId<Word>(client1Snapshot.Snapshots[entity2Id].Id);
        client1Entity2.Text.Should().Be("entity2");
    }

    [Fact]
    public async Task CanSyncMultipleTimes()
    {
        var entity1Id = Guid.NewGuid();
        var entity2Id = Guid.NewGuid();
        await _client1.WriteNextChange(_client1.SetWord(entity1Id, "entity1"));
        await _client1.DataModel.SyncWith(_client2.DataModel);

        await _client2.WriteNextChange(_client2.SetWord(entity2Id, "entity2"));
        await _client1.WriteNextChange(_client1.SetWord(entity1Id, "entity1.1"));

        //act
        await _client1.DataModel.SyncWith(_client2.DataModel);

        var client2Entity = await _client2.DataModel.GetLatest<Word>(entity1Id);
        client2Entity!.Text.Should().Be("entity1.1");
        var client1Entity = await _client1.DataModel.GetLatest<Word>(entity1Id);
        client1Entity!.Text.Should().Be("entity1.1");
    }

    [Theory]
    [InlineData(10)]
    public async Task SyncMultipleClientChanges(int clientCount)
    {
        //for this test client1 will be the server
        var entity1Id = Guid.NewGuid();
        await _client1.WriteNextChange(_client1.SetWord(entity1Id, "entity1"));
        var clients = Enumerable.Range(0, clientCount).Select(_ => new DataModelTestBase()).ToArray();
        for (var i = 0; i < clients.Length; i++)
        {
            var client = clients[i];
            await client.InitializeAsync();
            client.SetCurrentDate(new DateTime(2001, 1, 1).AddDays(i));
            await client.WriteNextChange(client.SetWord(Guid.NewGuid(), "entityClient" + i));
        }

        await _client1.DataModel.SyncMany(clients.Select(c => c.DataModel).ToArray());

        var serverSnapshot = await _client1.DataModel.GetProjectSnapshot();
        serverSnapshot.Snapshots.Should().HaveCount(clientCount + 1);
        foreach (var entitySnapshot in serverSnapshot.Snapshots.Values)
        {
            var serverEntity = await _client1.DataModel.GetBySnapshotId<Word>(entitySnapshot.Id);
            foreach (var client in clients)
            {
                var clientSnapshot = await client.DataModel.GetProjectSnapshot();
                var simpleSnapshot = clientSnapshot.Snapshots.Should().ContainKey(entitySnapshot.EntityId).WhoseValue;
                var entity = await client.DataModel.GetBySnapshotId<Word>(simpleSnapshot.Id);
                  entity.Should().BeEquivalentTo(serverEntity);
            }
        }
    }

    [Fact]
    public async Task CanSync_AddDependentWithMultipleChanges()
    {
        var entity1Id = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        await _client1.WriteNextChange(_client1.SetWord(entity1Id, "entity1"));
        await _client1.WriteNextChange(_client1.NewDefinition(entity1Id, "def1", "pos1", definitionId: definitionId));
        await _client1.WriteNextChange(new SetDefinitionPartOfSpeechChange(definitionId, "pos2"));

        await _client2.DataModel.SyncWith(_client1.DataModel);

        _client2.DataModel.GetLatestObjects<Definition>().Should()
            .BeEquivalentTo(_client1.DataModel.GetLatestObjects<Definition>());
    }
}