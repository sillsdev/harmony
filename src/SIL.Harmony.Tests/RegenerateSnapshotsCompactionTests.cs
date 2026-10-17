using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// Compaction only shows up on a file database, the shared test.db is used by other tests, so this one gets its own file
/// </summary>
public class RegenerateSnapshotsCompactionTests : DataModelTestBase
{
    private static readonly string DbPath = Path.Combine(Path.GetTempPath(), $"harmony-compaction-{Guid.NewGuid():N}.db");

    public RegenerateSnapshotsCompactionTests() : base(new SqliteConnection($"Data Source={DbPath}"))
    {
    }

    [Fact]
    public async Task RegenerateSnapshots_ReturnsFreedPagesToTheFile()
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "test root"));
        await WriteNextChange(SetWord(entityId, "test1"));
        await LeaveAHole();
        var beforeRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(TestContext.Current.CancellationToken);

        await DataModel.RegenerateSnapshots();

        (await FreelistCount()).Should().Be(0);
        var afterRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(TestContext.Current.CancellationToken);
        afterRegenerate.Should().BeEquivalentTo(beforeRegenerate);
    }

    [Fact]
    public async Task FirstSyncIntoAnEmptyDatabase_CompactsTheFile()
    {
        await LeaveAHole();
        var entityId = Guid.NewGuid();
        var commits = new[] { NextCommit(SetWord(entityId, "test root")), NextCommit(SetWord(entityId, "test1")) };

        await AddCommitsViaSync(commits);

        (await FreelistCount()).Should().Be(0);
    }

    [Fact]
    public async Task LaterSyncs_LeaveTheFileAlone()
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "test root"));
        await LeaveAHole();

        await AddCommitsViaSync([NextCommit(SetWord(entityId, "test1"))]);

        (await FreelistCount()).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CompactDatabase_CanBeCalledByTheApplication()
    {
        await LeaveAHole();

        await DataModel.CompactDatabase();

        (await FreelistCount()).Should().Be(0);
    }

    /// <summary>a dropped table of filler rows puts its pages on the freelist, where only VACUUM takes them off</summary>
    private async Task LeaveAHole()
    {
        var token = TestContext.Current.CancellationToken;
        await DbContext.Database.ExecuteSqlRawAsync("CREATE TABLE Filler (Id INTEGER PRIMARY KEY, Payload TEXT);", token);
        await DbContext.Database.ExecuteSqlRawAsync(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) INSERT INTO Filler (Payload) SELECT hex(randomblob(256)) FROM n;", token);
        await DbContext.Database.ExecuteSqlRawAsync("DROP TABLE Filler;", token);
        (await FreelistCount()).Should().BeGreaterThan(0);
    }

    private async Task<long> FreelistCount()
    {
        var connection = DbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA freelist_count;";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
    }
}
