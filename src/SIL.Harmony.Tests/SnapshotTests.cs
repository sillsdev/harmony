using SIL.Harmony.Sample.Models;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Tests;

public class SnapshotTests : DataModelTestBase
{
    [Fact]
    public async Task FirstChangeResultsInRootSnapshot()
    {
        await WriteNextChange(SetWord(Guid.NewGuid(), "test"));
        var snapshot = DbContext.Snapshots.Should().ContainSingle().Subject;
        snapshot.IsRoot.Should().BeTrue();
        snapshot.Entity.Should().NotBeNull();
    }

    [Fact]
    public async Task MultipleChangesPreservesRootSnapshot()
    {
        var entityId = Guid.NewGuid();
        var commits = new List<Commit>();
        for (int i = 0; i < 4; i++)
        {
            commits.Add(await WriteChange(_localClientId,
                new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(i),
                SetWord(entityId, $"test {i}"),
                add: false));
        }

        await AddCommitsViaSync(commits);

        var snapshots = await DbContext.Snapshots.ToArrayAsync();
        snapshots.Should().HaveCountGreaterThan(1);
        snapshots.Should().ContainSingle(s => s.IsRoot);
    }

    [Fact]
    public async Task MultipleChangesPreservesSomeIntermediateSnapshots()
    {
        var entityId = Guid.NewGuid();
        var commits = new List<Commit>();
        for (int i = 0; i < 6; i++)
        {
            commits.Add(await WriteChange(_localClientId,
                new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(i),
                [SetWord(entityId, $"test {i}"), SetWord(entityId, $"test {i} again")],
                add: false));
        }

        await AddCommitsViaSync(commits);

        var latestSnapshot = await DataModel.GetLatestSnapshotByObjectId(entityId);
        var snapshots = await DbContext.Snapshots.ToArrayAsync();
        snapshots.Should().HaveCountGreaterThan(2);
        snapshots.Should().ContainSingle(s => s.Id == latestSnapshot.Id);
        snapshots.Should().ContainSingle(s => s.IsRoot);
        var intermediateSnapshot = snapshots.FirstOrDefault(s => !s.IsRoot && s.Id != latestSnapshot.Id);
        ArgumentNullException.ThrowIfNull(intermediateSnapshot);
        intermediateSnapshot.IsRoot.Should().BeFalse();
        intermediateSnapshot.Id.Should().NotBe(latestSnapshot.Id);
        intermediateSnapshot.Entity.Should().BeOfType<Word>().Which.Text.Should().Contain("again");
    }

    [Fact]
    public async Task OnlySaveTheLastSnapshotWhenThereAreMultipleChangesToAnEntityInOneCommit()
    {
        var entityId = Guid.NewGuid();
        await WriteChange(_localClientId,
            DateTimeOffset.Now,
            [
                SetWord(entityId, "change1"),
                SetWord(entityId, "change2"),
                SetWord(entityId, "change3"),
            ]);
        DbContext.Snapshots.Should().ContainSingle();
    }

    [Fact]
    public async Task DontAddCreateTheSameSnapshotTwice()
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "test root"));
        await WriteNextChange(SetWord(entityId, "test non root"));

        await AddCommitsViaSync([
            //the order here is important, the second commit was causing the snapshot for 'test non root' to attempt to be inserted again
            await WriteNextChange(SetWord(Guid.NewGuid(), "test 1"), add: false),
            await WriteNextChange(SetWord(entityId, "test 2"), add: false),
        ]);
    }

    [Fact]
    public async Task CanRecreateUniqueConstraintConflictingValueInOneCommit()
    {
        var entityId = Guid.NewGuid();
        await WriteChange(_localClientId,
            DateTimeOffset.Now,
            [
                SetTag(entityId, "tag-1"),
            ]);
        await WriteChange(_localClientId,
            DateTimeOffset.Now,
            [
                DeleteTag(entityId),
                SetTag(Guid.NewGuid(), "tag-1"),
            ]);
    }
}
