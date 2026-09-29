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

    /// <summary>a policy over the batch, as the worker would build it</summary>
    private static SnapshotCheckpointPolicy PolicyFor(Commit[] commits, int maxChangesBetweenCheckpoints) =>
        new(new SortedSet<Commit>(commits), maxChangesBetweenCheckpoints);

    private static ObjectSnapshot SnapshotAt(Commit commit, bool isRoot = false) => ObjectSnapshot.ForTesting(commit, isRoot: isRoot);

    private static bool[] Checkpoints(SnapshotCheckpointPolicy policy) => [.. policy.CheckpointFlags().Select(f => f.IsCheckpoint)];

    [Theory]
    // maxChanges: 4 makes commits 3, 7, 11 etc. required checkpoints, where the running total reaches 4, 8, 12
    [InlineData(0, 2, false)]
    [InlineData(0, 4, true)]
    [InlineData(3, 4, true)]
    [InlineData(4, 7, false)]
    [InlineData(4, 8, true)]
    public void KeepsASupersededSnapshotOnlyWhenARequiredCheckpointIsInItsCoverage(
        int snapshotPosition, // the batch position of the snapshot we're deciding whether we need to keep
        int newSnapshotPosition, // the batch position of a snapshot that was just generated and may or may not supersede the one before it
        bool mustKeep)
    {
        var commits = SingleChangeCommits(20);
        var policy = PolicyFor(commits, maxChangesBetweenCheckpoints: 4);

        policy.Supersede(SnapshotAt(commits[snapshotPosition]), by: SnapshotAt(commits[newSnapshotPosition])).Should().Be(mustKeep);
    }

    [Fact]
    public void ABigCommitIsARequiredCheckpointOfItsOwn()
    {
        // commit 1 alone carries more than maxChanges changes, so it is a required checkpoint
        var commits = Commits([1, 5, 1, 1, 1]);
        var policy = PolicyFor(commits, maxChangesBetweenCheckpoints: 4);
        policy.Supersede(SnapshotAt(commits[0]), by: SnapshotAt(commits[1])).Should().BeFalse("the first commit is one change, not a required checkpoint");
        policy.Supersede(SnapshotAt(commits[1]), by: SnapshotAt(commits[2])).Should().BeTrue("the second commit's five changes take the total past 4, so it is a required checkpoint");
    }

    [Fact]
    public void CountsChangesNotCommits()
    {
        // ten single-change commits hold no required checkpoint at maxChanges 20; ten five-change commits hold two
        var singleChange = Commits(Enumerable.Repeat(1, 11));
        PolicyFor(singleChange, 20).Supersede(SnapshotAt(singleChange[0]), by: SnapshotAt(singleChange[10])).Should().BeFalse();
        var fiveChanges = Commits(Enumerable.Repeat(5, 11));
        PolicyFor(fiveChanges, 20).Supersede(SnapshotAt(fiveChanges[0]), by: SnapshotAt(fiveChanges[10])).Should().BeTrue();
    }

    [Fact]
    public void KeepsEverySnapshotWhenEveryChangeIsACheckpoint()
    {
        // maxChanges 1 makes every commit a required checkpoint, so no snapshot is ever droppable
        var commits = SingleChangeCommits(5);
        var everyCommit = PolicyFor(commits, maxChangesBetweenCheckpoints: 1);
        foreach (var from in Enumerable.Range(0, 4))
            everyCommit.Supersede(SnapshotAt(commits[from]), by: SnapshotAt(commits[from + 1])).Should().BeTrue();
    }

    [Fact]
    public void ACommitBiggerThanTheLimitIsOneRequiredCheckpointAndStartsTheCountOver()
    {
        // commit 0 alone is 10 changes: one required checkpoint, and the next one is 4 changes later at commit 4
        var commits = Commits([10, 1, 1, 1, 1]);
        var policy = PolicyFor(commits, maxChangesBetweenCheckpoints: 4);

        policy.Supersede(SnapshotAt(commits[0]), by: SnapshotAt(commits[1])).Should().BeTrue();
        policy.Supersede(SnapshotAt(commits[1]), by: SnapshotAt(commits[4])).Should().BeFalse("no required checkpoint in commits 1 to 3");
    }

    [Fact]
    public void ChangesBeyondTheLimitDoNotCountTowardTheNextRequiredCheckpoint()
    {
        // commit 1 takes the total to 6; the 2 beyond the limit don't carry over, so commit 2 only reaches 3
        var commits = Commits([3, 3, 3, 3]);
        var policy = PolicyFor(commits, maxChangesBetweenCheckpoints: 4);

        policy.Supersede(SnapshotAt(commits[1]), by: SnapshotAt(commits[2])).Should().BeTrue("commit 1 is a required checkpoint");
        policy.Supersede(SnapshotAt(commits[2]), by: SnapshotAt(commits[3])).Should().BeFalse("commit 2 is not, though it would be if the 2 extra changes carried over");
    }

    [Fact]
    public void ARootSnapshotIsAlwaysKept()
    {
        var commits = SingleChangeCommits(3);
        var policy = PolicyFor(commits, maxChangesBetweenCheckpoints: 1000);

        policy.Supersede(SnapshotAt(commits[0], isRoot: true), by: SnapshotAt(commits[1])).Should().BeTrue();
        Checkpoints(policy).Should().Equal(new[] { true, true, true }, "nothing was dropped");
    }

    [Fact]
    public void ASnapshotSupersededWithinItsOwnCommitIsNeverKeptAndLeavesNoHole()
    {
        var commits = SingleChangeCommits(2);
        var policy = PolicyFor(commits, maxChangesBetweenCheckpoints: 1);

        policy.Supersede(SnapshotAt(commits[0], isRoot: true), by: SnapshotAt(commits[0])).Should().BeFalse("only 1 snapshot per entity per commit, even a root");
        Checkpoints(policy).Should().Equal(true, true);
    }

    /// <summary>
    /// The checkpoint flags after a snapshot at <c>From</c> was superseded by one at <c>ToExclusive</c> for each drop,
    /// with a maxChanges so high that there are no required checkpoints
    /// </summary>
    private static bool[] CheckpointsAfterDropping(int commitCount, params (int From, int ToExclusive)[] drops)
    {
        var commits = SingleChangeCommits(commitCount);
        var policy = PolicyFor(commits, maxChangesBetweenCheckpoints: 1000);
        foreach (var (from, toExclusive) in drops)
        {
            policy.Supersede(SnapshotAt(commits[from]), by: SnapshotAt(commits[toExclusive])).Should().BeFalse();
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
        var policy = PolicyFor(commits, maxChangesBetweenCheckpoints: 1000);
        policy.CheckpointFlags().Select(f => f.Commit).Should().Equal(commits);
    }
}
