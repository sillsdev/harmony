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
        builder.HasIndex(s => s.EntityId);
        builder
            .HasOne(s => s.Commit)
            .WithMany(c => c.Snapshots)
            .HasForeignKey(s => s.CommitId);
        var entityProperty = builder.Property(s => s.Entity)
            .HasColumnType("jsonb");
        if (EF.IsDesignTime)
        {
            entityProperty.HasConversion(
                entry => Serialize(entry, null),
                json => DeserializeObject(json, null)
            );
        }
        else
        {
            entityProperty.HasConversion(
                entry => Serialize(entry, jsonSerializerOptions),
                json => DeserializeObject(json, jsonSerializerOptions)
            );
        }
    }

    public static IObjectBase DeserializeObject(string json, JsonSerializerOptions? jsonSerializerOptions)
    {
        return JsonSerializer.Deserialize<IObjectBase>(json, jsonSerializerOptions) ??
               throw new SerializationException($"Could not deserialize Entry: {json}");
    }

    public static string Serialize(IObjectBase change, JsonSerializerOptions? jsonSerializerOptions)
    {
        return JsonSerializer.Serialize(change, jsonSerializerOptions);
    }
}
