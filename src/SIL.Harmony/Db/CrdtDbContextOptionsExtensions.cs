using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Db.EntityConfig;

namespace SIL.Harmony.Db;

public static class CrdtDbContextModelExtensions
{
    public static ModelBuilder UseCrdt(this ModelBuilder modelBuilder,
        CrdtConfig crdtConfig)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CommitEntityConfig).Assembly)
            .ApplyConfiguration(new SnapshotEntityConfig(crdtConfig.JsonSerializerOptions))
            .ApplyConfiguration(new ChangeEntityConfig(crdtConfig.JsonSerializerOptions));
        foreach (var modelConfiguration in crdtConfig.ObjectTypeListBuilder.ModelConfigurations)
        {
            modelConfiguration(modelBuilder, crdtConfig);
        }
        return modelBuilder;
    }
}