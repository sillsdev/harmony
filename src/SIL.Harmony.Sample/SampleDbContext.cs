using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony.Sample;

public class SampleDbContext(DbContextOptions<SampleDbContext> options, IOptions<HarmonyConfig> crdtConfig) : DbContext(options), ICrdtDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCrdt(crdtConfig.Value);
    }

    public DbSet<Commit> Commits => Set<Commit>();
    public DbSet<ChangeEntity<IChange>> ChangeEntities => Set<ChangeEntity<IChange>>();
    public DbSet<ObjectSnapshot> Snapshots => Set<ObjectSnapshot>();
}
