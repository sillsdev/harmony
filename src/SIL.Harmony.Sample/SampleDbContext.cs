using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SIL.Harmony.Sample;

public class SampleDbContext(DbContextOptions<SampleDbContext>options, IOptions<CrdtConfig> crdtConfig): DbContext(options), ICrdtDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCrdt(crdtConfig.Value);
    }

    public DbSet<Commit> Commits => Set<Commit>();
    public DbSet<ChangeEntity<IChange>> ChangeEntities => Set<ChangeEntity<IChange>>();
    public DbSet<ObjectSnapshot> Snapshots => Set<ObjectSnapshot>();
}