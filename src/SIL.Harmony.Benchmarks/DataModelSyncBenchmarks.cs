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
        var commits = Workload switch
        {
            SyncWorkload.CreateWords => BuildCreateWords(clientId),
            SyncWorkload.WordsWithDefinitions => BuildWordsWithDefinitions(clientId),
            SyncWorkload.WordsWithTags => BuildWordsWithTags(clientId),
            SyncWorkload.ModifySameWord => BuildModifySameWord(clientId),
            SyncWorkload.CreateThenDelete => BuildCreateThenDelete(clientId),
            SyncWorkload.CreateDeleteModify => BuildCreateDeleteModify(clientId),
            SyncWorkload.OutOfOrderInsert => BuildOutOfOrderInsert(clientId),
            _ => throw new ArgumentOutOfRangeException()
        };
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

    private List<Commit> BuildCreateWords(Guid clientId)
    {
        var commits = new List<Commit>(ChangeCount);
        for (var i = 0; i < ChangeCount; i++)
        {
            commits.Add(NewCommit(clientId, remote.NextDate(),
                remote.SetWord(Guid.NewGuid(), $"entity {i}")));
        }
        return commits;
    }

    private List<Commit> BuildWordsWithDefinitions(Guid clientId)
    {
        var commits = new List<Commit>(ChangeCount);
        for (var i = 0; i < ChangeCount; i++)
        {
            var wordId = Guid.NewGuid();
            commits.Add(NewCommit(clientId, remote.NextDate(),
                remote.SetWord(wordId, $"entity {i}"),
                remote.NewDefinition(wordId, $"definition {i}", "noun")));
        }
        return commits;
    }

    private List<Commit> BuildWordsWithTags(Guid clientId)
    {
        var commits = new List<Commit>(ChangeCount);
        for (var i = 0; i < ChangeCount; i++)
        {
            var wordId = Guid.NewGuid();
            var tagId = Guid.NewGuid();
            commits.Add(NewCommit(clientId, remote.NextDate(),
                remote.SetWord(wordId, $"entity {i}"),
                remote.SetTag(tagId, $"tag {i}"),
                remote.TagWord(wordId, tagId)));
        }
        return commits;
    }

    private List<Commit> BuildModifySameWord(Guid clientId)
    {
        var wordId = Guid.NewGuid();
        var commits = new List<Commit>(ChangeCount);
        commits.Add(NewCommit(clientId, remote.NextDate(),
            remote.SetWord(wordId, "entity 0")));
        for (var i = 1; i < ChangeCount; i++)
        {
            commits.Add(NewCommit(clientId, remote.NextDate(),
                remote.SetWord(wordId, $"entity {i}")));
        }
        return commits;
    }

    private List<Commit> BuildCreateThenDelete(Guid clientId)
    {
        var commits = new List<Commit>(ChangeCount * 2);
        for (var i = 0; i < ChangeCount; i++)
        {
            var wordId = Guid.NewGuid();
            commits.Add(NewCommit(clientId, remote.NextDate(),
                remote.SetWord(wordId, $"entity {i}")));
            commits.Add(NewCommit(clientId, remote.NextDate(),
                remote.DeleteWord(wordId)));
        }
        return commits;
    }

    private List<Commit> BuildCreateDeleteModify(Guid clientId)
    {
        var commits = new List<Commit>(ChangeCount * 3);
        for (var i = 0; i < ChangeCount; i++)
        {
            var wordId = Guid.NewGuid();
            commits.Add(NewCommit(clientId, remote.NextDate(),
                remote.SetWord(wordId, $"entity {i}")));
            commits.Add(NewCommit(clientId, remote.NextDate(),
                remote.DeleteWord(wordId)));
            // SetWordNote supports apply-on-existing (including deleted) without undeleting
            commits.Add(NewCommit(clientId, remote.NextDate(),
                remote.SetWordNote(wordId, $"note {i}")));
        }
        return commits;
    }

    private List<Commit> BuildOutOfOrderInsert(Guid clientId)
    {
        var seed = new List<Commit>(ChangeCount * 2);
        var toSync = new List<Commit>(ChangeCount);
        for (var i = 0; i < ChangeCount; i++)
        {
            var wordId = Guid.NewGuid();
            var createTime = remote.NextDate();
            var midTime = remote.NextDate();
            var lateTime = remote.NextDate();

            seed.Add(NewCommit(clientId, createTime,
                remote.SetWord(wordId, $"entity {i}")));
            // Mid-history change is what gets synced after local already has create + late
            toSync.Add(NewCommit(clientId, midTime,
                remote.SetWordNote(wordId, $"note {i}")));
            seed.Add(NewCommit(clientId, lateTime,
                remote.SetWord(wordId, $"entity {i} late")));
        }

        _syncCommitIds = toSync.Select(c => c.Id).ToHashSet();
        return [..seed, ..toSync];
    }

    private static Commit NewCommit(Guid clientId, DateTimeOffset dateTime, params IChange[] changes)
    {
        var commit = new Commit(Guid.NewGuid())
        {
            ClientId = clientId,
            HybridDateTime = new HybridDateTime(dateTime, 0)
        };
        for (var i = 0; i < changes.Length; i++)
        {
            commit.ChangeEntities.Add(DataModel.ToChangeEntity(changes[i], i, commit.Id));
        }
        return commit;
    }
}
