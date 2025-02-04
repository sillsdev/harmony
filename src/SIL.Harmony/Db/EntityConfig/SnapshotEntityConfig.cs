using System.Runtime.Serialization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SIL.Harmony.Db.EntityConfig;

public class SnapshotEntityConfig(JsonSerializerOptions jsonSerializerOptions) : IEntityTypeConfiguration<ObjectSnapshot>
{
    public void Configure(EntityTypeBuilder<ObjectSnapshot> builder)
    {
        builder.ToTable("Snapshots");
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => new { s.CommitId, s.EntityId }).IsUnique();
        builder
            .HasOne(s => s.Commit)
            .WithMany(c => c.Snapshots)
            .HasForeignKey(s => s.CommitId);
        builder.Property(s => s.Entity)
            .HasColumnType("jsonb")
            .HasConversion(
                entry => JsonSerializer.Serialize(entry, jsonSerializerOptions),
                json => DeserializeObject(json)
            );
    }

    private IObjectBase DeserializeObject(string json)
    {
        return JsonSerializer.Deserialize<IObjectBase>(json, jsonSerializerOptions) ??
               throw new SerializationException($"Could not deserialize Entry: {json}");
    }
}
