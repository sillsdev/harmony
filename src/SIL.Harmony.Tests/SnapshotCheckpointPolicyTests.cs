using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Tests.Mocks;

namespace SIL.Harmony.Tests;

public class SnapshotCheckpointPolicyTests
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

    private static ObjectSnapshot SnapshotAt(Commit commit, bool isRoot = false) => ObjectSnapshot.ForTesting(commit, isRoot: isRoot);

    private static bool[] Checkpoints(SnapshotCheckpointPolicy policy) => [.. policy.CheckpointFlags().Select(f => f.IsCheckpoint)];

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
        var policy = new SnapshotCheckpointPolicy(commits, maxChangesBetweenCheckpoints: 4);

        policy.MustKeep(SnapshotAt(commits[snapshotPosition]), SnapshotAt(commits[newSnapshotPosition])).Should().Be(mustKeep);
    }

    [Fact]
    public void ABigCommitIsACheckpointBoundaryOfItsOwn()
    {
        // commit 1 alone carries a whole checkpoint interval of changes, so the snapshot before commit 2 must be kept
        var commits = Commits([1, 5, 1, 1, 1]);
        var policy = new SnapshotCheckpointPolicy(commits, maxChangesBetweenCheckpoints: 4);
        policy.MustKeep(SnapshotAt(commits[0]), SnapshotAt(commits[1])).Should().BeFalse("the first commit is one change, no boundary yet");
        policy.MustKeep(SnapshotAt(commits[1]), SnapshotAt(commits[2])).Should().BeTrue("the second commit's five changes cross a boundary, so we'd keep its snapshots");
    }

    [Fact]
    public void CountsChangesNotCommits()
    {
        // ten single-change commits hold no boundary at maxChanges 20; ten five-change commits hold two
        var singleChange = Commits(Enumerable.Repeat(1, 11));
        new SnapshotCheckpointPolicy(singleChange, 20).MustKeep(SnapshotAt(singleChange[0]), SnapshotAt(singleChange[10])).Should().BeFalse();
        var fiveChanges = Commits(Enumerable.Repeat(5, 11));
        new SnapshotCheckpointPolicy(fiveChanges, 20).MustKeep(SnapshotAt(fiveChanges[0]), SnapshotAt(fiveChanges[10])).Should().BeTrue();
    }

    [Fact]
    public void KeepsEverySnapshotWhenEveryChangeIsACheckpoint()
    {
        // maxChanges 1 forces a boundary at every commit, so no snapshot is ever droppable
        var commits = SingleChangeCommits(5);
        var everyCommit = new SnapshotCheckpointPolicy(commits, maxChangesBetweenCheckpoints: 1);
        foreach (var from in Enumerable.Range(0, 4))
            everyCommit.MustKeep(SnapshotAt(commits[from]), SnapshotAt(commits[from + 1])).Should().BeTrue();
    }

    [Fact]
    public void ARootSnapshotIsAlwaysKept()
    {
        var commits = SingleChangeCommits(3);
        var policy = new SnapshotCheckpointPolicy(commits, maxChangesBetweenCheckpoints: 1000);

        policy.MustKeep(SnapshotAt(commits[0], isRoot: true), SnapshotAt(commits[1])).Should().BeTrue();
        Checkpoints(policy).Should().Equal(true, true, true, "nothing was dropped");
    }

    [Fact]
    public void ASnapshotSupersededWithinItsOwnCommitIsNeverKeptAndLeavesNoHole()
    {
        var commits = SingleChangeCommits(2);
        var policy = new SnapshotCheckpointPolicy(commits, maxChangesBetweenCheckpoints: 1);

        policy.MustKeep(SnapshotAt(commits[0], isRoot: true), SnapshotAt(commits[0])).Should().BeFalse("only 1 snapshot per entity per commit, even a root");
        Checkpoints(policy).Should().Equal(true, true);
    }

    /// <summary>
    /// The checkpoint flags after a snapshot at <c>From</c> was superseded by one at <c>ToExclusive</c> for each drop,
    /// with a maxChanges so high that nothing straddles a boundary
    /// </summary>
    private static bool[] CheckpointsAfterDropping(int commitCount, params (int From, int ToExclusive)[] drops)
    {
        var commits = SingleChangeCommits(commitCount);
        var policy = new SnapshotCheckpointPolicy(commits, maxChangesBetweenCheckpoints: 1000);
        foreach (var (from, toExclusive) in drops)
        {
            policy.MustKeep(SnapshotAt(commits[from]), SnapshotAt(commits[toExclusive])).Should().BeFalse();
        }

        return Checkpoints(policy);
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
        var policy = new SnapshotCheckpointPolicy(commits, maxChangesBetweenCheckpoints: 1000);
        policy.CheckpointFlags().Select(f => f.Commit).Should().Equal(commits);
    }
}
