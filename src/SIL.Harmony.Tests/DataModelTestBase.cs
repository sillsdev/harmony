using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;
using SIL.Harmony.Tests.Mocks;

namespace SIL.Harmony.Tests;

public class DataModelTestBase : CommitTestBase, IAsyncLifetime
{
    protected readonly ServiceProvider _services;
    private readonly bool _performanceTest;
    private readonly Action<IServiceCollection>? _configure;
    public readonly DataModel DataModel;
    public readonly SampleDbContext DbContext;
    protected readonly MockTimeProvider MockTimeProvider = new();
    protected readonly HarmonyConfig HarmonyConfig;
    private readonly TempDbFile? _dbFile;

    public DataModelTestBase(bool saveToDisk = false, bool alwaysValidate = true,
        Action<IServiceCollection>? configure = null, bool performanceTest = false)
        : this(saveToDisk ? new TempDbFile() : null, alwaysValidate, configure, performanceTest)
    {
    }

    private DataModelTestBase(TempDbFile? dbFile, bool alwaysValidate, Action<IServiceCollection>? configure,
        bool performanceTest)
        : this(new SqliteConnection(dbFile?.ConnectionString ?? "Data Source=:memory:"), alwaysValidate, configure,
            performanceTest)
    {
        _dbFile = dbFile;
    }

    public DataModelTestBase() : this(new SqliteConnection("Data Source=:memory:"))
    {
    }

    public DataModelTestBase(SqliteConnection connection, bool alwaysValidate = true, Action<IServiceCollection>? configure = null, bool performanceTest = false)
    {
        _performanceTest = performanceTest;
        _configure = configure;
        var serviceCollection = new ServiceCollection().AddCrdtDataSample(builder =>
            {
                builder.UseSqlite(connection, true);
            }, performanceTest)
            .Configure<HarmonyConfig>(config => config.AlwaysValidateCommits = alwaysValidate)
            .Replace(ServiceDescriptor.Singleton<IHybridDateTimeProvider>(MockTimeProvider));
        configure?.Invoke(serviceCollection);
        _services = serviceCollection.BuildServiceProvider();
        HarmonyConfig = _services.GetRequiredService<IOptions<HarmonyConfig>>().Value;
        DbContext = _services.GetRequiredService<SampleDbContext>();
        DbContext.Database.OpenConnection();
        DbContext.Database.EnsureCreated();
        DataModel = _services.GetRequiredService<DataModel>();
    }

    public DataModelTestBase ForkDatabase(bool alwaysValidate = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        if (DbContext.Database.GetDbConnection() is not SqliteConnection existingConnection) throw new InvalidOperationException("Database is not SQLite");
        existingConnection.BackupDatabase(connection);
        //the fork has to be configured like its source, otherwise it replays under different settings
        var newTestBase = new DataModelTestBase(connection, alwaysValidate, _configure, _performanceTest);
        newTestBase.SetCurrentDate(CurrentDate);
        return newTestBase;
    }

    /// <summary>
    /// Creates a repository over this instance's DbContext. Exposed so benchmarks can drive
    /// <see cref="CrdtRepository"/> methods (e.g. AddSnapshots) directly without going through the sync pipeline.
    /// </summary>
    internal CrdtRepository CreateRepository() =>
        _services.GetRequiredService<CrdtRepositoryFactory>().CreateRepositorySync();

    internal HarmonyConfig CrdtConfig => _services.GetRequiredService<IOptions<HarmonyConfig>>().Value;

    public async ValueTask<Commit> WriteNextChange(IChange change)
    {
        return await WriteChange(_localClientId, NextDate(), change);
    }

    public async ValueTask<Commit> WriteNextChange(IEnumerable<IChange> changes)
    {
        return await WriteChange(_localClientId, NextDate(), changes);
    }

    public async ValueTask<Commit> WriteChangeAt(DateTimeOffset dateTime, IChange change)
    {
        return await WriteChange(_localClientId, dateTime, change);
    }

    public async ValueTask<Commit> WriteChangeAfter(Commit after, IChange change)
    {
        return await WriteChange(_localClientId, after.DateTime.AddHours(1), change);
    }

    /// <summary>A commit with no changes in it. Triggers a history replay without affecting data.</summary>
    public async ValueTask<Commit> WriteNoOpCommit()
    {
        return await WriteChange(_localClientId, NextDate(), []);
    }

    /// <summary>A commit with no changes in it. Triggers a history replay without affecting data.</summary>
    public async ValueTask<Commit> WriteNoOpCommitAfter(Commit after)
    {
        return await WriteChange(_localClientId, after.DateTime.AddHours(1), []);
    }

    public async ValueTask<Commit> WriteChangeBefore(Commit before, IChange change)
    {
        return await WriteChange(_localClientId, before.DateTime.AddHours(-1), change);
    }

    public async ValueTask<Commit> WriteChange(Guid clientId,
        DateTimeOffset dateTime,
        IChange change)
    {
        return await WriteChange(clientId, dateTime, [change]);
    }

    public async ValueTask<Commit> WriteChange(Guid clientId,
        DateTimeOffset dateTime,
        IEnumerable<IChange> changes)
    {
        MockTimeProvider.SetNextDateTime(dateTime);
        return await DataModel.AddChanges(clientId, changes);
    }

    public async Task AddCommitsViaSync(IEnumerable<Commit> commits)
    {
        await ((ISyncable)DataModel).AddRangeFromSync(commits);
    }

    /// <summary>makes the database look like one written before snapshot checkpoints existed</summary>
    public async Task ClearCheckpointFlags()
    {
        await DbContext.Commits.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsSnapshotCheckpoint, false),
            TestContext.Current.CancellationToken);
        DbContext.ChangeTracker.Clear();
    }

    public virtual ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _dbFile?.Dispose();
    }

    protected IEnumerable<object> AllData()
    {
        return DbContext.Commits
            .Include(c => c.ChangeEntities)
            .Include(c => c.Snapshots)
            .DefaultOrder()
            .ToArray()
            .OfType<object>()
            .Concat(DbContext.Set<Word>().OrderBy(w => w.Text));
    }
}
