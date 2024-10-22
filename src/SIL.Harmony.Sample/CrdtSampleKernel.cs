using System.Data.Common;
using System.Diagnostics;
using SIL.Harmony.Changes;
using SIL.Harmony.Linq2db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Sample;

public static class CrdtSampleKernel
{
    public static IServiceCollection AddCrdtDataSample(this IServiceCollection services, string dbPath)
    {
        return services.AddCrdtDataSample(builder => builder.UseSqlite($"Data Source={dbPath}"));
    }
    public static IServiceCollection AddCrdtDataSample(this IServiceCollection services, DbConnection connection)
    {
        return services.AddCrdtDataSample(builder => builder.UseSqlite(connection, true));
    }

    public static IServiceCollection AddCrdtDataSample(this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsBuilder) 
    {
        services.AddDbContext<SampleDbContext>((provider, builder) =>
        {
            //this ensures that Ef Conversion methods will not be cached across different IoC containers
            //this can show up as the second instance using the JsonSerializerOptions from the first container
            builder.UseRootApplicationServiceProvider();
            builder.UseLinqToDbCrdt(provider);
            optionsBuilder(builder);
            builder.EnableDetailedErrors();
            builder.EnableSensitiveDataLogging();
#if DEBUG
            builder.LogTo(s => Debug.WriteLine(s));
#endif
        });
        services.AddCrdtData<SampleDbContext>(config =>
        {
            config.EnableProjectedTables = true;
            config.ChangeTypeListBuilder
                .Add<NewWordChange>()
                .Add<NewDefinitionChange>()
                .Add<NewExampleChange>()
                .Add<EditExampleChange>()
                .Add<SetWordTextChange>()
                .Add<SetWordNoteChange>()
                .Add<AddAntonymReferenceChange>()
                .Add<SetOrderChange<Definition>>()
                .Add<SetDefinitionPartOfSpeechChange>()
                .Add<DeleteChange<Word>>()
                .Add<DeleteChange<Definition>>()
                .Add<DeleteChange<Example>>()
                ;
            config.ObjectTypeListBuilder.DefaultAdapter()
                .Add<Word>()
                .Add<Definition>(builder =>
                {
                    builder.HasOne<Word>()
                        .WithMany()
                        .HasForeignKey(d => d.WordId)
                        .OnDelete(DeleteBehavior.Cascade);
                })
                .Add<Example>();
        });
        return services;
    }
}