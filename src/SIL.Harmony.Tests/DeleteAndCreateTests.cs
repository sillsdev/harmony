using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class DeleteAndCreateTests : DataModelTestBase
{
    [Fact]
    public async Task DeleteAndUndeleteInSameCommitWorks()
    {
        var wordId = Guid.NewGuid();

        await WriteNextChange(new NewWordChange(wordId, "original"));

        await WriteNextChange(
            [
                new DeleteChange<Word>(wordId),
                new NewWordChange(wordId, "Undeleted"),
            ]);

        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        word.DeletedAt.Should().BeNull();
        word.Text.Should().Be("Undeleted");

        var entityWord = await DataModel.QueryLatest<Word>().Where(w => w.Id == wordId).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.DeletedAt.Should().BeNull();
        entityWord.Text.Should().Be("Undeleted");
    }

    [Fact]
    public async Task DeleteAndUndeleteInSameSyncWorks()
    {
        var wordId = Guid.NewGuid();

        await WriteNextChange(new NewWordChange(wordId, "original"));

        await AddCommitsViaSync([
            await WriteNextChange(new DeleteChange<Word>(wordId), add: false),
            await WriteNextChange(new NewWordChange(wordId, "Undeleted"), add: false),
        ]);

        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        word.DeletedAt.Should().BeNull();
        word.Text.Should().Be("Undeleted");

        var entityWord = await DataModel.QueryLatest<Word>().Where(w => w.Id == wordId).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.DeletedAt.Should().BeNull();
        entityWord.Text.Should().Be("Undeleted");
    }

    [Fact]
    public async Task UpdateAndUndeleteInSameCommitWorks()
    {
        var wordId = Guid.NewGuid();

        await WriteNextChange(new NewWordChange(wordId, "original"));
        await WriteNextChange(new DeleteChange<Word>(wordId));

        await WriteNextChange([
            new SetWordNoteChange(wordId, "overridden-note"),
            new NewWordChange(wordId, "Undeleted"),
        ]);

        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        word.DeletedAt.Should().BeNull();
        word.Text.Should().Be("Undeleted");

        var entityWord = await DataModel.QueryLatest<Word>().Where(w => w.Id == wordId).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.DeletedAt.Should().BeNull();
        entityWord.Text.Should().Be("Undeleted");
    }

    [Fact]
    public async Task UpdateAndUndeleteInSameSyncWorks()
    {
        var wordId = Guid.NewGuid();

        await WriteNextChange(new NewWordChange(wordId, "original"));
        await WriteNextChange(new DeleteChange<Word>(wordId));

        await AddCommitsViaSync([
            await WriteNextChange(new SetWordNoteChange(wordId, "overridden-note"), add: false),
            await WriteNextChange(new NewWordChange(wordId, "Undeleted"), add: false),
        ]);

        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        word.DeletedAt.Should().BeNull();
        word.Text.Should().Be("Undeleted");

        var entityWord = await DataModel.QueryLatest<Word>().Where(w => w.Id == wordId).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.DeletedAt.Should().BeNull();
        entityWord.Text.Should().Be("Undeleted");
    }

    [Fact]
    public async Task CreateDeleteAndUndeleteInSameCommitWorks()
    {
        var wordId = Guid.NewGuid();

        await WriteNextChange(
            [
                new NewWordChange(wordId, "original"),
                new DeleteChange<Word>(wordId),
                new NewWordChange(wordId, "Undeleted"),
            ]);

        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        word.DeletedAt.Should().BeNull();
        word.Text.Should().Be("Undeleted");

        var entityWord = await DataModel.QueryLatest<Word>().Where(w => w.Id == wordId).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.DeletedAt.Should().BeNull();
        entityWord.Text.Should().Be("Undeleted");
    }

    [Fact]
    public async Task CreateDeleteAndUndeleteInSameSyncWorks()
    {
        var wordId = Guid.NewGuid();

        await AddCommitsViaSync([
            await WriteNextChange(new NewWordChange(wordId, "original"), add: false),
            await WriteNextChange(new DeleteChange<Word>(wordId), add: false),
            await WriteNextChange(new NewWordChange(wordId, "Undeleted"), add: false),
        ]);

        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        word.DeletedAt.Should().BeNull();
        word.Text.Should().Be("Undeleted");

        var entityWord = await DataModel.QueryLatest<Word>().Where(w => w.Id == wordId).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.DeletedAt.Should().BeNull();
        entityWord.Text.Should().Be("Undeleted");
    }

    [Fact]
    public async Task CreateAndDeleteInSameCommitWorks()
    {
        var wordId = Guid.NewGuid();

        await WriteNextChange(
            [
                new NewWordChange(wordId, "original"),
                new DeleteChange<Word>(wordId),
            ]);

        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        word.DeletedAt.Should().NotBeNull();
        word.Text.Should().Be("original");

        var entityWord = await DataModel.QueryLatest<Word>().Where(w => w.Id == wordId).SingleOrDefaultAsync();
        entityWord.Should().BeNull();
    }

    [Fact]
    public async Task CreateAndDeleteInSameSyncWorks()
    {
        var wordId = Guid.NewGuid();

        await AddCommitsViaSync([
            await WriteNextChange(new NewWordChange(wordId, "original"), add: false),
            await WriteNextChange(new DeleteChange<Word>(wordId), add: false),
        ]);

        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        word.DeletedAt.Should().NotBeNull();
        word.Text.Should().Be("original");

        var entityWord = await DataModel.QueryLatest<Word>().Where(w => w.Id == wordId).SingleOrDefaultAsync();
        entityWord.Should().BeNull();
    }

    [Fact]
    public async Task NewEntityOnExistingEntityIsNoOp()
    {
        var wordId = Guid.NewGuid();

        await WriteNextChange(new NewWordChange(wordId, "original"));
        var snapshotsBefore = await DbContext.Snapshots.Where(s => s.EntityId == wordId).ToArrayAsync();

        await WriteNextChange(
            [
                new NewWordChange(wordId, "Undeleted"),
            ]);

        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        word.DeletedAt.Should().BeNull();
        word.Text.Should().Be("original");

        var entityWord = await DataModel.QueryLatest<Word>().Where(w => w.Id == wordId).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.DeletedAt.Should().BeNull();
        entityWord.Text.Should().Be("original");

        var snapshotsAfter = await DbContext.Snapshots.Where(s => s.EntityId == wordId).ToArrayAsync();
        snapshotsAfter.Select(s => s.Id).Should().BeEquivalentTo(snapshotsBefore.Select(s => s.Id));
    }
}
