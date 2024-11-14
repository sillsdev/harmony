using SIL.Harmony.Sample.Models;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Tests;

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
        var entry = await DataModel.GetBySnapshotId<Word>(simpleSnapshot.Id);
        entry.Text.Should().Be("second");
        snapshot.LastChange.Should().Be(secondChange.DateTime);
    }

    [Fact]
    public async Task CanGetWordForASpecificCommit()
    {
        var entityId = Guid.NewGuid();
        var firstCommit = await WriteNextChange(SetWord(entityId, "first"));
        var secondCommit = await WriteNextChange(SetWord(entityId, "second"));
        var thirdCommit = await WriteNextChange(SetWord(entityId, "third"));
        await ClearNonRootSnapshots();
        var firstWord = await DataModel.GetAtCommit<Word>(firstCommit.Id, entityId);
        firstWord.Should().NotBeNull();
        firstWord.Text.Should().Be("first");

        var secondWord = await DataModel.GetAtCommit<Word>(secondCommit.Id, entityId);
        secondWord.Should().NotBeNull();
        secondWord.Text.Should().Be("second");

        var thirdWord = await DataModel.GetAtCommit<Word>(thirdCommit.Id, entityId);
        thirdWord.Should().NotBeNull();
        thirdWord.Text.Should().Be("third");
    }

    [Fact]
    public async Task CanGetWordForASpecificTime()
    {
        var entityId = Guid.NewGuid();
        var firstCommit = await WriteNextChange(SetWord(entityId, "first"));
        var secondCommit = await WriteNextChange(SetWord(entityId, "second"));
        var thirdCommit = await WriteNextChange(SetWord(entityId, "third"));
        //ensures that SnapshotWorker.ApplyCommitsToSnapshots will be called when getting the snapshots
        await ClearNonRootSnapshots();
        var firstWord = await DataModel.GetAtTime<Word>(firstCommit.DateTime.AddMinutes(5), entityId);
        firstWord.Should().NotBeNull();
        firstWord.Text.Should().Be("first");

        var secondWord = await DataModel.GetAtTime<Word>(secondCommit.DateTime.AddMinutes(5), entityId);
        secondWord.Should().NotBeNull();
        secondWord.Text.Should().Be("second");

        //just before the 3rd commit should still be second
        secondWord = await DataModel.GetAtTime<Word>(thirdCommit.DateTime.Subtract(TimeSpan.FromSeconds(5)), entityId);
        secondWord.Should().NotBeNull();
        secondWord.Text.Should().Be("second");

        var thirdWord = await DataModel.GetAtTime<Word>(thirdCommit.DateTime.AddMinutes(5), entityId);
        thirdWord.Should().NotBeNull();
        thirdWord.Text.Should().Be("third");
    }

    private Task ClearNonRootSnapshots()
    {
        return DbContext.Snapshots.Where(s => !s.IsRoot).ExecuteDeleteAsync();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
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
            var snapshots = await DataModel.GetSnapshotsAtCommit(changes[i]);
            var entry = snapshots[entityId].Entity.Is<Word>();
            entry.Text.Should().Be($"change {i}");
            snapshots.Values.Should().HaveCount(1 + i);
        }
    }

    [Fact]
    public async Task WorstCaseSnapshotReApply()
    {
        int changeCount = 1_000;
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "first"));
        //adding all in one AddRange means there's sparse snapshots
        await AddCommitsViaSync(Enumerable.Range(0, changeCount)
            .Select(i => WriteNextChange(SetWord(entityId, $"change {i}"), false).Result));

        var latestSnapshot = await DataModel.GetLatestSnapshotByObjectId(entityId);
        //delete snapshots so when we get at then we need to re-apply
        await DbContext.Snapshots.Where(s => !s.IsRoot).ExecuteDeleteAsync();

        var computedModelSnapshots = await DataModel.GetSnapshotsAtCommit(latestSnapshot.Commit);

        var entitySnapshot = computedModelSnapshots.Should().ContainSingle().Subject.Value;
        entitySnapshot.Should().BeEquivalentTo(latestSnapshot, options => options.Excluding(snapshot => snapshot.Id).Excluding(snapshot => snapshot.Commit).Excluding(s => s.Entity.DbObject));
        var latestSnapshotEntry = latestSnapshot.Entity.Is<Word>();
        var entitySnapshotEntry = entitySnapshot.Entity.Is<Word>();
        entitySnapshotEntry.Text.Should().Be(latestSnapshotEntry.Text);
    }
}
