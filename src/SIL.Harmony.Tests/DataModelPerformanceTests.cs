using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using JetBrains.Profiler.SelfApi;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Tests.Benchmarks;
using Xunit.Abstractions;

namespace SIL.Harmony.Tests;

[Trait("Category", "Performance")]
public class DataModelPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void AddingChangePerformance()
    {
        #if DEBUG
        Assert.Fail("This test is disabled in debug builds, not reliable");
        #endif
        var summary =
            BenchmarkRunner.Run<ChangeThroughput>(
                ManualConfig.CreateEmpty()
                    .AddExporter(JsonExporter.FullCompressed)
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
            dataModelTest.DbContext.Add(commit);
            dataModelTest.DbContext.Add(new ObjectSnapshot(await change.NewEntity(commit, null!), commit, true));
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

