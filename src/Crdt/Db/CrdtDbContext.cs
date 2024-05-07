using System.Runtime.Serialization;
using System.Text.Json;
using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crdt.Db;

public class CrdtDbContext(
    DbContextOptions<CrdtDbContext> options,
    IOptions<CrdtConfig> crdtConfig,
    JsonSerializerOptions jsonSerializerOptions)
    : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        foreach (var modelConvention in crdtConfig.Value.ObjectTypeListBuilder.ModelConventions)
        {
            modelConvention.Invoke(configurationBuilder);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        var commitEntity = builder.Entity<Commit>();
        commitEntity.HasKey(c => c.Id);
        commitEntity.ComplexProperty(c => c.HybridDateTime,
            hybridEntity =>
            {
                hybridEntity.Property(h => (DateTimeOffset?)h.DateTime)
                    .HasConversion(
                        d => d!.Value.ToUniversalTime().DateTime,
                        d => new DateTimeOffset(d, TimeSpan.Zero))
                    .HasColumnName("DateTime");
                hybridEntity.Property(h => h.Counter).HasColumnName("Counter");
            });
        commitEntity.HasMany(c => c.ChangeEntities)
            .WithOne()
            .HasForeignKey(c => c.CommitId);
        var snapshotObject = builder.Entity<ObjectSnapshot>();
        snapshotObject.HasKey(s => s.Id);
        snapshotObject.HasIndex(s => new { s.CommitId, s.EntityId }).IsUnique();
        snapshotObject
            .HasOne(s => s.Commit)
            .WithMany(c => c.Snapshots)
            .HasForeignKey(s => s.CommitId);
        snapshotObject.Property(s => s.Entity)
            .HasColumnType("jsonb")
            .HasConversion(
                entry =>  JsonSerializer.Serialize(entry, jsonSerializerOptions),
                json => DeserializeObject(json)
            );
        var changeEntity = builder.Entity<ChangeEntity<IChange>>();
        changeEntity.HasKey(c => new {c.CommitId, c.Index});
        changeEntity.Property(c => c.Change)
            .HasColumnType("jsonb")
            .HasConversion(
                change => JsonSerializer.Serialize(change, jsonSerializerOptions),
                json => DeserializeChange(json)
            );

        foreach (var modelConfiguration in crdtConfig.Value.ObjectTypeListBuilder.ModelConfigurations)
        {
            modelConfiguration(builder, crdtConfig.Value);
        }
    }

    private IChange DeserializeChange(string json)
    {
        return JsonSerializer.Deserialize<IChange>(json, jsonSerializerOptions) ??
               throw new SerializationException("Could not deserialize Change: " + json);
    }

    private IObjectBase DeserializeObject(string json)
    {
        return JsonSerializer.Deserialize<IObjectBase>(json, jsonSerializerOptions) ??
               throw new SerializationException("Could not deserialize Entry: " + json);
    }

    public DbSet<Commit> Commits { get; set; } = null!;
    public DbSet<ChangeEntity<IChange>> ChangeEntities { get; set; } = null!;
    public DbSet<ObjectSnapshot> Snapshots { get; set; } = null!;
}

public static class DbSetExtensions
{
    public static IQueryable<ObjectSnapshot> DefaultOrder(this IQueryable<ObjectSnapshot> queryable)
    {
        return queryable
            .OrderBy(c => c.Commit.HybridDateTime.DateTime)
            .ThenBy(c => c.Commit.HybridDateTime.Counter)
            .ThenBy(c => c.Commit.Id);
    }
}
