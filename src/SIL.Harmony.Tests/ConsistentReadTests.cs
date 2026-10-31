using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>a reader and a syncing writer on separate connections to one database file</summary>
public class ConsistentReadTests
{
    [Fact]
    public async Task InWalModeAReadRepositoryKeepsItsStateWhileASyncRewritesSnapshots()
    {
        using var file = new TempDbFile();
        await using var reader = new DataModelTestBase(new SqliteConnection(file.ConnectionString), alwaysValidate: false);
        await reader.DbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", TestContext.Current.CancellationToken);
        var (wordId, firstCommit) = await Seed(reader);
        //short, so a sync blocked by the read fails the test quickly instead of waiting out the default 30s
        await using var writer = new DataModelTestBase(new SqliteConnection(file.ConnectionString + ";Default Timeout=3"), alwaysValidate: false);

        await using (var repo = await reader.RepositoryFactory.CreateReadRepository())
        {
            (await TextOf(repo, wordId)).Should().Be("before");

            await writer.AddCommitsViaSync([LateCommit(writer, firstCommit, wordId)]);

            (await TextOf(repo, wordId)).Should().Be("before", "the late commit landed after the read started");
        }

        //tests share one context between repositories, so this also checks the read let go of it
        await using var afterRead = await reader.RepositoryFactory.CreateReadRepository();
        (await TextOf(afterRead, wordId)).Should().Be("late");
    }

    [Fact]
    public async Task OutsideWalModeAReadRepositoryDoesNotBlockASync()
    {
        using var file = new TempDbFile();
        await using var reader = new DataModelTestBase(new SqliteConnection(file.ConnectionString), alwaysValidate: false);
        await reader.DbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;", TestContext.Current.CancellationToken);
        var (wordId, firstCommit) = await Seed(reader);
        await using var writer = new DataModelTestBase(new SqliteConnection(file.ConnectionString + ";Default Timeout=3"), alwaysValidate: false);

        await using var repo = await reader.RepositoryFactory.CreateReadRepository();
        await TextOf(repo, wordId);

        await writer.Invoking(w => w.AddCommitsViaSync([LateCommit(w, firstCommit, wordId)])).Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposingAReadRepositoryLetsGoOfTheConnection()
    {
        using var file = new TempDbFile();
        await using var model = new DataModelTestBase(new SqliteConnection(file.ConnectionString), alwaysValidate: false);
        await model.DbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", TestContext.Current.CancellationToken);
        await model.DbContext.Database.CloseConnectionAsync();

        //tests share one context between repositories, as CrdtDbContextNoDisposeFactory does in an app
        await using (await model.RepositoryFactory.CreateReadRepository())
        {
        }

        model.DbContext.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);
    }

    private static async Task<(Guid wordId, Commit firstCommit)> Seed(DataModelTestBase model)
    {
        var wordId = Guid.NewGuid();
        Commit[] commits = [model.NextCommit(new SetWordTextChange(wordId, "before")), model.NextCommit(new SetWordNoteChange(wordId, "note"))];
        await model.AddCommitsViaSync(commits);
        return (wordId, commits[0]);
    }

    private static Commit LateCommit(DataModelTestBase model, Commit after, Guid wordId) =>
        model.CommitAt(after.DateTime.AddMinutes(1), new SetWordTextChange(wordId, "late"));

    //a fresh view each time, so nothing is answered from a cache
    private static async Task<string> TextOf(CrdtRepository repo, Guid wordId) =>
        ((Word)(await repo.CurrentSnapshotView().GetAsync(wordId))!.Entity.DbObject).Text;
}
