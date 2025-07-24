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
    private readonly ICrdtRepository _repository;
    private readonly SampleDbContext _crdtDbContext;

    public RepositoryTests()
    {
        _services = new ServiceCollection()
            .AddCrdtDataSample(":memory:")
            .BuildServiceProvider();

        _repository = _services.GetRequiredService<ICrdtRepositoryFactory>().CreateRepositorySync();
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
        await _repository.DisposeAsync();
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

    private ObjectSnapshot Snapshot(Guid entityId, Guid commitId, HybridDateTime time, string? text = null)
    {
        return new(new Word { Text = text ?? "test", Id = entityId }, Commit(commitId, time), false) { };
    }

    private async Task AddSnapshots(IEnumerable<ObjectSnapshot> snapshots)
    {
        _crdtDbContext.AddRange(snapshots);
        await _crdtDbContext.SaveChangesAsync();
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
    public async Task AddSnapshots_Works()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Guid.NewGuid(), Time(1, 0));
        await _repository.AddCommit(snapshot.Commit);

        await _repository.AddSnapshots([snapshot]);

        _crdtDbContext.ChangeTracker.Clear();
        var actualSnapshot = await _crdtDbContext.Snapshots.SingleAsync(s => s.Id == snapshot.Id);
        actualSnapshot.Should().BeEquivalentTo(snapshot, o => o
            .Excluding(s => s.Entity.DbObject)
            .Excluding(s => s.Commit));
    }

    [Fact]
    public async Task AddSnapshots_StoresReferences()
    {
        var wordId = Guid.NewGuid();
        var snapshot = Snapshot(wordId, Guid.NewGuid(), Time(1, 0));
        await _repository.AddCommit(snapshot.Commit);
        await _repository.AddSnapshots([snapshot]);
        var commit = Commit(Guid.NewGuid(), Time(2, 0));
        await _repository.AddCommit(commit);
        var defSnapshot = new ObjectSnapshot(
            new Definition
            {
                Id = Guid.NewGuid(),
                Text = "word",
                Order = 0,
                PartOfSpeech = "noun",
                WordId = wordId
            },
            commit,
            true
        );
        await _repository.AddSnapshots([defSnapshot]);

        _crdtDbContext.ChangeTracker.Clear();
        var actualSnapshot = await _crdtDbContext.Snapshots.SingleAsync(s => s.Id == defSnapshot.Id);
        actualSnapshot.Should().BeEquivalentTo(defSnapshot, o => o
            .Excluding(s => s.Entity.DbObject)
            .Excluding(s => s.Commit));
    }

    [Fact]
    public async Task AddSnapshots_HandlesTheSameSnapshotTwice()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Guid.NewGuid(), Time(1, 0));
        await _repository.AddCommit(snapshot.Commit);

        await _repository.AddSnapshots([snapshot]);
        await _repository.AddSnapshots([snapshot]);
        _crdtDbContext.Snapshots.Should().ContainSingle(s => s.Id == snapshot.Id);
    }

    [Fact]
    public async Task AddSnapshots_Works_InsertsIntoProjectedTable()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Guid.NewGuid(), Time(1, 0));
        await _repository.AddCommit(snapshot.Commit);

        await _repository.AddSnapshots([snapshot]);

        _crdtDbContext.Set<Word>().Should().Contain(w => w.Id == snapshot.EntityId);
    }

    [Fact]
    public async Task AddSnapshots_Works_UpdatesProjectedTable()
    {
        var entityId = Guid.NewGuid();
        await AddSnapshots([Snapshot(entityId, Guid.NewGuid(), Time(1, 0))]);

        var snapshot = Snapshot(entityId, Guid.NewGuid(), Time(2, 0), text: "updated");
        await _repository.AddCommit(snapshot.Commit);
        await _repository.AddSnapshots([snapshot]);

        var word = await _crdtDbContext.Set<Word>().FindAsync(entityId);
        word.Should().NotBeNull();
        word.Text.Should().Be("updated");
    }

    [Fact]
    public async Task AddSnapshots_Works_DeletesFromProjectedTable()
    {
        var entityId = Guid.NewGuid();
        var firstSnapshot = Snapshot(entityId, Guid.NewGuid(), Time(1, 0));
        await _repository.AddCommit(firstSnapshot.Commit);
        await _repository.AddSnapshots([firstSnapshot]);
        _crdtDbContext.Set<Word>().Should().ContainSingle(w => w.Id == entityId);

        var time = Time(2, 0);
        var snapshot = new ObjectSnapshot(new Word
            {
                Text = "test",
                Id = entityId,
                //mark as deleted
                DeletedAt = time.DateTime
            },
            Commit(Guid.NewGuid(), time),
            false);
        await _repository.AddCommit(snapshot.Commit);
        await _repository.AddSnapshots([snapshot]);

        _crdtDbContext.Set<Word>().Should().NotContain(w => w.Id == entityId);
    }

    [Fact]
    public async Task AddSnapshots_DeletesFirst()
    {
        var firstTagId = Guid.NewGuid();
        var secondTagId = Guid.NewGuid();
        //must be the same text so that the unique constraint is violated if the delete is not executed first
        var tagText = "tag";
        await AddSnapshots([
            new ObjectSnapshot(new Tag()
                {
                    Id = firstTagId,
                    Text = tagText
                },
                Commit(Guid.NewGuid(), Time(1, 0)),
                true)
        ]);
        var secondCommit = Commit(Guid.NewGuid(), Time(2, 0));
        await _repository.AddCommit(secondCommit);

        //act
        await _repository.AddSnapshots([
            new ObjectSnapshot(new Tag()
                {
                    Id = secondTagId,
                    Text = tagText
                },
                secondCommit,
                true),
            new ObjectSnapshot(new Tag()
                {
                    Id = firstTagId,
                    Text = tagText,
                    DeletedAt = secondCommit.DateTime
                },
                secondCommit,
                false),
        ]);

        //assert
        _crdtDbContext.Set<Tag>().Should().ContainSingle(t => t.Id == secondTagId);
    }

    [Fact]
    public async Task AddSnapshots_LastSnapshotWins()
    {
        var wordId = Guid.NewGuid();
        var firstCommit = Commit(Guid.NewGuid(), Time(1, 0));
        var secondCommit = Commit(Guid.NewGuid(), Time(2, 0));
        await _repository.AddCommit(firstCommit);
        await _repository.AddCommit(secondCommit);

        await _repository.AddSnapshots([
            new ObjectSnapshot(new Word()
                {
                    Id = wordId,
                    Text = "first"
                },
                firstCommit,
                true),
            new ObjectSnapshot(new Word()
                {
                    Id = wordId,
                    Text = "second"
                },
                secondCommit,
                false)
        ]);

        _crdtDbContext.Set<Word>().Should().ContainSingle(w => w.Id == wordId).Which.Text.Should().Be("second");
    }

    [Fact]
    public async Task AddSnapshots_InsertsInTheCorrectOrder()
    {
        var wordId = Guid.NewGuid();
        var firstCommit = Commit(Guid.NewGuid(), Time(1, 0));
        var secondCommit = Commit(Guid.NewGuid(), Time(2, 0));
        await _repository.AddCommit(firstCommit);
        await _repository.AddCommit(secondCommit);

        await _repository.AddSnapshots([
            new ObjectSnapshot(new Word()
                {
                    Id = wordId,
                    Text = "first"
                },
                firstCommit,
                true),
            new ObjectSnapshot(new Definition()
                {
                    Id = Guid.NewGuid(),
                    Text = "second",
                    WordId = wordId,
                    Order = 0,
                    PartOfSpeech = "noun"
                },
                secondCommit,
                false)
        ]);

        _crdtDbContext.Set<Word>().Should().ContainSingle(w => w.Id == wordId).Which.Text.Should().Be("first");
        _crdtDbContext.Set<Definition>().Should().ContainSingle(d => d.WordId == wordId).Which.Text.Should().Be("second");
    }

    [Fact]
    public async Task CurrentSnapshots_Works()
    {
        await AddSnapshots([Snapshot(Guid.NewGuid(), Guid.NewGuid(), Time(1, 0))]);
        var snapshots = await _repository.CurrentSnapshots().ToArrayAsync();
        snapshots.Should().ContainSingle();
    }

    [Fact]
    public async Task CurrentSnapshots_CanFilterByRefs()
    {
        var wordId = Guid.NewGuid();
        var snapshot = Snapshot(wordId, Guid.NewGuid(), Time(1, 0));
        await _repository.AddCommit(snapshot.Commit);
        await _repository.AddSnapshots([snapshot]);
        var commit = Commit(Guid.NewGuid(), Time(2, 0));
        await _repository.AddCommit(commit);
        var defSnapshot = new ObjectSnapshot(
            new Definition
            {
                Id = Guid.NewGuid(),
                Text = "word",
                Order = 0,
                PartOfSpeech = "noun",
                WordId = wordId
            },
            commit,
            true
        );
        await _repository.AddSnapshots([defSnapshot]);

        var objectSnapshots = await _repository.CurrentSnapshots().Where(s => s.References.Contains(wordId)).ToArrayAsync();
        objectSnapshots.Should().ContainSingle().Which.Id.Should().Be(defSnapshot.Id);
    }

    [Fact]
    public async Task CurrentSnapshots_GroupsByEntityIdSortedByTime()
    {
        var entityId = Guid.NewGuid();
        var expectedTime = Time(2, 0);
        await AddSnapshots([
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
        await AddSnapshots([
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
        await AddSnapshots([
            Snapshot(entityId, ids[0], time),
            Snapshot(entityId, ids[1], time),
        ]);
        var snapshots = await _repository.CurrentSnapshots().Include(s => s.Commit).ToArrayAsync();
        snapshots.Should().ContainSingle().Which.Commit.Id.Should().Be(ids[1]);
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
        await AddSnapshots([
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
        await AddSnapshots([
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
        await AddSnapshots([
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
        await AddSnapshots([
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
        await AddSnapshots([
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
