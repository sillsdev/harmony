using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SIL.Harmony.Prototype;

// PROTOTYPE — throwaway, answers sillsdev/harmony#110. Not production code.
// Question: can "the newest safe position to resume from" be derived from hole records
// as one readable, EF-translatable, indexable query?

/// <summary>
/// An interval of commit positions at which one entity's newest surviving snapshot is older than its most recent
/// touch, because the snapshot at that touch was not kept. Half open: [From, To). Nothing may resume inside one.
/// </summary>
/// <remarks>
/// A hole exists only where a snapshot recording a state change was dropped, so never pruning means no rows at all.
/// Endpoints are commit positions, denormalised as the ordering tuple (DateTime, Counter, CommitId) so the stabbing
/// query needs no join back to Commits. They are positions, never batch indices: a late commit may land inside a
/// hole, and an index-based endpoint would silently shift under it.
/// </remarks>
public class SnapshotHole
{
    public Guid Id { get; init; }
    public Guid EntityId { get; init; }

    /// <summary>the dropped touch: the first position at which the entity's snapshots no longer hold its state</summary>
    public required DateTimeOffset FromDateTime { get; init; }
    public required long FromCounter { get; init; }
    public required Guid FromCommitId { get; init; }

    /// <summary>the entity's next surviving snapshot, which holds its state again</summary>
    public required DateTimeOffset ToDateTime { get; init; }
    public required long ToCounter { get; init; }
    public required Guid ToCommitId { get; init; }
}

public class SnapshotHoleEntityConfig : IEntityTypeConfiguration<SnapshotHole>
{
    public void Configure(EntityTypeBuilder<SnapshotHole> builder)
    {
        builder.ToTable("SnapshotHoles");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.FromDateTime).HasConversion(d => d.UtcDateTime, d => new DateTimeOffset(d.Ticks, TimeSpan.Zero));
        builder.Property(h => h.ToDateTime).HasConversion(d => d.UtcDateTime, d => new DateTimeOffset(d.Ticks, TimeSpan.Zero));
        // the stabbing query seeks holes whose From is at or before the candidate position
        builder.HasIndex(h => new { h.FromDateTime, h.FromCounter, h.FromCommitId });
    }
}
