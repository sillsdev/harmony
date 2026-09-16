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

namespace SIL.Harmony.Tests;

[Trait("Category", "Performance")]
public class DataModelPerformanceTests(ITestOutputHelper output)
{
    [Fact(
#if DEBUG
        Skip = "This test is disabled in debug builds, not reliable"
#endif
        )]
    public void AddingChangePerformance()
    {
        var summary =
            BenchmarkRunner.Run<DataModelPerformanceBenchmarks>(
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
        var dataModelTest = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        // warmup the code, this causes jit to run and keeps our actual test below consistent
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity 0"));
        var runtimeAddChange1Snapshot = await MeasureTime(() => dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity 1")).AsTask());
        output.WriteLine($"Runtime AddChange with 1 Snapshot: {runtimeAddChange1Snapshot.TotalMilliseconds:N}ms");

        await BulkInsertChanges(dataModelTest);
        //fork the database, this creates a new DbContext which does not have a cache of all the snapshots created above
        //that cache causes DetectChanges (used by SaveChanges) to be slower than it should be
        dataModelTest = dataModelTest.ForkDatabase(false);
        //warmup the forked context too — it has a fresh ServiceProvider/DbContext/DataModel,
        //so the first WriteNextChange pays EF Core query-compilation cost that the measurement shouldn't include
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity warmup"));

        await StartTrace();
        var runtimeAddChange10000Snapshots = await MeasureTime(() => dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity1")).AsTask());
        StopTrace();
        output.WriteLine($"Runtime AddChange with 10,000 Snapshots: {runtimeAddChange10000Snapshots.TotalMilliseconds:N}ms");
        runtimeAddChange10000Snapshots.Should()
            .BeCloseTo(runtimeAddChange1Snapshot, runtimeAddChange1Snapshot * 4);
        await dataModelTest.DisposeAsync();
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(500)]
    public async Task SimpleAddCountChangesPerformanceTest(int count)
    {
        //disable validation because it's slow
        var dataModelTest = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        // warmup the code, this causes jit to run and keeps our actual test below consistent
        await MeasureTime(() => dataModelTest.WriteNextChange(GetChanges(dataModelTest, count)).AsTask());
        var runtimeAddChange1Snapshot = await MeasureTime(() => dataModelTest.WriteNextChange(GetChanges(dataModelTest, count)).AsTask());
        output.WriteLine($"Runtime AddChange with 1 Snapshot: {runtimeAddChange1Snapshot.TotalMilliseconds:N}ms");

        await BulkInsertChanges(dataModelTest);
        //fork the database, this creates a new DbContext which does not have a cache of all the snapshots created above
        //that cache causes DetectChanges (used by SaveChanges) to be slower than it should be
        dataModelTest = dataModelTest.ForkDatabase(false);
        //warmup the forked context too — it has a fresh ServiceProvider/DbContext/DataModel,
        //so the first WriteNextChange pays EF Core query-compilation cost that the measurement shouldn't include
        await MeasureTime(() => dataModelTest.WriteNextChange(GetChanges(dataModelTest, count)).AsTask());

        await StartTrace();
        var runtimeAddChange10000Snapshots = await MeasureTime(() => dataModelTest.WriteNextChange(GetChanges(dataModelTest, count)).AsTask());
        StopTrace();
        output.WriteLine($"Runtime AddChange with 10,000 Snapshots: {runtimeAddChange10000Snapshots.TotalMilliseconds:N}ms");
        runtimeAddChange10000Snapshots.Should()
            .BeCloseTo(runtimeAddChange1Snapshot, runtimeAddChange1Snapshot * 4);
        // snapshots.Should().HaveCount(1002);
        await dataModelTest.DisposeAsync();
    }

    /// <summary>
    /// Asserts that <see cref="HarmonyConfig.PrefetchSnapshotsBreakpoint"/> (currently 220) is a good crossover value:
    /// the point where bulk-prefetching current snapshots in one query starts beating fetching them one per entity.
    /// <para>
    /// The batches edit <em>existing</em> entities, because that is what the breakpoint is tuned for — a batch of
    /// brand-new entities has no current snapshot to fetch, so prefetching only adds a query that returns nothing and
    /// never wins. For edits of existing entities the crossover sits right around the configured 220:
    /// </para>
    /// <list type="bullet">
    /// <item>Below the breakpoint (50 edits) forcing prefetch (breakpoint = 0) is ~1.6x slower than the default.</item>
    /// <item>Above the breakpoint (500 edits) forcing no prefetch (breakpoint = int.MaxValue) is ~1.3x slower.</item>
    /// </list>
    /// If either assertion fails the breakpoint has drifted from where the bulk query starts paying off.
    /// </summary>
    [Fact(
#if DEBUG
        Skip = "Perf test, breakpoint effect only reliable in release builds"
#endif
        )]
    public async Task PrefetchSnapshotsBreakpointIsAGoodChoice()
    {
        const int belowBreakpointCount = 50;   // well below default breakpoint (220)
        const int aboveBreakpointCount = 1000; // well above default breakpoint (220), clear of the noisy crossover

        // Take the best (min) of a few runs per config: a single GC/scheduling pause in the faster config could
        // otherwise flip the comparison, and the min rejects those transient stalls while keeping the assertion strict.
        var belowDefault = await BestBatchWrite(belowBreakpointCount, breakpoint: null); // default 220 -> no prefetch
        var belowPrefetch = await BestBatchWrite(belowBreakpointCount, breakpoint: 0);   // force prefetch
        output.WriteLine($"Below breakpoint ({belowBreakpointCount}): default {belowDefault.TotalMilliseconds:N}ms vs forced prefetch {belowPrefetch.TotalMilliseconds:N}ms");
        belowPrefetch.Should().BeGreaterThan(belowDefault,
            "below the breakpoint, fetching each snapshot with a query per entity should beat one bulk query");

        var aboveDefault = await BestBatchWrite(aboveBreakpointCount, breakpoint: null);        // default 220 -> prefetch
        var aboveNoPrefetch = await BestBatchWrite(aboveBreakpointCount, breakpoint: int.MaxValue); // force no prefetch
        output.WriteLine($"Above breakpoint ({aboveBreakpointCount}): default {aboveDefault.TotalMilliseconds:N}ms vs forced no-prefetch {aboveNoPrefetch.TotalMilliseconds:N}ms");
        aboveNoPrefetch.Should().BeGreaterThan(aboveDefault,
            "above the breakpoint, one bulk query should beat many queries per entity");
    }

    /// <summary>Best (minimum) of three <see cref="MeasureBatchWrite"/> runs, editing existing entities.</summary>
    private static async Task<TimeSpan> BestBatchWrite(int count, int? breakpoint)
    {
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var runtime = await MeasureBatchWrite(count, breakpoint, editExisting: true);
            if (runtime < best) best = runtime;
        }
        return best;
    }

    /// <summary>
    /// Measures the average time to write a batch of <paramref name="count"/> changes against a database seeded with
    /// 10,000 snapshots. When <paramref name="breakpoint"/> is provided it overrides
    /// <see cref="HarmonyConfig.PrefetchSnapshotsBreakpoint"/> so the prefetch path can be forced on or off. When
    /// <paramref name="editExisting"/> is true the batch edits existing seeded entities (so each has a current snapshot
    /// to fetch); otherwise it creates brand-new entities.
    /// </summary>
    private static async Task<TimeSpan> MeasureBatchWrite(int count, int? breakpoint, bool editExisting = false)
    {
        //disable validation because it's slow
        var dataModelTest = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        await BulkInsertChanges(dataModelTest);
        //fork the database, this creates a new DbContext which does not have a cache of all the snapshots created above
        //that cache causes DetectChanges (used by SaveChanges) to be slower than it should be
        dataModelTest = dataModelTest.ForkDatabase(false);
        //ForkDatabase does not propagate config, so set the breakpoint on the forked (measured) instance directly.
        //DataModel reads PrefetchSnapshotsBreakpoint fresh on each write, so mutating the singleton config takes effect.
        if (breakpoint is { } value) dataModelTest.CrdtConfig.PrefetchSnapshotsBreakpoint = value;

        var existingIds = editExisting
            ? dataModelTest.DbContext.Snapshots.Select(s => s.EntityId).Distinct().Take(count).ToList()
            : null;
        Func<IEnumerable<IChange>> makeChanges = existingIds is null
            ? () => GetChanges(dataModelTest, count)
            : () => existingIds.Select(id => dataModelTest.SetWord(id, $"edit {Guid.NewGuid()}"));

        //warmup the forked context too — it has a fresh ServiceProvider/DbContext/DataModel,
        //so the first WriteNextChange pays EF Core query-compilation cost that the measurement shouldn't include
        await MeasureTime(() => dataModelTest.WriteNextChange(makeChanges()).AsTask());

        var runtime = await MeasureTime(() => dataModelTest.WriteNextChange(makeChanges()).AsTask());
        await dataModelTest.DisposeAsync();
        return runtime;
    }

    private static IEnumerable<IChange> GetChanges(DataModelTestBase dataModelTest, int count)
    {
        return Enumerable.Range(0, count).Select(i => dataModelTest.SetWord(Guid.NewGuid(), $"entity {i}"));
    }

    internal static async Task BulkInsertChanges(DataModelTestBase dataModelTest, int count = 10_000)
    {
        var parentHash = (await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity 1"))).Hash;
        for (var i = 0; i < count; i++)
        {
            var change = (SetWordTextChange)dataModelTest.SetWord(Guid.NewGuid(), $"entity {i}");
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
        _templateModel = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        DataModelPerformanceTests.BulkInsertChanges(_templateModel, StartingSnapshots).GetAwaiter().GetResult();
    }

    [Params(0, 1000, 10_000)]
    public int StartingSnapshots { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _emptyDataModel = new(alwaysValidate: false, performanceTest: true);
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
