using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SIL.Harmony.Prototype;

// PROTOTYPE — throwaway, answers sillsdev/harmony#115. Not production code.

/// <summary>
/// A run of consecutive commit positions a replay may resume from, closed at both ends. Every entity's newest
/// snapshot at or before any position in here holds that entity's state there.
/// </summary>
/// <remarks>
/// Ranges are the complement of the union of holes over every entity, so unlike the holes they are built from they
/// are disjoint and totally ordered. That is what makes them seekable: the range with the greatest From at or before
/// a position is the only one that can contain it.
///
/// Both endpoints are real commits, and the read path depends on that: when a position falls inside a range, the
/// answer is the newest commit before it, and the range's From is what bounds that search.
/// </remarks>
public class ResumeRange
{
    public Guid Id { get; init; }

    public required DateTimeOffset FromDateTime { get; init; }
    public required long FromCounter { get; init; }
    public required Guid FromCommitId { get; init; }

    public required DateTimeOffset ToDateTime { get; init; }
    public required long ToCounter { get; init; }
    public required Guid ToCommitId { get; init; }
}

public class ResumeRangeEntityConfig : IEntityTypeConfiguration<ResumeRange>
{
    public void Configure(EntityTypeBuilder<ResumeRange> builder)
    {
        builder.ToTable("ResumeRanges");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.FromDateTime).HasConversion(d => d.UtcDateTime, d => new DateTimeOffset(d.Ticks, TimeSpan.Zero));
        builder.Property(r => r.ToDateTime).HasConversion(d => d.UtcDateTime, d => new DateTimeOffset(d.Ticks, TimeSpan.Zero));
        //seek 1 of the read: the newest range starting at or before a position
        builder.HasIndex(r => new { r.FromDateTime, r.FromCounter, r.FromCommitId });
    }
}
