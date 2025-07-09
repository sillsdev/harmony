using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SIL.Harmony.Db;

public class CrdtDbContextFactory<TContext>(IDbContextFactory<TContext> dbContextFactory) : ICrdtDbContextFactory
    where TContext : DbContext, ICrdtDbContext
{
    public async Task<ICrdtDbContext> CreateDbContextAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        return await dbContextFactory.CreateDbContextAsync(cancellationToken);
    }

    public ICrdtDbContext CreateDbContext()
    {
        return dbContextFactory.CreateDbContext();
    }
}

public interface ICrdtDbContextFactory
{
    Task<ICrdtDbContext> CreateDbContextAsync(CancellationToken cancellationToken = new CancellationToken());
    ICrdtDbContext CreateDbContext();
}

public class CrdtDbContextNoDisposeFactory<TContext>(TContext dbContext) : ICrdtDbContextFactory
    where TContext : ICrdtDbContext
{
    public Task<ICrdtDbContext> CreateDbContextAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        return Task.FromResult<ICrdtDbContext>(new NoDisposeWrapper(dbContext));
    }

    public ICrdtDbContext CreateDbContext()
    {
        return new NoDisposeWrapper(dbContext);
    }

    private class NoDisposeWrapper(ICrdtDbContext context): ICrdtDbContext
    {
        public void Dispose()
        {
            //noop, don't dispose because the context is owned by the ioc container
        }

        public ValueTask DisposeAsync()
        {
            //noop, don't dispose because the context is owned by the ioc container
            return ValueTask.CompletedTask;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return context.SaveChangesAsync(cancellationToken);
        }

        public ValueTask<object?> FindAsync(Type entityType, params object?[]? keyValues)
        {
            return context.FindAsync(entityType, keyValues);
        }

        public DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            return context.Set<TEntity>();
        }

        public DatabaseFacade Database => context.Database;

        public ChangeTracker ChangeTracker => context.ChangeTracker;

        public EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class
        {
            return context.Entry(entity);
        }

        public EntityEntry Entry(object entity)
        {
            return context.Entry(entity);
        }

        public EntityEntry Add(object entity)
        {
            return context.Add(entity);
        }

        public void AddRange(IEnumerable<object> entities)
        {
            context.AddRange(entities);
        }

        public EntityEntry Remove(object entity)
        {
            return context.Remove(entity);
        }
    }
}
