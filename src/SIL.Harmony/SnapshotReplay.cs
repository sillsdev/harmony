using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// Rebuilds and persists snapshots by replaying commits. Callers hold the repository lock and a transaction.
/// </summary>
internal static class SnapshotReplay
{
    /// <summary>
    /// Rebuilds every snapshot the window covers by replaying its commits onto the state it resumes from.
    /// A window resuming from nothing rebuilds all of history.
    /// </summary>
    public static async Task UpdateSnapshots(CrdtRepository repo, CrdtRepository.ReplayWindow window, HarmonyConfig config)
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

        await PreloadTouched(baseline, commitsToApply, config);
        var (newSnapshots, checkpoints) = await SnapshotWorker.ComputeNewSnapshotsAndCheckpoints(baseline, commitsToApply, config);
        await repo.AddSnapshots(newSnapshots, checkpoints);
    }

    /// <summary>Rebuilds every snapshot from all of history.</summary>
    public static async Task RegenerateAll(CrdtRepository repo, HarmonyConfig config)
    {
        var wholeHistory = await repo.WholeHistory();
        //Replay does nothing without commits, which would leave snapshots with no history behind them in place
        if (wholeHistory.Commits.Count == 0) await repo.DeleteSnapshotsAndProjectedTables();
        else await UpdateSnapshots(repo, wholeHistory, config);
    }

    public static async Task PreloadTouched(ISnapshotView baseline, IEnumerable<Commit> commits, HarmonyConfig config)
    {
        var entityIds = commits.SelectMany(c => c.ChangeEntities.Select(ce => ce.EntityId)).ToHashSet();
        if (entityIds.Count > config.PrefetchSnapshotsBreakpoint) await baseline.PreloadAsync(entityIds);
    }
}
