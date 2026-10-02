using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

[SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits")]
[SimpleJob]
[MemoryDiagnoser]
public class GetSyncStateBenchmarks
{
    private DataModelTestBase _model = null!;

    [Params(10_000, 100_000)]
    public int CommitCount { get; set; }

    [Params(2, 10)]
    public int ClientCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _model = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        var clientIds = Enumerable.Range(0, ClientCount).Select(_ => Guid.NewGuid()).ToArray();
        var commits = Enumerable.Range(0, CommitCount)
            .Select(i => BenchmarkWorkloadBuilders.NewCommit(clientIds[i % clientIds.Length], _model.NextDate()));
        // only the commit rows matter here, so skip the snapshot pipeline; SaveChanges keeps rows tracked, so clear after each chunk
        using var repository = _model.CreateRepository();
        foreach (var chunk in commits.Chunk(10_000))
        {
            repository.AddCommits(chunk).Wait();
            repository.ClearChangeTracker();
        }
    }

    [Benchmark]
    public SyncState GetSyncState() => _model.DataModel.GetSyncState().Result;

    [GlobalCleanup]
    public void GlobalCleanup() => _model.DisposeAsync().AsTask().Wait();
}
