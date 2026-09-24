using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Tests.Mocks;

namespace SIL.Harmony.Tests;

public class ReplaySnapshotsTests
{
    /// <summary>a batch of commits with the given number of changes each, in order</summary>
    private static Commit[] Commits(IEnumerable<int> changesPerCommit)
    {
        return [.. changesPerCommit.Select((changeCount, position) =>
        {
            var commit = new Commit { ClientId = Guid.Empty, HybridDateTime = MockTimeProvider.Time(position, 0) };
            for (var i = 0; i < changeCount; i++)
            {
                var change = new SetWordTextChange(Guid.NewGuid(), $"word {i}");
                commit.ChangeEntities.Add(new ChangeEntity<IChange> { Index = i, CommitId = commit.Id, EntityId = change.EntityId, Change = change });
            }

            return commit;
        })];
    }

    private static Commit[] SingleChangeCommits(int commitCount) => Commits(Enumerable.Repeat(1, commitCount));

    private static readonly Guid Entity = Guid.NewGuid();

    private static ObjectSnapshot SnapshotAt(Commit commit, Guid? entityId = null, bool isRoot = false) =>
        ObjectSnapshot.ForTesting(commit, entityId ?? Entity, isRoot);

    private static bool[] Checkpoints(ReplaySnapshots snapshots) => [.. snapshots.CheckpointFlags().Select(f => f.IsCheckpoint)];

    [Theory]
    // maxChanges: 4 means that boundaries land at/before multiples of 4 (4, 8, 12 etc.)
    [InlineData(0, 2, false)]
    [InlineData(0, 4, true)]
    [InlineData(3, 4, true)]
    [InlineData(4, 7, false)]
    [InlineData(4, 8, true)]
    public void KeepsASupersededSnapshotOnlyWhenACheckpointBoundaryFallsInTheGap(
        int snapshotPosition, // the batch position of the snapshot we're deciding whether we need to keep
        int newSnapshotPosition, // the batch position of a snapshot that was just generated and may or may not supersede the one before it
        bool mustKeep)
    {
        var commits = SingleChangeCommits(20);
        var snapshots = new ReplaySnapshots(commits, maxChangesBetweenCheckpoints: 4);
        var older = SnapshotAt(commits[snapshotPosition]);
        var newer = SnapshotAt(commits[newSnapshotPosition]);

        snapshots.Add(older);
        snapshots.Add(newer);

        snapshots.SnapshotsToPersist().Should().Contain(newer);
        if (mustKeep)
            snapshots.SnapshotsToPersist().Should().Contain(older);
        else
            snapshots.SnapshotsToPersist().Should().NotContain(older);
    }

    [Fact]
    public void ABigCommitIsACheckpointBoundaryOfItsOwn()
    {
        // commit 1 alone carries a whole checkpoint interval of changes, so the snapshot before commit 2 must be kept
        var commits = Commits([1, 5, 1, 1, 1]);
        var snapshots = new ReplaySnapshots(commits, maxChangesBetweenCheckpoints: 4);
        var atFirst = SnapshotAt(commits[0]);
        var atSecond = SnapshotAt(commits[1]);
        var atThird = SnapshotAt(commits[2]);

        snapshots.Add(atFirst);
        snapshots.Add(atSecond);
        snapshots.Add(atThird);

        snapshots.SnapshotsToPersist().Should().NotContain(atFirst, "the first commit is one change, no boundary yet");
        snapshots.SnapshotsToPersist().Should().Contain(atSecond, "the second commit's five changes cross a boundary, so we keep its snapshots");
    }

    [Fact]
    public void CountsChangesNotCommits()
    {
        // ten single-change commits hold no boundary at maxChanges 20; ten five-change commits hold two
        SupersededSnapshotIsKept(Commits(Enumerable.Repeat(1, 11)), maxChanges: 20).Should().BeFalse();
        SupersededSnapshotIsKept(Commits(Enumerable.Repeat(5, 11)), maxChanges: 20).Should().BeTrue();

        static bool SupersededSnapshotIsKept(Commit[] commits, int maxChanges)
        {
            var snapshots = new ReplaySnapshots(commits, maxChanges);
            var older = SnapshotAt(commits[0]);
            snapshots.Add(older);
            snapshots.Add(SnapshotAt(commits[10]));
            return snapshots.SnapshotsToPersist().Contains(older);
        }
    }

    [Fact]
    public void KeepsEverySnapshotWhenEveryChangeIsACheckpoint()
    {
        // maxChanges 1 forces a boundary at every commit, so no snapshot is ever droppable
        var commits = SingleChangeCommits(5);
        var snapshots = new ReplaySnapshots(commits, maxChangesBetweenCheckpoints: 1);
        var all = commits.Select(c => SnapshotAt(c)).ToArray();
        foreach (var snapshot in all) snapshots.Add(snapshot);

        snapshots.SnapshotsToPersist().Should().BeEquivalentTo(all);
    }

    [Fact]
    public void ARootSnapshotIsAlwaysKept()
    {
        var commits = SingleChangeCommits(3);
        var snapshots = new ReplaySnapshots(commits, maxChangesBetweenCheckpoints: 1000);
        var root = SnapshotAt(commits[0], isRoot: true);
        snapshots.Add(root);
        snapshots.Add(SnapshotAt(commits[1]));

        snapshots.SnapshotsToPersist().Should().Contain(root);
        Checkpoints(snapshots).Should().Equal(true, true, true, "nothing was dropped");
    }

    [Fact]
    public void OnlyTheNewestSnapshotPerEntityPerCommitIsPersisted()
    {
        var commits = SingleChangeCommits(2);
        var snapshots = new ReplaySnapshots(commits, maxChangesBetweenCheckpoints: 1);
        var first = SnapshotAt(commits[0]);
        var second = SnapshotAt(commits[0]);
        snapshots.Add(first);
        snapshots.Add(second);

        snapshots.SnapshotsToPersist().Should().Equal(second);
        Checkpoints(snapshots).Should().Equal(true, true, "replacing a snapshot within its commit leaves no hole");
    }

    [Fact]
    public void TheNewestSnapshotIsTheEntitysLatest()
    {
        var commits = SingleChangeCommits(2);
        var snapshots = new ReplaySnapshots(commits, maxChangesBetweenCheckpoints: 1000);
        var other = SnapshotAt(commits[0], entityId: Guid.NewGuid());
        var newer = SnapshotAt(commits[1]);

        snapshots.Latest(Entity).Should().BeNull();
        snapshots.Contains(Entity).Should().BeFalse();
        snapshots.Add(other);
        snapshots.Add(SnapshotAt(commits[0]));
        snapshots.Add(newer);

        snapshots.Latest(Entity).Should().BeSameAs(newer);
        snapshots.Contains(Entity).Should().BeTrue();
        snapshots.LatestSnapshots.Should().BeEquivalentTo([other, newer]);
    }

    /// <summary>
    /// The checkpoint flags after one entity per drop had a snapshot at <c>From</c> superseded by one at <c>ToExclusive</c>,
    /// with a maxChanges so high that nothing straddles a boundary
    /// </summary>
    private static bool[] CheckpointsAfterDropping(int commitCount, params (int From, int ToExclusive)[] drops)
    {
        var commits = SingleChangeCommits(commitCount);
        var snapshots = new ReplaySnapshots(commits, maxChangesBetweenCheckpoints: 1000);
        foreach (var (from, toExclusive) in drops)
        {
            var entityId = Guid.NewGuid();
            snapshots.Add(SnapshotAt(commits[from], entityId));
            snapshots.Add(SnapshotAt(commits[toExclusive], entityId));
        }

        return Checkpoints(snapshots);
    }

    [Fact]
    public void EveryCommitIsACheckpointWhenNothingWasDropped()
    {
        CheckpointsAfterDropping(5).Should().Equal(true, true, true, true, true);
    }

    [Fact]
    public void ADroppedSnapshotClearsOnlyTheCommitsItCovers()
    {
        // [1,3) is half-open: commits 1 and 2 unsafe, 3 safe again
        CheckpointsAfterDropping(5, (1, 3)).Should().Equal(true, false, false, true, true);
    }

    [Fact]
    public void AdjacentAndOverlappingDropsUnion()
    {
        CheckpointsAfterDropping(6, (0, 2), (2, 4)).Should().Equal(false, false, false, false, true, true);
        CheckpointsAfterDropping(6, (0, 3), (1, 2)).Should().Equal(false, false, false, true, true, true);
    }

    [Fact]
    public void TheLastCommitStaysACheckpointWhenADropRunsRightUpToIt()
    {
        //the end commit holds the entity's next snapshot, so it stays safe — including at the end of the batch
        const int commitCount = 5;
        const int lastCommitIndex = commitCount - 1;
        CheckpointsAfterDropping(commitCount, (0, lastCommitIndex)).Should().Equal(false, false, false, false, true);
    }

    [Fact]
    public void CheckpointFlagsPairEachBatchCommitWithItsOutcome()
    {
        var commits = SingleChangeCommits(3);
        var snapshots = new ReplaySnapshots(commits, maxChangesBetweenCheckpoints: 1000);
        snapshots.CheckpointFlags().Select(f => f.Commit).Should().Equal(commits);
    }
}
