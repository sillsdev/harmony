using BenchmarkDotNet_GitCompare;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.dotTrace;
using BenchmarkDotNet.Engines;
using SIL.Harmony.Linq2db;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

[SimpleJob(RunStrategy.Throughput)]
[MemoryDiagnoser]
// [DotTraceDiagnoser]
// [GitJob(gitReference: "HEAD", id: "before", baseline: true)]
public class AddChangeBenchmarks
{
    private DataModelTestBase _emptyDataModel = null!;

    [Params(200)]
    public int ChangeCount { get; set; }
    [Params(true, false)]
    public bool UseLinq2DbRepo { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _emptyDataModel = new(alwaysValidate: false, performanceTest: true, configure: collection =>
        {
            if (UseLinq2DbRepo)
                collection.AddLinq2DbRepository();
        });
        _emptyDataModel.WriteNextChange(_emptyDataModel.SetWord(Guid.NewGuid(), "entity1")).GetAwaiter().GetResult();
    }

    [Benchmark(OperationsPerInvoke = 200)]
    public List<Commit> AddChanges()
    {
        var commits = new List<Commit>();
        for (var i = 0; i < ChangeCount; i++)
        {
            commits.Add(_emptyDataModel.WriteNextChange(_emptyDataModel.SetWord(Guid.NewGuid(), "entity1")).Result);
        }

        return commits;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _emptyDataModel.DisposeAsync().GetAwaiter().GetResult();
    }
}