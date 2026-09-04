using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// The smallest shape of the late commit bug, on a single client.
/// Adding several commits at once only keeps a snapshot for every other one, so an entity ends up
/// with a commit that has no snapshot. A commit dated between that commit and the next one that
/// does have a snapshot then makes the replay start after its own parent (the commit with no
/// snapshot), while snapshots are only deleted from the late commit onwards. The entity resumes
/// from the older snapshot it still has, and everything between that snapshot and the late
/// commit's parent is applied by nobody.
/// </summary>
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

        // the rename snapshot is gone and the word resumed from create, so nothing re-applied the note
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

        // the definition resumes from its creation snapshot, and replaying the delete commit does not
        // cascade again, so the edit lands on a live definition whose word is still deleted.
        // Projecting that row back in breaks the foreign key to the word.
        await WriteChangeAfter(delete, SetWord(Guid.NewGuid(), "written late"));

        (await DataModel.GetLatest<Definition>(definitionId))!.DeletedAt.Should().NotBeNull();
    }
}
