using LinqToDB;
using LinqToDB.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SIL.Harmony.Tests;

public class DbContextTests : DataModelTestBase
{
    [Fact]
    public async Task VerifyModel()
    {
        await Verify(DbContext.Model.ToDebugString(MetadataDebugStringOptions.LongDefault));
    }

    // EFCore.ComplexIndexes only reaches EnsureCreated via UseComplexIndexes(); without it these
    // exist in migrated databases only.
    [Fact]
    public async Task EnsureCreatedCreatesTheComplexIndexes()
    {
        var indexNames = await GetIndexNames("Commits");
        indexNames.Should().Contain(["IX_Commits_DateTime_Counter_Id", "IX_Commits_Checkpoint"]);
    }

    [Fact]
    public async Task EnsureCreatedStillCreatesPlainIndexes()
    {
        var indexNames = await GetIndexNames("Snapshots");
        indexNames.Should().Contain(["IX_Snapshots_CommitId_EntityId", "IX_Snapshots_EntityId"]);
    }

    private async Task<List<string>> GetIndexNames(string table)
    {
        await using var command = DbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'index' and tbl_name = $table";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) names.Add(reader.GetString(0));
        return names;
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
            .Value(c => c.IsSnapshotCheckpoint, false)
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
        var commits = await DbContext.Commits
            .Where(c => c.HybridDateTime.DateTime > baseDateTime.Add(new TimeSpan((long)(25 * scale))))
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ToArrayAsyncEF(TestContext.Current.CancellationToken);
        commits.Should().HaveCount(24);
    }
}
