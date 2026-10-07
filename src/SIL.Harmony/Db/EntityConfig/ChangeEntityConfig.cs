using System.Runtime.Serialization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIL.Harmony.Changes;

namespace SIL.Harmony.Db.EntityConfig;

public class ChangeEntityConfig(JsonSerializerOptions jsonSerializerOptions)
    : IEntityTypeConfiguration<ChangeEntity<IChange>>
{
    public ChangeEntityConfig() : this(JsonSerializerOptions.Default)
    {
    }

    public void Configure(EntityTypeBuilder<ChangeEntity<IChange>> builder)
    {
        builder.ToTable("ChangeEntities");
        builder.HasKey(c => new { c.CommitId, c.Index });
        var changeProperty = builder.Property(c => c.Change)
            .HasColumnType("jsonb");
        if (EF.IsDesignTime)
        {
            changeProperty
                .HasConversion(
                    change => SerializeChange(change, null),
                    json => DeserializeChange(json, null)
                );
        }
        else
        {
            changeProperty
                .HasConversion(
                    change => SerializeChange(change, jsonSerializerOptions),
                    json => DeserializeChange(json, jsonSerializerOptions)
                );
        }
    }

    public static IChange DeserializeChange(string json, JsonSerializerOptions? jsonSerializerOptions)
    {
        return JsonSerializer.Deserialize<IChange>(json, jsonSerializerOptions) ??
               throw new SerializationException("Could not deserialize Change: " + json);
    }

    public static string SerializeChange(IChange change, JsonSerializerOptions? jsonSerializerOptions)
    {
        return JsonSerializer.Serialize(change, jsonSerializerOptions);
    }
}
