using SIL.Harmony.Core;
using SIL.Harmony.Db;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Models;
using SIL.Harmony.Tests.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;

namespace SIL.Harmony.Tests;

public class RepositoryTests : IAsyncLifetime
{
    private readonly ServiceProvider _services;
    private readonly CrdtRepository _repository;
    private readonly SampleDbContext _crdtDbContext;

    public RepositoryTests()
    {
        _services = new ServiceCollection()
            .AddCrdtDataSample(":memory:")
            .BuildServiceProvider();

        _repository = _services.GetRequiredService<CrdtRepository>();
        _crdtDbContext = _services.GetRequiredService<SampleDbContext>();
    }

    public async Task InitializeAsync()
    {
        // Open the connection manually, otherwise it will be closed after each command, wiping out the in memory sqlite db
        await _crdtDbContext.Database.OpenConnectionAsync();
        await _crdtDbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
    }

    private Commit Commit(Guid id, HybridDateTime hybridDateTime)
    {
        var entityId = Guid.NewGuid();
        return new Commit(id)
        {
            ClientId = Guid.Empty, HybridDateTime = hybridDateTime, ChangeEntities =
            [
                new ChangeEntity<IChange>()
                {
                    Change = new SetWordTextChange(entityId, "test"),
                    CommitId = id,
                    EntityId = entityId,
                    Index = 0
                }
            ]
        };
    }

    private ObjectSnapshot Snapshot(Guid entityId, Guid commitId, HybridDateTime time)
    {
        return new(new Word { Text = "test", Id = entityId }, Commit(commitId, time), false) { };
    }

    private Guid[] OrderedIds(int count)
    {
        return Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).Order().ToArray();
    }

    private HybridDateTime Time(int hour, int counter) => MockTimeProvider.Time(hour, counter);

    [Fact]
    public void GetLatestDateTime_Works()
    {
        _repository.GetLatestDateTime().Should().BeNull();
    }

    [Fact]
    public async Task GetLatestDateTime_ReturnsCommitDateTime()
    {
        var expectedDateTime = new HybridDateTime(new DateTime(2000, 1, 1), 0);
        await _repository.AddCommit(Commit(Guid.NewGuid(), expectedDateTime));
        _repository.GetLatestDateTime().Should().Be(expectedDateTime);
    }

    [Fact]
    public async Task GetLatestDateTime_ReturnsLatestCommitDateTime()
    {
        var commit1Time = Time(1, 0);
        var commit2Time = Time(2, 0);
        await _repository.AddCommits([
            Commit(Guid.NewGuid(), commit1Time),
            Commit(Guid.NewGuid(), commit2Time),
        ]);
        _repository.GetLatestDateTime().Should().Be(commit2Time);
    }

    [Fact]
    public async Task GetLatestDateTime_ReturnsLatestCommitDateTimeByCount()
    {
        var commit1Time = Time(1, 0);
        var commit2Time = Time(1, 1);
        await _repository.AddCommits([
            Commit(Guid.NewGuid(), commit1Time),
            Commit(Guid.NewGuid(), commit2Time),
        ]);
        _repository.GetLatestDateTime().Should().Be(commit2Time);
    }

    [Fact]
    public async Task CurrentCommits_OrdersCommitsByDate()
    {
        var commit1Time = Time(1, 0);
        var commit2Time = Time(2, 0);
        await _repository.AddCommits([
            Commit(Guid.NewGuid(), commit1Time),
            Commit(Guid.NewGuid(), commit2Time),
        ]);
        var commits = await _repository.CurrentCommits().ToArrayAsync();
        commits.Select(c => c.HybridDateTime).Should().ContainInConsecutiveOrder(commit1Time, commit2Time);
    }

    [Fact]
    public async Task CurrentCommits_OrdersCommitsByCounter()
    {
        var commit1Time = Time(1, 0);
        var commit2Time = Time(1, 1);
        await _repository.AddCommits([
            Commit(Guid.NewGuid(), commit1Time),
            Commit(Guid.NewGuid(), commit2Time),
        ]);
        var commits = await _repository.CurrentCommits().ToArrayAsync();
        commits.Select(c => c.HybridDateTime).Should().ContainInConsecutiveOrder(commit1Time, commit2Time);
    }

    [Fact]
    public async Task CurrentCommits_OrdersCommitsById()
    {
        var commitTime = Time(1, 0);
        var ids = OrderedIds(2);
        await _repository.AddCommits([
            Commit(ids[0], commitTime),
            Commit(ids[1], commitTime),
        ]);
        var commits = await _repository.CurrentCommits().ToArrayAsync();
        commits.Select(c => c.Id).Should().ContainInConsecutiveOrder(ids);
    }

    [Fact]
    public async Task CurrentSnapshots_Works()
    {
        await _repository.AddSnapshots([Snapshot(Guid.NewGuid(), Guid.NewGuid(), Time(1, 0))]);
        var snapshots = await _repository.CurrentSnapshots().ToArrayAsync();
        snapshots.Should().ContainSingle();
    }

    [Fact]
    public async Task CurrentSnapshots_GroupsByEntityIdSortedByTime()
    {
        var entityId = Guid.NewGuid();
        var expectedTime = Time(2, 0);
        await _repository.AddSnapshots([
            Snapshot(entityId, Guid.NewGuid(), Time(1, 0)),
            Snapshot(entityId, Guid.NewGuid(), expectedTime),
        ]);
        var snapshots = await _repository.CurrentSnapshots().Include(s => s.Commit).ToArrayAsync();
        snapshots.Should().ContainSingle().Which.Commit.HybridDateTime.Should().BeEquivalentTo(expectedTime);
    }

    [Fact]
    public async Task CurrentSnapshots_GroupsByEntityIdSortedByCount()
    {
        var entityId = Guid.NewGuid();
        await _repository.AddSnapshots([
            Snapshot(entityId, Guid.NewGuid(), Time(1, 0)),
            Snapshot(entityId, Guid.NewGuid(), Time(1, 1)),
        ]);
        var snapshots = await _repository.CurrentSnapshots().Include(s => s.Commit).ToArrayAsync();
        snapshots.Should().ContainSingle().Which.Commit.HybridDateTime.Counter.Should().Be(1);
    }

    [Fact]
    public async Task CurrentSnapshots_GroupsByEntityIdSortedByCommitId()
    {
        var time = Time(1, 1);
        var entityId = Guid.NewGuid();
        var ids = OrderedIds(2);
        await _repository.AddSnapshots([
            Snapshot(entityId, ids[0], time),
            Snapshot(entityId, ids[1], time),
        ]);
        var snapshots = await _repository.CurrentSnapshots().Include(s => s.Commit).ToArrayAsync();
        snapshots.Should().ContainSingle().Which.Commit.Id.Should().Be(ids[1]);
    }

    [Fact]
    public async Task CurrentSnapshots_FiltersByDate()
    {
        var entityId = Guid.NewGuid();
        var commit1Time = Time(1, 0);
        var commit2Time = Time(3, 0);
        await _repository.AddSnapshots([
            Snapshot(entityId, Guid.NewGuid(), commit1Time),
            Snapshot(entityId, Guid.NewGuid(), commit2Time),
        ]);

        var snapshots = await _repository.CurrentSnapshots().Include(s => s.Commit).ToArrayAsync();
        snapshots.Should().ContainSingle().Which.Commit.HybridDateTime.Should().BeEquivalentTo(commit2Time);

        var newCurrentTime = Time(2, 0).DateTime;
        snapshots = await _repository.GetScopedRepository(newCurrentTime).CurrentSnapshots().Include(s => s.Commit).ToArrayAsync();
        snapshots.Should().ContainSingle().Which.Commit.HybridDateTime.Should().BeEquivalentTo(commit1Time);
    }

    [Fact]
    public async Task ScopedRepo_CurrentSnapshots_FiltersByCounter()
    {
        var entityId = Guid.NewGuid();
        //not sorting as we want to order based on the hybrid date time counter
        Guid[] commitIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var snapshot1 = Snapshot(entityId, commitIds[0], Time(1, 0));
        var snapshot2 = Snapshot(entityId, commitIds[1], Time(2, 0));
        var snapshot3 = Snapshot(entityId, commitIds[2], Time(2, 1));
        await _repository.AddSnapshots([
            snapshot3,
            snapshot1,
            snapshot2,
        ]);

        var snapshots = await _repository.CurrentSnapshots().Include(s => s.Commit).ToArrayAsync();
        var commit = snapshots.Should().ContainSingle().Subject.Commit;
        commit.Id.Should().Be(commitIds[2]);

        snapshots = await _repository.GetScopedRepository(snapshot2.Commit).CurrentSnapshots().Include(s => s.Commit)
            .ToArrayAsync();
        commit = snapshots.Should().ContainSingle().Subject.Commit;
        commit.Id.Should().Be(commitIds[1], $"commit order: [{string.Join(", ", commitIds)}]");
    }

    [Fact]
    public async Task ScopedRepo_CurrentSnapshots_FiltersByCommitId()
    {
        var entityId = Guid.NewGuid();
        Guid[] commitIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        Array.Sort(commitIds);
        var snapshot1 = Snapshot(entityId, commitIds[0], Time(1, 0));
        var snapshot2 = Snapshot(entityId, commitIds[1], Time(2, 0));
        var snapshot3 = Snapshot(entityId, commitIds[2], Time(2, 0));
        await _repository.AddSnapshots([
            snapshot3,
            snapshot1,
            snapshot2,
        ]);

        var snapshots = await _repository.CurrentSnapshots().Include(s => s.Commit).ToArrayAsync();
        var commit = snapshots.Should().ContainSingle().Subject.Commit;
        commit.Id.Should().Be(commitIds[2]);

        snapshots = await _repository.GetScopedRepository(snapshot2.Commit).CurrentSnapshots().Include(s => s.Commit).ToArrayAsync();
        commit = snapshots.Should().ContainSingle().Subject.Commit;
        commit.Id.Should().Be(commitIds[1], $"commit order: [{string.Join(", ", commitIds)}]");
    }

    [Fact]
    public async Task DeleteStaleSnapshots_Works()
    {
        await _repository.DeleteStaleSnapshots(Commit(Guid.NewGuid(), Time(1, 0)));
    }

    [Fact]
    public async Task DeleteStaleSnapshots_DeletesSnapshotsAfterCommitByTime()
    {
        await _repository.AddSnapshots([
            Snapshot(Guid.NewGuid(), Guid.NewGuid(), Time(1, 0)),
            Snapshot(Guid.NewGuid(), Guid.NewGuid(), Time(3, 0)),
        ]);
        await _repository.DeleteStaleSnapshots(Commit(Guid.NewGuid(), Time(2, 0)));

        _crdtDbContext.Snapshots.Include(s => s.Commit).Should().ContainSingle()
            .Which.Commit.HybridDateTime.DateTime.Hour.Should().Be(1);
    }

    [Fact]
    public async Task DeleteStaleSnapshots_DeletesSnapshotsAfterCommitByCount()
    {
        await _repository.AddSnapshots([
            Snapshot(Guid.NewGuid(), Guid.NewGuid(), Time(1, 0)),
            Snapshot(Guid.NewGuid(), Guid.NewGuid(), Time(1, 2)),
        ]);
        await _repository.DeleteStaleSnapshots(Commit(Guid.NewGuid(), Time(1, 1)));

        _crdtDbContext.Snapshots.Include(s => s.Commit).Should().ContainSingle()
            .Which.Commit.HybridDateTime.Counter.Should().Be(0);
    }

    [Fact]
    public async Task DeleteStaleSnapshots_DeletesSnapshotsAfterCommitByCommitId()
    {
        var time = Time(1, 1);
        var entityId = Guid.NewGuid();
        var ids = OrderedIds(3);
        await _repository.AddSnapshots([
            Snapshot(entityId, ids[0], time),
            Snapshot(entityId, ids[2], time),
        ]);
        await _repository.DeleteStaleSnapshots(Commit(ids[1], time));

        _crdtDbContext.Snapshots.Should().ContainSingle()
            .Which.CommitId.Should().Be(ids[0]);
    }

    [Fact]
    public async Task GetChanges_HandlesExactDateFilters()
    {
        var tmpTime = Time(2, 0);
        //by adding a tick we cause an error and commit 2 will be returned by the query
        var commit2Time = tmpTime with { DateTime = tmpTime.DateTime.AddTicks(1) };
        await _repository.AddCommits([
            Commit(Guid.NewGuid(), Time(1, 0)),
            Commit(Guid.NewGuid(), commit2Time),
            Commit(Guid.NewGuid(), Time(3, 0)),
        ]);

        var changes = await _repository.GetChanges(new SyncState(new()
        {
            { Guid.Empty, commit2Time.DateTime.ToUnixTimeMilliseconds() }
        }));
        changes.MissingFromClient.Select(c => c.DateTime.ToUnixTimeMilliseconds()).Should().ContainSingle("because {0} is only before the last commit", commit2Time.DateTime.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task AddCommit_RoundTripsData()
    {
        var commit = Commit(Guid.NewGuid(), Time(1, 0));
        await _repository.AddCommit(commit);

        var queriedCommit = _repository.CurrentCommits()
            .AsNoTracking()//ensures that the commit which is tracked above is not returned
            .Include(c => c.ChangeEntities)
            .Should().ContainSingle().Subject;
        queriedCommit.Should().NotBeSameAs(commit).And.BeEquivalentTo(commit);
    }

    [Fact]
    public async Task FindPreviousCommit_Works()
    {
        var commit1 = Commit(Guid.NewGuid(), Time(1, 0));
        var commit2 = Commit(Guid.NewGuid(), Time(2, 0));
        await _repository.AddCommits([commit1, commit2]);

        var previousCommit = await _repository.FindPreviousCommit(commit2);
        ArgumentNullException.ThrowIfNull(previousCommit);
        previousCommit.Id.Should().Be(commit1.Id);
    }

    [Fact]
    public async Task FindPreviousCommit_ReturnsNullForFirstCommit()
    {
        var commit1 = Commit(Guid.NewGuid(), Time(1, 0));
        var commit2 = Commit(Guid.NewGuid(), Time(2, 0));
        await _repository.AddCommits([commit1, commit2]);

        var previousCommit = await _repository.FindPreviousCommit(commit1);
        previousCommit.Should().BeNull();
    }
}
