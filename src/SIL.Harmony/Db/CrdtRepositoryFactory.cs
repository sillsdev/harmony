using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Db;

public interface ICrdtRepositoryFactory
{
    Task<ICrdtRepository> CreateRepository();
    ICrdtRepository CreateRepositorySync();
    Task<T> Execute<T>(Func<ICrdtRepository, Task<T>> func);
    ValueTask<T> Execute<T>(Func<ICrdtRepository, ValueTask<T>> func);
}

public class CrdtRepositoryFactory(IServiceProvider serviceProvider, ICrdtDbContextFactory dbContextFactory) : ICrdtRepositoryFactory
{
    public async Task<ICrdtRepository> CreateRepository()
    {
        return ActivatorUtilities.CreateInstance<CrdtRepository>(serviceProvider, await dbContextFactory.CreateDbContextAsync());
    }

    public ICrdtRepository CreateRepositorySync()
    {
        return ActivatorUtilities.CreateInstance<CrdtRepository>(serviceProvider, dbContextFactory.CreateDbContext());
    }

    public async Task<T> Execute<T>(Func<ICrdtRepository, Task<T>> func)
    {
        await using var repo = await CreateRepository();
        return await func(repo);
    }

    public async ValueTask<T> Execute<T>(Func<ICrdtRepository, ValueTask<T>> func)
    {
        await using var repo = await CreateRepository();
        return await func(repo);
    }
}