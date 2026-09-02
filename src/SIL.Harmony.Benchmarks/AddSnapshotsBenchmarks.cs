using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Db;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

public enum AddSnapshotsWorkload
{
    /// <summary>Create many distinct entities (root snapshots, all inserts, no FindAsync hits).</summary>
    CreateNew,

    /// <summary>Word + Definition per commit — two projected table types.</summary>
    MultiTypeCreate,

    /// <summary>Word + Tag + WordTag per commit — three types plus a reference graph.</summary>
    ReferencedCreate,

    /// <summary>Seed created words, then update each once — updates project onto existing rows (FindAsync per snapshot).</summary>
    UpdateExisting,

    /// <summary>Modify one entity many times — large snapshot count (many intermediates), dedup, few projected rows.</summary>
    ModifySameEntity,

    /// <summary>Create then delete each entity — delete group + insert group, two SaveChanges.</summary>
    CreateThenDelete,

    /// <summary>Create, delete, then apply-on-deleted — mixed EntityIsDeleted projection skips.</summary>
    CreateDeleteModify,
}

// Isolates CrdtRepository.AddSnapshots (the slow, non-FAST path) from the rest of the sync pipeline.
// Expensive DB seeding happens once in GlobalSetup; each iteration gets a clean copy via ForkDatabase() and
// recomputes the snapshot batch so no EF-tracked state leaks across iterations.
// disable warning about waiting for sync code, benchmarkdotnet does not support async code, and it doesn't deadlock when waiting.
[SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits")]
public class AddSnapshotsBenchmarks
{
    private DataModelTestBase _template = null!;
    private HashSet<Guid> _measuredCommitIds = null!;

    private DataModelTestBase _local = null!;
    private CrdtRepository _repository = null!;
    private ObjectSnapshot[] _snapshotsToAdd = null!;

    [Params(
        1000
        // ,        10_000
        )]
    public int ChangeCount { get; set; }

    [ParamsAllValues]
    public AddSnapshotsWorkload Workload { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _template = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        var clientId = Guid.NewGuid();

        List<Commit> seed;
        List<Commit> measured;
        switch (Workload)
        {
            case AddSnapshotsWorkload.CreateNew:
                seed = [];
                measured = BenchmarkWorkloadBuilders.BuildCreateWords(_template, clientId, ChangeCount);
                break;
            case AddSnapshotsWorkload.MultiTypeCreate:
                seed = [];
                measured = BenchmarkWorkloadBuilders.BuildWordsWithDefinitions(_template, clientId, ChangeCount);
                break;
            case AddSnapshotsWorkload.ReferencedCreate:
                seed = [];
                measured = BenchmarkWorkloadBuilders.BuildWordsWithTags(_template, clientId, ChangeCount);
                break;
            case AddSnapshotsWorkload.UpdateExisting:
                (seed, measured) = BenchmarkWorkloadBuilders.BuildUpdateExisting(_template, clientId, ChangeCount);
                break;
            case AddSnapshotsWorkload.ModifySameEntity:
                seed = [];
                measured = BenchmarkWorkloadBuilders.BuildModifySameWord(_template, clientId, ChangeCount);
                break;
            case AddSnapshotsWorkload.CreateThenDelete:
                seed = [];
                measured = BenchmarkWorkloadBuilders.BuildCreateThenDelete(_template, clientId, ChangeCount);
                break;
            case AddSnapshotsWorkload.CreateDeleteModify:
                seed = [];
                measured = BenchmarkWorkloadBuilders.BuildCreateDeleteModify(_template, clientId, ChangeCount);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        // Seed commits go through the full pipeline so their snapshots and projected rows already exist.
        if (seed.Count > 0)
            ((ISyncable)_template.DataModel).AddRangeFromSync(seed).Wait();

        // The measured commits are present in the database but their snapshots are NOT yet persisted; that's the
        // work AddSnapshots performs. Adding only the commits mirrors the state right before UpdateSnapshots runs.
        var repository = _template.CreateRepository();
        repository.AddCommits(measured).GetAwaiter().GetResult();
        _measuredCommitIds = measured.Select(c => c.Id).ToHashSet();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _local = _template.ForkDatabase(alwaysValidate: false);
        _repository = _local.CreateRepository();

        // Load the measured commits fresh from the fork (tracked) so AddSnapshots resolves their Commit navigation
        // from the change tracker instead of trying to re-insert them.
        var measuredCommits = _local.DbContext.Commits
            .Include(c => c.ChangeEntities)
            .Where(c => EF.Parameter(_measuredCommitIds).Contains(c.Id))
            .ToArray()
            .ToSortedSet();

        // Prepopulate the snapshot lookup the same way DataModel.UpdateSnapshots does: existing snapshots (untracked)
        // plus null for entities without one, so SnapshotWorker doesn't issue a per-entity query while computing.
        var entityIds = measuredCommits
            .SelectMany(c => c.ChangeEntities.Select(ce => ce.EntityId))
            .ToHashSet();
        var snapshotLookup = _repository.CurrentSnapshots()
            .Include(s => s.Commit)
            .Where(s => EF.Parameter(entityIds).Contains(s.EntityId))
            .ToDictionary(s => s.EntityId, s => (ObjectSnapshot?)s);
        foreach (var entityId in entityIds)
            snapshotLookup.TryAdd(entityId, null);

        var worker = new SnapshotWorker(snapshotLookup, _repository, _local.CrdtConfig);
        _snapshotsToAdd = worker.ComputeSnapshotsToPersist(measuredCommits).GetAwaiter().GetResult().ToArray();
    }

    [Benchmark]
    public void AddSnapshots()
    {
        _repository.AddSnapshots(_snapshotsToAdd).Wait();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _repository.DisposeAsync().AsTask().Wait();
        _local.DisposeAsync().AsTask().Wait();
        _repository = null!;
        _local = null!;
        _snapshotsToAdd = null!;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _template.DisposeAsync().AsTask().Wait();
        _template = null!;
    }
}
