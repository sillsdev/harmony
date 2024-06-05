using Crdt.Changes;
using Crdt.Sample.Changes;
using Crdt.Sample.Models;
using Crdt.Tests;

namespace Tests;

public class DataModelReferenceTests : DataModelTestBase
{
    private readonly Guid _word1Id = Guid.NewGuid();
    private readonly Guid _word2Id = Guid.NewGuid();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await WriteNextChange(SetWord(_word1Id, "entity1"));
        await WriteNextChange(SetWord(_word2Id, "entity2"));
    }


    [Fact]
    public async Task DeleteAfterTheFactRewritesReferences()
    {
        var addRef = await WriteNextChange(new AddAntonymReferenceChange(_word1Id, _word2Id));
        var entryWithRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithRef!.AntonymId.Should().Be(_word2Id);

        await WriteChangeBefore(addRef, new DeleteChange<Word>(_word2Id));
        var entryWithoutRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithoutRef!.AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task DeleteRemovesAllReferences()
    {
        await WriteNextChange(new AddAntonymReferenceChange(_word1Id, _word2Id));
        var entryWithRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithRef!.AntonymId.Should().Be(_word2Id);

        await WriteNextChange(new DeleteChange<Word>(_word2Id));
        var entryWithoutRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithoutRef!.AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotsDontGetMutatedByADelete()
    {
        var refAdd = await WriteNextChange(new AddAntonymReferenceChange(_word1Id, _word2Id));
        await WriteNextChange(new DeleteChange<Word>(_word2Id));
        var entitySnapshot1 = await DataModel.GetEntitySnapshotAtTime(refAdd.DateTime, _word1Id);
        entitySnapshot1.Should().NotBeNull();
        entitySnapshot1!.Entity.Is<Word>().AntonymId.Should().Be(_word2Id);
    }

    [Fact]
    public async Task DeleteRetroactivelyRemovesRefs()
    {
        var entityId3 = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId3, "entity3"));
        await WriteNextChange(new AddAntonymReferenceChange(_word1Id, _word2Id));
        var delete = await WriteNextChange(new DeleteChange<Word>(_word2Id));

        //a ref was synced in the past, it happened before the delete, the reference should be retroactively removed
        await WriteChangeBefore(delete, new AddAntonymReferenceChange(entityId3, _word2Id));
        var entry = await DataModel.GetLatest<Word>(entityId3);
        entry!.AntonymId.Should().BeNull();
    }
}
