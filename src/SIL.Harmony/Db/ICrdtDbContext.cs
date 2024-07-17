using SIL.Harmony.Changes;
using SIL.Harmony.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SIL.Harmony.Db;

public interface ICrdtDbContext
{
    DbSet<Commit> Commits => Set<Commit>();
    DbSet<ObjectSnapshot> Snapshots => Set<ObjectSnapshot>();
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    ValueTask<object?> FindAsync(Type entityType, params object?[]? keyValues);
    DbSet<TEntity> Set<TEntity>() where TEntity : class;
    DatabaseFacade Database { get; }
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
    EntityEntry Entry(object entity);
    EntityEntry Add(object entity);
    EntityEntry Remove(object entity);
}