using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SIL.Harmony.Db.EntityConfig;

public class LocalStateEntryConfig : IEntityTypeConfiguration<LocalStateEntry>
{
    public void Configure(EntityTypeBuilder<LocalStateEntry> builder)
    {
        builder.ToTable("LocalState");
        builder.HasKey(e => e.Key);
    }
}
