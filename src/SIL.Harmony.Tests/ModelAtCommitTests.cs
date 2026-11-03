using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class ModelAtCommitTests() : DataModelTestBase(configure: services =>
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

        await using var model = await DataModel.OpenAtCommit(commits[1].Id);

        (await model.Get<Word>(wordId))!.Text.Should().Be("first");
        (await model.WhereReferences(wordId).ToArrayAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle().Which.EntityId.Should().Be(definitionId);
    }

    [Fact]
    public async Task BeforeTheFirstCommitThereIsNoModel()
    {
        var commits = await SyncInOneBatch(new SetWordTextChange(Guid.NewGuid(), "first"));

        (await DataModel.OpenBeforeCommit(commits[0])).Should().BeNull();
    }

    [Fact]
    public async Task ReadsBeforeACommit()
    {
        var wordId = Guid.NewGuid();
        var commits = await SyncInOneBatch(new SetWordTextChange(wordId, "first"), new SetWordTextChange(wordId, "second"));

        await using var model = await DataModel.OpenBeforeCommit(commits[1].Id);

        model!.Commit.Id.Should().Be(commits[0].Id);
        (await model.Get<Word>(wordId))!.Text.Should().Be("first");
    }

    [Fact]
    public async Task AtACheckpointReadsThePersistedSnapshotsWithoutReplaying()
    {
        var wordId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "first"));
        var commit = await WriteNextChange(SetWord(wordId, "second"));
        var persisted = await DbContext.Snapshots.SingleAsync(s => s.CommitId == commit.Id, TestContext.Current.CancellationToken);
        (await IsCheckpoint(commit)).Should().BeTrue();

        await using var model = await DataModel.OpenAtCommit(commit);
        var read = await model.GetSnapshot(wordId);

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

        await using var model = await DataModel.OpenAtCommit(commit);
        var referencing = await model.WhereReferences(antonymAtCommit).ToArrayAsync(TestContext.Current.CancellationToken);

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
            await using var model = await DataModel.OpenAtCommit(commit);
            var word = await model.Get<Word>(wordId);
            var definition = await model.Get<Definition>(definitionId);

            word.Should().BeEquivalentTo(await DataModel.GetAtCommit<Word>(commit, wordId));
            definition.Should().BeEquivalentTo(await DataModel.GetAtCommit<Definition>(commit, definitionId));
        }
    }

    [Fact]
    public async Task ADisposedModelThrows()
    {
        var wordId = Guid.NewGuid();
        var commits = await SyncInOneBatch(new SetWordTextChange(wordId, "first"), new SetWordTextChange(wordId, "second"));
        var model = await DataModel.OpenAtCommit(commits[0]);
        await model.Get<Word>(wordId);

        await model.DisposeAsync();

        await model.Invoking(m => m.Get<Word>(wordId)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetRejectsATypeThatIsNotRegistered()
    {
        var commits = await SyncInOneBatch(new SetWordTextChange(Guid.NewGuid(), "first"));
        await using var model = await DataModel.OpenAtCommit(commits[0]);

        //a missing entity too, so the check can't depend on there being a snapshot to cast
        await model.Invoking(m => m.Get<object>(Guid.NewGuid())).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TheCommitIsNotTracked()
    {
        var commits = await SyncInOneBatch(new SetWordTextChange(Guid.NewGuid(), "first"), new SetWordTextChange(Guid.NewGuid(), "second"));

        await using var atCommit = await DataModel.OpenAtCommit(commits[1]);
        await using var beforeCommit = await DataModel.OpenBeforeCommit(commits[1]);

        DbContext.Entry(atCommit.Commit).State.Should().Be(EntityState.Detached);
        DbContext.Entry(beforeCommit!.Commit).State.Should().Be(EntityState.Detached);
    }

    [Fact]
    public async Task OpeningWaitsForTheRepositoryLock()
    {
        //on disk so the lock isn't the one every in-memory test shares
        await using var onDisk = new DataModelTestBase(saveToDisk: true);
        var commit = await onDisk.WriteNextChange(SetWord(Guid.NewGuid(), "first"));

        Task<ModelAtCommit> opening;
        await using (var repo = onDisk.CreateRepository())
        using (await repo.Lock())
        {
            opening = onDisk.DataModel.OpenAtCommit(commit);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            opening.IsCompleted.Should().BeFalse();
        }

        await using var model = await opening;
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
