using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class LateCommitTests : DataModelTestBase
{
    private async Task AssertSnapshotWasPruned(Commit commit, Guid entityId)
    {
        var snapshots = await DbContext.Snapshots.AsNoTracking()
            .CountAsync(s => s.CommitId == commit.Id && s.EntityId == entityId);
        snapshots.Should().Be(0, "otherwise there's no gap and the test proves nothing");
    }

    [Fact]
    public async Task ALateCommitKeepsAnEditWhoseSnapshotWasPruned()
    {
        var wordId = Guid.NewGuid();
        // add: false builds the commit without applying it, so all three land in one batch below
        var create = await WriteNextChange(SetWord(wordId, "word"), add: false);
        var setNote = await WriteNextChange(new SetWordNoteChange(wordId, "a note"), add: false);
        var rename = await WriteNextChange(new SetWordTextChange(wordId, "renamed word"), add: false);
        await AddCommitsViaSync([create, setNote, rename]);
        // the batch keeps the word's snapshots at create and rename, but not the one in the middle
        await AssertSnapshotWasPruned(setNote, wordId);

        await WriteChangeAfter(setNote, SetWord(Guid.NewGuid(), "written late"));

        // the replay drops the rename snapshot and resumes the word from create, so it has to re-apply the note itself
        var word = await DataModel.GetLatest<Word>(wordId);
        word!.Text.Should().Be("renamed word");
        word.Note.Should().Be("a note");
    }

    [Fact]
    public async Task ALateCommitKeepsACascadeDeleteWhoseSnapshotWasPruned()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var create = await WriteNextChange(SetWord(wordId, "word"), add: false);
        // only here to shift which snapshots the batch keeps, so the definition loses the one below
        var unrelated = await WriteNextChange(SetWord(Guid.NewGuid(), "another word"), add: false);
        var newDefinition = await WriteNextChange(NewDefinition(wordId, "a definition", "noun", definitionId: definitionId), add: false);
        // deleting the word deletes its definition too, but only as a snapshot: no commit records it
        var delete = await WriteNextChange(DeleteWord(wordId), add: false);
        var editDefinition = await WriteNextChange(new SetDefinitionPartOfSpeechChange(definitionId, "verb"), add: false);
        await AddCommitsViaSync([create, unrelated, newDefinition, delete, editDefinition]);
        await AssertSnapshotWasPruned(delete, definitionId);

        // the definition resumes from its creation snapshot, so the replay has to re-run the cascade. Miss it and the
        // edit lands on a live definition whose word is still deleted, and projecting that row back in breaks its
        // foreign key to the word.
        await WriteChangeAfter(delete, SetWord(Guid.NewGuid(), "written late"));

        (await DataModel.GetLatest<Definition>(definitionId))!.DeletedAt.Should().NotBeNull();
    }
}
