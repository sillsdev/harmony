using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// Decides, for a replay of one batch, whether a superseded snapshot must still be persisted, and tracks which of the
/// batch's commits a later replay can resume from as a result. Has mutable state, one instance per replay.
/// </summary>
/// <remarks>
/// A superseded snapshot at commit X, replaced by one at commit Y, is its entity's state for every commit in [X, Y).
/// Dropping it leaves a hole there: the entity's state isn't persisted for those commits, so none of them is a checkpoint.
/// Some commits are required checkpoints, one each time <c>maxChangesBetweenCheckpoints</c> changes have accumulated
/// since the previous one. A snapshot whose coverage holds a required checkpoint is always kept, so no hole contains one,
/// which guarantees a checkpoint at least every <c>maxChangesBetweenCheckpoints</c> changes.
/// </remarks>
internal sealed class SnapshotCheckpointPolicy
{
    /// <summary>the batch, as given</summary>
    private readonly SortedSet<Commit> _commits;
    /// <summary>what comes after each batch commit</summary>
    private readonly CommitChain _chain;
    /// <summary>ids of the commits inside the coverage of any dropped snapshot</summary>
    private readonly HashSet<Guid> _holes;

    /// <param name="commits">the batch being replayed, in replay order</param>
    /// <param name="maxChangesBetweenCheckpoints">force a checkpoint at least this many changes apart</param>
    internal SnapshotCheckpointPolicy(SortedSet<Commit> commits, int maxChangesBetweenCheckpoints)
    {
        if (maxChangesBetweenCheckpoints < 1) throw new ArgumentOutOfRangeException(nameof(maxChangesBetweenCheckpoints));
        _commits = commits;
        _chain = new CommitChain(commits.Count);
        _holes = new HashSet<Guid>(commits.Count);
        // commits since the last checkpoint
        var awaitingCheckpoint = new List<Commit>(Math.Min(maxChangesBetweenCheckpoints, commits.Count));
        Commit? previous = null;
        var changesSinceCheckpoint = 0L;

        foreach (var commit in commits)
        {
            if (previous is not null) _chain.SetNext(previous, commit);
            previous = commit;

            awaitingCheckpoint.Add(commit);
            changesSinceCheckpoint += commit.ChangeEntities.Count;
            // the total just reached or passed maxChanges; a commit several times that size is still one checkpoint
            if (changesSinceCheckpoint >= maxChangesBetweenCheckpoints)
            {
                foreach (var awaiting in awaitingCheckpoint)
                {
                    _chain.SetNextRequiredCheckpoint(awaiting, commit);
                }

                awaitingCheckpoint.Clear();
                changesSinceCheckpoint = 0;
            }
        }
    }

    /// <summary>
    /// Records that <paramref name="by"/> is now the newest snapshot of <paramref name="older"/>'s entity, and says whether
    /// <paramref name="older"/> must still be persisted. When it needn't be it is dropped, and the commits it was the
    /// entity's state for stop being checkpoints.
    /// </summary>
    /// <returns>whether to keep <paramref name="older"/></returns>
    internal bool Supersede(ObjectSnapshot older, ObjectSnapshot by)
    {
        if (older.CommitId == by.CommitId)
        {
            // we (can) only keep 1 snapshot per entity per commit, so the new one replaces it and no history is lost
            return false;
        }

        if (older.IsRoot) return true; // always keep root snapshots

        // a required checkpoint in [older, by) needs the older snapshot as its entity's state there
        var requiredCheckpoint = _chain.NextRequiredCheckpoint(older.Commit);
        if (requiredCheckpoint is not null && requiredCheckpoint < by.Commit)
        {
            return true;
        }

        // the entity's state is no longer stored from the older commit up to (not including) the new one
        foreach (var commit in _chain.Between(older.Commit, by.Commit))
        {
            _holes.Add(commit.Id);
        }

        return false;
    }

    /// <summary>
    /// Each batch commit, in order, paired with whether a later replay can resume from it. Only valid once the replay is
    /// done: until then we don't know which snapshots get dropped.
    /// </summary>
    internal CheckpointFlag[] CheckpointFlags() =>
        [.. _commits.Select(c => new CheckpointFlag(c, IsCheckpoint: !_holes.Contains(c.Id)))];
}

/// <param name="IsCheckpoint">whether a replay can resume from <paramref name="Commit"/></param>
internal readonly record struct CheckpointFlag(Commit Commit, bool IsCheckpoint);
