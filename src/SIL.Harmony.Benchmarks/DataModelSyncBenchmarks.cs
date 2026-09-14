using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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

    [Params(1000, 10_000)]
    public int ChangeCount { get; set; }

    [ParamsAllValues]
    public SyncWorkload Workload { get; set; }

    private Commit[] _commits = null!;
    private List<Commit>? _toSeed = null;
    private List<Commit>? _toSync = null;
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
                _toSeed = seed;
                _toSync = toSync;
                commits = [.. seed, .. toSync];
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        ((ISyncable)remote.DataModel).AddRangeFromSync(commits).Wait();
        _toSync ??= commits;
    }

    private T Copy<T>(T obj) where T : class
    {
        var json = JsonSerializer.Serialize(obj, remote.CrdtConfig.JsonSerializerOptions);
        return JsonSerializer.Deserialize<T>(json, remote.CrdtConfig.JsonSerializerOptions)!;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        local = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        _ = local.WriteNextChange(local.SetWord(Guid.NewGuid(), "entity1")).Result;

        if (_toSeed is not null)
        {
            ((ISyncable)local.DataModel).AddRangeFromSync(Copy(_toSeed)).Wait();
        }
        if (_toSync is not null)
        {
            //cant share commits between iterations, because EF modifies them
            _commits = Copy(_toSync).ToArray();
            return;
        }
        throw new InvalidOperationException("No commits to sync");
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
