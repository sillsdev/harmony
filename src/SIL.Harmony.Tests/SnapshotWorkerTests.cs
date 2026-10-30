using System.Text.Json;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;
using SIL.Harmony.Tests.Mocks;

namespace SIL.Harmony.Tests;

/// <summary>drives <see cref="SnapshotWorker"/> directly, over an in-memory baseline, so no snapshot is ever persisted</summary>
public class SnapshotWorkerTests : CommitTestBase
{
    private FakeSnapshotView Baseline(params IObjectBase[] entities)
    {
        var baselineCommit = NextCommit();
        return new FakeSnapshotView([.. entities.Select(e => new ObjectSnapshot(e, baselineCommit, isRoot: true))]);
    }

    private static SnapshotReplay Replay(HarmonyConfig config) => new(Options.Create(config));

    private static Word WordFrom(ObjectSnapshot snapshot) => (Word)snapshot.Entity.DbObject;

    [Fact]
    public async Task AFirstChangeMakesARootSnapshot()
    {
        var wordId = Guid.NewGuid();
        var commit = NextCommit(new NewWordChange(wordId, "hello"));

        var (snapshots, _) = await Replay(SampleConfig).ComputeNewSnapshotsAndCheckpoints(EmptySnapshotView.Instance, [commit]);

        var snapshot = snapshots.Should().ContainSingle().Subject;
        snapshot.EntityId.Should().Be(wordId);
        snapshot.IsRoot.Should().BeTrue();
        snapshot.CommitId.Should().Be(commit.Id);
        WordFrom(snapshot).Text.Should().Be("hello");
    }

    [Fact]
    public async Task TwoChangesToOneEntityInOneCommitKeepOnlyTheLast()
    {
        var wordId = Guid.NewGuid();
        var commit = NextCommit(new NewWordChange(wordId, "hello"), SetWord(wordId, "goodbye"));

        var (snapshots, _) = await Replay(SampleConfig).ComputeNewSnapshotsAndCheckpoints(EmptySnapshotView.Instance, [commit]);

        var snapshot = snapshots.Should().ContainSingle().Subject;
        snapshot.IsRoot.Should().BeTrue("the root was replaced within its own commit, so the replacement is the root");
        WordFrom(snapshot).Text.Should().Be("goodbye");
    }

    [Fact]
    public async Task AnEditOnTopOfTheBaselineIsNotRoot()
    {
        var word = new Word { Id = Guid.NewGuid(), Text = "hello" };
        var baseline = Baseline(word);
        var edit = NextCommit(SetWord(word.Id, "goodbye"));

        var (snapshots, _) = await Replay(SampleConfig).ComputeNewSnapshotsAndCheckpoints(baseline, [edit]);

        var snapshot = snapshots.Should().ContainSingle().Subject;
        snapshot.IsRoot.Should().BeFalse();
        snapshot.CommitId.Should().Be(edit.Id);
        WordFrom(snapshot).Text.Should().Be("goodbye");
        var baselineSnapshot = (await baseline.GetAsync(word.Id))!;
        baselineSnapshot.IsRoot.Should().BeTrue();
        WordFrom(baselineSnapshot).Text.Should().Be("hello", "the baseline is never mutated");
    }

    [Fact]
    public async Task ASupersededSnapshotIsKeptOnlyWhenARequiredCheckpointFallsInItsCoverage()
    {
        var config = new HarmonyConfig { MaxChangesBetweenSnapshotCheckpoints = 2 };
        CrdtSampleKernel.ConfigureSample(config);
        var wordId = Guid.NewGuid();
        var create = NextCommit(new NewWordChange(wordId, "0"));
        var edit1 = NextCommit(SetWord(wordId, "1"));
        var edit2 = NextCommit(SetWord(wordId, "2"));
        var lastEdit = NextCommit(SetWord(wordId, "3"));

        var (snapshots, flags) = await Replay(config).ComputeNewSnapshotsAndCheckpoints(
            EmptySnapshotView.Instance, [create, edit1, edit2, lastEdit]);

        //the running total reaches 2 at edit1 and 4 at lastEdit, so those are required checkpoints and the snapshots covering them stay
        snapshots.Select(s => s.CommitId).Should().BeEquivalentTo([create.Id, edit1.Id, lastEdit.Id],
            "the root is always kept, edit1 is a required checkpoint, edit2's snapshot covers no required checkpoint, and lastEdit's is the newest so nothing supersedes it");
        flags.Should().Equal(
            new CheckpointFlag(create, IsCheckpoint: true),
            new CheckpointFlag(edit1, IsCheckpoint: true),
            new CheckpointFlag(edit2, IsCheckpoint: false),
            new CheckpointFlag(lastEdit, IsCheckpoint: true));
    }

    [Fact]
    public async Task DeletingAnEntityDropsReferencesToItFromBaselineEntities()
    {
        var antonym = new Word { Id = Guid.NewGuid(), Text = "hot" };
        var word = new Word { Id = Guid.NewGuid(), Text = "cold", AntonymId = antonym.Id };
        var baseline = Baseline(antonym, word);
        var delete = NextCommit(DeleteWord(antonym.Id));

        var (snapshots, _) = await Replay(SampleConfig).ComputeNewSnapshotsAndCheckpoints(baseline, [delete]);

        //one change, two snapshots: the delete cascades to the word that referenced the antonym
        snapshots.Should().HaveCount(2);
        snapshots.Single(s => s.EntityId == antonym.Id).EntityIsDeleted.Should().BeTrue();
        var wordSnapshot = snapshots.Single(s => s.EntityId == word.Id);
        wordSnapshot.IsRoot.Should().BeFalse();
        wordSnapshot.CommitId.Should().Be(delete.Id);
        WordFrom(wordSnapshot).AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task DeletingAnEntityCascadesThroughReferrersThatRemovingTheReferenceDeletes()
    {
        var word = new Word { Id = Guid.NewGuid(), Text = "hello" };
        var definition = new Definition { Id = Guid.NewGuid(), Text = "a greeting", Order = 0, PartOfSpeech = "noun", WordId = word.Id };
        var example = new Example { Id = Guid.NewGuid(), DefinitionId = definition.Id };
        var baseline = Baseline(word, definition, example);
        var delete = NextCommit(DeleteWord(word.Id));

        var (snapshots, _) = await Replay(SampleConfig).ComputeNewSnapshotsAndCheckpoints(baseline, [delete]);

        //the example only references the definition, so it's deleted by the recursive MarkDeleted
        snapshots.Should().HaveCount(3).And.OnlyContain(s => s.EntityIsDeleted && s.CommitId == delete.Id);
        snapshots.Select(s => s.EntityId).Should().BeEquivalentTo([word.Id, definition.Id, example.Id]);
    }

    [Fact]
    public async Task ADeletedEntityIsRevivedByACreateChange()
    {
        var word = new Word { Id = Guid.NewGuid(), Text = "hello", DeletedAt = DateTimeOffset.UtcNow };
        var baseline = Baseline(word);
        var revive = NextCommit(new NewWordChange(word.Id, "hello again"));

        var (snapshots, _) = await Replay(SampleConfig).ComputeNewSnapshotsAndCheckpoints(baseline, [revive]);

        var snapshot = snapshots.Should().ContainSingle().Subject;
        snapshot.EntityIsDeleted.Should().BeFalse();
        snapshot.IsRoot.Should().BeFalse("the deleted snapshot is still the entity's root");
        WordFrom(snapshot).Text.Should().Be("hello again");
    }

    [Fact]
    public async Task AnOpaqueChangeForAnUnknownEntityIsSkipped()
    {
        var opaque = new OpaqueChange { TypeName = "not-a-known-change", RawJson = JsonDocument.Parse("{}").RootElement, EntityId = Guid.NewGuid() };
        var commit = NextCommit(opaque);

        var (snapshots, _) = await Replay(SampleConfig).ComputeNewSnapshotsAndCheckpoints(EmptySnapshotView.Instance, [commit]);

        snapshots.Should().BeEmpty("an entity this client can't create stays absent until it understands the change");
    }

    [Fact]
    public async Task WhereReturnsTheReplayedSnapshotOnceAndExcludesWhatThePredicateRejects()
    {
        var edited = new Word { Id = Guid.NewGuid(), Text = "old" };
        var untouched = new Word { Id = Guid.NewGuid(), Text = "same" };
        var baseline = Baseline(edited, untouched);
        var edit = NextCommit(SetWord(edited.Id, "new"));

        var view = await Replay(SampleConfig).ReplayCommits(baseline, [edit]);

        var matches = await view.Where(s => s.EntityId == edited.Id).ToArrayAsync(TestContext.Current.CancellationToken);
        var match = matches.Should().ContainSingle("the baseline copy of the edited word is shadowed and the untouched word is excluded by the predicate").Subject;
        WordFrom(match).Text.Should().Be("new");
    }

    [Fact]
    public async Task AllReturnsEveryEntityOnceWithReplayedSnapshotsShadowingTheBaseline()
    {
        var edited = new Word { Id = Guid.NewGuid(), Text = "old" };
        var untouched = new Word { Id = Guid.NewGuid(), Text = "same" };
        var baseline = Baseline(edited, untouched);
        var edit = NextCommit(SetWord(edited.Id, "new"));

        var view = await Replay(SampleConfig).ReplayCommits(baseline, [edit]);

        var all = await view.All().ToArrayAsync(TestContext.Current.CancellationToken);
        all.Should().HaveCount(2, "the edited word appears once, not once per layer");
        WordFrom(all.Single(s => s.EntityId == edited.Id)).Text.Should().Be("new");
        WordFrom(all.Single(s => s.EntityId == untouched.Id)).Text.Should().Be("same");
    }
}
