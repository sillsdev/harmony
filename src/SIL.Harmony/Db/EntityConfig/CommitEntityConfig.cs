using System.Text.Json;
using EFCore.ComplexIndexes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SIL.Harmony.Db.EntityConfig;

public class CommitEntityConfig : IEntityTypeConfiguration<Commit>
{
    public void Configure(EntityTypeBuilder<Commit> builder)
    {
        builder.ToTable("Commits");
        builder.HasKey(c => c.Id);
        builder.ComplexProperty(c => c.HybridDateTime,
            hybridEntity =>
            {
                hybridEntity.Property(h => h.DateTime)
                    .HasConversion(
                        d => d.UtcDateTime,
                        //need to use ticks here because the DateTime is stored as UTC, but the db records it as unspecified
                        d => new DateTimeOffset(d.Ticks, TimeSpan.Zero))
                    .HasColumnName("DateTime");
                hybridEntity.Property(h => h.Counter).HasColumnName("Counter");
            });
        // EF Core 10 cannot express indexes mixing ComplexProperty members + scalars (efcore#11336, targeted
        // for 11), hence EFCore.ComplexIndexes for both of these.
        // Both Harmony sorts are uniform-direction, so this ASC index serves DefaultOrderDescending via reverse scan.
        builder.HasComplexCompositeIndex(
            c => new { c.HybridDateTime.DateTime, c.HybridDateTime.Counter, c.Id },
            indexName: "IX_Commits_DateTime_Counter_Id");
        // Ordering-only: a leading bool column can't seek, so filter on it instead.
        builder.HasComplexCompositeIndex(
            c => new { c.HybridDateTime.DateTime, c.HybridDateTime.Counter, c.Id },
            index =>
            {
                index.HasName("IX_Commits_Checkpoint");
                index.HasFilter("\"IsSnapshotCheckpoint\"");
            });
        builder.Property(c => c.Metadata)
            .HasColumnType("jsonb")
            .HasConversion(
                m => JsonSerializer.Serialize(m, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<CommitMetadata>(json, (JsonSerializerOptions?)null) ?? new()
            );
        builder.HasMany(c => c.ChangeEntities)
            .WithOne()
            .HasForeignKey(c => c.CommitId);
    }
}
