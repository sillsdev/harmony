using Crdt.Sample.Models;

namespace Crdt.Tests;

public class DataModelIntegrityTests : DataModelTestBase
{
    [Fact]
    public async Task CanAddTheSameCommitMultipleTimes()
    {
        var entity1Id = Guid.NewGuid();
        var first = await WriteNextChange(SetWord(entity1Id, "entity1"));
        await WriteNextChange(SetWord(entity1Id, "entity1.1"));
        await DataModel.Add(first);
        await DataModel.Add(first);

        var entry = await DataModel.GetLatest<Word>(entity1Id);
        entry!.Text.Should().Be("entity1.1");
    }

    [Fact]
    public async Task CanAddTheSameCommitMultipleTimesAtOnce()
    {
        var entity1Id = Guid.NewGuid();
        var first = await WriteNextChange(SetWord(entity1Id, "entity1"));
        await WriteNextChange(SetWord(entity1Id, "entity1.1"));
        await DataModel.AddRange(Enumerable.Repeat(first, 5));

        var entry = await DataModel.GetLatest<Word>(entity1Id);
        entry!.Text.Should().Be("entity1.1");
    }
}