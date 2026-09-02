using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// A commit dated before commits a replica already holds makes it roll its snapshots back and replay history.
/// These tests sync edits to the replica in one batch, which prunes some of their snapshots, and then land a late commit in the gap.
/// </summary>
public class LateCommitTests : IAsyncLifetime
{
    private readonly DataModelTestBase _author = new();
    private readonly DataModelTestBase _replica = new();
    private readonly DataModelTestBase _offlineClient = new();

    public async ValueTask InitializeAsync()
    {
        await _author.InitializeAsync();
        await _replica.InitializeAsync();
        await _offlineClient.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _author.DisposeAsync();
        await _replica.DisposeAsync();
        await _offlineClient.DisposeAsync();
    }

    // which of the edits' snapshots the batch prunes depends on their position in it, so land the late commit after each edit
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EditsSyncedInOneBatchSurviveALateCommitBetweenThem(int lateCommitAfterEdit)
    {
        var wordId = Guid.NewGuid();
        var antonymId = Guid.NewGuid();
        await _author.WriteNextChange([_author.SetWord(wordId, "word"), _author.SetWord(antonymId, "antonym")]);
        Commit[] edits =
        [
            await _author.WriteNextChange(new SetWordNoteChange(wordId, "a note")),
            await _author.WriteNextChange(new SetAntonymReferenceChange(wordId, antonymId)),
            await _author.WriteNextChange(new SetWordTextChange(wordId, "renamed word")),
        ];
        await _replica.DataModel.SyncWith(_author.DataModel);

        // another client wrote an unrelated word while offline, dated between two of the edits
        await _offlineClient.WriteChangeAfter(edits[lateCommitAfterEdit], _offlineClient.SetWord(Guid.NewGuid(), "written offline"));
        await _replica.DataModel.SyncWith(_offlineClient.DataModel);

        var replicaWord = await _replica.DataModel.GetLatest<Word>(wordId);
        var authorWord = await _author.DataModel.GetLatest<Word>(wordId);
        replicaWord.Should().BeEquivalentTo(authorWord);
    }

    // whether the replay keeps the definition's cascade-delete snapshot depends on the edit's position in the batch,
    // which the size of the backlog before the delete shifts by one
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CascadeDeleteSurvivesALateCommitAfterIt(int backlogSize)
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        await _author.WriteNextChange(_author.SetWord(wordId, "word"));
        await _author.WriteNextChange(_author.NewDefinition(wordId, "a definition", "noun", definitionId: definitionId));
        await _replica.DataModel.SyncWith(_author.DataModel);
        await _offlineClient.DataModel.SyncWith(_author.DataModel);

        // deleting the word deletes its definition too, but no commit records that: it only exists as a snapshot
        var delete = await _author.WriteNextChange(_author.DeleteWord(wordId));
        await _replica.DataModel.SyncWith(_author.DataModel);

        // meanwhile the offline client wrote some words before the delete happened...
        var backlog = delete;
        for (var i = 0; i < backlogSize; i++)
        {
            backlog = await _offlineClient.WriteChangeBefore(backlog, _offlineClient.SetWord(Guid.NewGuid(), $"offline word {i}"));
        }
        // ...and, never having received the delete, edits the definition after it
        _offlineClient.SetCurrentDate(delete.DateTime);
        var edit = await _offlineClient.WriteNextChange(new SetDefinitionPartOfSpeechChange(definitionId, "verb"));
        await _replica.DataModel.SyncWith(_offlineClient.DataModel);
        (await _replica.DataModel.GetLatest<Definition>(definitionId))!.DeletedAt.Should().NotBeNull();

        // the author writes another word, dated between the delete and the edit it doesn't know about yet
        await _author.WriteChangeBefore(edit, _author.SetWord(Guid.NewGuid(), "another word"));
        await _replica.DataModel.SyncWith(_author.DataModel);

        (await _replica.DataModel.GetLatest<Definition>(definitionId))!.DeletedAt.Should().NotBeNull();
    }
}
