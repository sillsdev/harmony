using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Db;

public interface ICrdtRepositoryFactory
{
    Task<ICrdtRepository> CreateRepository();
    ICrdtRepository CreateRepositorySync();

    public async Task<T> Execute<T>(Func<ICrdtRepository, Task<T>> func)
    {
        await using var repo = await CreateRepository();
        return await func(repo);
    }

    public async Task Execute(Func<ICrdtRepository, Task> func)
    {
        await using var repo = await CreateRepository();
        await func(repo);
    }

    public async ValueTask<T> Execute<T>(Func<ICrdtRepository, ValueTask<T>> func)
    {
        await using var repo = await CreateRepository();
        return await func(repo);
    }
}

public class CrdtRepositoryFactory(IServiceProvider serviceProvider, ICrdtDbContextFactory dbContextFactory) : ICrdtRepositoryFactory
{
    public async Task<ICrdtRepository> CreateRepository()
    {
        return CreateInstance(await dbContextFactory.CreateDbContextAsync());
    }

    public ICrdtRepository CreateRepositorySync()
    {
        return CreateInstance(dbContextFactory.CreateDbContext());
    }

    public ICrdtRepository CreateInstance(ICrdtDbContext dbContext)
    {
        return ActivatorUtilities.CreateInstance<CrdtRepository>(serviceProvider, dbContext);
    }
}