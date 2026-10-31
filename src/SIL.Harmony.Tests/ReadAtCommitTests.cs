using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class ReadAtCommitTests() : DataModelTestBase(configure: services =>
    services.Configure<HarmonyConfig>(config => config.MaxChangesBetweenSnapshotCheckpoints = TestMaxChanges))
{
    private const int TestMaxChanges = 8;

    [Fact]
    public async Task ReadsEveryEntityAsOfTheCommit()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var commits = await SyncInOneBatch(
            new SetWordTextChange(wordId, "first"),
            NewDefinition(definitionId, wordId, "a definition"),
            new SetWordTextChange(wordId, "second"));

        var (word, definitions) = await DataModel.ReadAtCommit(commits[1].Id, async view =>
            ((Word)(await view.GetAsync(wordId))!.Entity.DbObject,
                await view.WhereReferences(wordId, includeDeleted: false).ToArrayAsync()));

        word.Text.Should().Be("first");
        definitions.Should().ContainSingle().Which.EntityId.Should().Be(definitionId);
    }

    [Fact]
    public async Task BeforeTheFirstCommitTheViewIsEmpty()
    {
        var commits = await SyncInOneBatch(new SetWordTextChange(Guid.NewGuid(), "first"));

        var all = await DataModel.ReadBeforeCommit(commits[0], view => view.All().ToArrayAsync().AsTask());

        all.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadsBeforeACommit()
    {
        var wordId = Guid.NewGuid();
        var commits = await SyncInOneBatch(new SetWordTextChange(wordId, "first"), new SetWordTextChange(wordId, "second"));

        var word = await DataModel.ReadBeforeCommit(commits[1].Id, async view => (Word)(await view.GetAsync(wordId))!.Entity.DbObject);

        word.Text.Should().Be("first");
    }

    [Fact]
    public async Task AtACheckpointReadsThePersistedSnapshotsWithoutReplaying()
    {
        var wordId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "first"));
        var commit = await WriteNextChange(SetWord(wordId, "second"));
        var persisted = await DbContext.Snapshots.SingleAsync(s => s.CommitId == commit.Id, TestContext.Current.CancellationToken);
        (await IsCheckpoint(commit)).Should().BeTrue();

        var read = await DataModel.ReadAtCommit(commit, view => view.GetAsync(wordId).AsTask());

        read!.Id.Should().Be(persisted.Id, "a replay would have built a new snapshot");
    }

    [Fact]
    public async Task WhereReferencesSeesAReferenceThatALaterCheckpointNoLongerHas()
    {
        var antonymAtCommit = Guid.NewGuid();
        var antonymLater = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        var commits = await SyncInOneBatch(
            new SetWordTextChange(antonymAtCommit, "hot"),
            new SetWordTextChange(antonymLater, "warm"),
            new SetWordTextChange(wordId, "cold"),
            new SetAntonymReferenceChange(wordId, antonymAtCommit),
            new SetAntonymReferenceChange(wordId, antonymLater));
        var commit = commits[3];
        (await IsCheckpoint(commit)).Should().BeFalse("the word's snapshot at the commit was pruned, so reading it needs a replay");

        var referencing = await DataModel.ReadAtCommit(commit,
            view => view.WhereReferences(antonymAtCommit, includeDeleted: false).Select(s => s.EntityId).ToArrayAsync().AsTask());

        referencing.Should().Equal(wordId);
    }

    [Fact]
    public async Task MatchesGetAtCommitAcrossAReplayedWindow()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        IChange[] changes =
        [
            new SetWordTextChange(wordId, "text 0"),
            NewDefinition(definitionId, wordId, "definition 0"),
            .. Enumerable.Range(1, 20).Select(i => i % 3 == 0
                ? (IChange)new SetDefinitionPartOfSpeechChange(definitionId, $"part of speech {i}")
                : i % 2 == 0 ? new SetWordNoteChange(wordId, $"note {i}") : new SetWordTextChange(wordId, $"text {i}")),
        ];
        var commits = await SyncInOneBatch(changes);
        (await DbContext.Commits.CountAsync(c => !c.IsSnapshotCheckpoint, TestContext.Current.CancellationToken))
            .Should().BePositive("otherwise nothing here replays");

        foreach (var commit in commits[1..])
        {
            var (word, definition) = await DataModel.ReadAtCommit(commit, async view =>
                ((await view.GetAsync(wordId))!.Entity.DbObject, (await view.GetAsync(definitionId))!.Entity.DbObject));

            word.Should().BeEquivalentTo(await DataModel.GetAtCommit<Word>(commit, wordId));
            definition.Should().BeEquivalentTo(await DataModel.GetAtCommit<Definition>(commit, definitionId));
        }
    }

    [Fact]
    public async Task WalkCommitsMatchesReadAtCommitAtEveryPause()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        IChange[] changes =
        [
            new SetWordTextChange(wordId, "text 0"),
            NewDefinition(definitionId, wordId, "definition 0"),
            .. Enumerable.Range(1, 20).Select(i => i % 3 == 0
                ? (IChange)new SetDefinitionPartOfSpeechChange(definitionId, $"part of speech {i}")
                : i % 2 == 0 ? new SetWordNoteChange(wordId, $"note {i}") : new SetWordTextChange(wordId, $"text {i}")),
            new DeleteChange<Word>(wordId),
        ];
        var commits = await SyncInOneBatch(changes);
        //a checkpoint, because the walk must still pause at one it could have resumed from
        var fromIndex = 2;
        while (!await IsCheckpoint(commits[fromIndex])) fromIndex++;
        var from = commits[fromIndex];
        var walked = new List<(Commit, ObjectSnapshot[])>();

        await DataModel.WalkCommits(from, commits[^1], async (commit, view) =>
            walked.Add((commit, await view.WhereReferences(wordId, includeDeleted: true).ToArrayAsync())));

        walked.Select(w => w.Item1.Id).Should().Equal(commits[fromIndex..].Select(c => c.Id));
        foreach (var (commit, referencing) in walked)
        {
            var expected = await DataModel.ReadAtCommit(commit, view => view.WhereReferences(wordId, includeDeleted: true).ToArrayAsync().AsTask());
            referencing.Select(s => s.Entity.DbObject).Should().BeEquivalentTo(expected.Select(s => s.Entity.DbObject));
        }
    }

    private async Task<Commit[]> SyncInOneBatch(params IChange[] changes)
    {
        Commit[] commits = [.. changes.Select(change => NextCommit(change))];
        await AddCommitsViaSync(commits);
        return commits;
    }

    private async Task<bool> IsCheckpoint(Commit commit) =>
        await DbContext.Commits.Where(c => c.Id == commit.Id).Select(c => c.IsSnapshotCheckpoint)
            .SingleAsync(TestContext.Current.CancellationToken);

    private static NewDefinitionChange NewDefinition(Guid definitionId, Guid wordId, string text) =>
        new(definitionId) { WordId = wordId, Text = text, PartOfSpeech = "noun", Order = 1 };
}
