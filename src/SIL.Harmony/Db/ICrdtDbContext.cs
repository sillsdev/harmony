using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SIL.Harmony.Db;

public interface ICrdtDbContext : IDisposable, IAsyncDisposable
{
    IQueryable<Commit> Commits => Set<Commit>();
    IQueryable<ObjectSnapshot> Snapshots => Set<ObjectSnapshot>();
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    ValueTask<object?> FindAsync(Type entityType, params object?[]? keyValues);
    DbSet<TEntity> Set<TEntity>() where TEntity : class;
    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
    EntityEntry Entry(object entity);
    EntityEntry Add(object entity);
    void AddRange(IEnumerable<object> entities);
    EntityEntry Remove(object entity);
}
