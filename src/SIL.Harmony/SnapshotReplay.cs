using Microsoft.Extensions.Options;
using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// Replays commits into snapshots, creating a <see cref="SnapshotWorker"/> per replay.
/// Callers of the persisting methods hold the repository lock and a transaction.
/// </summary>
internal class SnapshotReplay(IOptions<HarmonyConfig> config)
{
    private HarmonyConfig Config => config.Value;

    /// <inheritdoc cref="SnapshotWorker.ReplayCommits"/>
    public Task<ISnapshotView> ReplayCommits(ISnapshotView baseline, SortedSet<Commit> commits)
    {
        return SnapshotWorker.ReplayCommits(baseline, commits, Config);
    }

    /// <inheritdoc cref="SnapshotWorker.ComputeNewSnapshotsAndCheckpoints"/>
    public Task<(IReadOnlyList<ObjectSnapshot> Snapshots, CheckpointFlag[] CheckpointFlags)> ComputeNewSnapshotsAndCheckpoints(
        ISnapshotView baseline, SortedSet<Commit> commits)
    {
        return SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(baseline, commits, Config);
    }

    /// <summary>
    /// Rebuilds every snapshot the window covers by replaying its commits onto the state it resumes from.
    /// A window resuming from nothing rebuilds all of history.
    /// </summary>
    public async Task UpdateSnapshots(CrdtRepository repo, CrdtRepository.ReplayWindow window)
    {
        var (checkpoint, commitsToApply) = window;
        if (commitsToApply.Count == 0) return;

        ISnapshotView baseline;
        // A database with no checkpoints replays all of history,
        // which is what we want, because it will trigger creating checkpoints
        if (checkpoint is null)
        {
            await repo.DeleteSnapshotsAndProjectedTables();
            //the delete left the table empty, so there's nothing to query
            baseline = EmptySnapshotView.Instance;
        }
        else
        {
            await repo.DeleteSnapshotsAfter(checkpoint.Commit);
            //the current table is the state at the checkpoint, because the delete above just made it so
            baseline = repo.CurrentSnapshotView();
        }

        await PreloadTouched(baseline, commitsToApply);
        var (newSnapshots, checkpoints) = await ComputeNewSnapshotsAndCheckpoints(baseline, commitsToApply);
        await repo.AddSnapshots(newSnapshots, checkpoints);
    }

    /// <summary>Rebuilds every snapshot from all of history.</summary>
    public async Task RegenerateAll(CrdtRepository repo)
    {
        var wholeHistory = await repo.WholeHistory();
        //Replay does nothing without commits, which would leave snapshots with no history behind them in place
        if (wholeHistory.Commits.Count == 0) await repo.DeleteSnapshotsAndProjectedTables();
        else await UpdateSnapshots(repo, wholeHistory);
    }

    /// <summary>one query for every entity the commits touch beats a lookup per entity only for large batches</summary>
    public async Task PreloadTouched(ISnapshotView baseline, IEnumerable<Commit> commits)
    {
        var entityIds = commits.SelectMany(c => c.ChangeEntities.Select(ce => ce.EntityId)).ToHashSet();
        if (entityIds.Count > Config.PrefetchSnapshotsBreakpoint) await baseline.PreloadAsync(entityIds);
    }
}
