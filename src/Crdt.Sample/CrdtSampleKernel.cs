using System.Diagnostics;
using Crdt.Changes;
using Crdt.Linq2db;
using Crdt.Sample.Changes;
using Crdt.Sample.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crdt.Sample;

public static class CrdtSampleKernel
{
    public static IServiceCollection AddCrdtDataSample(this IServiceCollection services,
        string dbPath,
        bool enableProjectedTables = true)
    {
        services.AddCrdtLinq2db(
            (_, builder) =>
            {
                builder.UseSqlite($"Data Source={dbPath}");
                builder.EnableDetailedErrors();
                builder.EnableSensitiveDataLogging();
                #if DEBUG
                builder.LogTo(s => Debug.WriteLine(s));
                #endif
            },
            config =>
            {
                config.EnableProjectedTables = enableProjectedTables;
                config.ChangeTypeListBuilder
                    .Add<NewWordChange>()
                    .Add<NewDefinitionChange>()
                    .Add<NewExampleChange>()
                    .Add<EditExampleChange>()
                    .Add<SetWordTextChange>()
                    .Add<SetWordNoteChange>()
                    .Add<AddAntonymReferenceChange>()
                    .Add<SetOrderChange<Definition>>()
                    .Add<DeleteChange<Word>>()
                    .Add<DeleteChange<Definition>>()
                    .Add<DeleteChange<Example>>()
                    ;
                config.ObjectTypeListBuilder
                    .Add<Word>()
                    .Add<Definition>()
                    .Add<Example>();
            });
        return services;
    }
}