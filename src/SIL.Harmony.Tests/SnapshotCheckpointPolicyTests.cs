namespace SIL.Harmony.Tests;

public class SnapshotCheckpointPolicyTests
{
    // one change per commit makes the floor reduce to the old grid: keep iff a multiple of maxChanges falls in the gap
    private static SnapshotCheckpointPolicy SingleChangeCommits(int commitCount, int maxChanges) =>
        new(Enumerable.Repeat(1, commitCount), maxChanges);

    [Theory]
    [InlineData(0, 2, false)] // gap [0,2) spans no floor boundary
    [InlineData(0, 4, true)]  // boundary before commit 4
    [InlineData(3, 4, true)]  // that boundary is the gap's own end
    [InlineData(4, 7, false)]
    [InlineData(4, 8, true)]  // boundary before commit 8
    public void KeepsASnapshotOnlyWhenAFloorBoundaryFallsInTheGap(int from, int to, bool mustKeep)
    {
        SingleChangeCommits(20, maxChanges: 4).MustKeepSnapshot(from, to).Should().Be(mustKeep);
    }

    [Fact]
    public void ABigCommitIsAFloorBoundaryOfItsOwn()
    {
        // commit 1 alone carries a whole floor interval of changes, so the snapshot before commit 2 must be kept
        var policy = new SnapshotCheckpointPolicy([1, 5, 1, 1, 1], maxChangesBetweenCheckpoints: 4);
        policy.MustKeepSnapshot(0, 1).Should().BeFalse("the first commit is one change, no boundary yet");
        policy.MustKeepSnapshot(1, 2).Should().BeTrue("the second commit's five changes cross a boundary");
    }

    [Fact]
    public void CountsChangesNotCommits()
    {
        // ten single-change commits hold no boundary at maxChanges 20; ten five-change commits hold two
        new SnapshotCheckpointPolicy(Enumerable.Repeat(1, 10), 20).MustKeepSnapshot(0, 10).Should().BeFalse();
        new SnapshotCheckpointPolicy(Enumerable.Repeat(5, 10), 20).MustKeepSnapshot(0, 10).Should().BeTrue();
    }

    [Fact]
    public void KeepsEverySnapshotWhenTheFloorIsEveryChange()
    {
        // maxChanges 1 forces a boundary at every commit, so no snapshot is ever droppable
        var everyCommit = SingleChangeCommits(5, maxChanges: 1);
        foreach (var from in Enumerable.Range(0, 4))
            everyCommit.MustKeepSnapshot(from, from + 1).Should().BeTrue();
    }

    private static bool[] CheckpointsAfterDropping(int commitCount, params (int From, int ToExclusive)[] drops)
    {
        var policy = SingleChangeCommits(commitCount, maxChanges: 1000);
        foreach (var (from, toExclusive) in drops)
        {
            policy.SnapshotDropped(from, toExclusive);
        }

        return [.. Enumerable.Range(0, commitCount).Select(policy.IsCheckpoint)];
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
    public void ADropReachingTheLastCommitClearsUpToButNotIncludingIt()
    {
        //a gap's end is always a re-touch, so it can never be the last commit; the last one stays safe
        CheckpointsAfterDropping(5, (2, 4)).Should().Equal(true, true, false, false, true);
    }
}
