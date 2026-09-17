using BenchmarkDotNet.Attributes;

namespace SIL.Harmony.Benchmarks;

[SimpleJob]
public class BuildSyncStateBenchmarks
{
    private SimpleCommit[] SimpleCommits = [];
    private Guid[] ClientIds = [];
    [Params(10_000, 100_000)]
    public int CommitCount { get; set; }
    [Params(2, 10)]
    public int ClientCount { get; set; }
    [GlobalSetup]
    public void GlobalSetup()
    {
        ClientIds = Enumerable.Range(0, ClientCount).Select(_ => Guid.NewGuid()).ToArray();
        SimpleCommits = Enumerable.Range(1, CommitCount)
            .Select(i => new SimpleCommit(ClientIds[i % ClientIds.Length], Guid.NewGuid()))
            .OrderBy(k => k.ClientId)
            .ToArray();
        dict = ClientIds.ToDictionary(c => c, c => new ClientStateBuilder()
        {
            ClientId = c
        });
    }

    private Dictionary<Guid, ClientStateBuilder> dict = [];

    [Benchmark]
    public ClientState[] Build()
    {
        foreach (Guid dictKey in dict.Keys)
        {
            dict[dictKey] = new ClientStateBuilder()
            {
                ClientId = dictKey
            };
        }

        return QueryHelpers.BuildSyncState(SimpleCommits, dict);
    }
}
