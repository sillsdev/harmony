using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// Decides, for a replay of one batch, whether a superseded snapshot must still be persisted, and tracks which of the
/// batch's commits a later replay can resume from as a result. Has mutable state, one instance per replay.
/// </summary>
/// <remarks>
/// A superseded snapshot at commit X, replaced by one at commit Y, is its entity's state for every commit in [X, Y).
/// Dropping it leaves a hole there: the entity's state isn't persisted for those commits, so none of them is a checkpoint.
/// Some commits are required checkpoints, one each time the running total of changes reaches a multiple of
/// <c>maxChangesBetweenCheckpoints</c>. A snapshot whose coverage holds a required checkpoint is always kept, so no hole
/// contains one, which guarantees a checkpoint at least every <c>maxChangesBetweenCheckpoints</c> changes.
/// </remarks>
internal sealed class SnapshotCheckpointPolicy
{
    /// <summary>the batch, as given</summary>
    private readonly SortedSet<Commit> _commits;
    /// <summary>what comes after each batch commit, by commit id</summary>
    private readonly Dictionary<Guid, Successors> _successors;
    /// <summary>ids of the commits inside the coverage of any dropped snapshot</summary>
    private readonly HashSet<Guid> _holes;

    /// <param name="commits">the batch being replayed, in replay order</param>
    /// <param name="maxChangesBetweenCheckpoints">force a checkpoint at least this many changes apart</param>
    internal SnapshotCheckpointPolicy(SortedSet<Commit> commits, int maxChangesBetweenCheckpoints)
    {
        if (maxChangesBetweenCheckpoints < 1) throw new ArgumentOutOfRangeException(nameof(maxChangesBetweenCheckpoints));
        _commits = commits;
        _successors = new Dictionary<Guid, Successors>(commits.Count);
        _holes = new HashSet<Guid>(commits.Count);
        // commits since the last required checkpoint, which is the next required checkpoint of each of them
        var awaitingRequiredCheckpoint = new List<Commit>();
        Commit? previous = null;
        var changesSoFar = 0L;
        foreach (var commit in commits)
        {
            // a commit may already be in the map, or not yet, depending on which of its successors we learn first
            if (previous is not null) _successors[previous.Id] = _successors.GetValueOrDefault(previous.Id) with { Next = commit };
            previous = commit;

            awaitingRequiredCheckpoint.Add(commit);
            var changesBefore = changesSoFar;
            changesSoFar += commit.ChangeEntities.Count;
            // the total just reached or passed a multiple of maxChanges; a commit spanning several is still one checkpoint
            if (changesSoFar / maxChangesBetweenCheckpoints > changesBefore / maxChangesBetweenCheckpoints)
            {
                foreach (var awaiting in awaitingRequiredCheckpoint)
                {
                    _successors[awaiting.Id] = _successors.GetValueOrDefault(awaiting.Id) with { NextRequiredCheckpoint = commit };
                }

                awaitingRequiredCheckpoint.Clear();
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="superseded"/> must still be persisted now that <paramref name="newer"/> is its entity's
    /// newest snapshot. Answering no drops it, and the commits it was the entity's state for stop being checkpoints.
    /// </summary>
    internal bool MustKeep(ObjectSnapshot superseded, ObjectSnapshot newer)
    {
        if (superseded.CommitId == newer.CommitId)
        {
            // we (can) only keep 1 snapshot per entity per commit, so the new one replaces it and no history is lost
            return false;
        }

        if (superseded.IsRoot) return true; // always keep root snapshots

        // a required checkpoint in [superseded, newer) needs the superseded snapshot as its entity's state there
        var requiredCheckpoint = _successors.GetValueOrDefault(superseded.CommitId).NextRequiredCheckpoint;
        if (requiredCheckpoint is not null && _commits.Comparer.Compare(requiredCheckpoint, newer.Commit) < 0)
        {
            return true;
        }

        // the entity's state is no longer stored from the superseded commit up to (not including) the new one
        for (var commit = superseded.Commit; commit.Id != newer.CommitId; commit = _successors[commit.Id].Next!)
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

    /// <param name="Next">the next commit in the batch; none for the last</param>
    /// <param name="NextRequiredCheckpoint">
    /// the first required checkpoint at or after this commit; none after the last one. A required checkpoint is a commit
    /// where the running total of changes reached or passed a multiple of maxChanges.
    /// </param>
    private readonly record struct Successors(Commit? Next, Commit? NextRequiredCheckpoint);
}

/// <param name="IsCheckpoint">whether a replay can resume from <paramref name="Commit"/></param>
internal readonly record struct CheckpointFlag(Commit Commit, bool IsCheckpoint);
