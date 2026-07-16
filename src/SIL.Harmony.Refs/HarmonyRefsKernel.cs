using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Refs.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Refs;

public static class HarmonyRefsKernel
{
    /// <summary>
    /// Registers branch/tag ref entities and change types. Sets a main-line-only materialization filter
    /// as a fallback when <see cref="AddHarmonyRefsDataModel"/> is not used.
    /// </summary>
    public static CrdtConfig AddHarmonyRefs(this CrdtConfig config)
    {
        config.ObjectTypeListBuilder.DefaultAdapter().Add<Branch>();
        config.ObjectTypeListBuilder.DefaultAdapter().Add<Tag>(builder => builder.ToTable("HarmonyTags"));
        config.ChangeTypeListBuilder.Add<CreateBranchChange>();
        config.ChangeTypeListBuilder.Add<MergeBranchChange>();
        config.ChangeTypeListBuilder.Add<CreateTagChange>();
        config.ChangeTypeListBuilder.Add<MoveTagChange>();
        config.CommitMaterializationFilter = MainLineOnlyMaterializationFilter.Instance;
        return config;
    }

    /// <summary>
    /// Registers checkout-aware materialization and <see cref="RefsDataModel"/>.
    /// Prefer this for apps that switch between main and branch views.
    /// </summary>
    public static IServiceCollection AddHarmonyRefsDataModel(this IServiceCollection services)
    {
        services.AddScoped<CheckoutMaterializationFilter>();
        services.AddScoped<ICommitMaterializationFilter>(sp =>
            sp.GetRequiredService<CheckoutMaterializationFilter>());
        services.AddScoped<RefsDataModel>();
        return services;
    }
}
