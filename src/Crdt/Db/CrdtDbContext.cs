using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crdt.Db;

[Obsolete($"use {nameof(ICrdtDbContext)} instead")]
public class CrdtDbContext(
    DbContextOptions<CrdtDbContext> options,
    IOptions<CrdtConfig> crdtConfig)
    : DbContext(options), ICrdtDbContext
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.UseCrdt(crdtConfig.Value);
    }
}
