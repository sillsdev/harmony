using SIL.Harmony.Core;
using SIL.Harmony.Db;
using LinqToDB;
using LinqToDB.AspNet.Logging;
using LinqToDB.EntityFrameworkCore;
using LinqToDB.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SIL.Harmony.Linq2db;

public static class Linq2dbKernel
{
    public static DbContextOptionsBuilder UseLinqToDbCrdt(this DbContextOptionsBuilder builder, IServiceProvider provider)
    {
        LinqToDBForEFTools.Initialize();
        return builder.UseLinqToDB(optionsBuilder =>
        {
            var mappingSchema = optionsBuilder.DbContextOptions.GetLinqToDBOptions()?.ConnectionOptions
                .MappingSchema;
            if (mappingSchema is null)
            {
                mappingSchema = new MappingSchema();
                optionsBuilder.AddMappingSchema(mappingSchema);
            }

            new FluentMappingBuilder(mappingSchema).HasAttribute<Commit>(new ColumnAttribute("DateTime",
                    nameof(Commit.HybridDateTime) + "." + nameof(HybridDateTime.DateTime)))
                .HasAttribute<Commit>(new ColumnAttribute(nameof(HybridDateTime.Counter),
                    nameof(Commit.HybridDateTime) + "." + nameof(HybridDateTime.Counter)))
                .Entity<Commit>()
                //need to use ticks here because the DateTime is stored as UTC, but the db records it as unspecified
                .Property(commit => commit.HybridDateTime.DateTime).HasConversionFunc(dt => dt.UtcDateTime,
                    dt => new DateTimeOffset(dt.Ticks, TimeSpan.Zero))
                .Build();

            var loggerFactory = provider.GetService<ILoggerFactory>();
            if (loggerFactory is not null)
                optionsBuilder.AddCustomOptions(dataOptions => dataOptions.UseLoggerFactory(loggerFactory));
        });
    }
}
