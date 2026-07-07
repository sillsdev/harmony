using LinqToDB;
using LinqToDB.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SIL.Harmony.Tests;

public class DbContextTests: DataModelTestBase
{
    [Fact]
    public async Task VerifyModel()
    {
        await Verify(DbContext.Model.ToDebugString(MetadataDebugStringOptions.LongDefault));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-4)]
    public async Task CanRoundTripDatesFromEf(int offset)
    {
        var commitId = Guid.NewGuid();
        var expectedDateTime = new DateTimeOffset(2000, 1, 1, 1, 11, 11, TimeSpan.FromHours(offset));
        var commit = new Commit(commitId)
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = new HybridDateTime(expectedDateTime, 0)
        };
        DbContext.Add(commit);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var actualCommit = await DbContext.Commits.AsNoTracking().SingleOrDefaultAsyncEF(c => c.Id == commitId, TestContext.Current.CancellationToken);
        actualCommit!.HybridDateTime.DateTime.Should().Be(expectedDateTime, "EF");
        actualCommit = await DbContext.Commits.ToLinqToDB().SingleOrDefaultAsyncLinqToDB(c => c.Id == commitId, TestContext.Current.CancellationToken);
        actualCommit!.HybridDateTime.DateTime.Should().Be(expectedDateTime, "LinqToDB");
    }


    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-4)]
    public async Task CanRoundTripDatesFromLinq2Db(int offset)
    {

        var commitId = Guid.NewGuid();
        var expectedDateTime = new DateTimeOffset(2000, 1, 1, 1, 11, 11, TimeSpan.FromHours(offset));

        await DbContext.Set<Commit>().ToLinqToDBTable().AsValueInsertable()
            .Value(c => c.Id, commitId)
            .Value(c => c.ClientId, Guid.NewGuid())
            .Value(c => c.HybridDateTime.DateTime, expectedDateTime)
            .Value(c => c.HybridDateTime.Counter, 0)
            .Value(c => c.Metadata, new CommitMetadata())
            .Value(c => c.Hash, "")
            .Value(c => c.ParentHash, "")
            .InsertAsync(TestContext.Current.CancellationToken);
        var actualCommit = await DbContext.Commits.SingleOrDefaultAsyncEF(c => c.Id == commitId, TestContext.Current.CancellationToken);
        actualCommit!.HybridDateTime.DateTime.Should().Be(expectedDateTime, "EF");
        actualCommit = await DbContext.Commits.ToLinqToDB().SingleOrDefaultAsyncLinqToDB(c => c.Id == commitId, TestContext.Current.CancellationToken);
        actualCommit!.HybridDateTime.DateTime.Should().Be(expectedDateTime, "LinqToDB");
    }

    [Theory]
    [InlineData(TimeSpan.TicksPerHour)]
    [InlineData(TimeSpan.TicksPerMinute)]
    [InlineData(TimeSpan.TicksPerSecond)]
    [InlineData(TimeSpan.TicksPerMillisecond)]
    [InlineData(TimeSpan.TicksPerMicrosecond)]
    [InlineData(1)]
    public async Task CanFilterCommitsByDateTime(double scale)
    {
        var baseDateTime = await SeedCommitsAtTickScale(scale);
        var commits = await DbContext.Commits
            .Where(c => c.HybridDateTime.DateTime > baseDateTime.Add(new TimeSpan((long)(25 * scale))))
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ToArrayAsyncEF(TestContext.Current.CancellationToken);
        commits.Should().HaveCount(24);
    }

    //no sub-millisecond scales here: linq2db wraps SQLite timestamp comparisons in
    //strftime('%Y-%m-%d %H:%M:%f', ...), which normalizes both sides to milliseconds
    [Theory]
    [InlineData(TimeSpan.TicksPerHour)]
    [InlineData(TimeSpan.TicksPerMinute)]
    [InlineData(TimeSpan.TicksPerSecond)]
    [InlineData(TimeSpan.TicksPerMillisecond)]
    public async Task CanFilterCommitsByDateTimeViaLinq2db(double scale)
    {
        var baseDateTime = await SeedCommitsAtTickScale(scale);
        var cutoff = baseDateTime.Add(new TimeSpan((long)(25 * scale)));
        var commits = await DbContext.Commits
            .ToLinqToDB()
            .Where(c => c.HybridDateTime.DateTime > cutoff)
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ToArrayAsyncLinqToDB(TestContext.Current.CancellationToken);
        commits.Should().HaveCount(24);
    }

    //commits sharing a millisecond compare equal under linq2db (see above), so WhereAfter must
    //never see the target itself as "after": the parameter has to render byte-identical to the
    //stored text or strftime can normalize the two differently and delete the target commit
    [Fact]
    public async Task WhereAfterViaLinq2dbExcludesTheTargetCommit()
    {
        //sub-millisecond part must be >= 0.5ms: SQLite's strftime rounds to the nearest millisecond
        //while a truncating parameter rendering lands one millisecond lower, which is the disagreement
        var baseDateTime = new DateTimeOffset(2000, 1, 1, 1, 11, 11, TimeSpan.Zero).Add(TimeSpan.FromTicks(6000));
        for (int i = 0; i < 50; i++)
        {
            DbContext.Add(new Commit
            {
                ClientId = Guid.NewGuid(),
                HybridDateTime = new HybridDateTime(baseDateTime.Add(TimeSpan.FromTicks(i)), 0)
            });
        }

        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var commits = await DbContext.Commits.ToArrayAsyncEF(TestContext.Current.CancellationToken);
        foreach (var target in commits)
        {
            var after = await DbContext.Commits
                .WhereAfter(target)
                .ToLinqToDB()
                .Select(c => c.Id)
                .ToArrayAsyncLinqToDB(TestContext.Current.CancellationToken);
            after.Should().NotContain(target.Id,
                $"WhereAfter via linq2db should never include the target commit itself (target at {target.HybridDateTime.DateTime:O})");
        }
    }

    //characterizes the limitation documented in Linq2dbKernel rather than a desired behavior:
    //if the linq2db side starts seeing the later commit, its SQLite translation has changed —
    //revisit whether timestamp comparisons still need to stay in EF
    [Fact]
    public async Task Linq2dbCannotOrderCommitsWithinTheSameMillisecond()
    {
        var baseDateTime = new DateTimeOffset(2000, 1, 1, 1, 11, 11, TimeSpan.Zero);
        var earlier = new Commit(Guid.NewGuid())
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = new HybridDateTime(baseDateTime.Add(TimeSpan.FromTicks(100)), 0)
        };
        var later = new Commit(Guid.NewGuid())
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = new HybridDateTime(baseDateTime.Add(TimeSpan.FromTicks(200)), 0)
        };
        DbContext.AddRange(earlier, later);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var linq2dbCount = await DbContext.Commits.ToLinqToDB()
            .Where(c => c.HybridDateTime.DateTime > earlier.HybridDateTime.DateTime)
            .CountAsyncLinqToDB(TestContext.Current.CancellationToken);
        linq2dbCount.Should().Be(0, "linq2db compares SQLite timestamps at millisecond precision (strftime '%f')");

        var efCount = await DbContext.Commits
            .Where(c => c.HybridDateTime.DateTime > earlier.HybridDateTime.DateTime)
            .CountAsyncEF(TestContext.Current.CancellationToken);
        efCount.Should().Be(1, "EF compares the stored text at full precision");
    }

    private async Task<DateTimeOffset> SeedCommitsAtTickScale(double scale)
    {
        var baseDateTime = new DateTimeOffset(2000, 1, 1, 1, 11, 11, TimeSpan.Zero);
        for (int i = 0; i < 50; i++)
        {
            var offset = new TimeSpan((long)(i * scale));
            DbContext.Add(new Commit
            {
                ClientId = Guid.NewGuid(),
                HybridDateTime = new HybridDateTime(baseDateTime.Add(offset), 0)
            });
        }

        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return baseDateTime;
    }
}
