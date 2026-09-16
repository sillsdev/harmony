using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony;

public static class CrdtKernel
{
    public static IServiceCollection AddCrdtDataDbFactory<TContext>(this IServiceCollection services,
        Action<HarmonyConfig> configureCrdt) where TContext : DbContext, ICrdtDbContext
    {
        services.AddCrdtDataCore(configureCrdt);
        services.AddScoped<ICrdtDbContextFactory, CrdtDbContextFactory<TContext>>();
        return services;
    }

    public static IServiceCollection AddCrdtData<TContext>(this IServiceCollection services,
        Action<HarmonyConfig> configureCrdt) where TContext : DbContext, ICrdtDbContext
    {
        services.AddCrdtDataCore(configureCrdt);
        services.AddScoped<ICrdtDbContextFactory, CrdtDbContextNoDisposeFactory<TContext>>();
        return services;
    }

    public static IServiceCollection AddCrdtRemoteResources<TMetadata>(this IServiceCollection services,
        Action<HarmonyConfig>? configureCrdt = null, string? cachePath = null)
        where TMetadata : class
    {
        services.Configure<HarmonyConfig>(config =>
        {
            config.AddRemoteResourceEntity<TMetadata>(cachePath);
            configureCrdt?.Invoke(config);
        });
        services.AddScoped<ResourceService<TMetadata>>(provider => new ResourceService<TMetadata>(
            provider.GetRequiredService<CrdtRepositoryFactory>(),
            provider.GetRequiredService<IOptions<HarmonyConfig>>(),
            provider.GetRequiredService<DataModel>(),
            provider.GetRequiredService<ILogger<ResourceService<TMetadata>>>()
        ));
        return services;
    }

    public static IServiceCollection AddCrdtDataCore(this IServiceCollection services, Action<HarmonyConfig> configureCrdt)
    {
        services.AddLogging();
        services.AddOptions<HarmonyConfig>().Configure(configureCrdt)
            .PostConfigure(crdtConfig => crdtConfig.ObjectTypeListBuilder.Freeze());
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<HarmonyConfig>>().Value.JsonSerializerOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IHybridDateTimeProvider>(NewTimeProvider);
        services.AddSingleton<FastProjection>();
        services.AddScoped<CrdtRepositoryFactory>();
        //must use factory method because DataModel constructor is internal
        services.AddScoped<DataModel>(provider => new DataModel(
            provider.GetRequiredService<CrdtRepositoryFactory>(),
            provider.GetRequiredService<JsonSerializerOptions>(),
            provider.GetRequiredService<IHybridDateTimeProvider>(),
            provider.GetRequiredService<IOptions<HarmonyConfig>>(),
            provider.GetRequiredService<ILogger<DataModel>>()
        ));
        return services;
    }

    public static HybridDateTimeProvider NewTimeProvider(IServiceProvider serviceProvider)
    {
        //todo, if this causes issues getting the order correct, we can update the last date time after the db is created
        //as long as it's before we get a date time from the provider
        //todo use IMemoryCache to store the last date time, possibly based on the current project
        using var repo = serviceProvider.GetRequiredService<CrdtRepositoryFactory>().CreateRepositorySync();
        var hybridDateTime = repo.GetLatestDateTime();
        hybridDateTime ??= HybridDateTimeProvider.DefaultLastDateTime;
        return ActivatorUtilities.CreateInstance<HybridDateTimeProvider>(serviceProvider, hybridDateTime);
    }
}
