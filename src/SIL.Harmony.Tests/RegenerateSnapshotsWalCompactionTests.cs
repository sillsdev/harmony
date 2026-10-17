using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// Same as <see cref="RegenerateSnapshotsCompactionTests"/> but in WAL mode, where VACUUM leaves a log as big as the
/// database behind unless it's truncated afterwards
/// </summary>
public class RegenerateSnapshotsWalCompactionTests : DataModelTestBase
{
    private static readonly string DbPath = Path.Combine(Path.GetTempPath(), $"harmony-wal-compaction-{Guid.NewGuid():N}.db");

    public RegenerateSnapshotsWalCompactionTests() : base(new SqliteConnection($"Data Source={DbPath}"))
    {
        DbContext.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    [Fact]
    public async Task RegenerateSnapshots_TruncatesTheWriteAheadLog()
    {
        var token = TestContext.Current.CancellationToken;
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "test root"));
        await WriteNextChange(SetWord(entityId, "test1"));
        //some bulk so the WAL VACUUM writes is clearly bigger than nothing
        await DbContext.Database.ExecuteSqlRawAsync("CREATE TABLE Filler (Id INTEGER PRIMARY KEY, Payload TEXT);", token);
        await DbContext.Database.ExecuteSqlRawAsync(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) INSERT INTO Filler (Payload) SELECT hex(randomblob(256)) FROM n;", token);
        var walPath = DbPath + "-wal";
        new FileInfo(walPath).Length.Should().BeGreaterThan(0);
        var beforeRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(token);

        await DataModel.RegenerateSnapshots();

        new FileInfo(walPath).Length.Should().Be(0);
        var afterRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(token);
        afterRegenerate.Should().BeEquivalentTo(beforeRegenerate);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
        File.Delete(DbPath + "-wal");
        File.Delete(DbPath + "-shm");
    }
}
