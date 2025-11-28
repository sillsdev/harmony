using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class DataModelIntegrityTests : DataModelTestBase
{
    [Fact]
    public async Task CanAddTheSameCommitMultipleTimes()
    {
        var entity1Id = Guid.NewGuid();
        var change = SetWord(entity1Id, "entity1");
        var first = await WriteNextChange(change);
        await WriteNextChange(SetWord(entity1Id, "entity1.1"));
        await DataModel.AddChange(_localClientId, change, first.Id);
        await DataModel.AddChange(_localClientId, change, first.Id);

        var entry = await DataModel.GetLatest<Word>(entity1Id);
        entry!.Text.Should().Be("entity1.1");
    }

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

    [Fact]
    public async Task InvalidCommitHashesResultInException()
    {
        // arrange
        var addedCommit = await WriteNextChange(SetWord(Guid.NewGuid(), "word"));

        // act - break the hash
        addedCommit.SetParentHash("BBAADD");
        DbContext.SaveChanges();

        // act - add another commit to trigger validation
        Func<Task> act = async () => await WriteNextChange(SetWord(Guid.NewGuid(), "word"));

        // assert - validation detects the broken hash
        await act.Should().ThrowAsync<CommitValidationException>().WithMessage("*does not match expected hash*");
    }
}
