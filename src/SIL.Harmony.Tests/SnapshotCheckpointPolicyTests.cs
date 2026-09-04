namespace SIL.Harmony.Tests;

public class SnapshotCheckpointPolicyTests
{
    private static readonly SnapshotCheckpointPolicy Policy = new(4);

    [Theory]
    [InlineData(1, 10, false)]
    [InlineData(4, 10, true)]
    [InlineData(8, 10, true)]
    [InlineData(9, 10, false)]
    [InlineData(10, 10, true)]
    public void PicksEveryNthCommitOfTheBatchAndItsLast(int commitIndex, int commitCount, bool isCheckpoint)
    {
        Policy.IsCheckpoint(commitIndex, commitCount).Should().Be(isCheckpoint);
    }

    [Theory]
    [InlineData(1, 3, false)]
    [InlineData(1, 5, true)]
    //a checkpoint at the snapshot's own commit counts, that's the position a replay would seed the entity from
    [InlineData(4, 5, true)]
    [InlineData(5, 8, false)]
    [InlineData(5, 9, true)]
    public void KeepsASnapshotOnlyWhenACheckpointFallsInTheGapItWouldLeave(int commitIndex, int nextCommitIndex, bool mustKeep)
    {
        Policy.MustKeepSnapshot(commitIndex, nextCommitIndex).Should().Be(mustKeep);
    }

    [Fact]
    public void KeepsExactlyTheSnapshotsTheCheckpointsItPicksNeed()
    {
        const int commitCount = 40;
        var policy = new SnapshotCheckpointPolicy(7);
        var checkpoints = Enumerable.Range(1, commitCount).Where(i => policy.IsCheckpoint(i, commitCount)).ToHashSet();

        for (var commitIndex = 1; commitIndex <= commitCount; commitIndex++)
        {
            for (var nextCommitIndex = commitIndex + 1; nextCommitIndex <= commitCount; nextCommitIndex++)
            {
                var gapSpansACheckpoint = Enumerable.Range(commitIndex, nextCommitIndex - commitIndex).Any(checkpoints.Contains);
                policy.MustKeepSnapshot(commitIndex, nextCommitIndex).Should().Be(gapSpansACheckpoint,
                    $"the gap [{commitIndex}, {nextCommitIndex}) of a {commitCount} commit batch");
            }
        }
    }

    [Fact]
    public void KeepsEverySnapshotAtTheNeverPruneEndOfTheDensityDial()
    {
        var everyCommit = new SnapshotCheckpointPolicy(1);
        foreach (var commitIndex in Enumerable.Range(1, 5))
        {
            everyCommit.IsCheckpoint(commitIndex, 5).Should().BeTrue();
            everyCommit.MustKeepSnapshot(commitIndex, commitIndex + 1).Should().BeTrue();
        }
    }
}
