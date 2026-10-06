using System.Text.Json;
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

    private static Word WordFrom(ObjectSnapshot snapshot) => (Word)snapshot.Entity.DbObject;

    [Fact]
    public async Task AFirstChangeMakesARootSnapshot()
    {
        var wordId = Guid.NewGuid();
        var commit = NextCommit(new NewWordChange(wordId, "hello"));

        var (snapshots, _) = await SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(EmptySnapshotView.Instance, [commit], SampleConfig);

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

        var (snapshots, _) = await SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(EmptySnapshotView.Instance, [commit], SampleConfig);

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

        var (snapshots, _) = await SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(baseline, [edit], SampleConfig);

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

        var (snapshots, flags) = await SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(
            EmptySnapshotView.Instance, [create, edit1, edit2, lastEdit], config);

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

        var (snapshots, _) = await SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(baseline, [delete], SampleConfig);

        //one change, two snapshots: the delete cascades to the word that referenced the antonym
        snapshots.Should().HaveCount(2);
        snapshots.Single(s => s.EntityId == antonym.Id).EntityIsDeleted.Should().BeTrue();
        var wordSnapshot = snapshots.Single(s => s.EntityId == word.Id);
        wordSnapshot.IsRoot.Should().BeFalse();
        wordSnapshot.CommitId.Should().Be(delete.Id);
        WordFrom(wordSnapshot).AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task ADeletedEntityIsRevivedByACreateChange()
    {
        var word = new Word { Id = Guid.NewGuid(), Text = "hello", DeletedAt = DateTimeOffset.UtcNow };
        var baseline = Baseline(word);
        var revive = NextCommit(new NewWordChange(word.Id, "hello again"));

        var (snapshots, _) = await SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(baseline, [revive], SampleConfig);

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

        var (snapshots, _) = await SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(EmptySnapshotView.Instance, [commit], SampleConfig);

        snapshots.Should().BeEmpty("an entity this client can't create stays absent until it understands the change");
    }

    [Fact]
    public async Task WhereReturnsTheReplayedSnapshotOnceAndExcludesWhatThePredicateRejects()
    {
        var edited = new Word { Id = Guid.NewGuid(), Text = "old" };
        var untouched = new Word { Id = Guid.NewGuid(), Text = "same" };
        var baseline = Baseline(edited, untouched);
        var edit = NextCommit(SetWord(edited.Id, "new"));

        var view = await SnapshotWorker.ReplayCommits(baseline, [edit], SampleConfig);

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

        var view = await SnapshotWorker.ReplayCommits(baseline, [edit], SampleConfig);

        var all = await view.All().ToArrayAsync(TestContext.Current.CancellationToken);
        all.Should().HaveCount(2, "the edited word appears once, not once per layer");
        WordFrom(all.Single(s => s.EntityId == edited.Id)).Text.Should().Be("new");
        WordFrom(all.Single(s => s.EntityId == untouched.Id)).Text.Should().Be("same");
    }
}
