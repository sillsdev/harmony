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

        await using var view = await DataModel.OpenViewAtCommit(commits[1].Id);

        ((Word)(await view.GetAsync(wordId))!.Entity.DbObject).Text.Should().Be("first");
        (await ((ISnapshotView)view).WhereReferences(wordId, includeDeleted: false).ToArrayAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle().Which.EntityId.Should().Be(definitionId);
    }

    [Fact]
    public async Task BeforeTheFirstCommitTheViewIsEmpty()
    {
        var commits = await SyncInOneBatch(new SetWordTextChange(Guid.NewGuid(), "first"));

        await using var view = await DataModel.OpenViewBeforeCommit(commits[0]);

        (await view.All().ToArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadsBeforeACommit()
    {
        var wordId = Guid.NewGuid();
        var commits = await SyncInOneBatch(new SetWordTextChange(wordId, "first"), new SetWordTextChange(wordId, "second"));

        await using var view = await DataModel.OpenViewBeforeCommit(commits[1].Id);

        ((Word)(await view.GetAsync(wordId))!.Entity.DbObject).Text.Should().Be("first");
    }

    [Fact]
    public async Task AtACheckpointReadsThePersistedSnapshotsWithoutReplaying()
    {
        var wordId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "first"));
        var commit = await WriteNextChange(SetWord(wordId, "second"));
        var persisted = await DbContext.Snapshots.SingleAsync(s => s.CommitId == commit.Id, TestContext.Current.CancellationToken);
        (await IsCheckpoint(commit)).Should().BeTrue();

        await using var view = await DataModel.OpenViewAtCommit(commit);
        var read = await view.GetAsync(wordId);

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

        await using var view = await DataModel.OpenViewAtCommit(commit);
        var referencing = await ((ISnapshotView)view).WhereReferences(antonymAtCommit, includeDeleted: false).ToArrayAsync(TestContext.Current.CancellationToken);

        referencing.Select(s => s.EntityId).Should().Equal(wordId);
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
            await using var view = await DataModel.OpenViewAtCommit(commit);
            var word = (await view.GetAsync(wordId))!.Entity.DbObject;
            var definition = (await view.GetAsync(definitionId))!.Entity.DbObject;

            word.Should().BeEquivalentTo(await DataModel.GetAtCommit<Word>(commit, wordId));
            definition.Should().BeEquivalentTo(await DataModel.GetAtCommit<Definition>(commit, definitionId));
        }
    }

    [Fact]
    public async Task ADisposedViewThrows()
    {
        var wordId = Guid.NewGuid();
        var commits = await SyncInOneBatch(new SetWordTextChange(wordId, "first"), new SetWordTextChange(wordId, "second"));
        var view = await DataModel.OpenViewAtCommit(commits[0]);
        await view.GetAsync(wordId);

        await view.DisposeAsync();

        await view.Invoking(v => v.GetAsync(wordId).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task ACommitReplayMatchesOpenViewAtCommitAtEveryStep()
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
        //a checkpoint, because the replay must still stop at one it could have resumed from
        var fromIndex = 2;
        while (!await IsCheckpoint(commits[fromIndex])) fromIndex++;

        await using var replay = await DataModel.StartCommitReplay(commits[fromIndex], commits[^1]);
        await ShouldMatch(replay, await DataModel.OpenViewBeforeCommit(commits[fromIndex]));
        var applied = new List<Guid>();
        while (await replay.ApplyNextCommit() is { } commit)
        {
            applied.Add(commit.Id);
            replay.CurrentCommit.Should().Be(commit);
            await ShouldMatch(replay, await DataModel.OpenViewAtCommit(commit));
        }

        applied.Should().Equal(commits[fromIndex..].Select(c => c.Id));

        async Task ShouldMatch(ISnapshotView actual, SnapshotView expected)
        {
            await using var _ = expected;
            var actualReferencing = await actual.WhereReferences(wordId, includeDeleted: true).ToArrayAsync();
            var expectedReferencing = await ((ISnapshotView)expected).WhereReferences(wordId, includeDeleted: true).ToArrayAsync();
            actualReferencing.Select(s => s.Entity.DbObject).Should().BeEquivalentTo(expectedReferencing.Select(s => s.Entity.DbObject));
            object? actualWord = (await actual.GetAsync(wordId))?.Entity.DbObject;
            actualWord.Should().BeEquivalentTo((await expected.GetAsync(wordId))?.Entity.DbObject);
        }
    }

    [Fact]
    public async Task ACommitReplayFromTheFirstCommitStartsEmptyAndStaysAtTheLastCommit()
    {
        var wordId = Guid.NewGuid();
        var commits = await SyncInOneBatch(new SetWordTextChange(wordId, "first"), new SetWordTextChange(wordId, "second"));

        await using var replay = await DataModel.StartCommitReplay(commits[0], commits[^1]);

        (await replay.All().ToArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await replay.ApplyNextCommit()).Should().Be(commits[0]);
        Text(await replay.GetAsync(wordId)).Should().Be("first");
        (await replay.ApplyNextCommit()).Should().Be(commits[1]);
        (await replay.ApplyNextCommit()).Should().BeNull();
        Text(await replay.GetAsync(wordId)).Should().Be("second");

        static string Text(ObjectSnapshot? snapshot) => ((Word)snapshot!.Entity.DbObject).Text;
    }

    [Fact]
    public async Task ADisposedCommitReplayThrows()
    {
        var wordId = Guid.NewGuid();
        var commits = await SyncInOneBatch(new SetWordTextChange(wordId, "first"));
        var replay = await DataModel.StartCommitReplay(commits[0], commits[0]);

        await replay.DisposeAsync();

        await replay.Invoking(r => r.GetAsync(wordId).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
        await replay.Invoking(r => r.ApplyNextCommit()).Should().ThrowAsync<ObjectDisposedException>();
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
