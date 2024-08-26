using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;
using SIL.Harmony.Changes;
using SIL.Harmony.Core;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Models;
using Xunit.Abstractions;

namespace SIL.Harmony.Tests;

public class DataModelPerformanceTests(ITestOutputHelper output) : DataModelTestBase
{
    [Fact]
    public void AddingChangePerformance()
    {
        using var fileStream = File.Open("DataModelPerformanceTests.txt", FileMode.Create);
        var summary =
            BenchmarkRunner.Run<DataModelPerformanceBenchmarks>(
                ManualConfig.CreateEmpty()
                    .AddColumnProvider(DefaultColumnProviders.Instance)
                    .AddLogger(new XUnitBenchmarkLogger(output))
                    .AddLogger(new TextLogger(new StreamWriter(fileStream)))
            );
        foreach (var benchmarkCase in summary.BenchmarksCases.Where(b => !summary.IsBaseline(b)))
        {
            var ratio = double.Parse(BaselineRatioColumn.RatioMean.GetValue(summary, benchmarkCase));
            ratio.Should().BeInRange(0, 2, "performance should not get worse, benchmark " + benchmarkCase.DisplayInfo);
        }
    }

    [Fact]
    public async Task SimpleAddChangePerformanceTest()
    {
        var dataModelTest = new DataModelTestBase();
        // warmup the code, this causes jit to run and keeps our actual test below consistent
        var word1Id = Guid.NewGuid();
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(word1Id, "entity 0"));
        var start = Stopwatch.GetTimestamp();
        var parentHash = (await dataModelTest.WriteNextChange(dataModelTest.SetWord(word1Id, "entity 1"))).Hash;
        var runtimeAddChange1Snapshot = Stopwatch.GetElapsedTime(start);
        for (var i = 0; i < 10_000; i++)
        {
            var change = dataModelTest.SetWord(Guid.NewGuid(), $"entity {i}");
            var commitId = Guid.NewGuid();
            var commit = new Commit(commitId)
            {
                ClientId = Guid.NewGuid(),
                HybridDateTime = new HybridDateTime(dataModelTest.NextDate(), 0), 
                ChangeEntities =
                [
                    new ChangeEntity<IChange>()
                    {
                        Change = change,
                        Index = 0,
                        CommitId = commitId,
                        EntityId = change.EntityId
                    }
                ]
            };
            commit.SetParentHash(parentHash);
            parentHash = commit.Hash;
            dataModelTest.DbContext.Commits.Add(commit);
            dataModelTest.DbContext.Snapshots.Add(new ObjectSnapshot(await change.NewEntity(commit, null!), commit, true));
        }

        await dataModelTest.DbContext.SaveChangesAsync();

        start = Stopwatch.GetTimestamp();
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "entity1"));
        // var snapshots = await dataModelTest.CrdtRepository.CurrenSimpleSnapshots().ToArrayAsync();
        var runtimeAddChange10000Snapshots = Stopwatch.GetElapsedTime(start);
        runtimeAddChange10000Snapshots.Should()
            .BeCloseTo(runtimeAddChange1Snapshot, runtimeAddChange1Snapshot / 10);
        // snapshots.Should().HaveCount(1002);
        await dataModelTest.DisposeAsync();
    }

    private class XUnitBenchmarkLogger(ITestOutputHelper output) : ILogger
    {
        public string Id => nameof(XUnitBenchmarkLogger);
        public int Priority => 0;
        private StringBuilder? sb;

        public void Write(LogKind logKind, string text)
        {
            if (sb == null)
            {
                sb = new StringBuilder();
            }

            sb.Append(text);
        }

        public void WriteLine()
        {
            if (sb is not null)
            {
                output.WriteLine(sb.ToString());
                sb.Clear();
            }
            else
                output.WriteLine(string.Empty);
        }

        public void WriteLine(LogKind logKind, string text)
        {
            if (sb is not null)
            {
                output.WriteLine(sb.Append(text).ToString());
                sb.Clear();
            }
            else
                output.WriteLine(text);
        }

        public void Flush()
        {
            if (sb is not null)
            {
                output.WriteLine(sb.ToString());
                sb.Clear();
            }
        }
    }
}

[SimpleJob(RunStrategy.Monitoring, warmupCount: 2)]
public class DataModelPerformanceBenchmarks
{
    private DataModelTestBase _templateModel = null!;
    private DataModelTestBase _dataModelTestBase = null!;
    private DataModelTestBase _emptyDataModel = null!;


    [GlobalSetup]
    public void GlobalSetup()
    {
        _templateModel = new DataModelTestBase();
        for (var i = 0; i < StartingSnapshots; i++)
        {
            _ = _templateModel.WriteNextChange(_templateModel.SetWord(Guid.NewGuid(), $"entity {i}")).Result;
        }
    }

    [Params(0, 1000)]
    public int StartingSnapshots { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _emptyDataModel = new();
        _dataModelTestBase = _templateModel.ForkDatabase();
    }

    [Benchmark(Baseline = true)]
    public Commit AddSingleChangePerformance()
    {
        return _emptyDataModel.WriteNextChange(_emptyDataModel.SetWord(Guid.NewGuid(), "entity1")).Result;
    }

    [Benchmark]
    public Commit AddSingleChangeWithManySnapshots()
    {
        return _dataModelTestBase.WriteNextChange(_dataModelTestBase.SetWord(Guid.NewGuid(), "entity1")).Result;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _emptyDataModel.DisposeAsync().GetAwaiter().GetResult();
        _dataModelTestBase.DisposeAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _templateModel.DisposeAsync().GetAwaiter().GetResult();
    }
}