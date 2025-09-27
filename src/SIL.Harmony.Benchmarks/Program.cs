using BenchmarkDotNet.Running;
using SIL.Harmony.Tests.Benchmarks;

BenchmarkSwitcher.FromAssemblies([typeof(Program).Assembly, typeof(ChangeThroughput).Assembly]).Run(args);