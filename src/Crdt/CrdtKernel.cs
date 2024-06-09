using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Crdt.Core;
using Crdt.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crdt;

public static class CrdtKernel
{
    public static IServiceCollection AddCrdtData(this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureOptions,
        Action<CrdtConfig> configureCrdt)
    {
        return AddCrdtData(services, (_, builder) => configureOptions(builder), configureCrdt);
    }

    public static IServiceCollection AddCrdtData(this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> configureOptions,
        Action<CrdtConfig> configureCrdt)
    {
        services.AddOptions<CrdtConfig>().Configure(configureCrdt);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CrdtConfig>>().Value.JsonSerializerOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IHybridDateTimeProvider>(NewTimeProvider);
        services.AddDbContext<CrdtDbContext>((provider, builder) =>
            {
                configureOptions(provider, builder);
                builder
                    .AddInterceptors(provider.GetServices<IInterceptor>().ToArray())
                    .EnableDetailedErrors()
                    .EnableSensitiveDataLogging();
            },
            ServiceLifetime.Scoped);
        services.AddScoped<CrdtRepository>();
        //must use factory method because DataModel constructor is internal
        services.AddScoped<DataModel>(provider => new DataModel(
            provider.GetRequiredService<CrdtRepository>(),
            provider.GetRequiredService<JsonSerializerOptions>(),
            provider.GetRequiredService<IHybridDateTimeProvider>()
        ));
        return services;
    }

    public static HybridDateTimeProvider NewTimeProvider(IServiceProvider serviceProvider)
    {
        //todo, if this causes issues getting the order correct, we can update the last date time after the db is created
        //as long as it's before we get a date time from the provider
        //todo use IMemoryCache to store the last date time, possibly based on the current project
        var hybridDateTime = serviceProvider.GetRequiredService<CrdtRepository>().GetLatestDateTime();
        hybridDateTime ??= HybridDateTimeProvider.DefaultLastDateTime;
        return ActivatorUtilities.CreateInstance<HybridDateTimeProvider>(serviceProvider, hybridDateTime);
    }
}
