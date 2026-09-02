using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Tests;

public class SnapshotCheckpointTests : DataModelTestBase
{
    private async Task<Guid[]> CheckpointIds()
    {
        return await DbContext.Commits.AsNoTracking()
            .Where(c => c.IsSnapshotCheckpoint)
            .Select(c => c.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EveryLocallyAuthoredCommitIsACheckpoint()
    {
        var entityId = Guid.NewGuid();
        var commit1 = await WriteNextChange(SetWord(entityId, "first"));
        var commit2 = await WriteNextChange(SetWord(entityId, "second"));

        (await CheckpointIds()).Should().BeEquivalentTo([commit1.Id, commit2.Id]);
    }

    [Fact]
    public async Task ASyncedBatchOnlyMakesItsLastCommitACheckpoint()
    {
        var entityId = Guid.NewGuid();
        var commits = new[]
        {
            await WriteNextChange(SetWord(entityId, "first"), add: false),
            await WriteNextChange(SetWord(entityId, "second"), add: false),
            await WriteNextChange(SetWord(entityId, "third"), add: false),
        };

        await AddCommitsViaSync(commits);

        (await CheckpointIds()).Should().BeEquivalentTo([commits[2].Id]);
    }

    [Fact]
    public async Task ALateCommitClearsTheCheckpointsItReplays()
    {
        var entityId = Guid.NewGuid();
        var commit1 = await WriteNextChange(SetWord(entityId, "first"));
        var commit2 = await WriteNextChange(SetWord(entityId, "second"));
        var commit3 = await WriteNextChange(SetWord(entityId, "third"));

        var lateCommit = await WriteChangeBefore(commit2, SetWord(Guid.NewGuid(), "late"));

        //the replay resumed from commit1 and ran through commit3, so only its last commit is a checkpoint again
        (await CheckpointIds()).Should().BeEquivalentTo([commit1.Id, commit3.Id]);
        lateCommit.IsSnapshotCheckpoint.Should().BeFalse();
    }

    [Fact]
    public async Task RegeneratingSnapshotsLeavesOnlyTheLastCommitAsACheckpoint()
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "first"));
        var lastCommit = await WriteNextChange(SetWord(entityId, "second"));

        await DataModel.RegenerateSnapshots();

        (await CheckpointIds()).Should().BeEquivalentTo([lastCommit.Id]);
    }
}
