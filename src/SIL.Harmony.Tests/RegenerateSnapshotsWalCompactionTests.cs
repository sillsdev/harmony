using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// Same as <see cref="RegenerateSnapshotsCompactionTests"/> but in WAL mode, where VACUUM leaves a log as big as the
/// database behind unless it's truncated afterwards
/// </summary>
public class RegenerateSnapshotsWalCompactionTests : DataModelTestBase
{
    public RegenerateSnapshotsWalCompactionTests() : base(saveToDisk: true)
    {
        DbContext.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    private string WalPath => DbContext.Database.GetDbConnection().DataSource + "-wal";

    [Fact]
    public async Task RegenerateSnapshots_TruncatesTheWriteAheadLog()
    {
        var token = TestContext.Current.CancellationToken;
        await WriteNextChange(SetWord(Guid.NewGuid(), "test root"));
        await FillTheWal();
        var beforeRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(token);

        await DataModel.RegenerateSnapshots();

        new FileInfo(WalPath).Length.Should().Be(0);
        var afterRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(token);
        afterRegenerate.Should().BeEquivalentTo(beforeRegenerate);
    }

    private async Task FillTheWal()
    {
        var token = TestContext.Current.CancellationToken;
        await DbContext.Database.ExecuteSqlRawAsync("CREATE TABLE Filler (Id INTEGER PRIMARY KEY, Payload TEXT);", token);
        await DbContext.Database.ExecuteSqlRawAsync(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) INSERT INTO Filler (Payload) SELECT hex(randomblob(256)) FROM n;", token);
        new FileInfo(WalPath).Length.Should().BeGreaterThan(0);
    }
}
