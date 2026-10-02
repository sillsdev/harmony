using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;

namespace SIL.Harmony.Benchmarks;

[SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits")]
[SimpleJob]
[MemoryDiagnoser]
public class BuildSyncStateBenchmarks
{
    private SimpleCommit[] SimpleCommits = [];
    [Params(10_000, 100_000)]
    public int CommitCount { get; set; }
    [Params(2, 10)]
    public int ClientCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var clientIds = Enumerable.Range(0, ClientCount).Select(_ => Guid.NewGuid()).ToArray();
        SimpleCommits = Enumerable.Range(1, CommitCount)
            .Select(i => new SimpleCommit(clientIds[i % clientIds.Length], Guid.NewGuid(),
                DateTimeOffset.UnixEpoch.AddMilliseconds(i)))
            .ToArray();
    }

    [Benchmark]
    public ClientState[] Build() => QueryHelpers.BuildSyncState(SimpleCommits.ToAsyncEnumerable()).Result;
}
