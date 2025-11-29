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

    public static IServiceCollection AddCrdtDataSample(this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsBuilder, bool performanceTest = false, bool useLinq2DbRepo = false)
    {
        services.AddDbContext<SampleDbContext>((provider, builder) =>
        {
            //this ensures that Ef Conversion methods will not be cached across different IoC containers
            //this can show up as the second instance using the JsonSerializerOptions from the first container
            //only needed for testing scenarios
            builder.EnableServiceProviderCaching(performanceTest);
            builder.UseLinqToDbCrdt(provider, !performanceTest);
            optionsBuilder(builder);
            builder.EnableDetailedErrors();
            builder.EnableSensitiveDataLogging();
        });
        services.AddCrdtData<SampleDbContext>(config =>
        {
            config.EnableProjectedTables = true;
            config.AddRemoteResourceEntity();
            config.ChangeTypeListBuilder
                .Add<NewWordChange>()
                .Add<NewDefinitionChange>()
                .Add<NewExampleChange>()
                .Add<EditExampleChange>()
                .Add<SetWordTextChange>()
                .Add<SetWordNoteChange>()
                .Add<SetAntonymReferenceChange>()
                .Add<AddWordImageChange>()
                .Add<SetOrderChange<Definition>>()
                .Add<SetDefinitionPartOfSpeechChange>()
                .Add<SetTagChange>()
                .Add<TagWordChange>()
                .Add<DeleteChange<Word>>()
                .Add<DeleteChange<Definition>>()
                .Add<DeleteChange<Example>>()
                .Add<DeleteChange<Tag>>()
                ;
            config.ObjectTypeListBuilder.DefaultAdapter()
                .Add<Word>(builder =>
                {
                    builder.HasMany(w => w.Tags)
                        .WithMany()
                        .UsingEntity<WordTag>();
                    builder.HasOne((w) => w.Antonym)
                        .WithMany()
                        .HasForeignKey(w => w.AntonymId)
                        .OnDelete(DeleteBehavior.SetNull);
                })
                .Add<Definition>(builder =>
                {
                    builder.HasOne<Word>()
                        .WithMany()
                        .HasForeignKey(d => d.WordId)
                        .OnDelete(DeleteBehavior.Cascade);
                })
                .Add<Example>()
                .Add<Tag>(builder =>
                {
                    builder.HasIndex(tag => tag.Text).IsUnique();
                })
                .Add<WordTag>(builder =>
                {
                    builder.HasKey(wt => wt.Id);
                    builder.HasIndex(wt => new { wt.WordId, wt.TagId }).IsUnique();
                });
        });
        if (useLinq2DbRepo)
            services.AddLinq2DbRepository();
        return services;
    }
}
