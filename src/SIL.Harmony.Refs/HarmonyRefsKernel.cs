using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Refs.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Refs;

public static class HarmonyRefsKernel
{
    /// <summary>
    /// Registers branch ref entities, change types, and main-line-only materialization on an existing Harmony <see cref="CrdtConfig"/>.
    /// </summary>
    public static CrdtConfig AddHarmonyRefs(this CrdtConfig config)
    {
        config.ObjectTypeListBuilder.DefaultAdapter().Add<Branch>();
        config.ChangeTypeListBuilder.Add<CreateBranchChange>();
        config.CommitMaterializationFilter = MainLineOnlyMaterializationFilter.Instance;
        return config;
    }

    /// <summary>
    /// Registers <see cref="RefsDataModel"/> for DI. Call alongside <see cref="AddHarmonyRefs"/>.
    /// </summary>
    public static IServiceCollection AddHarmonyRefsDataModel(this IServiceCollection services)
    {
        services.AddScoped<RefsDataModel>();
        return services;
    }
}
