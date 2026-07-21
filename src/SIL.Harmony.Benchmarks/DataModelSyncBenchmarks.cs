using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

[SimpleJob(RunStrategy.Monitoring)]
[SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits")]
public class DataModelSyncBenchmarks
{
    private DataModelTestBase remote = null!;
    private DataModelTestBase local = null!;

    [Params(1000, 10_000, 100_000)]
    public int ChangeCount { get; set; }
    private Commit[] _commits = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        remote = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        var clientId = Guid.NewGuid();
        var commits = new List<Commit>();
        Commit? currentCommit = null;
        for (int i = 0; i < ChangeCount; i++)
        {
            if (currentCommit == null)
            {
                var commitId = Guid.NewGuid();
                currentCommit = new Commit(commitId)
                {
                    ClientId = clientId,
                    HybridDateTime = new HybridDateTime(remote.NextDate(), 0)
                };
            }

            if (i % 100 == 0)
            {
                var tagId = Guid.NewGuid();
                var wordId = Guid.NewGuid();
                currentCommit.ChangeEntities.Add(DataModel.ToChangeEntity(remote.SetWord(wordId, $"entity {i}"), i, currentCommit.Id));
                currentCommit.ChangeEntities.Add(DataModel.ToChangeEntity(remote.SetTag(tagId, $"tag {i}"), i, currentCommit.Id));
                currentCommit.ChangeEntities.Add(DataModel.ToChangeEntity(remote.TagWord(wordId, tagId), i, currentCommit.Id));
            }
            else if (i % 25 == 0)
            {
                var wordId = Guid.NewGuid();
                currentCommit.ChangeEntities.Add(DataModel.ToChangeEntity(remote.SetWord(wordId, $"entity {i}"), i, currentCommit.Id));
                currentCommit.ChangeEntities.Add(DataModel.ToChangeEntity(remote.NewDefinition(wordId, $"definition {i}", "noun"), i, currentCommit.Id));
            }
            else
            {
                currentCommit.ChangeEntities.Add(DataModel.ToChangeEntity(remote.SetWord(Guid.NewGuid(), $"entity {i}"), i, currentCommit.Id));
            }
            if (i % 5 == 0 || i % 3 == 0)
            {
                for (var i1 = 0; i1 < currentCommit.ChangeEntities.Count; i1++)
                {
                    currentCommit.ChangeEntities[i1].Index = i1;
                }
                commits.Add(currentCommit);
                currentCommit = null;
            }
        }
        if (currentCommit != null)
        {
            commits.Add(currentCommit);
        }
        ((ISyncable)remote.DataModel).AddRangeFromSync(commits).Wait();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        local = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        _ = local.WriteNextChange(local.SetWord(Guid.NewGuid(), "entity1")).Result;
        //cant share commits between iterations, because EF modifies them
        _commits = remote.DataModel.GetChanges(new SyncState([])).Result.MissingFromClient;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        local.DisposeAsync().AsTask().Wait();
        local = null!;
    }

    [Benchmark]
    public void SyncCommits()
    {
        ((ISyncable)local.DataModel).AddRangeFromSync(_commits)
            .Wait();
    }
}
