using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace SIL.Harmony.Tests.Benchmarks;


// disable warning about waiting for sync code, benchmarkdotnet does not support async code, and it doesn't deadlock when waiting.
#pragma warning disable VSTHRD002
[SimpleJob(RunStrategy.Throughput, warmupCount: 2)]
public class ChangeThroughput
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