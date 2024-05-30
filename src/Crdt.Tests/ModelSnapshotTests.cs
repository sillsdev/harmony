using Crdt.Sample.Models;
using Microsoft.EntityFrameworkCore;

namespace Crdt.Tests;

public class ModelSnapshotTests : DataModelTestBase
{
    [Fact]
    public void CanGetEmptyModelSnapshot()
    {
        DataModel.GetProjectSnapshot().Should().NotBeNull();
    }

    [Fact]
    public async Task CanGetModelSnapshot()
    {
        await WriteNextChange(SetWord(Guid.NewGuid(), "entity1"));
        await WriteNextChange(SetWord(Guid.NewGuid(), "entity2"));
        var snapshot = await DataModel.GetProjectSnapshot();
        snapshot.Snapshots.Should().HaveCount(2);
    }

    [Fact]
    public async Task ModelSnapshotShowsMultipleChanges()
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "first"));
        var secondChange = await WriteNextChange(SetWord(entityId, "second"));
        var snapshot = await DataModel.GetProjectSnapshot();
        var simpleSnapshot = snapshot.Snapshots.Values.First();
        var entity = await DataModel.GetBySnapshotId(simpleSnapshot.Id);
        var entry = entity.Is<Word>();
        entry.Text.Should().Be("second");
        snapshot.LastChange.Should().Be(secondChange.DateTime);
    }

    [Theory]
    [InlineData(10)]
    // [InlineData(100)]
    //not going higher because we run into insert performance issues
    // [InlineData(1_000)]
    public async Task CanGetSnapshotFromEarlier(int changeCount)
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "first"));
        var changes = new List<Commit>(changeCount);
        var addNew = new List<Commit>(changeCount);
        for (var i = 0; i < changeCount; i++)
        {
            changes.Add(await WriteNextChange(SetWord(entityId, $"change {i}"), false).AsTask());
            addNew.Add(await WriteNextChange(SetWord(Guid.NewGuid(), $"add {i}"), false).AsTask());
        }

        //adding all via sync means there's sparse snapshots
        await AddCommitsViaSync(changes.Concat(addNew));
        //there will only be a snapshot for every other commit, but there's change count * 2 commits, plus a first and last change
        DbContext.Snapshots.Should().HaveCount(2 + changeCount);

        for (int i = 0; i < changeCount; i++)
        {
            var snapshots = await DataModel.GetSnapshotsAt(changes[i].DateTime);
            var entry = snapshots[entityId].Entity.Is<Word>();
            entry.Text.Should().Be($"change {i}");
            snapshots.Values.Should().HaveCount(1 + i);
        }
    }


    /// <summary>
    /// test isn't super useful as a perf test as 99% of the time is just inserting data
    /// </summary>
    [Fact(Skip = "Slow test")]
    public async Task WorstCaseSnapshotReApply()
    {
        int changeCount = 10_000;
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "first"));
        //adding all in one AddRange means there's sparse snapshots
        await AddCommitsViaSync(Enumerable.Range(0, changeCount)
            .Select(i => WriteNextChange(SetWord(entityId, $"change {i}"), false).Result));

        var latestSnapshot = await DataModel.GetLatestSnapshotByObjectId(entityId);
        //delete snapshots so when we get at then we need to re-apply
        await DbContext.Snapshots.Where(s => !s.IsRoot).ExecuteDeleteAsync();

        var computedModelSnapshots = await DataModel.GetSnapshotsAt(latestSnapshot.Commit.DateTime);

        var entitySnapshot = computedModelSnapshots.Should().ContainSingle().Subject.Value;
        entitySnapshot.Should().BeEquivalentTo(latestSnapshot, options => options.Excluding(snapshot => snapshot.Id));
        var latestSnapshotEntry = latestSnapshot.Entity.Is<Word>();
        var entitySnapshotEntry = entitySnapshot.Entity.Is<Word>();
        entitySnapshotEntry.Text.Should().Be(latestSnapshotEntry.Text);
    }
}
