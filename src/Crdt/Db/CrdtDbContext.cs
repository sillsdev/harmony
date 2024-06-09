using System.Text.Json;
using Crdt.Changes;
using Crdt.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crdt.Db;

public class CrdtDbContext(
    DbContextOptions<CrdtDbContext> options,
    IOptions<CrdtConfig> crdtConfig)
    : DbContext(options), ICrdtDbContext
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.UseCrdt(crdtConfig.Value);
    }

    public DbSet<Commit> Commits => Set<Commit>();
    public DbSet<ChangeEntity<IChange>> ChangeEntities => Set<ChangeEntity<IChange>>();
    public DbSet<ObjectSnapshot> Snapshots => Set<ObjectSnapshot>();
}
