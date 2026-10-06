using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class LateCommitTests : DataModelTestBase
{
    private async Task AssertSnapshotWasDropped(Commit commit, Guid entityId)
    {
        var snapshots = await DbContext.Snapshots.AsNoTracking()
            .CountAsync(s => s.CommitId == commit.Id && s.EntityId == entityId);
        snapshots.Should().Be(0, "otherwise there's no gap and the test proves nothing");
    }

    [Fact]
    public async Task AnEditSyncedBeforeItsCreateIsAppliedOnceTheCreateArrives()
    {
        var wordId = Guid.NewGuid();
        var create = NextCommit(SetWord(wordId, "word"));
        var setNote = NextCommit(new SetWordNoteChange(wordId, "a note"));

        await AddCommitsViaSync([setNote]);
        (await DataModel.GetLatest<Word>(wordId)).Should().BeNull();

        await AddCommitsViaSync([create]);
        (await DataModel.GetLatest<Word>(wordId))!.Note.Should().Be("a note");
    }

    [Fact]
    public async Task ALateCommitKeepsAnEditWhoseSnapshotWasPruned()
    {
        var wordId = Guid.NewGuid();
        var create = NextCommit(SetWord(wordId, "word"));
        var setNote = NextCommit(new SetWordNoteChange(wordId, "a note"));
        var rename = NextCommit(new SetWordTextChange(wordId, "renamed word"));
        await AddCommitsViaSync([create, setNote, rename]);
        // the batch keeps the word's snapshots at create and rename, but not the one in the middle
        await AssertSnapshotWasDropped(setNote, wordId);

        await WriteChangeAfter(setNote, SetWord(Guid.NewGuid(), "written late"));

        // the replay drops the only snapshot with the note, but correctly replays the set-note change
        // even though the inserted change is newer than the note change
        var word = await DataModel.GetLatest<Word>(wordId);
        word!.Text.Should().Be("renamed word");
        word.Note.Should().Be("a note");
    }

    [Fact]
    public async Task ALateCommitKeepsACascadeDeleteWhoseSnapshotWasPruned()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var create = NextCommit(SetWord(wordId, "word"));
        // only here to shift which snapshots the batch keeps, so the definition loses the one below
        var unrelated = NextCommit(SetWord(Guid.NewGuid(), "another word"));
        var newDefinition = NextCommit(NewDefinition(wordId, "a definition", "noun", definitionId: definitionId));
        // deleting the word deletes its definition too, but only as a snapshot: no commit records it
        var delete = NextCommit(DeleteWord(wordId));
        var editDefinition = NextCommit(new SetDefinitionPartOfSpeechChange(definitionId, "verb"));
        await AddCommitsViaSync([create, unrelated, newDefinition, delete, editDefinition]);
        await AssertSnapshotWasDropped(delete, definitionId);

        await WriteChangeAfter(delete, SetWord(Guid.NewGuid(), "written late"));

        // the replay drops the only snapshot that shows the definition's word is deleted, but correctly replays the delete change
        // even though the inserted change is newer than the delete change.
        (await DataModel.GetLatest<Definition>(definitionId))!.DeletedAt.Should().NotBeNull();
    }
}
