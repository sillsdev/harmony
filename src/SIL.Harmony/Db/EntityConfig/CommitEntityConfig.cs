using System.Text.Json;
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
