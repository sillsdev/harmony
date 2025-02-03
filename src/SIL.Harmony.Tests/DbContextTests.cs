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
        await DbContext.SaveChangesAsync();
        var actualCommit = await DbContext.Commits.AsNoTracking().SingleOrDefaultAsyncEF(c => c.Id == commitId);
        actualCommit!.HybridDateTime.DateTime.Should().Be(expectedDateTime, "EF");
        actualCommit = await DbContext.Commits.ToLinqToDB().SingleOrDefaultAsyncLinqToDB(c => c.Id == commitId);
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
            .InsertAsync();
        var actualCommit = await DbContext.Commits.SingleOrDefaultAsyncEF(c => c.Id == commitId);
        actualCommit!.HybridDateTime.DateTime.Should().Be(expectedDateTime, "EF");
        actualCommit = await DbContext.Commits.ToLinqToDB().SingleOrDefaultAsyncLinqToDB(c => c.Id == commitId);
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

        await DbContext.SaveChangesAsync();
        var commits = await DbContext.Commits
            .Where(c => c.HybridDateTime.DateTime > baseDateTime.Add(new TimeSpan((long)(25 * scale))))
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ToArrayAsyncEF();
        commits.Should().HaveCount(24);
    }
}
