using System.Text;

namespace SIL.Harmony.Tests.PropertyBased;

/// <summary>
/// An immutable description of a single commit to be minted at feed time.
/// We store specs rather than <see cref="Commit"/> instances because the engine
/// mutates a commit's Hash/ParentHash (and attaches Snapshots) while ingesting it,
/// so a single <see cref="Commit"/> instance cannot be safely fed to two engines or
/// replayed. Minting a fresh <see cref="Commit"/> from a spec keeps the logical
/// identity (<see cref="Id"/>) stable so dedup and canonical ordering are reproducible.
/// </summary>
/// <param name="Id">Stable, deterministic commit id — the final canonical-order tiebreaker and the dedup key.</param>
/// <param name="Time">The hybrid logical clock stamp used for canonical ordering.</param>
/// <param name="Changes">The change(s) this commit carries, applied in list order (ChangeEntity.Index).</param>
public sealed record CommitSpec(Guid Id, HybridDateTime Time, IReadOnlyList<ChangeSpec> Changes)
{
    /// <summary>
    /// The canonical total order key, matching <c>CommitBase.CompareKey</c>
    /// (DateTime, then Counter, then Id). Used by the generator meta-tests to reason
    /// about ties and stragglers without touching the engine.
    /// </summary>
    public (DateTimeOffset, long, Guid) CompareKey => (Time.DateTime, Time.Counter, Id);
}

/// <summary>One ingest batch: the commits that arrive together in a single sync call.</summary>
public sealed record Arrival(IReadOnlyList<CommitSpec> Batch);

/// <summary>
/// A generated test schedule: one canonical commit set, delivered two independent ways.
/// <see cref="ArrivalsA"/> and <see cref="ArrivalsB"/> each deliver the full
/// <see cref="Commits"/> set (plus possible duplicate re-sends) in an independently
/// shuffled and batched order, so a single set can be cross-checked for
/// arrival-order independence (replica convergence).
/// </summary>
public sealed record Schedule(
    IReadOnlyList<CommitSpec> Commits,
    IReadOnlyList<Arrival> ArrivalsA,
    IReadOnlyList<Arrival> ArrivalsB)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Schedule: {Commits.Count} commits, {ArrivalsA.Count} arrivals A, {ArrivalsB.Count} arrivals B");
        sb.AppendLine("Commits:");
        foreach (var commit in Commits.OrderBy(c => c.CompareKey))
        {
            sb.AppendLine($"  {commit.Id} @ {commit.Time} ({commit.Changes.Count} changes), [{string.Join(", ", commit.Changes.Select(c => $"EntityId: {c.EntityId}, Type: {c.GetType().Name}"))}]");
        }
        sb.AppendLine("Arrivals A:");
        foreach (var arrival in ArrivalsA)
        {
            sb.AppendLine($"  Batch of {arrival.Batch.Count} commits [{string.Join(", ", arrival.Batch.Select(c => c.Id))}]");
        }
        sb.AppendLine("Arrivals B:");
        foreach (var arrival in ArrivalsB)
        {
            sb.AppendLine($"  Batch of {arrival.Batch.Count} commits [{string.Join(", ", arrival.Batch.Select(c => c.Id))}]");
        }
        return sb.ToString();
    }
}
