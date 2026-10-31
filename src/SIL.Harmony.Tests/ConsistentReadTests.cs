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
    public async Task InWalModeAConsistentReadKeepsItsStateWhileASyncRewritesSnapshots()
    {
        using var file = new TempDbFile();
        await using var reader = await Model(file, wal: true);
        var (wordId, firstCommit) = await Seed(reader);
        //short, so a sync blocked by the read fails the test quickly instead of waiting out the default 30s
        await using var writer = new DataModelTestBase(new SqliteConnection(file.ConnectionString + ";Default Timeout=3"), alwaysValidate: false);

        await using (var repo = await ConsistentRead(reader))
        {
            (await TextOf(repo, wordId)).Should().Be("before");

            await writer.AddCommitsViaSync([LateCommit(writer, firstCommit, wordId)]);

            (await TextOf(repo, wordId)).Should().Be("before", "the late commit landed after the read started");
        }

        await using var afterRead = await ConsistentRead(reader);
        (await TextOf(afterRead, wordId)).Should().Be("late");
    }

    [Fact]
    public async Task OutsideWalModeAConsistentReadDoesNotBlockASync()
    {
        using var file = new TempDbFile();
        await using var reader = await Model(file, wal: false);
        var (wordId, firstCommit) = await Seed(reader);
        await using var writer = new DataModelTestBase(new SqliteConnection(file.ConnectionString + ";Default Timeout=3"), alwaysValidate: false);

        await using var repo = await ConsistentRead(reader);
        await TextOf(repo, wordId);

        await writer.Invoking(w => w.AddCommitsViaSync([LateCommit(w, firstCommit, wordId)])).Should().NotThrowAsync();
    }

    [Fact]
    public async Task AReadOnASharedContextDoesNotGetInTheWayOfWrites()
    {
        using var file = new TempDbFile();
        //tests share one context between repositories, as AddCrdtData does in an app
        await using var model = await Model(file, wal: true);
        var (wordId, firstCommit) = await Seed(model);
        var newWordId = Guid.NewGuid();

        await using (var repo = await model.RepositoryFactory.CreateReadRepository())
        {
            await TextOf(repo, wordId);

            await model.WriteNextChange(new SetWordTextChange(newWordId, "added"));
            await model.AddCommitsViaSync([LateCommit(model, firstCommit, wordId)]);
        }

        (await model.DataModel.GetLatest<Word>(newWordId))!.Text.Should().Be("added");
        (await model.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("late");
    }

    private static async Task<DataModelTestBase> Model(TempDbFile file, bool wal)
    {
        var model = new DataModelTestBase(new SqliteConnection(file.ConnectionString), alwaysValidate: false);
        await model.DbContext.Database.ExecuteSqlRawAsync(wal ? "PRAGMA journal_mode=WAL;" : "PRAGMA journal_mode=DELETE;", TestContext.Current.CancellationToken);
        return model;
    }

    private static async Task<CrdtRepository> ConsistentRead(DataModelTestBase model)
    {
        var repo = model.CreateRepositoryWithOwnConnection();
        await repo.BeginConsistentRead();
        return repo;
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
