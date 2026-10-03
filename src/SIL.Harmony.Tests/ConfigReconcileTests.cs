using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// The database starts out written by an "old app" that doesn't know <see cref="SetWordNoteChange"/>,
/// and is then opened by a "new app" that does, by forking it with a different config.
/// </summary>
public class ConfigReconcileTests() : DataModelTestBase(configure: OldApp)
{
    private const string NoteChangeType = nameof(SetWordNoteChange);

    private static void OldApp(IServiceCollection services)
    {
        services.Configure<HarmonyConfig>(config =>
        {
            config.UnknownChangeHandling = UnknownChangeHandling.Fallback;
            config.ChangeTypeListBuilder.Remove<SetWordNoteChange>();
        });
    }

    private static void NewApp(IServiceCollection services)
    {
        services.Configure<HarmonyConfig>(config => config.UnknownChangeHandling = UnknownChangeHandling.Fallback);
    }

    private static readonly JsonSerializerOptions NewAppJsonOptions = CreateNewAppJsonOptions();

    private static JsonSerializerOptions CreateNewAppJsonOptions()
    {
        using var services = new ServiceCollection().AddCrdtDataSample(":memory:").BuildServiceProvider();
        return services.GetRequiredService<JsonSerializerOptions>();
    }

    /// <summary>a note change as the old app sees it, after it was synced from a newer client</summary>
    private static IChange OpaqueNote(DataModelTestBase oldApp, Guid wordId, string note)
    {
        var json = JsonSerializer.Serialize<IChange>(new SetWordNoteChange(wordId, note), NewAppJsonOptions);
        var change = JsonSerializer.Deserialize<IChange>(json, oldApp.CrdtConfig.JsonSerializerOptions);
        return change.Should().BeOfType<OpaqueChange>().Subject;
    }

    private static async Task<Commit> SyncOpaqueNote(DataModelTestBase oldApp, Guid wordId, string note)
    {
        var commit = await oldApp.WriteNextChange(OpaqueNote(oldApp, wordId, note), add: false);
        await oldApp.AddCommitsViaSync([commit]);
        return commit;
    }

    private static async Task<string?> NoteOf(DataModelTestBase app, Guid wordId)
    {
        var word = await app.DataModel.GetLatest<Word>(wordId);
        word.Should().NotBeNull();
        var projectedWord = await app.DbContext.Set<Word>().AsNoTracking()
            .SingleAsync(w => w.Id == wordId, TestContext.Current.CancellationToken);
        projectedWord.Note.Should().Be(word.Note, "the projected table should match the snapshot");
        return word.Note;
    }

    [Fact]
    public async Task AddedChangeTypeIsAppliedAfterReconcile()
    {
        await DataModel.ReconcileConfigChanges();
        var wordId = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(wordId, "hello"));
        var noteCommit = await SyncOpaqueNote(this, wordId, "a note");
        (await NoteOf(this, wordId)).Should().BeNull("the old app can't apply the note change");

        await using var newApp = ForkDatabase(NewApp);
        (await NoteOf(newApp, wordId)).Should().BeNull("nothing replays the note change until the config change is detected");

        var result = await newApp.DataModel.ReconcileConfigChanges();

        result.ConfigChanged.Should().BeTrue();
        result.AddedChangeTypes.Should().Equal(NoteChangeType);
        result.RemovedChangeTypes.Should().BeEmpty();
        result.ReplayedFrom.Should().NotBeNull();
        result.ReplayedFrom.Id.Should().Be(noteCommit.Id);
        (await NoteOf(newApp, wordId)).Should().Be("a note");
    }

    [Fact]
    public async Task ReconcileWithTheSameConfigDoesNothing()
    {
        (await DataModel.ReconcileConfigChanges()).ConfigChanged.Should().BeTrue("no config was stored yet");
        var wordId = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(wordId, "hello"));
        var snapshotIds = await SnapshotIds(this);

        var result = await DataModel.ReconcileConfigChanges();

        result.Should().Be(ConfigReconcileResult.Unchanged);
        (await SnapshotIds(this)).Should().Equal(snapshotIds);
    }

    [Fact]
    public async Task NewDatabaseStoresTheConfigWithoutReplaying()
    {
        var result = await DataModel.ReconcileConfigChanges();

        result.ConfigChanged.Should().BeTrue();
        result.ReplayedFrom.Should().BeNull();
        (await DataModel.GetLocalState<JsonElement?>("harmony:config")).Should().NotBeNull();
        (await DataModel.ReconcileConfigChanges()).ConfigChanged.Should().BeFalse();
    }

    [Fact]
    public async Task ReplayResumesFromTheCheckpointBeforeTheAddedChange()
    {
        void SmallCheckpoints(IServiceCollection services) =>
            services.Configure<HarmonyConfig>(config => config.MaxChangesBetweenSnapshotCheckpoints = 4);
        await using var oldApp = ForkDatabase(services =>
        {
            OldApp(services);
            SmallCheckpoints(services);
        });
        await oldApp.DataModel.ReconcileConfigChanges();
        var wordId = Guid.NewGuid();
        await oldApp.WriteNextChange(new NewWordChange(wordId, "text 0"));
        for (var i = 1; i < 20; i++)
        {
            await oldApp.WriteNextChange(new SetWordTextChange(wordId, $"text {i}"));
        }
        var noteCommit = await SyncOpaqueNote(oldApp, wordId, "a note");
        await oldApp.WriteNextChange(new SetWordTextChange(wordId, "last text"));

        await using var newApp = oldApp.ForkDatabase(services =>
        {
            NewApp(services);
            SmallCheckpoints(services);
        });
        var checkpoint = await newApp.DbContext.Commits.AsNoTracking()
            .Where(c => c.IsSnapshotCheckpoint)
            .WhereBefore(noteCommit, inclusive: false)
            .DefaultOrderDescending()
            .FirstAsync(TestContext.Current.CancellationToken);
        var commitIdsUpToCheckpoint = newApp.DbContext.Commits.WhereBefore(checkpoint, inclusive: true).Select(c => c.Id);
        var snapshotsUpToCheckpoint = await newApp.DbContext.Snapshots.AsNoTracking()
            .Where(s => commitIdsUpToCheckpoint.Contains(s.CommitId))
            .Select(s => s.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        snapshotsUpToCheckpoint.Should().NotBeEmpty();

        var result = await newApp.DataModel.ReconcileConfigChanges();

        result.ReplayedFrom!.Id.Should().Be(noteCommit.Id);
        (await SnapshotIds(newApp)).Should().Contain(snapshotsUpToCheckpoint, "snapshots before the checkpoint should not be rebuilt");
        var word = await newApp.DataModel.GetLatest<Word>(wordId);
        word!.Note.Should().Be("a note");
        word.Text.Should().Be("last text");

        var stateAfterReconcile = await newApp.DataModel.GetProjectSnapshot(includeDeleted: true);
        await newApp.DataModel.RegenerateSnapshots();
        var stateAfterRegenerate = await newApp.DataModel.GetProjectSnapshot(includeDeleted: true);
        stateAfterReconcile.Snapshots.Values.Select(s => (s.EntityId, s.CommitId, s.EntityIsDeleted))
            .Should().BeEquivalentTo(stateAfterRegenerate.Snapshots.Values.Select(s => (s.EntityId, s.CommitId, s.EntityIsDeleted)));
    }

    [Fact]
    public async Task RemovedChangeTypeIsUnappliedAfterReconcile()
    {
        await using var newApp = ForkDatabase(NewApp);
        await newApp.DataModel.ReconcileConfigChanges();
        var wordId = Guid.NewGuid();
        await newApp.WriteNextChange(new NewWordChange(wordId, "hello"));
        var noteCommit = await newApp.WriteNextChange(new SetWordNoteChange(wordId, "a note"));

        await using var oldApp = newApp.ForkDatabase(OldApp);
        (await NoteOf(oldApp, wordId)).Should().Be("a note", "the snapshots were made by the new app");
        var downgrade = await oldApp.DataModel.ReconcileConfigChanges();

        downgrade.AddedChangeTypes.Should().BeEmpty();
        downgrade.RemovedChangeTypes.Should().Equal(NoteChangeType);
        downgrade.ReplayedFrom!.Id.Should().Be(noteCommit.Id);
        (await NoteOf(oldApp, wordId)).Should().BeNull("the old app can't apply the note change");

        await using var upgradedAgain = oldApp.ForkDatabase(NewApp);
        var upgrade = await upgradedAgain.DataModel.ReconcileConfigChanges();

        upgrade.AddedChangeTypes.Should().Equal(NoteChangeType);
        (await NoteOf(upgradedAgain, wordId)).Should().Be("a note");
    }

    [Fact]
    public async Task RemovedCreateChangeTypeRemovesTheEntity()
    {
        await using var newApp = ForkDatabase(NewApp);
        await newApp.DataModel.ReconcileConfigChanges();
        var wordId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        await newApp.WriteNextChange(new NewWordChange(wordId, "hello"));
        await newApp.WriteNextChange(new SetTagChange(tagId, "tag"));

        await using var oldApp = newApp.ForkDatabase(services =>
        {
            NewApp(services);
            services.Configure<HarmonyConfig>(config => config.ChangeTypeListBuilder.Remove<SetTagChange>());
        });
        var result = await oldApp.DataModel.ReconcileConfigChanges();

        result.RemovedChangeTypes.Should().Equal(nameof(SetTagChange));
        (await oldApp.DataModel.GetLatest<Tag>(tagId)).Should().BeNull();
        (await oldApp.DbContext.Set<Tag>().AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        (await oldApp.DataModel.GetLatest<Word>(wordId)).Should().NotBeNull();
    }

    [Fact]
    public async Task MissingConfigWithReplayAllReplaysAllOfHistory()
    {
        var wordId = Guid.NewGuid();
        var firstCommit = await WriteNextChange(new NewWordChange(wordId, "hello"));
        await SyncOpaqueNote(this, wordId, "a note");

        await using var newApp = ForkDatabase(NewApp);
        var result = await newApp.DataModel.ReconcileConfigChanges(MissingConfigBehavior.ReplayAll);

        result.ConfigChanged.Should().BeTrue();
        result.AddedChangeTypes.Should().BeEmpty("there was no stored config to compare with");
        result.ReplayedFrom!.Id.Should().Be(firstCommit.Id);
        (await NoteOf(newApp, wordId)).Should().Be("a note");
    }

    [Fact]
    public async Task MissingConfigWithAssumeCurrentConfigDoesNotReplay()
    {
        var wordId = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(wordId, "hello"));
        var snapshotIds = await SnapshotIds(this);

        var result = await DataModel.ReconcileConfigChanges(MissingConfigBehavior.AssumeCurrentConfig);

        result.ConfigChanged.Should().BeTrue();
        result.ReplayedFrom.Should().BeNull();
        (await SnapshotIds(this)).Should().Equal(snapshotIds);

        //the stored config is used from now on
        await using var newApp = ForkDatabase(NewApp);
        var upgrade = await newApp.DataModel.ReconcileConfigChanges(MissingConfigBehavior.AssumeCurrentConfig);
        upgrade.AddedChangeTypes.Should().Equal(NoteChangeType);
    }

    [Fact]
    public async Task FindOldestCommitWithChangeTypesUsesCommitOrder()
    {
        await using var newApp = ForkDatabase(NewApp);
        var wordId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        await newApp.WriteNextChange(new NewWordChange(wordId, "hello"));
        var laterNote = await newApp.WriteNextChange(new SetWordNoteChange(wordId, "later"));
        var tagCommit = await newApp.WriteNextChange(new SetTagChange(tagId, "tag"));
        //added last, but sorts before the other note
        var earlierNote = await newApp.WriteChangeBefore(laterNote, new SetWordNoteChange(wordId, "earlier"));
        await using var repo = newApp.CreateRepository();

        (await repo.FindOldestCommitWithChangeTypes([NoteChangeType]))!.Id.Should().Be(earlierNote.Id);
        (await repo.FindOldestCommitWithChangeTypes([nameof(SetTagChange)]))!.Id.Should().Be(tagCommit.Id);
        (await repo.FindOldestCommitWithChangeTypes([nameof(SetTagChange), NoteChangeType]))!.Id.Should().Be(earlierNote.Id);
        (await repo.FindOldestCommitWithChangeTypes([nameof(EditExampleChange)])).Should().BeNull();
        (await repo.FindOldestCommitWithChangeTypes([])).Should().BeNull();
    }

    [Fact]
    public async Task LocalStateCanBeSetReadAndRemoved()
    {
        (await DataModel.GetLocalState<string>("app:missing")).Should().BeNull();

        await DataModel.SetLocalState("app:value", new LocalStateTestValue("first", 1));
        (await DataModel.GetLocalState<LocalStateTestValue>("app:value")).Should().Be(new LocalStateTestValue("first", 1));

        await DataModel.SetLocalState("app:value", new LocalStateTestValue("second", 2));
        (await DataModel.GetLocalState<LocalStateTestValue>("app:value")).Should().Be(new LocalStateTestValue("second", 2));

        await DataModel.RemoveLocalState("app:value");
        (await DataModel.GetLocalState<LocalStateTestValue>("app:value")).Should().BeNull();
        await DataModel.RemoveLocalState("app:value");
    }

    private record LocalStateTestValue(string Text, int Number);

    private static async Task<Guid[]> SnapshotIds(DataModelTestBase app)
    {
        return await app.DbContext.Snapshots.AsNoTracking()
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }
}
