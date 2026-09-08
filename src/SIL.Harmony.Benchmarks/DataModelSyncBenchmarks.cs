using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SIL.Harmony.Changes;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

public enum SyncWorkload
{
    /// <summary>Create many distinct words (one SetWord per commit).</summary>
    CreateWords,

    /// <summary>Create words each with a new definition in the same commit.</summary>
    WordsWithDefinitions,

    /// <summary>Create words each with a tag and WordTag link in the same commit.</summary>
    WordsWithTags,

    /// <summary>Create one word, then modify that same word repeatedly.</summary>
    ModifySameWord,

    /// <summary>Create words then delete them.</summary>
    CreateThenDelete,

    /// <summary>Create, delete, then modify (apply-after-delete) for each word.</summary>
    CreateDeleteModify,

    /// <summary>
    /// Local already has create + late modify; sync inserts a mid-history modify
    /// for each word (forces stale snapshot deletion / rebuild).
    /// </summary>
    OutOfOrderInsert,
}

// [SimpleJob(RunStrategy.Monitoring)]
[MemoryDiagnoser]
[SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits")]
public class DataModelSyncBenchmarks
{
    private DataModelTestBase remote = null!;
    private DataModelTestBase local = null!;

    [Params(
        1000
        // ,         10_000
        )]
    public int ChangeCount { get; set; }

    [ParamsAllValues]
    public SyncWorkload Workload { get; set; }

    private Commit[] _commits = null!;
    private HashSet<Guid>? _syncCommitIds;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _syncCommitIds = null;
        remote = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        var clientId = Guid.NewGuid();
        List<Commit> commits;
        switch (Workload)
        {
            case SyncWorkload.CreateWords:
                commits = BenchmarkWorkloadBuilders.BuildCreateWords(remote, clientId, ChangeCount);
                break;
            case SyncWorkload.WordsWithDefinitions:
                commits = BenchmarkWorkloadBuilders.BuildWordsWithDefinitions(remote, clientId, ChangeCount);
                break;
            case SyncWorkload.WordsWithTags:
                commits = BenchmarkWorkloadBuilders.BuildWordsWithTags(remote, clientId, ChangeCount);
                break;
            case SyncWorkload.ModifySameWord:
                commits = BenchmarkWorkloadBuilders.BuildModifySameWord(remote, clientId, ChangeCount);
                break;
            case SyncWorkload.CreateThenDelete:
                commits = BenchmarkWorkloadBuilders.BuildCreateThenDelete(remote, clientId, ChangeCount);
                break;
            case SyncWorkload.CreateDeleteModify:
                commits = BenchmarkWorkloadBuilders.BuildCreateDeleteModify(remote, clientId, ChangeCount);
                break;
            case SyncWorkload.OutOfOrderInsert:
                var (seed, toSync) = BenchmarkWorkloadBuilders.BuildOutOfOrderInsert(remote, clientId, ChangeCount);
                _syncCommitIds = toSync.Select(c => c.Id).ToHashSet();
                commits = [.. seed, .. toSync];
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        ((ISyncable)remote.DataModel).AddRangeFromSync(commits).Wait();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        local = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        _ = local.WriteNextChange(local.SetWord(Guid.NewGuid(), "entity1")).Result;
        //cant share commits between iterations, because EF modifies them
        var allCommits = remote.DataModel.GetChanges(new SyncState([])).Result.MissingFromClient;

        if (_syncCommitIds is null)
        {
            _commits = allCommits;
            return;
        }

        var seed = new List<Commit>(allCommits.Length);
        var toSync = new List<Commit>(_syncCommitIds.Count);
        foreach (var commit in allCommits)
        {
            if (_syncCommitIds.Contains(commit.Id))
                toSync.Add(commit);
            else
                seed.Add(commit);
        }

        ((ISyncable)local.DataModel).AddRangeFromSync(seed).Wait();
        _commits = toSync.ToArray();
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
