namespace SIL.Harmony.Tests;

public class SnapshotCheckpointPolicyTests
{
    // one change per commit makes the floor reduce to the old grid: keep iff a multiple of maxChanges falls in the gap
    private static SnapshotCheckpointPolicy SingleChangeCommits(int commitCount, int maxChanges) =>
        new(Enumerable.Repeat(1, commitCount), maxChanges);

    [Theory]
    [InlineData(1, 3, false)] // gap [1,3) spans no floor boundary
    [InlineData(1, 5, true)]  // boundary at commit 4
    [InlineData(4, 5, true)]  // that boundary is the gap's own start
    [InlineData(5, 8, false)]
    [InlineData(5, 9, true)]  // boundary at commit 8
    public void KeepsASnapshotOnlyWhenAFloorBoundaryFallsInTheGap(int from, int to, bool mustKeep)
    {
        SingleChangeCommits(20, maxChanges: 4).MustKeepSnapshot(from, to).Should().Be(mustKeep);
    }

    [Fact]
    public void ABigCommitIsAFloorBoundaryOfItsOwn()
    {
        // commit 2 alone carries a whole floor interval of changes, so the snapshot before commit 3 must be kept
        var policy = new SnapshotCheckpointPolicy([1, 5, 1, 1, 1], maxChanges: 4);
        policy.MustKeepSnapshot(1, 2).Should().BeFalse("commit 1 is one change, no boundary yet");
        policy.MustKeepSnapshot(2, 3).Should().BeTrue("commit 2's five changes cross a boundary");
    }

    [Fact]
    public void CountsChangesNotCommits()
    {
        // ten single-change commits hold no boundary at maxChanges 20; ten five-change commits hold two
        new SnapshotCheckpointPolicy(Enumerable.Repeat(1, 10), 20).MustKeepSnapshot(1, 11).Should().BeFalse();
        new SnapshotCheckpointPolicy(Enumerable.Repeat(5, 10), 20).MustKeepSnapshot(1, 11).Should().BeTrue();
    }

    [Fact]
    public void KeepsEverySnapshotWhenTheFloorIsEveryChange()
    {
        // maxChanges 1 forces a boundary at every commit, so no snapshot is ever droppable
        var everyCommit = SingleChangeCommits(5, maxChanges: 1);
        foreach (var from in Enumerable.Range(1, 4))
            everyCommit.MustKeepSnapshot(from, from + 1).Should().BeTrue();
    }

    private static bool[] CheckpointsOf(int commitCount, params (int From, int ToExclusive)[] holes) =>
        SingleChangeCommits(commitCount, maxChanges: 1).DiscoverCheckpoints(holes);

    [Fact]
    public void EveryCommitIsACheckpointWhenNothingWasDropped()
    {
        CheckpointsOf(5).Should().Equal(true, true, true, true, true);
    }

    [Fact]
    public void AHoleClearsOnlyThePositionsItCovers()
    {
        // [2,4) is half-open: positions 2 and 3 unsafe, 4 safe again
        CheckpointsOf(5, (2, 4)).Should().Equal(true, false, false, true, true);
    }

    [Fact]
    public void AdjacentAndOverlappingHolesUnion()
    {
        CheckpointsOf(6, (1, 3), (3, 5)).Should().Equal(false, false, false, false, true, true);
        CheckpointsOf(6, (1, 4), (2, 3)).Should().Equal(false, false, false, true, true, true);
    }

    [Fact]
    public void AHoleReachingTheLastCommitClearsUpToButNotIncludingIt()
    {
        //a hole's end is always a re-touch, so it can never be the last commit; the last position stays safe
        CheckpointsOf(5, (3, 5)).Should().Equal(true, true, false, false, true);
    }
}
