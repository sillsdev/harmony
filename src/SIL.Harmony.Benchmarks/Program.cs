using BenchmarkDotNet.Running;
using SIL.Harmony.Benchmarks;


BenchmarkSwitcher
    .FromTypes([typeof(DataModelSyncBenchmarks), typeof(AddSnapshotsBenchmarks), typeof(BuildSyncStateBenchmarks)])
    .Run(args);
