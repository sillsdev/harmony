using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using JetBrains.Profiler.SelfApi;
using Microsoft.Data.Sqlite;
using SIL.Harmony.Changes;
using SIL.Harmony.Core;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;
using Xunit.Abstractions;

namespace SIL.Harmony.Tests;

[Trait("Category", "Performance")]
public class DataModelPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void AddingChangePerformance()
    {
        var summary =
            BenchmarkRunner.Run<DataModelPerformanceBenchmarks>(
                ManualConfig.CreateEmpty()
                    .AddColumnProvider(DefaultColumnProviders.Instance)
                    .AddLogger(new XUnitBenchmarkLogger(output))
            );
        foreach (var benchmarkCase in summary.BenchmarksCases.Where(b => !summary.IsBaseline(b)))
        {
            var ratio = double.Parse(BaselineRatioColumn.RatioMean.GetValue(summary, benchmarkCase), System.Globalization.CultureInfo.InvariantCulture);
            //for now it just makes sure that no case is worse that 7x, this is based on the 10_000 test being 5 times worse.
            //it would be better to have this scale off the number of changes
            ratio.Should().BeInRange(0, 7, "performance should not get worse, benchmark " + benchmarkCase.DisplayInfo);
        }
    }
    
    //enable this to profile tests
    private static readonly bool trace = (Environment.GetEnvironmentVariable("DOTNET_TRACE") ?? "false") != "false";
    private async Task StartTrace()
    {
        if (!trace) return;
        await DotTrace.InitAsync();
        // config that sets the save directory
        var config = new DotTrace.Config();
        var dirPath = Path.Combine(Path.GetTempPath(), "harmony-perf");
        Directory.CreateDirectory(dirPath);
        config.SaveToDir(dirPath);
        DotTrace.Attach(config);
        DotTrace.StartCollectingData();
    }
    
    private void StopTrace()
    {
        if (!trace) return;
        DotTrace.SaveData();
        DotTrace.Detach();
    }
    
    private static async Task<TimeSpan> MeasureTime(Func<Task> action, int iterations = 10)
    {
        var total = TimeSpan.Zero;
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await action();
            total += Stopwatch.GetElapsedTime(start);
        }
        return total / iterations;
    }

    [Fact]
    public async Task SimpleAddChangePerformanceTest()
    {
        //disable validation because it's slow
        var dataModelTest = new DataModelTestBase(alwaysValidate: false);
        // warmup the code, this causes jit to run and keeps our actual test below consistent
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity 0"));
        var runtimeAddChange1Snapshot = await MeasureTime(() => dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity 1")).AsTask());

        await BulkInsertChanges(dataModelTest);
        //fork the database, this creates a new DbContext which does not have a cache of all the snapshots created above
        //that cache causes DetectChanges (used by SaveChanges) to be slower than it should be
        dataModelTest = dataModelTest.ForkDatabase(false);

        await StartTrace();
        var runtimeAddChange10000Snapshots = await MeasureTime(() => dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity1")).AsTask());
        StopTrace();
        output.WriteLine($"Runtime AddChange with 10,000 Snapshots: {runtimeAddChange10000Snapshots.TotalMilliseconds:N}ms");
        runtimeAddChange10000Snapshots.Should()
            .BeCloseTo(runtimeAddChange1Snapshot, runtimeAddChange1Snapshot * 4);
        // snapshots.Should().HaveCount(1002);
        await dataModelTest.DisposeAsync();
    }

    internal static async Task BulkInsertChanges(DataModelTestBase dataModelTest, int count = 10_000)
    {
        var parentHash = (await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity 1"))).Hash;
        for (var i = 0; i < count; i++)
        {
            var change = (SetWordTextChange) dataModelTest.SetWord(Guid.NewGuid(), $"entity {i}");
            var commitId = Guid.NewGuid();
            var commit = new Commit(commitId)
            {
                ClientId = Guid.NewGuid(),
                HybridDateTime = new HybridDateTime(dataModelTest.NextDate(), 0), 
                ChangeEntities =
                [
                    new ChangeEntity<IChange>()
                    {
                        Change = change,
                        Index = 0,
                        CommitId = commitId,
                        EntityId = change.EntityId
                    }
                ]
            };
            commit.SetParentHash(parentHash);
            parentHash = commit.Hash;
            dataModelTest.DbContext.Commits.Add(commit);
            dataModelTest.DbContext.Snapshots.Add(new ObjectSnapshot(await change.NewEntity(commit, null!), commit, true));
        }

        await dataModelTest.DbContext.SaveChangesAsync();
        //ensure changes were made correctly
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity after bulk insert"));
    }

    private class XUnitBenchmarkLogger(ITestOutputHelper output) : ILogger
    {
        public string Id => nameof(XUnitBenchmarkLogger);
        public int Priority => 0;
        private StringBuilder? _sb;

        public void Write(LogKind logKind, string text)
        {
            _sb ??= new StringBuilder();

            _sb.Append(text);
        }

        public void WriteLine()
        {
            if (_sb is not null)
            {
                output.WriteLine(_sb.ToString());
                _sb.Clear();
            }
            else
                output.WriteLine(string.Empty);
        }

        public void WriteLine(LogKind logKind, string text)
        {
            if (_sb is not null)
            {
                output.WriteLine(_sb.Append(text).ToString());
                _sb.Clear();
            }
            else
                output.WriteLine(text);
        }

        public void Flush()
        {
            if (_sb is not null)
            {
                output.WriteLine(_sb.ToString());
                _sb.Clear();
            }
        }
    }
}

// disable warning about waiting for sync code, benchmarkdotnet does not support async code, and it doesn't deadlock when waiting.
#pragma warning disable VSTHRD002
[SimpleJob(RunStrategy.Throughput, warmupCount: 2)]
public class DataModelPerformanceBenchmarks
{
    private DataModelTestBase _templateModel = null!;
    private DataModelTestBase _dataModelTestBase = null!;
    private DataModelTestBase _emptyDataModel = null!;


    [GlobalSetup]
    public void GlobalSetup()
    {
        _templateModel = new DataModelTestBase(alwaysValidate: false);
        DataModelPerformanceTests.BulkInsertChanges(_templateModel, StartingSnapshots).GetAwaiter().GetResult();
    }

    [Params(0, 1000, 10_000)]
    public int StartingSnapshots { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _emptyDataModel = new(alwaysValidate: false);
        _ = _emptyDataModel.WriteNextChange(_emptyDataModel.SetWord(Guid.NewGuid(), "entity1")).Result;
        _dataModelTestBase = _templateModel.ForkDatabase(false);
    }
    
    [Benchmark(Baseline = true), BenchmarkCategory("WriteChange")]
    public Commit AddSingleChangePerformance()
    {
        return _emptyDataModel.WriteNextChange(_emptyDataModel.SetWord(Guid.NewGuid(), "entity1")).Result;
    }
    
    [Benchmark, BenchmarkCategory("WriteChange")]
    public Commit AddSingleChangeWithManySnapshots()
    {
        var count = _dataModelTestBase.DbContext.Snapshots.Count();
        // had a bug where there were no snapshots, this means the test was useless, this is slower, but it's better that then a useless test
        if (count < (StartingSnapshots - 5)) throw new Exception($"Not enough snapshots, found {count}");
        return _dataModelTestBase.WriteNextChange(_dataModelTestBase.SetWord(Guid.NewGuid(), "entity1")).Result;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _emptyDataModel.DisposeAsync().GetAwaiter().GetResult();
        _dataModelTestBase.DisposeAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _templateModel.DisposeAsync().GetAwaiter().GetResult();
    }
}
#pragma warning restore VSTHRD002