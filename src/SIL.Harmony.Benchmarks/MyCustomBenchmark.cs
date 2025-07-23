using BenchmarkDotNet_GitCompare;
using BenchmarkDotNet.Attributes;

namespace SIL.Harmony.Benchmarks;

[SimpleJob(id: "now")]
[GitJob(gitReference: "HEAD", id: "before", baseline: true)]
public class MyCustomBenchmark
{
    [Benchmark]
    public Task Test()
    {
        return Task.Delay(10);
    }
}