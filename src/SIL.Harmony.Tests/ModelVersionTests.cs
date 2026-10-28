using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// The database starts out written by an "old app" that only has the sample's model version 0,
/// and is then opened by a "new app" with more model versions, by forking it with a different config.
/// </summary>
public class ModelVersionTests() : DataModelTestBase(configure: OldApp)
{
    private const string NoteChangeType = nameof(SetWordNoteChange);

    /// <summary>knows <see cref="SetWordNoteChange"/>, but applies it the old way</summary>
    private static void OldApp(IServiceCollection services)
    {
        services.Configure<HarmonyConfig>(config => config.UnknownChangeHandling = UnknownChangeHandling.Fallback);
    }

    /// <summary>replaces the sample's versions with a version 0 that lists every registered change type except <paramref name="newTypes"/></summary>
    private static void VersionZeroWithout(HarmonyConfig config, params string[] newTypes)
    {
        config.ModelVersionBuilder.Clear();
        config.ModelVersionBuilder.Add(0, "Before model versions", v =>
        {
            v.RemoteResourceChanges<MediaMetadata>();
            var others = config.ChangeTypes.Select(ct => ct.Discriminator)
                .Except(v.Changes.Keys) // exclude the resource change types added above
                .Except(newTypes);
            foreach (var discriminator in others) v.Change(discriminator);
        });
    }

    /// <summary>an old app that doesn't know <typeparamref name="T"/> at all</summary>
    private static void WithoutChangeType<T>(IServiceCollection services) where T : IChange, IPolyType
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config =>
        {
            config.ChangeTypeListBuilder.Remove<T>();
            VersionZeroWithout(config);
        });
    }

    /// <summary>the app that adds <typeparamref name="T"/> in version 1</summary>
    private static void AddingChangeType<T>(IServiceCollection services) where T : IChange, IPolyType
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config =>
        {
            VersionZeroWithout(config, T.TypeName);
            config.ModelVersionBuilder.Add(1, "Added " + T.TypeName, v => v.Change<T>());
        });
    }

    /// <summary>doesn't know <see cref="SetWordNoteChange"/> at all</summary>
    private static void OldAppWithoutNotes(IServiceCollection services) => WithoutChangeType<SetWordNoteChange>(services);

    /// <summary>adds <see cref="SetWordNoteChange"/>, which <see cref="OldAppWithoutNotes"/> doesn't know</summary>
    private static void NewAppAddingNotes(IServiceCollection services) => AddingChangeType<SetWordNoteChange>(services);

    /// <summary>changes how <see cref="SetWordNoteChange"/> applies</summary>
    private static void NewApp(IServiceCollection services)
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config =>
            config.ModelVersionBuilder.Add(1, "Notes", v => v.Change<SetWordNoteChange>()));
    }

    /// <summary>the sample's version 0, then <paramref name="versions"/></summary>
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

    /// <summary>a commit authored by another client, with the model version that client's app authored it with</summary>
    private static Task<Commit> SyncFromOtherClient(DataModelTestBase app, IChange change, int version) =>
        SyncFromOtherClient(app, [change], version);

    private static async Task<Commit> SyncFromOtherClient(DataModelTestBase app, IChange[] changes, int version)
    {
        var commit = BuildCommit(Guid.NewGuid(), app.NextDate(), changes);
        foreach (var changeEntity in commit.ChangeEntities) changeEntity.AuthoredModelVersion = version;
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

    private static async Task<int[]> AuthoredModelVersions(DataModelTestBase app, Commit commit)
    {
        return await app.DbContext.Set<ChangeEntity<IChange>>().AsNoTracking()
            .Where(ce => ce.CommitId == commit.Id)
            .OrderBy(ce => ce.Index)
            .Select(ce => ce.AuthoredModelVersion)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AuthoredChangesGetTheLatestModelVersion()
    {
        await using var app = ForkDatabase(AppWith(versions => versions
            .Add(1, "Notes", v => v.Change<SetWordNoteChange>())
            .Add(2, "Notes and tags", v => v.Change<SetWordNoteChange>().Change<SetTagChange>())));
        var wordId = Guid.NewGuid();

        var commit = await app.WriteNextChange([
            new NewWordChange(wordId, "hello"),
            new SetTagChange(Guid.NewGuid(), "tag"),
            new SetWordNoteChange(wordId, "note")
        ]);

        commit.ChangeEntities.Select(ce => ce.AuthoredModelVersion).Should().Equal([2, 2, 2],
            "every change gets the latest version, even NewWordChange, which no later version lists");
        (await AuthoredModelVersions(app, commit)).Should().Equal(2, 2, 2);
        var json = JsonSerializer.Serialize(commit, app.CrdtConfig.JsonSerializerOptions);
        JsonSerializer.Deserialize<Commit>(json, app.CrdtConfig.JsonSerializerOptions)!
            .ChangeEntities.Select(ce => ce.AuthoredModelVersion).Should().Equal(2, 2, 2);
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

        await using var newApp = oldApp.ForkDatabase(NewAppAddingNotes);
        var result = await newApp.ModelVersionService.ReconcileModelVersions();

        result.Should().Be(new ModelVersionReconcileResult(0, 1, false, result.OldestInvalidatedCommit));
        result.OldestInvalidatedCommit!.Id.Should().Be(noteCommit.Id);
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
        (await AuthoredModelVersions(this, oldNote)).Should().Equal(0);
        (await NoteOf(this, wordId)).Should().Be("newer note", "the old app applies a newer app's change with its own code");

        await using var newApp = ForkDatabase(NewApp);
        var result = await newApp.ModelVersionService.ReconcileModelVersions();

        result.OldestInvalidatedCommit!.Id.Should().Be(newerNote.Id,
            "the old note was authored and applied by the old app, so it doesn't need a replay");
        result.FullRegeneration.Should().BeFalse();
        (await NoteOf(newApp, wordId)).Should().Be("newer note");
    }

    /// <summary>the app that changed how notes apply was released, but forgot to list them in its model version</summary>
    private static void AppThatForgotNotes(IServiceCollection services)
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config =>
            config.ModelVersionBuilder.Add(1, "Tags", v => v.Change<SetTagChange>()));
    }

    private static void AppThatFixedForgottenNotes(IServiceCollection services)
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config => config.ModelVersionBuilder
            .Add(1, "Tags", v => v.Change<SetTagChange>())
            .Add(2, "Forgotten notes", v => v.Change<SetWordNoteChange>(invalidateFrom: 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgottenTypeIsReplayedByTheVersionThatFixesIt(bool upgradedToTheAppThatForgot)
    {
        await DataModel.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(wordId, "hello"));
        var oldNote = await WriteNextChange(new SetWordNoteChange(wordId, "old note"));
        (await AuthoredModelVersions(this, oldNote)).Should().Equal(0);
        //the app that forgot notes authors them with its own version, 1, though it doesn't list them
        var forgottenNote = await SyncFromOtherClient(this, new SetWordNoteChange(wordId, "a note"), version: 1);

        var beforeFix = (DataModelTestBase)this;
        await using var upgraded = upgradedToTheAppThatForgot ? ForkDatabase(AppThatForgotNotes) : null;
        if (upgraded is not null)
        {
            var forgotResult = await upgraded.ModelVersionService.ReconcileModelVersions();
            forgotResult.OldestInvalidatedCommit.Should().BeNull("the app that forgot notes doesn't know to replay them");
            beforeFix = upgraded;
        }

        await using var fixedApp = beforeFix.ForkDatabase(AppThatFixedForgottenNotes);
        var result = await fixedApp.ModelVersionService.ReconcileModelVersions();

        (result.OldestInvalidatedCommit?.Id).Should().Be(forgottenNote.Id,
            "the old app authored the old note and applied it the old way, so only notes from the app that forgot are replayed");
        result.FullRegeneration.Should().BeFalse();
        (await NoteOf(fixedApp, wordId)).Should().Be("a note");
    }

    /// <summary>the old code for <see cref="SetWordNoteChange"/>, which can't apply it, like a patch of a property the old model doesn't have</summary>
    public class NoteChangeTheOldAppCantApply(Guid entityId, string note) : EditChange<Word>(entityId), IPolyType
    {
        public static string TypeName => nameof(SetWordNoteChange);
        public string Note { get; } = note;

        public override ValueTask ApplyChange(Word entity, IChangeContext context) =>
            throw new InvalidOperationException("Word has no note");
    }

    private static void OldAppThatCantApplyNotes(IServiceCollection services)
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config =>
        {
            config.ChangeTypeListBuilder.Remove<SetWordNoteChange>();
            config.ChangeTypeListBuilder.Add<NoteChangeTheOldAppCantApply>();
            VersionZeroWithout(config);
        });
    }

    private static void AppWithNotesThatApply(IServiceCollection services)
    {
        OldApp(services);
        services.Configure<HarmonyConfig>(config =>
        {
            VersionZeroWithout(config);
            config.ModelVersionBuilder.Add(1, "Notes", v => v.Change<SetWordNoteChange>());
        });
    }

    private static void AppWithNotesThatStillThrow(IServiceCollection services)
    {
        OldAppThatCantApplyNotes(services);
        services.Configure<HarmonyConfig>(config =>
            config.ModelVersionBuilder.Add(1, "Notes", v => v.Change<NoteChangeTheOldAppCantApply>()));
    }

    private async Task<(DataModelTestBase oldApp, Guid wordId)> OldAppThatSkippedANote()
    {
        var oldApp = ForkDatabase(OldAppThatCantApplyNotes);
        await oldApp.ModelVersionService.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        await oldApp.WriteNextChange(new NewWordChange(wordId, "hello"));
        await SyncFromOtherClient(oldApp,
            [new NoteChangeTheOldAppCantApply(wordId, "a note"), new SetWordTextChange(wordId, "renamed")], version: 1);
        return (oldApp, wordId);
    }

    [Fact]
    public async Task ChangeFromANewerVersionThatThrowsIsSkipped()
    {
        var (oldApp, wordId) = await OldAppThatSkippedANote();
        await using var _ = oldApp;

        var word = await oldApp.DataModel.GetLatest<Word>(wordId);
        word!.Note.Should().BeNull();
        word.Text.Should().Be("renamed", "the rest of the commit still applies");
    }

    [Fact]
    public async Task ChangeFromTheCurrentVersionThatThrowsFailsTheSync()
    {
        await using var oldApp = ForkDatabase(OldAppThatCantApplyNotes);
        await oldApp.ModelVersionService.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        await oldApp.WriteNextChange(new NewWordChange(wordId, "hello"));

        var sync = () => SyncFromOtherClient(oldApp, new NoteChangeTheOldAppCantApply(wordId, "a note"), version: 0);

        await sync.Should().ThrowAsync<InvalidOperationException>().WithMessage("Word has no note");
    }

    [Fact]
    public async Task SkippedChangeIsAppliedAfterUpgrade()
    {
        var (oldApp, wordId) = await OldAppThatSkippedANote();
        await using var _ = oldApp;

        await using var newApp = oldApp.ForkDatabase(AppWithNotesThatApply);
        await newApp.ModelVersionService.ReconcileModelVersions();

        (await NoteOf(newApp, wordId)).Should().Be("a note");
    }

    [Fact]
    public async Task SkippedChangeThatStillThrowsAfterUpgradeFailsTheReconcile()
    {
        var (oldApp, wordId) = await OldAppThatSkippedANote();
        await using var _ = oldApp;

        await using var newApp = oldApp.ForkDatabase(AppWithNotesThatStillThrow);
        var reconcile = () => newApp.ModelVersionService.ReconcileModelVersions();

        await reconcile.Should().ThrowAsync<InvalidOperationException>().WithMessage("Word has no note");
        (await newApp.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("renamed", "the failed reconcile rolls back");
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
            NewAppAddingNotes(services);
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

        result.OldestInvalidatedCommit!.Id.Should().Be(noteCommit.Id);
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
        result.OldestInvalidatedCommit!.Id.Should().Be(firstCommit.Id);
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
    public async Task VersionZeroIsStoredSoItCantBeModifiedLater()
    {
        var result = await ModelVersionService.ReconcileModelVersions();

        result.Should().Be(new ModelVersionReconcileResult(0, 0, false, null));
        (await DataModel.GetLocalState<JsonElement?>("harmony:modelVersions")).Should().NotBeNull();
        await using var withoutTags = ForkDatabase(WithoutChangeType<SetTagChange>);
        var act = () => withoutTags.ModelVersionService.ReconcileModelVersions();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Model version 0 (Before model versions) was modified*");
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

        result.Should().Be(new ModelVersionReconcileResult(1, 0, true, result.OldestInvalidatedCommit));
        result.OldestInvalidatedCommit!.Id.Should().Be(firstCommit.Id);
        await ShouldBeRegenerated(oldApp, snapshotIds);
        (await NoteOf(oldApp, wordId)).Should().Be("a note");
    }

    [Fact]
    public async Task DowngradeToAnAppWithoutAChangeTypeRemovesItsEntities()
    {
        await using var newApp = ForkDatabase(AddingChangeType<SetTagChange>);
        await newApp.ModelVersionService.ReconcileModelVersions();
        var wordId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        await newApp.WriteNextChange(new NewWordChange(wordId, "hello"));
        await newApp.WriteNextChange(new SetTagChange(tagId, "tag"));

        await using var oldApp = newApp.ForkDatabase(WithoutChangeType<SetTagChange>);
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

        await using var otherTypes = newApp.ForkDatabase(AppWith(versions => versions.Add(1, "Notes", v => v.Change<SetTagChange>())));
        var act = () => otherTypes.ModelVersionService.ReconcileModelVersions();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Model version 1 (Notes) was modified*");

        await using var nowMajor = newApp.ForkDatabase(AppWith(versions => versions.AddMajor(1, "Notes", v => v.Change<SetWordNoteChange>())));
        act = () => nowMajor.ModelVersionService.ReconcileModelVersions();
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var nowInvalidates = newApp.ForkDatabase(AppWith(versions => versions.Add(1, "Notes", v => v.Change<SetWordNoteChange>(invalidateFrom: 0))));
        act = () => nowInvalidates.ModelVersionService.ReconcileModelVersions();
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var renamed = newApp.ForkDatabase(AppWith(versions => versions.Add(1, "Word notes", v => v.Change<SetWordNoteChange>())));
        (await renamed.ModelVersionService.ReconcileModelVersions()).Changed.Should().BeFalse("names are not stored");
    }

    [Fact]
    public async Task DescribeModelVersions()
    {
        await using var app = ForkDatabase(AppWith(versions => versions
            .Add(1, "Notes", v => v.Change<SetWordNoteChange>())
            .AddMajor(2, "Rewrite")
            .Add(3, "Tags and notes", v => v.Change<SetWordNoteChange>().Change<SetTagChange>())
            .Add(4, "Fix tags", v => v.Change<SetTagChange>(invalidateFrom: 1).Change<SetWordNoteChange>())));

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
                config.ModelVersionBuilder.Add(1, "Notes", v => v.Change<SetWordNoteChange>());
            })
            .BuildServiceProvider();

        var act = () => services.GetRequiredService<IOptions<HarmonyConfig>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void NoModelVersionsFailsValidation()
    {
        using var services = new ServiceCollection()
            .AddCrdtDataSample(":memory:")
            .Configure<HarmonyConfig>(config => config.ModelVersionBuilder.Clear())
            .BuildServiceProvider();

        var act = () => services.GetRequiredService<IOptions<HarmonyConfig>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("Declare model versions*");
    }

    [Fact]
    public void ChangeTypeNotListedByAModelVersionFailsValidation()
    {
        using var services = new ServiceCollection()
            .AddCrdtDataSample(":memory:")
            .Configure<HarmonyConfig>(config => VersionZeroWithout(config, NoteChangeType, nameof(SetTagChange)))
            .BuildServiceProvider();

        var act = () => services.GetRequiredService<IOptions<HarmonyConfig>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage($"*aren't listed by a model version: {nameof(SetTagChange)}, {NoteChangeType}.*");
    }

    [Fact]
    public void ModelVersionsToInvalidateStartAtTheFirstNewVersion()
    {
        var versions = new ModelVersionBuilder()
            .Add(0, "Before", v => v.Change<SetWordNoteChange>().Change<SetTagChange>())
            .Add(1, "A", v => v.Change<SetWordNoteChange>())
            .Add(2, "B", v => v.Change<SetTagChange>().Change<SetWordNoteChange>())
            .Add(3, "C", v => v.Change<SetTagChange>())
            .Versions;

        ModelVersionService.ModelVersionsToInvalidate(1, versions)
            .Should().BeEquivalentTo([(nameof(SetTagChange), 2), (nameof(SetWordNoteChange), 2)]);
        ModelVersionService.ModelVersionsToInvalidate(0, versions)
            .Should().BeEquivalentTo([(nameof(SetWordNoteChange), 1), (nameof(SetTagChange), 2)],
                "version 0 is the state before model versions, so its changes were applied as authored");
        ModelVersionService.ModelVersionsToInvalidate(3, versions).Should().BeEmpty();
    }

    [Fact]
    public void ModelVersionsToInvalidateStartAtTheInvalidatedVersion()
    {
        var versions = new ModelVersionBuilder()
            .Add(0, "Before", v => v.Change<SetWordNoteChange>().Change<SetTagChange>())
            .Add(1, "A", v => v.Change<SetWordNoteChange>())
            //changed how tags apply, but forgot to list them
            .Add(2, "B", v => v.Change<SetWordNoteChange>())
            .Add(3, "C", v => v.Change<SetTagChange>(invalidateFrom: 2).Change<SetWordNoteChange>())
            .Add(4, "D", v => v.Change<SetTagChange>())
            .Versions;

        ModelVersionService.ModelVersionsToInvalidate(0, versions)
            .Should().BeEquivalentTo([(nameof(SetWordNoteChange), 1), (nameof(SetTagChange), 2)],
                "a database at 0 applied version 0 notes as authored, and C replays the tags B's apps authored because B forgot them");
        ModelVersionService.ModelVersionsToInvalidate(2, versions)
            .Should().BeEquivalentTo([(nameof(SetWordNoteChange), 3), (nameof(SetTagChange), 2)],
                "B's apps authored tags with version 2, tags from earlier apps still apply the way they did");
        ModelVersionService.ModelVersionsToInvalidate(3, versions)
            .Should().BeEquivalentTo([(nameof(SetTagChange), 4)], "the fix was already applied");
    }

    [Fact]
    public void ModelVersionsMustBeNumberedInOrderFromZero()
    {
        new ModelVersionBuilder().Invoking(b => b.Add(1, "A", _ => { })).Should().Throw<ArgumentOutOfRangeException>();
        new ModelVersionBuilder().Invoking(b => b.AddMajor(0, "Before"))
            .Should().Throw<ArgumentException>().WithMessage("Model version 0 (Before) can't be major*");
        var builder = new ModelVersionBuilder().Add(0, "Before", v => v.Change<SetWordNoteChange>());

        builder.Invoking(b => b.Add(2, "B", _ => { })).Should().Throw<ArgumentOutOfRangeException>();
        builder.Invoking(b => b.AddMajor(0, "Before again")).Should().Throw<ArgumentException>();
        builder.Versions.Should().ContainSingle();
    }

    [Fact]
    public void InvalidateFromMustBeThisVersionOrAnEarlierOne()
    {
        var builder = new ModelVersionBuilder().Add(0, "Before", v => v.Change<SetWordNoteChange>().Change<SetTagChange>());

        builder.Invoking(b => b.Add(1, "A", v => v.Change<SetTagChange>(invalidateFrom: 2))).Should().Throw<ArgumentOutOfRangeException>();
        builder.Invoking(b => b.Add(1, "A", v => v.Change<SetTagChange>(invalidateFrom: -1))).Should().Throw<ArgumentOutOfRangeException>();
        builder.Invoking(b => b.Add(1, "A", v => v.Change<SetTagChange>().Change<SetTagChange>()))
            .Should().Throw<ArgumentException>().WithMessage("*more than once*");
        builder.Versions.Should().ContainSingle("a version that failed validation isn't added");
        builder.Add(1, "A", v => v.Change<SetTagChange>(invalidateFrom: 0).Change<SetWordNoteChange>(invalidateFrom: 1).Change<EditExampleChange>());
        builder.Versions[1].Changes.Should().Equal(
            new ModelVersionChange(nameof(EditExampleChange), InvalidateFrom: 1),
            new ModelVersionChange(nameof(SetTagChange), InvalidateFrom: 0),
            new ModelVersionChange(nameof(SetWordNoteChange), InvalidateFrom: 1));
    }

    [Fact]
    public void RemoteResourceChangesMustBeListed()
    {
        using var services = new ServiceCollection()
            .AddCrdtDataSample(":memory:")
            .Configure<HarmonyConfig>(config =>
            {
                config.ModelVersionBuilder.Clear();
                config.ModelVersionBuilder.Add(0, "Before model versions", v =>
                {
                    foreach (var changeType in config.ChangeTypes) v.Change(changeType.Discriminator);
                });
            })
            .BuildServiceProvider();

        var act = () => services.GetRequiredService<IOptions<HarmonyConfig>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("Call RemoteResourceChanges<MediaMetadata> in a model version*");
    }

    [Fact]
    public async Task FindOldestCommitWithModelVersionsUsesCommitOrderAndVersion()
    {
        var wordId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(wordId, "hello"));
        var versionZeroNote = await WriteNextChange(new SetWordNoteChange(wordId, "version 0"));
        var laterNote = await SyncFromOtherClient(this, new SetWordNoteChange(wordId, "later"), version: 1);
        var tagCommit = await SyncFromOtherClient(this, new SetTagChange(tagId, "tag"), version: 2);
        //added last, but sorts before the other version 1 note
        var earlierNote = CommitBefore(laterNote, new SetWordNoteChange(wordId, "earlier"));
        earlierNote.ChangeEntities.Single().AuthoredModelVersion = 1;
        await AddCommitsViaSync([earlierNote]);
        await using var repo = CreateRepository();

        (await repo.FindOldestCommitWithModelVersions([(NoteChangeType, 1)]))!.Id.Should().Be(earlierNote.Id);
        (await repo.FindOldestCommitWithModelVersions([(NoteChangeType, 0)]))!.Id.Should().Be(versionZeroNote.Id);
        (await repo.FindOldestCommitWithModelVersions([(NoteChangeType, 2)])).Should().BeNull();
        (await repo.FindOldestCommitWithModelVersions([(nameof(SetTagChange), 2)]))!.Id.Should().Be(tagCommit.Id);
        (await repo.FindOldestCommitWithModelVersions([(nameof(SetTagChange), 2), (NoteChangeType, 1)]))!.Id.Should().Be(earlierNote.Id);
        (await repo.FindOldestCommitWithModelVersions([(nameof(EditExampleChange), 1)])).Should().BeNull();
        (await repo.FindOldestCommitWithModelVersions([])).Should().BeNull();
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
