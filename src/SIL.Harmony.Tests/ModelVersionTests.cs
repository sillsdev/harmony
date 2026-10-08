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
            config.ModelVersionBuilder.Add(1, "Notes", v => v.Change<SetWordNoteChange>(1)));
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
        var commit = BuildCommit(Guid.NewGuid(), app.NextDate(), [change]);
        commit.ChangeEntities.Single().Version = version;
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
            .Add(1, "Notes", v => v.Change<SetWordNoteChange>(1))
            .Add(2, "Notes and tags", v => v.Change<SetWordNoteChange>(2).Change<SetTagChange>(1))));
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

    /// <summary>the app that added notes was released, but forgot to list them in its model version</summary>
    private static void AppThatForgotNotes(IServiceCollection services)
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config =>
            config.ModelVersionBuilder.Add(1, "Tags", v => v.Change<SetTagChange>(1)));
    }

    private static void AppThatFixedForgottenNotes(IServiceCollection services)
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config => config.ModelVersionBuilder
            .Add(1, "Tags", v => v.Change<SetTagChange>(1))
            .Add(2, "Forgotten notes", v => v.Change<SetWordNoteChange>(1, invalidateFrom: 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgottenTypeIsReplayedByTheVersionThatFixesIt(bool upgradedToTheAppThatForgot)
    {
        await using var oldApp = ForkDatabase(OldAppWithoutNotes);
        await oldApp.ModelVersionService.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        await oldApp.WriteNextChange(new NewWordChange(wordId, "hello"));
        await using var appThatForgot = oldApp.ForkDatabase(AppThatForgotNotes);
        var forgottenVersion = appThatForgot.CrdtConfig.ChangeVersion(NoteChangeType);
        forgottenVersion.Should().Be(0, "the app that forgot notes authors them like the old app did");
        var noteCommit = await SyncFromOtherClient(oldApp, OpaqueNote(oldApp, wordId, "a note"), forgottenVersion);
        (await NoteOf(oldApp, wordId)).Should().BeNull("the old app can't apply the note change");

        var beforeFix = oldApp;
        await using var upgraded = upgradedToTheAppThatForgot ? oldApp.ForkDatabase(AppThatForgotNotes) : null;
        if (upgraded is not null)
        {
            await upgraded.ModelVersionService.ReconcileModelVersions();
            (await NoteOf(upgraded, wordId)).Should().BeNull("the app that forgot notes doesn't know to replay them");
            beforeFix = upgraded;
        }

        await using var fixedApp = beforeFix.ForkDatabase(AppThatFixedForgottenNotes);
        var result = await fixedApp.ModelVersionService.ReconcileModelVersions();

        (result.ReplayedFrom?.Id).Should().Be(noteCommit.Id);
        (await NoteOf(fixedApp, wordId)).Should().Be("a note");
        fixedApp.CrdtConfig.ChangeVersion(NoteChangeType).Should().Be(1, "notes authored after the fix can be told apart");
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

        await using var newApp = ForkDatabase(AppWith(versions => versions.AddMajor(1, "Rewrite")));
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
        await using var newApp = ForkDatabase(AppWith(versions => versions.Add(1, "Tags", v => v.Change<SetTagChange>(1))));
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

        await using var otherTypes = newApp.ForkDatabase(AppWith(versions => versions.Add(1, "Notes", v => v.Change<SetTagChange>(1))));
        var act = () => otherTypes.ModelVersionService.ReconcileModelVersions();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Model version 1 (Notes) was modified*");

        await using var nowMajor = newApp.ForkDatabase(AppWith(versions => versions.AddMajor(1, "Notes", v => v.Change<SetWordNoteChange>(1))));
        act = () => nowMajor.ModelVersionService.ReconcileModelVersions();
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var nowInvalidates = newApp.ForkDatabase(AppWith(versions => versions.Add(1, "Notes", v => v.Change<SetWordNoteChange>(1, invalidateFrom: 0))));
        act = () => nowInvalidates.ModelVersionService.ReconcileModelVersions();
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var renamed = newApp.ForkDatabase(AppWith(versions => versions.Add(1, "Word notes", v => v.Change<SetWordNoteChange>(1))));
        (await renamed.ModelVersionService.ReconcileModelVersions()).Changed.Should().BeFalse("names are not stored");
    }

    [Fact]
    public async Task DescribeModelVersions()
    {
        await using var app = ForkDatabase(AppWith(versions => versions
            .Add(1, "Notes", v => v.Change<SetWordNoteChange>(1))
            .AddMajor(2, "Rewrite")
            .Add(3, "Tags and notes", v => v.Change<SetWordNoteChange>(2).Change<SetTagChange>(1))
            .Add(4, "Fix tags", v => v.Change<SetTagChange>(2, invalidateFrom: 1).Change<SetWordNoteChange>(3))));

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
                config.ModelVersionBuilder.Add(1, "Notes", v => v.Change<SetWordNoteChange>(1));
            })
            .BuildServiceProvider();

        var act = () => services.GetRequiredService<IOptions<HarmonyConfig>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void ChangeVersionsToReplayStartAtTheFirstNewVersion()
    {
        var versions = new ModelVersionBuilder()
            .Add(1, "A", v => v.Change<SetWordNoteChange>(1))
            .Add(2, "B", v => v.Change<SetTagChange>(1).Change<SetWordNoteChange>(2))
            .Add(3, "C", v => v.Change<SetTagChange>(2))
            .Versions;

        ModelVersionService.ChangeVersionsToReplay(1, versions)
            .Should().BeEquivalentTo([(nameof(SetTagChange), 1), (NoteChangeType, 2)]);
        ModelVersionService.ChangeVersionsToReplay(0, versions)
            .Should().BeEquivalentTo([(NoteChangeType, 1), (nameof(SetTagChange), 1)]);
        ModelVersionService.ChangeVersionsToReplay(3, versions).Should().BeEmpty();
    }

    [Fact]
    public void ChangeVersionsToReplayStartAtTheInvalidatedVersion()
    {
        var versions = new ModelVersionBuilder()
            .Add(1, "A", v => v.Change<SetWordNoteChange>(1))
            //forgot to list tags
            .Add(2, "B", v => v.Change<SetWordNoteChange>(2))
            .Add(3, "C", v => v.Change<SetTagChange>(1, invalidateFrom: 0).Change<SetWordNoteChange>(3))
            .Add(4, "D", v => v.Change<SetTagChange>(2))
            .Versions;

        ModelVersionService.ChangeVersionsToReplay(0, versions)
            .Should().BeEquivalentTo([(NoteChangeType, 1), (nameof(SetTagChange), 0)]);
        ModelVersionService.ChangeVersionsToReplay(2, versions)
            .Should().BeEquivalentTo([(NoteChangeType, 3), (nameof(SetTagChange), 0)],
                "the apps with versions A and B authored tags with version 0");
        ModelVersionService.ChangeVersionsToReplay(3, versions)
            .Should().BeEquivalentTo([(nameof(SetTagChange), 2)], "the fix was already applied");
    }

    [Fact]
    public void ModelVersionsMustBeNumberedInOrder()
    {
        var builder = new ModelVersionBuilder().Add(1, "A", v => v.Change<SetWordNoteChange>(1));

        builder.Invoking(b => b.Add(3, "C", _ => { })).Should().Throw<ArgumentOutOfRangeException>();
        builder.Invoking(b => b.AddMajor(1, "A again")).Should().Throw<ArgumentOutOfRangeException>();
        builder.Versions.Should().ContainSingle();
    }

    [Fact]
    public void ChangeVersionsMustBeValid()
    {
        new ModelVersionBuilder().Invoking(b => b.Add(1, "A", v => v.Change<SetWordNoteChange>(2)))
            .Should().Throw<ArgumentException>().WithMessage("*must be 1, one more than its previous version 0");
        var builder = new ModelVersionBuilder().Add(1, "A", v => v.Change<SetWordNoteChange>(1).Change<SetTagChange>(1));

        builder.Invoking(b => b.Add(2, "B", v => v.Change<SetWordNoteChange>(1)))
            .Should().Throw<ArgumentException>().WithMessage("*must be 2, one more than its previous version 1");
        builder.Invoking(b => b.Add(2, "B", v => v.Change<SetWordNoteChange>(3)))
            .Should().Throw<ArgumentException>().WithMessage("*must be 2, one more than its previous version 1");
        builder.Invoking(b => b.Add(2, "B", v => v.Change<SetTagChange>(0))).Should().Throw<ArgumentOutOfRangeException>();
        builder.Invoking(b => b.Add(2, "B", v => v.Change<SetTagChange>(2, invalidateFrom: 3))).Should().Throw<ArgumentOutOfRangeException>();
        builder.Invoking(b => b.Add(2, "B", v => v.Change<SetTagChange>(2, invalidateFrom: -1))).Should().Throw<ArgumentOutOfRangeException>();
        builder.Invoking(b => b.Add(2, "B", v => v.Change<SetTagChange>(2).Change<SetTagChange>(2)))
            .Should().Throw<ArgumentException>().WithMessage("*more than once*");
        builder.Versions.Should().ContainSingle("a version that failed validation isn't added");
        builder.Add(2, "B", v => v.Change<SetWordNoteChange>(2)).Versions.Should().HaveCount(2);
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
        var earlierNote = CommitBefore(laterNote, new SetWordNoteChange(wordId, "earlier"));
        earlierNote.ChangeEntities.Single().Version = 1;
        await AddCommitsViaSync([earlierNote]);
        await using var repo = CreateRepository();

        (await repo.FindOldestCommitWithChangeVersions([(NoteChangeType, 1)]))!.Id.Should().Be(earlierNote.Id);
        (await repo.FindOldestCommitWithChangeVersions([(NoteChangeType, 0)]))!.Id.Should().Be(versionZeroNote.Id);
        (await repo.FindOldestCommitWithChangeVersions([(NoteChangeType, 2)])).Should().BeNull();
        (await repo.FindOldestCommitWithChangeVersions([(nameof(SetTagChange), 2)]))!.Id.Should().Be(tagCommit.Id);
        (await repo.FindOldestCommitWithChangeVersions([(nameof(SetTagChange), 2), (NoteChangeType, 1)]))!.Id.Should().Be(earlierNote.Id);
        (await repo.FindOldestCommitWithChangeVersions([(nameof(EditExampleChange), 1)])).Should().BeNull();
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

        //the test base shares one db context between calls, so a removed entry must not stay tracked
        await DataModel.SetLocalState("app:value", new LocalStateTestValue("third", 3));
        (await DataModel.GetLocalState<LocalStateTestValue>("app:value")).Should().Be(new LocalStateTestValue("third", 3));
    }

    private record LocalStateTestValue(string Text, int Number);
}
