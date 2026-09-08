using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using SIL.Harmony.Benchmarks;


var config = DefaultConfig.Instance
    .AddJob(Job.MediumRun.WithStrategy(RunStrategy.Monitoring));
BenchmarkSwitcher
    .FromTypes([typeof(DataModelSyncBenchmarks), typeof(AddSnapshotsBenchmarks)])
    .Run(args, config);
