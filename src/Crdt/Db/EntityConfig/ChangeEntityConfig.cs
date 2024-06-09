using System.Runtime.Serialization;
using System.Text.Json;
using Crdt.Changes;
using Crdt.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crdt.Db.EntityConfig;

public class ChangeEntityConfig(JsonSerializerOptions jsonSerializerOptions) : IEntityTypeConfiguration<ChangeEntity<IChange>>
{
    public void Configure(EntityTypeBuilder<ChangeEntity<IChange>> builder)
    {
        builder.HasKey(c => new { c.CommitId, c.Index });
        builder.Property(c => c.Change)
            .HasColumnType("jsonb")
            .HasConversion(
                change => JsonSerializer.Serialize(change, jsonSerializerOptions),
                json => DeserializeChange(json)
            );
    }

    private IChange DeserializeChange(string json)
    {
        return JsonSerializer.Deserialize<IChange>(json, jsonSerializerOptions) ??
               throw new SerializationException("Could not deserialize Change: " + json);
    }
}