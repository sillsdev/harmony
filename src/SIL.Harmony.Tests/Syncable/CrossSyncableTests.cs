using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests.Syncable;

public class CrossSyncableTests
{
    [Fact]
    public async Task DataModelAndJsonSyncable_CanSyncBidirectionally()
    {
        var dataBackend = new DataModelSyncBackend();
        var jsonBackend = new JsonSyncableSyncBackend();
        await using var dataModel = await dataBackend.CreateAsync();
        await using var jsonSyncable = await jsonBackend.CreateAsync();

        var entity1Id = Guid.NewGuid();
        var entity2Id = Guid.NewGuid();
        var dataModelCommit = CreateCommit(dataModel.ClientId, new SetWordTextChange(entity1Id, "from-datamodel"));
        await dataModel.Syncable.AddRangeFromSync([dataModelCommit]);
        await dataModel.MirrorToReadModelAsync([dataModelCommit]);
        var jsonSyncableCommit = CreateCommit(jsonSyncable.ClientId, new SetWordTextChange(entity2Id, "from-json"));
        await jsonSyncable.Syncable.AddRangeFromSync([jsonSyncableCommit]);
        await jsonSyncable.MirrorToReadModelAsync([jsonSyncableCommit]);

        var syncResults = await dataModel.Syncable.SyncWith(jsonSyncable.Syncable);
        await SyncableTestHelpers.MirrorToReadModelsAsync(dataModel, jsonSyncable, syncResults);

        (await dataModel.ReadModel.GetLatest<Word>(entity1Id))!.Text.Should().Be("from-datamodel");
        (await dataModel.ReadModel.GetLatest<Word>(entity2Id))!.Text.Should().Be("from-json");
        (await jsonSyncable.ReadModel.GetLatest<Word>(entity1Id))!.Text.Should().Be("from-datamodel");
        (await jsonSyncable.ReadModel.GetLatest<Word>(entity2Id))!.Text.Should().Be("from-json");

        var dmState = await dataModel.Syncable.GetSyncState();
        var jsonState = await jsonSyncable.Syncable.GetSyncState();
        dmState.ClientHeads.Should().BeEquivalentTo(jsonState.ClientHeads);
    }

    private static Commit CreateCommit(Guid clientId, IChange change)
    {
        var commitId = Guid.NewGuid();
        return new Commit(commitId)
        {
            ClientId = clientId,
            HybridDateTime = new HybridDateTime(DateTimeOffset.Now, 0),
            ChangeEntities = [new ChangeEntity<IChange>
                {
                    Index = 0,
                    CommitId = commitId,
                    EntityId = change.EntityId,
                    Change = change
                }
            ]
        };
    }
}
