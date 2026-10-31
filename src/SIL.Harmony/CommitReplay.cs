using System.Linq.Expressions;
using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// Steps through a range of commits, one replay for the whole range, from <see cref="DataModel.StartCommitReplay"/>.
/// It is itself the view of the snapshots as of <see cref="CurrentCommit"/>, so what it returns changes with each
/// <see cref="ApplyNextCommit"/>, and reading it before the first one throws; <see cref="ObjectSnapshot"/>s read from it
/// stay valid. Owns a database connection until disposed.
/// </summary>
public sealed class CommitReplay : ISnapshotView, IAsyncDisposable
{
    private readonly CrdtRepository _repo;
    private readonly ISnapshotView _baseline;
    private readonly SnapshotWorker _worker;
    private readonly Commit[] _commits;
    private readonly HarmonyConfig _config;
    private int _next;
    /// <summary>exclusive end of the commits whose touched entities are preloaded</summary>
    private int _preloadedUpTo;
    private bool _disposed;

    private CommitReplay(CrdtRepository repo, ISnapshotView baseline, Commit[] commits, HarmonyConfig config)
    {
        _repo = repo;
        _baseline = baseline;
        _worker = SnapshotWorker.Stepwise(baseline, config);
        _commits = commits;
        _config = config;
    }

    internal static async Task<CommitReplay> Start(CrdtRepository repo, Commit from, Commit to, HarmonyConfig config)
    {
        //strictly before, so `from` itself is one of the commits applied rather than the resume point
        var checkpoint = await repo.FindCheckpointBefore(from);
        var commits = await repo.GetCommitsBetween(afterExclusive: checkpoint, upToInclusive: to);
        var replay = new CommitReplay(repo, repo.SnapshotViewAsOf(checkpoint), [.. commits], config);
        while (replay._next < replay._commits.Length && replay._commits[replay._next].CompareKey.CompareTo(from.CompareKey) < 0)
        {
            await replay.ApplyNext();
        }

        return replay;
    }

    /// <summary>the last commit applied, null before the first <see cref="ApplyNextCommit"/></summary>
    public Commit? CurrentCommit { get; private set; }

    /// <returns>the commit just applied, or null once the range is done (the view then stays at the last one)</returns>
    public async Task<Commit?> ApplyNextCommit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_next == _commits.Length) return null;
        return CurrentCommit = await ApplyNext();
    }

    private async Task<Commit> ApplyNext()
    {
        //one preload per checkpoint window, so a long replay never holds every touched entity's baseline at once
        if (_next == _preloadedUpTo)
        {
            var windowEnd = _next;
            while (windowEnd < _commits.Length - 1 && !_commits[windowEnd].IsSnapshotCheckpoint) windowEnd++;
            _preloadedUpTo = windowEnd + 1;
            await SnapshotReplay.PreloadTouched(_baseline, _commits[_next.._preloadedUpTo], _config);
        }

        var commit = _commits[_next++];
        await _worker.ApplyCommit(commit);
        return commit;
    }

    public ValueTask<ObjectSnapshot?> GetAsync(Guid entityId) => Open().GetAsync(entityId);

    public IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicate) => Open().Where(predicate);

    public IAsyncEnumerable<ObjectSnapshot> All() => Open().All();

    public Task PreloadAsync(IReadOnlyCollection<Guid> entityIds) => Open().PreloadAsync(entityIds);

    public Task PreloadAllAsync() => Open().PreloadAllAsync();

    private SnapshotWorker Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        //before then the worker holds commits from before the range, applied to reach its start
        if (CurrentCommit is null) throw new InvalidOperationException($"Call {nameof(ApplyNextCommit)} before reading the replay");
        return _worker;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _repo.DisposeAsync();
    }
}
