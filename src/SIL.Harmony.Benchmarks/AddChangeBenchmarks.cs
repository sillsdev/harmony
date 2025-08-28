using BenchmarkDotNet_GitCompare;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.dotMemory;
using BenchmarkDotNet.Diagnostics.dotTrace;
using BenchmarkDotNet.Engines;
using SIL.Harmony.Linq2db;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

[SimpleJob(RunStrategy.Throughput)]
[MemoryDiagnoser]
// [DotMemoryDiagnoser]
// [DotTraceDiagnoser]
// [GitJob(gitReference: "HEAD", id: "before", baseline: true)]
public class AddChangeBenchmarks
{
    private DataModelTestBase _emptyDataModel = null!;
    private Guid _clientId = Guid.NewGuid();
    public const int ActualChangeCount = 2000;
    [Params(true, false)]
    public bool UseLinq2DbRepo { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _emptyDataModel = new(alwaysValidate: false, performanceTest: true, useLinq2DbRepo: UseLinq2DbRepo);
        _emptyDataModel.WriteNextChange(_emptyDataModel.SetWord(Guid.NewGuid(), "entity1")).GetAwaiter().GetResult();
    }

    [Benchmark(OperationsPerInvoke = ActualChangeCount)]
    public List<Commit> AddChanges()
    {
        var commits = new List<Commit>();
        for (var i = 0; i < ActualChangeCount; i++)
        {
            commits.Add(_emptyDataModel.WriteNextChange(_emptyDataModel.SetWord(Guid.NewGuid(), "entity1")).Result);
        }

        return commits;
    }

    [Benchmark(OperationsPerInvoke = ActualChangeCount)]
    public Commit AddChangesAllAtOnce()
    {
        return _emptyDataModel.WriteChange(_clientId, DateTimeOffset.Now, Enumerable.Range(0, ActualChangeCount)
            .Select(i =>
            _emptyDataModel.SetWord(Guid.NewGuid(), "entity1"))).Result;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _emptyDataModel.DisposeAsync().GetAwaiter().GetResult();
    }
}