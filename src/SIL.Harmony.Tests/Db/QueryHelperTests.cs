using SIL.Harmony.Db;
using SIL.Harmony.Tests.Mocks;

namespace SIL.Harmony.Tests.Db;

public class QueryHelperTests
{
    private Guid id1 = Guid.NewGuid();
    private Guid id2 = Guid.NewGuid();
    private Guid id3 = Guid.NewGuid();

    private HybridDateTime Time(int hour, int counter) => MockTimeProvider.Time(hour, counter);

    private Commit Commit(Guid id, HybridDateTime hybridDateTime) =>
        new(id) { ClientId = Guid.Empty, HybridDateTime = hybridDateTime };

    private ObjectSnapshot Snapshot(Guid commitId, HybridDateTime hybridDateTime) =>
        ObjectSnapshot.ForTesting(Commit(commitId, hybridDateTime));

    private IQueryable<Commit> Commits(IEnumerable<Commit> commits) => commits.AsQueryable();
    private IQueryable<ObjectSnapshot> Snapshots(IEnumerable<ObjectSnapshot> snapshots) => snapshots.AsQueryable();

    private Guid[] OrderedIds(int count)
    {
        return Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).Order().ToArray();
    }

    [Fact]
    public void CommitDefaultOrder_SortsByDate()
    {
        var commits = Commits([
            Commit(id3, Time(3, 0)),
            Commit(id2, Time(2, 0)),
            Commit(id1, Time(1, 0)),
        ]);
        commits.DefaultOrder().Select(c => c.Id).Should().ContainInOrder([id1, id2, id3]);
    }

    [Fact]
    public void CommitDefaultOrder_SortsByCounter()
    {
        var commits = Commits([
            Commit(id3, Time(1, 3)),
            Commit(id2, Time(1, 2)),
            Commit(id1, Time(1, 1)),
        ]);
        commits.DefaultOrder().Select(c => c.Id).Should().ContainInOrder([id1, id2, id3]);
    }

    [Fact]
    public void CommitDefaultOrder_SortsById()
    {
        var commits = Commits([
            Commit(id3, Time(1, 1)),
            Commit(id2, Time(1, 1)),
            Commit(id1, Time(1, 1)),
        ]);
        Guid[] ids = [id1, id2, id3];
        Array.Sort(ids);
        commits.DefaultOrder().Select(c => c.Id).Should().ContainInOrder(ids);
    }

    [Fact]
    public void SnapshotDefaultOrder_SortsByDate()
    {
        var snapshots = Snapshots([
            Snapshot(id3, Time(3, 0)),
            Snapshot(id2, Time(2, 0)),
            Snapshot(id1, Time(1, 0)),
        ]);
        snapshots.DefaultOrder().Select(s => s.CommitId).Should().ContainInOrder([id1, id2, id3]);
    }

    [Fact]
    public void SnapshotDefaultOrder_SortsByCounter()
    {
        var snapshots = Snapshots([
            Snapshot(id3, Time(1, 3)),
            Snapshot(id2, Time(1, 2)),
            Snapshot(id1, Time(1, 1)),
        ]);
        snapshots.DefaultOrder().Select(s => s.CommitId).Should().ContainInOrder([id1, id2, id3]);
    }

    [Fact]
    public void SnapshotDefaultOrder_SortsById()
    {
        var snapshots = Snapshots([
            Snapshot(id3, Time(1, 1)),
            Snapshot(id2, Time(1, 1)),
            Snapshot(id1, Time(1, 1)),
        ]);
        Guid[] ids = [id1, id2, id3];
        Array.Sort(ids);
        snapshots.DefaultOrder().Select(s => s.CommitId).Should().ContainInOrder(ids);
    }

    [Fact]
    public void CommitDefaultOrderDescending_SortsByDate()
    {
        var commits = Commits([
            Commit(id3, Time(3, 0)),
            Commit(id1, Time(1, 0)),
            Commit(id2, Time(2, 0)),
        ]);
        commits.DefaultOrderDescending().Select(c => c.Id).Should().ContainInOrder([
            id3,
            id2,
            id1,
        ]);
    }

    [Fact]
    public void CommitDefaultOrderDescending_SortsByCounter()
    {
        var commits = Commits([
            Commit(id3, Time(1, 3)),
            Commit(id1, Time(1, 1)),
            Commit(id2, Time(1, 2)),
        ]);
        commits.DefaultOrderDescending().Select(c => c.Id).Should().ContainInOrder([
            id3,
            id2,
            id1,
        ]);
    }

    [Fact]
    public void CommitDefaultOrderDescending_SortsById()
    {
        var commits = Commits([
            Commit(id3, Time(1, 1)),
            Commit(id1, Time(1, 1)),
            Commit(id2, Time(1, 1)),
        ]);
        Guid[] ids = [id1, id2, id3];
        Array.Sort(ids);
        Array.Reverse(ids);
        commits.DefaultOrderDescending().Select(c => c.Id).Should().ContainInOrder(ids);
    }

    [Fact]
    public void SnapshotDefaultOrderDescending_SortsByDate()
    {
        var snapshots = Snapshots([
            Snapshot(id3, Time(3, 0)),
            Snapshot(id1, Time(1, 0)),
            Snapshot(id2, Time(2, 0)),
        ]);
        snapshots.DefaultOrderDescending().Select(s => s.CommitId).Should().ContainInOrder([
            id3,
            id2,
            id1,
        ]);
    }

    [Fact]
    public void SnapshotDefaultOrderDescending_SortsByCounter()
    {
        var snapshots = Snapshots([
            Snapshot(id3, Time(1, 3)),
            Snapshot(id1, Time(1, 1)),
            Snapshot(id2, Time(1, 2)),
        ]);
        snapshots.DefaultOrderDescending().Select(s => s.CommitId).Should().ContainInOrder([
            id3,
            id2,
            id1,
        ]);
    }

    [Fact]
    public void SnapshotDefaultOrderDescending_SortsById()
    {
        var snapshots = Snapshots([
            Snapshot(id3, Time(1, 1)),
            Snapshot(id1, Time(1, 1)),
            Snapshot(id2, Time(1, 1)),
        ]);
        Guid[] ids = [id1, id2, id3];
        Array.Sort(ids);
        Array.Reverse(ids);
        snapshots.DefaultOrderDescending().Select(s => s.CommitId).Should().ContainInOrder(ids);
    }

    [Fact]
    public void WhereAfterCommit_FiltersOnDate()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(2, 0));
        var commits = Commits([
            Commit(id1, Time(1, 0)),
            Commit(id2, Time(3, 0)),
            Commit(id3, Time(4, 0)),
        ]);
        commits.WhereAfter(filterCommit).Select(c => c.Id).Should().BeEquivalentTo([
            id2,
            id3
        ]);
    }

    [Fact]
    public void WhereAfterCommit_FiltersOnCounter()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(1, 2));
        var commits = Commits([
            Commit(id1, Time(1, 1)),
            Commit(id2, Time(1, 3)),
            Commit(id3, Time(1, 4)),
        ]);
        commits.WhereAfter(filterCommit).Select(c => c.Id).Should().BeEquivalentTo([
            id2,
            id3
        ]);
    }

    [Fact]
    public void WhereAfterCommit_FiltersOnId()
    {
        var hybridDateTime = Time(1, 1);
        Guid[] ids = OrderedIds(4);
        var commitId1 = ids[0];
        var filterCommit = Commit(ids[1], hybridDateTime);
        var commitId2 = ids[2];
        var commitId3 = ids[3];
        var commits = Commits([
            Commit(commitId1, hybridDateTime),
            Commit(commitId2, hybridDateTime),
            Commit(commitId3, hybridDateTime),
        ]);
        commits.WhereAfter(filterCommit).Select(c => c.Id).Should().BeEquivalentTo([
            commitId2,
            commitId3
        ]);
    }

    [Fact]
    public void WhereAfterSnapshot_FiltersOnDate()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(2, 0));
        var snapshots = Snapshots([
            Snapshot(id1, Time(1, 0)),
            Snapshot(id2, Time(3, 0)),
            Snapshot(id3, Time(4, 0)),
        ]);
        snapshots.WhereAfter(filterCommit).Select(s => s.CommitId).Should().BeEquivalentTo([
            id2,
            id3
        ]);
    }

    [Fact]
    public void WhereAfterSnapshot_FiltersOnCounter()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(1, 2));
        var snapshots = Snapshots([
            Snapshot(id1, Time(1, 1)),
            Snapshot(id2, Time(1, 3)),
            Snapshot(id3, Time(1, 4)),
        ]);
        snapshots.WhereAfter(filterCommit).Select(s => s.CommitId).Should().BeEquivalentTo([
            id2,
            id3
        ]);
    }

    [Fact]
    public void WhereAfterSnapshot_FiltersOnId()
    {
        var hybridDateTime = Time(1, 1);
        Guid[] ids = OrderedIds(4);
        var commitId1 = ids[0];
        var filterCommit = Commit(ids[1], hybridDateTime);
        var commitId2 = ids[2];
        var commitId3 = ids[3];
        var snapshots = Snapshots([
            Snapshot(commitId1, hybridDateTime),
            Snapshot(commitId2, hybridDateTime),
            Snapshot(commitId3, hybridDateTime),
        ]);
        snapshots.WhereAfter(filterCommit).Select(s => s.CommitId).Should().BeEquivalentTo([
            commitId2,
            commitId3
        ]);
    }



    [Fact]
    public void WhereBeforeSnapshot_FiltersOnDate()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(2, 0));
        var snapshots = Snapshots([
            Snapshot(id1, Time(1, 0)),
            Snapshot(id2, Time(3, 0)),
            Snapshot(filterCommit.Id, filterCommit.HybridDateTime),
            Snapshot(id3, Time(4, 0)),
        ]);
        snapshots.WhereBefore(filterCommit).Select(c => c.CommitId).Should().BeEquivalentTo([
            id1
        ]);
    }

    [Fact]
    public void WhereBeforeSnapshotInclusive_FiltersOnDate()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(2, 0));
        var snapshots = Snapshots([
            Snapshot(id1, Time(1, 0)),
            Snapshot(id2, Time(3, 0)),
            Snapshot(id3, Time(4, 0)),
            Snapshot(filterCommit.Id, filterCommit.HybridDateTime)
        ]);
        snapshots.WhereBefore(filterCommit, inclusive: true).Select(c => c.CommitId).Should().BeEquivalentTo([
            id1,
            filterCommit.Id
        ]);
    }

    [Fact]
    public void WhereBeforeSnapshot_FiltersOnCounter()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(1, 2));
        var snapshots = Snapshots([
            Snapshot(id1, Time(1, 1)),
            Snapshot(id2, Time(1, 3)),
            Snapshot(filterCommit.Id, filterCommit.HybridDateTime),
            Snapshot(id3, Time(1, 4)),
        ]);
        snapshots.WhereBefore(filterCommit).Select(c => c.CommitId).Should().BeEquivalentTo([
            id1,
        ]);
    }

    [Fact]
    public void WhereBeforeSnapshotInclusive_FiltersOnCounter()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(1, 2));
        var snapshots = Snapshots([
            Snapshot(id1, Time(1, 1)),
            Snapshot(id2, Time(1, 3)),
            Snapshot(id3, Time(1, 4)),
            Snapshot(filterCommit.Id, filterCommit.HybridDateTime),
        ]);
        snapshots.WhereBefore(filterCommit, inclusive: true).Select(c => c.CommitId).Should().BeEquivalentTo([
            id1,
            filterCommit.Id
        ]);
    }

    [Fact]
    public void WhereBeforeSnapshot_FiltersOnId()
    {
        var hybridDateTime = Time(1, 1);
        Guid[] ids = OrderedIds(4);
        var commitId1 = ids[0];
        var filterCommit = Commit(ids[1], hybridDateTime);
        var commitId2 = ids[2];
        var commitId3 = ids[3];
        var snapshots = Snapshots([
            Snapshot(commitId1, hybridDateTime),
            Snapshot(commitId2, hybridDateTime),
            Snapshot(commitId3, hybridDateTime),
            Snapshot(filterCommit.Id, filterCommit.HybridDateTime),
        ]);
        snapshots.WhereBefore(filterCommit).Select(c => c.CommitId).Should().BeEquivalentTo([
            commitId1
        ]);
    }

    [Fact]
    public void WhereBeforeSnapshotInclusive_FiltersOnId()
    {
        var hybridDateTime = Time(1, 1);
        Guid[] ids = OrderedIds(4);
        var commitId1 = ids[0];
        var filterCommit = Commit(ids[1], hybridDateTime);
        var commitId2 = ids[2];
        var commitId3 = ids[3];
        var snapshots = Snapshots([
            Snapshot(commitId1, hybridDateTime),
            Snapshot(filterCommit.Id, filterCommit.HybridDateTime),
            Snapshot(commitId2, hybridDateTime),
            Snapshot(commitId3, hybridDateTime),
        ]);
        snapshots.WhereBefore(filterCommit, inclusive: true).Select(c => c.CommitId).Should().BeEquivalentTo([
            commitId1,
            filterCommit.Id
        ]);
    }

    [Fact]
    public void WhereBeforeCommit_FiltersOnDate()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(2, 0));
        var commits = Commits([
            Commit(id1, Time(1, 0)),
            Commit(id2, Time(3, 0)),
            Commit(id3, Time(4, 0)),
            filterCommit
        ]);
        commits.WhereBefore(filterCommit).Select(c => c.Id).Should().BeEquivalentTo([
            id1
        ]);
    }

    [Fact]
    public void WhereBeforeCommitInclusive_FiltersOnDate()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(2, 0));
        var commits = Commits([
            Commit(id1, Time(1, 0)),
            Commit(id2, Time(3, 0)),
            Commit(id3, Time(4, 0)),
            filterCommit
        ]);
        commits.WhereBefore(filterCommit, inclusive: true).Select(c => c.Id).Should().BeEquivalentTo([
            id1,
            filterCommit.Id
        ]);
    }

    [Fact]
    public void WhereBeforeCommit_FiltersOnCounter()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(1, 2));
        var commits = Commits([
            Commit(id1, Time(1, 1)),
            Commit(id2, Time(1, 3)),
            Commit(id3, Time(1, 4)),
            filterCommit
        ]);
        commits.WhereBefore(filterCommit).Select(c => c.Id).Should().BeEquivalentTo([
            id1,
        ]);
    }

    [Fact]
    public void WhereBeforeCommitInclusive_FiltersOnCounter()
    {
        var filterCommit = Commit(Guid.NewGuid(), Time(1, 2));
        var commits = Commits([
            Commit(id1, Time(1, 1)),
            Commit(id2, Time(1, 3)),
            Commit(id3, Time(1, 4)),
            filterCommit
        ]);
        commits.WhereBefore(filterCommit, inclusive: true).Select(c => c.Id).Should().BeEquivalentTo([
            id1,
            filterCommit.Id
        ]);
    }

    [Fact]
    public void WhereBeforeCommit_FiltersOnId()
    {
        var hybridDateTime = Time(1, 1);
        Guid[] ids = OrderedIds(4);
        var commitId1 = ids[0];
        var filterCommit = Commit(ids[1], hybridDateTime);
        var commitId2 = ids[2];
        var commitId3 = ids[3];
        var commits = Commits([
            Commit(commitId1, hybridDateTime),
            Commit(commitId2, hybridDateTime),
            Commit(commitId3, hybridDateTime),
            filterCommit
        ]);
        commits.WhereBefore(filterCommit).Select(c => c.Id).Should().BeEquivalentTo([
            commitId1
        ]);
    }

    [Fact]
    public void WhereBeforeCommitInclusive_FiltersOnId()
    {
        var hybridDateTime = Time(1, 1);
        Guid[] ids = OrderedIds(4);
        var commitId1 = ids[0];
        var filterCommit = Commit(ids[1], hybridDateTime);
        var commitId2 = ids[2];
        var commitId3 = ids[3];
        var commits = Commits([
            Commit(commitId1, hybridDateTime),
            filterCommit,
            Commit(commitId2, hybridDateTime),
            Commit(commitId3, hybridDateTime),
        ]);
        commits.WhereBefore(filterCommit, inclusive: true).Select(c => c.Id).Should().BeEquivalentTo([
            commitId1,
            filterCommit.Id
        ]);
    }
}
