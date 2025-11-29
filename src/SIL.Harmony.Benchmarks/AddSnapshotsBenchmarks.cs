using BenchmarkDotNet_GitCompare;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Core;
using SIL.Harmony.Db;
using SIL.Harmony.Linq2db;
using SIL.Harmony.Sample.Models;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

[SimpleJob(RunStrategy.Throughput)]
[MemoryDiagnoser]
// [GitJob(gitReference: "HEAD", id: "before", baseline: true)]
public class AddSnapshotsBenchmarks
{
    private DataModelTestBase _emptyDataModel = null!;
    private ICrdtRepository _repository = null!;
    private Commit _commit = null!;

    [Params(1000)]
    public int SnapshotCount { get; set; }

    [Params(true, false)]
    public bool UseLinq2DbRepo { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _emptyDataModel = new(alwaysValidate: false,
            performanceTest: true, useLinq2DbRepo: UseLinq2DbRepo);
        var crdtRepositoryFactory = _emptyDataModel.Services.GetRequiredService<ICrdtRepositoryFactory>();
        _repository = crdtRepositoryFactory.CreateRepositorySync();
        _repository.AddCommit(_commit = new Commit(Guid.NewGuid())
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = new HybridDateTime(DateTimeOffset.Now, 0),
        }).GetAwaiter().GetResult();
    }

    [Benchmark(OperationsPerInvoke = 1000)]
    public void AddSnapshotsOneAtATime()
    {
        for (var i = 0; i < SnapshotCount; i++)
        {
            _repository.AddSnapshots([
                new ObjectSnapshot(new Word()
                    {
                        Id = Guid.NewGuid(),
                        Text = "test",
                    }, _commit, true)
            ]).GetAwaiter().GetResult();
        }
    }

    [Benchmark(OperationsPerInvoke = 1000)]
    public void AddSnapshotsAllAtOnce()
    {
        var snapshots = Enumerable.Range(0, SnapshotCount)
            .Select(i => new ObjectSnapshot(new Word()
                {
                    Id = Guid.NewGuid(),
                    Text = "test",
                },
                _commit,
                true))
            .ToArray();

        _repository.AddSnapshots(snapshots).GetAwaiter().GetResult();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _repository.Dispose();
        _emptyDataModel.DisposeAsync().GetAwaiter().GetResult();
    }
}