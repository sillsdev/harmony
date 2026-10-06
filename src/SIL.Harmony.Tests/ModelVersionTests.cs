using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// The database starts out written by an "old app" that has no model versions,
/// and is then opened by a "new app" with model versions, by forking it with a different config.
/// </summary>
public class ModelVersionTests() : DataModelTestBase(configure: OldApp)
{
    private const string NoteChangeType = nameof(SetWordNoteChange);

    /// <summary>knows <see cref="SetWordNoteChange"/>, but applies it the old way</summary>
    private static void OldApp(IServiceCollection services)
    {
        services.Configure<HarmonyConfig>(config => config.UnknownChangeHandling = UnknownChangeHandling.Fallback);
    }

    /// <summary>doesn't know <see cref="SetWordNoteChange"/> at all</summary>
    private static void OldAppWithoutNotes(IServiceCollection services)
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config => config.ChangeTypeListBuilder.Remove<SetWordNoteChange>());
    }

    private static void NewApp(IServiceCollection services)
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config =>
            config.ModelVersionBuilder.Add("Notes", v => v.Change<SetWordNoteChange>()));
    }

    private static Action<IServiceCollection> AppWith(Action<ModelVersionBuilder> versions)
    {
        return services =>
        {
            OldApp(services);
            services.Configure<HarmonyConfig>(config => versions(config.ModelVersionBuilder));
        };
    }

    private static readonly JsonSerializerOptions NewAppJsonOptions = CreateNewAppJsonOptions();

    private static JsonSerializerOptions CreateNewAppJsonOptions()
    {
        using var services = new ServiceCollection().AddCrdtDataSample(":memory:").BuildServiceProvider();
        return services.GetRequiredService<JsonSerializerOptions>();
    }

    /// <summary>a note change as an app that doesn't know the type sees it, after it was synced from a newer client</summary>
    private static IChange OpaqueNote(DataModelTestBase oldApp, Guid wordId, string note)
    {
        var json = JsonSerializer.Serialize<IChange>(new SetWordNoteChange(wordId, note), NewAppJsonOptions);
        var change = JsonSerializer.Deserialize<IChange>(json, oldApp.CrdtConfig.JsonSerializerOptions);
        return change.Should().BeOfType<OpaqueChange>().Subject;
    }

    /// <summary>a commit authored by another client, with the change version that client's app gave it</summary>
    private static async Task<Commit> SyncFromOtherClient(DataModelTestBase app, IChange change, int version)
    {
        var commit = new Commit
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = new HybridDateTime(app.NextDate(), 0),
        };
        commit.ChangeEntities.Add(new ChangeEntity<IChange>
        {
            Change = change,
            CommitId = commit.Id,
            EntityId = change.EntityId,
            Index = 0,
            Version = version
        });
        await app.AddCommitsViaSync([commit]);
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

    private static async Task<Guid[]> SnapshotIds(DataModelTestBase app)
    {
        return await app.DbContext.Snapshots.AsNoTracking()
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>a regenerated snapshot is a new row with a new id, so none of the old ids may be left</summary>
    private static async Task ShouldBeRegenerated(DataModelTestBase app, Guid[] snapshotIdsBefore)
    {
        snapshotIdsBefore.Should().NotBeEmpty();
        var snapshotIdsAfter = await SnapshotIds(app);
        snapshotIdsAfter.Should().HaveSameCount(snapshotIdsBefore);
        snapshotIdsAfter.Should().NotIntersectWith(snapshotIdsBefore, "every snapshot should be recreated");
    }

    private static async Task<int[]> ChangeVersions(DataModelTestBase app, Commit commit)
    {
        return await app.DbContext.Set<ChangeEntity<IChange>>().AsNoTracking()
            .Where(ce => ce.CommitId == commit.Id)
            .OrderBy(ce => ce.Index)
            .Select(ce => ce.Version)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AuthoredChangesGetTheVersionOfTheirType()
    {
        await using var app = ForkDatabase(AppWith(versions => versions
            .Add("Notes", v => v.Change<SetWordNoteChange>())
            .Add("Notes and tags", v => v.Change<SetWordNoteChange>().Change<SetTagChange>())));
        var wordId = Guid.NewGuid();

        var commit = await app.WriteNextChange([
            new NewWordChange(wordId, "hello"),
            new SetTagChange(Guid.NewGuid(), "tag"),
            new SetWordNoteChange(wordId, "note")
        ]);

        commit.ChangeEntities.Select(ce => ce.Version).Should().Equal(0, 1, 2);
        (await ChangeVersions(app, commit)).Should().Equal(0, 1, 2);
        var json = JsonSerializer.Serialize(commit, app.CrdtConfig.JsonSerializerOptions);
        JsonSerializer.Deserialize<Commit>(json, app.CrdtConfig.JsonSerializerOptions)!
            .ChangeEntities.Select(ce => ce.Version).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task UnknownTypeIsAppliedAfterUpgrade()
    {
        await using var oldApp = ForkDatabase(OldAppWithoutNotes);
        await oldApp.ModelVersionService.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        await oldApp.WriteNextChange(new NewWordChange(wordId, "hello"));
        var noteCommit = await SyncFromOtherClient(oldApp, OpaqueNote(oldApp, wordId, "a note"), version: 1);
        (await NoteOf(oldApp, wordId)).Should().BeNull("the old app can't apply the note change");

        await using var newApp = oldApp.ForkDatabase(NewApp);
        var result = await newApp.ModelVersionService.ReconcileModelVersions();

        result.Should().Be(new ModelVersionReconcileResult(0, 1, false, result.ReplayedFrom));
        result.ReplayedFrom!.Id.Should().Be(noteCommit.Id);
        (await NoteOf(newApp, wordId)).Should().Be("a note");
    }

    [Fact]
    public async Task ModifiedTypeReplaysFromTheFirstChangeAuthoredByANewerApp()
    {
        await DataModel.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(wordId, "hello"));
        var oldNote = await WriteNextChange(new SetWordNoteChange(wordId, "old note"));
        var newerNote = await SyncFromOtherClient(this, new SetWordNoteChange(wordId, "newer note"), version: 1);
        (await ChangeVersions(this, oldNote)).Should().Equal(0);

        await using var newApp = ForkDatabase(NewApp);
        var result = await newApp.ModelVersionService.ReconcileModelVersions();

        result.ReplayedFrom!.Id.Should().Be(newerNote.Id,
            "the old note was authored and applied by the old app, so it doesn't need a replay");
        result.FullRegeneration.Should().BeFalse();
        (await NoteOf(newApp, wordId)).Should().Be("newer note");
    }

    [Fact]
    public async Task ReplayResumesFromTheCheckpointBeforeTheChange()
    {
        static void SmallCheckpoints(IServiceCollection services) =>
            services.Configure<HarmonyConfig>(config => config.MaxChangesBetweenSnapshotCheckpoints = 4);
        await using var oldApp = ForkDatabase(services =>
        {
            OldAppWithoutNotes(services);
            SmallCheckpoints(services);
        });
        await oldApp.ModelVersionService.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        await oldApp.WriteNextChange(new NewWordChange(wordId, "text 0"));
        for (var i = 1; i < 20; i++)
        {
            await oldApp.WriteNextChange(new SetWordTextChange(wordId, $"text {i}"));
        }
        var noteCommit = await SyncFromOtherClient(oldApp, OpaqueNote(oldApp, wordId, "a note"), version: 1);
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

        var result = await newApp.ModelVersionService.ReconcileModelVersions();

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
    public async Task MajorVersionRegeneratesAllSnapshots()
    {
        await DataModel.ReconcileModelVersions();
        var firstCommit = await WriteNextChange(new NewWordChange(Guid.NewGuid(), "hello"));
        await WriteNextChange(new NewWordChange(Guid.NewGuid(), "world"));

        await using var newApp = ForkDatabase(AppWith(versions => versions.AddMajor("Rewrite")));
        var snapshotIds = await SnapshotIds(newApp);
        var result = await newApp.ModelVersionService.ReconcileModelVersions();

        result.FullRegeneration.Should().BeTrue();
        result.ReplayedFrom!.Id.Should().Be(firstCommit.Id);
        result.CurrentVersion.Should().Be(1);
        await ShouldBeRegenerated(newApp, snapshotIds);
    }

    [Fact]
    public async Task SameVersionDoesNothing()
    {
        await using var app = ForkDatabase(NewApp);
        (await app.ModelVersionService.ReconcileModelVersions()).Changed.Should().BeTrue("no version was stored yet");
        await app.WriteNextChange(new NewWordChange(Guid.NewGuid(), "hello"));
        var snapshotIds = await SnapshotIds(app);

        var result = await app.ModelVersionService.ReconcileModelVersions();

        result.Should().Be(new ModelVersionReconcileResult(1, 1, false, null));
        result.Changed.Should().BeFalse();
        (await SnapshotIds(app)).Should().Equal(snapshotIds);
    }

    [Fact]
    public async Task NoStoredVersionIsVersionZero()
    {
        var wordId = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(wordId, "hello"));
        await WriteNextChange(new SetWordNoteChange(wordId, "a note"));

        await using var newApp = ForkDatabase(NewApp);
        var snapshotIds = await SnapshotIds(newApp);
        var result = await newApp.ModelVersionService.ReconcileModelVersions();

        result.Should().Be(new ModelVersionReconcileResult(0, 1, false, null),
            "all changes are version 0, so the old app applied them the way they were authored");
        (await SnapshotIds(newApp)).Should().Equal(snapshotIds);
        (await newApp.ModelVersionService.ReconcileModelVersions()).Changed.Should().BeFalse();
    }

    [Fact]
    public async Task NewDatabaseStoresTheVersionWithoutReplaying()
    {
        await using var app = ForkDatabase(NewApp);

        var result = await app.ModelVersionService.ReconcileModelVersions();

        result.Should().Be(new ModelVersionReconcileResult(0, 1, false, null));
        (await app.DataModel.GetLocalState<JsonElement?>("harmony:modelVersions")).Should().NotBeNull();
    }

    [Fact]
    public async Task DowngradeRegeneratesAllSnapshots()
    {
        await using var newApp = ForkDatabase(NewApp);
        await newApp.ModelVersionService.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        var firstCommit = await newApp.WriteNextChange(new NewWordChange(wordId, "hello"));
        await newApp.WriteNextChange(new SetWordNoteChange(wordId, "a note"));

        await using var oldApp = newApp.ForkDatabase(OldApp);
        var snapshotIds = await SnapshotIds(oldApp);
        var result = await oldApp.ModelVersionService.ReconcileModelVersions();

        result.Should().Be(new ModelVersionReconcileResult(1, 0, true, result.ReplayedFrom));
        result.ReplayedFrom!.Id.Should().Be(firstCommit.Id);
        await ShouldBeRegenerated(oldApp, snapshotIds);
        (await NoteOf(oldApp, wordId)).Should().Be("a note");
    }

    [Fact]
    public async Task DowngradeToAnAppWithoutAChangeTypeRemovesItsEntities()
    {
        await using var newApp = ForkDatabase(AppWith(versions => versions.Add("Tags", v => v.Change<SetTagChange>())));
        await newApp.ModelVersionService.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        await newApp.WriteNextChange(new NewWordChange(wordId, "hello"));
        await newApp.WriteNextChange(new SetTagChange(tagId, "tag"));

        await using var oldApp = newApp.ForkDatabase(services =>
        {
            OldApp(services);
            services.Configure<HarmonyConfig>(config => config.ChangeTypeListBuilder.Remove<SetTagChange>());
        });
        var snapshotIds = await SnapshotIds(oldApp);
        var result = await oldApp.ModelVersionService.ReconcileModelVersions();

        result.FullRegeneration.Should().BeTrue();
        (await SnapshotIds(oldApp)).Should().NotIntersectWith(snapshotIds, "every snapshot should be recreated");
        (await oldApp.DataModel.GetLatest<Tag>(tagId)).Should().BeNull();
        (await oldApp.DbContext.Set<Tag>().AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        (await oldApp.DataModel.GetLatest<Word>(wordId)).Should().NotBeNull();
    }

    [Fact]
    public async Task ModifiedAppliedVersionThrows()
    {
        await using var newApp = ForkDatabase(NewApp);
        await newApp.ModelVersionService.ReconcileModelVersions();

        await using var otherTypes = newApp.ForkDatabase(AppWith(versions => versions.Add("Notes", v => v.Change<SetTagChange>())));
        var act = () => otherTypes.ModelVersionService.ReconcileModelVersions();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Model version 1 (Notes) was modified*");

        await using var nowMajor = newApp.ForkDatabase(AppWith(versions => versions.AddMajor("Notes", v => v.Change<SetWordNoteChange>())));
        act = () => nowMajor.ModelVersionService.ReconcileModelVersions();
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var renamed = newApp.ForkDatabase(AppWith(versions => versions.Add("Word notes", v => v.Change<SetWordNoteChange>())));
        (await renamed.ModelVersionService.ReconcileModelVersions()).Changed.Should().BeFalse("names are not stored");
    }

    [Fact]
    public async Task DescribeModelVersions()
    {
        await using var app = ForkDatabase(AppWith(versions => versions
            .Add("Notes", v => v.Change<SetWordNoteChange>())
            .AddMajor("Rewrite")
            .Add("Tags and notes", v => v.Change<SetWordNoteChange>().Change<SetTagChange>())));

        await Verify(app.CrdtConfig.DescribeModelVersions());
    }

    [Fact]
    public void ModelVersionWithAnUnregisteredChangeTypeFailsValidation()
    {
        using var services = new ServiceCollection()
            .AddCrdtDataSample(":memory:")
            .Configure<HarmonyConfig>(config =>
            {
                config.ChangeTypeListBuilder.Remove<SetWordNoteChange>();
                config.ModelVersionBuilder.Add("Notes", v => v.Change<SetWordNoteChange>());
            })
            .BuildServiceProvider();

        var act = () => services.GetRequiredService<IOptions<HarmonyConfig>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void ChangeVersionsToReplayAreTheVersionsBeforeTheUpgrade()
    {
        ModelVersion[] versions =
        [
            new("A", false, [NoteChangeType]),
            new("B", false, [nameof(SetTagChange), NoteChangeType]),
            new("C", false, [nameof(SetTagChange)]),
        ];

        ModelVersionService.ChangeVersionsToReplay(1, versions)
            .Should().BeEquivalentTo([(nameof(SetTagChange), 0), (NoteChangeType, 1)]);
        ModelVersionService.ChangeVersionsToReplay(0, versions)
            .Should().BeEquivalentTo([(NoteChangeType, 0), (nameof(SetTagChange), 0)]);
        ModelVersionService.ChangeVersionsToReplay(3, versions).Should().BeEmpty();
    }

    [Fact]
    public async Task FindOldestCommitWithChangeVersionsUsesCommitOrderAndVersion()
    {
        var wordId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(wordId, "hello"));
        var versionZeroNote = await WriteNextChange(new SetWordNoteChange(wordId, "version 0"));
        var laterNote = await SyncFromOtherClient(this, new SetWordNoteChange(wordId, "later"), version: 1);
        var tagCommit = await SyncFromOtherClient(this, new SetTagChange(tagId, "tag"), version: 2);
        //added last, but sorts before the other version 1 note
        var earlierNote = await WriteChangeBefore(laterNote, new SetWordNoteChange(wordId, "earlier"), add: false);
        earlierNote.ChangeEntities.Single().Version = 1;
        await AddCommitsViaSync([earlierNote]);
        await using var repo = CreateRepository();

        (await repo.FindOldestCommitWithChangeVersions([(NoteChangeType, 0)]))!.Id.Should().Be(earlierNote.Id);
        (await repo.FindOldestCommitWithChangeVersions([(NoteChangeType, -1)]))!.Id.Should().Be(versionZeroNote.Id);
        (await repo.FindOldestCommitWithChangeVersions([(NoteChangeType, 1)])).Should().BeNull();
        (await repo.FindOldestCommitWithChangeVersions([(nameof(SetTagChange), 1)]))!.Id.Should().Be(tagCommit.Id);
        (await repo.FindOldestCommitWithChangeVersions([(nameof(SetTagChange), 1), (NoteChangeType, 0)]))!.Id.Should().Be(earlierNote.Id);
        (await repo.FindOldestCommitWithChangeVersions([(nameof(EditExampleChange), 0)])).Should().BeNull();
        (await repo.FindOldestCommitWithChangeVersions([])).Should().BeNull();
    }

    [Fact]
    public async Task DataModelReconcilePassesThroughToTheService()
    {
        await using var app = ForkDatabase(NewApp);

        var result = await app.DataModel.ReconcileModelVersions();

        result.Should().Be(new ModelVersionReconcileResult(0, 1, false, null));
        (await app.ModelVersionService.ReconcileModelVersions()).Changed.Should().BeFalse();
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
}
