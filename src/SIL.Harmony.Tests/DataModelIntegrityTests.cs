using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class DataModelIntegrityTests : DataModelTestBase
{
    [Fact]
    public async Task CanAddTheSameCommitMultipleTimesVisSync()
    {
        var entity1Id = Guid.NewGuid();
        var first = await WriteNextChange(SetWord(entity1Id, "entity1"));
        await WriteNextChange(SetWord(entity1Id, "entity1.1"));
        await AddCommitsViaSync(Enumerable.Repeat(first, 5));

        var entry = await DataModel.GetLatest<Word>(entity1Id);
        entry!.Text.Should().Be("entity1.1");
    }
}
