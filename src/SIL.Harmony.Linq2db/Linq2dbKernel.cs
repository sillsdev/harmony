using System.Globalization;
using SIL.Harmony.Core;
using LinqToDB.EntityFrameworkCore;
using LinqToDB.Extensions.Logging;
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

            //bind timestamp parameters as the exact TEXT Microsoft.Data.Sqlite writes for DateTime columns
            //("yyyy-MM-dd HH:mm:ss.FFFFFFF"): linq2db's own rendering truncates to milliseconds while
            //SQLite's strftime rounds, so comparisons against stored values can misclassify rows
            //(e.g. WhereAfter matching the target commit itself). Even with identical text, linq2db wraps
            //SQLite timestamp comparisons in strftime('...%f'), which is millisecond-grained — use EF for
            //comparisons that must distinguish same-millisecond commits.
            mappingSchema.SetConvertExpression((DateTime dt) => new LinqToDB.Data.DataParameter
            {
                Value = dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
                DataType = LinqToDB.DataType.Text
            });
            mappingSchema.SetConvertExpression((DateTimeOffset dto) => new LinqToDB.Data.DataParameter
            {
                Value = dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
                DataType = LinqToDB.DataType.Text
            });

            var loggerFactory = provider.GetService<ILoggerFactory>();
            if (loggerFactory is not null)
                optionsBuilder.AddCustomOptions(dataOptions => dataOptions.UseLoggerFactory(loggerFactory));
        });
    }
}
