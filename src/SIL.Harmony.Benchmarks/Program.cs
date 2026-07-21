using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using SIL.Harmony.Benchmarks;


var config = DefaultConfig.Instance;
BenchmarkRunner.Run<DataModelSyncBenchmarks>(config, args);
