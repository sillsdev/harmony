using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class ProjectedTableInfoCacheTests
{
    /// <summary>
    /// A single <see cref="HarmonyConfig"/> can be paired with more than one EF model. Here a second
    /// <see cref="ICrdtDbContext"/> type maps <see cref="Word"/> to a different table than
    /// <see cref="SampleDbContext"/>. If the projected-table metadata cache is keyed by CLR type
    /// alone, the second context reuses the first model's SQL and writes to the wrong table.
    /// </summary>
    [Fact]
    public async Task ProjectionMetadataIsScopedPerModel()
    {
        var connectionA = new SqliteConnection("Data Source=:memory:");
        await using var services = new ServiceCollection()
            .AddCrdtDataSample(builder => builder.UseSqlite(connectionA, true))
            .BuildServiceProvider();

        var contextA = services.GetRequiredService<SampleDbContext>();
        await contextA.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await contextA.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var config = services.GetRequiredService<IOptions<HarmonyConfig>>();
        var fastProjection = services.GetRequiredService<FastProjection>();

        // populate the cache from model A first (Word -> "Word")
        await fastProjection.AddSnapshotsRawAsync(contextA, [WordSnapshot()]);

        // a second context type over the SAME config but a separate database whose Word table is renamed
        var connectionB = new SqliteConnection("Data Source=:memory:");
        var optionsB = new DbContextOptionsBuilder<RenamedWordDbContext>()
            .UseSqlite(connectionB)
            .Options;
        await using var contextB = new RenamedWordDbContext(optionsB, config);
        await contextB.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await contextB.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        // model B's Word lives in "Word_Renamed"; reusing model A's cached "Word" SQL would fail here
        await fastProjection.AddSnapshotsRawAsync(contextB, [WordSnapshot()]);

        var words = await contextB.Set<Word>().AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        words.Should().ContainSingle();
    }

    private static ObjectSnapshot WordSnapshot()
    {
        var word = new Word { Text = "test", Id = Guid.NewGuid() };
        var commit = new Commit(Guid.NewGuid())
        {
            ClientId = Guid.Empty,
            HybridDateTime = new HybridDateTime(new DateTime(2000, 1, 1), 0),
            ChangeEntities =
            [
                new ChangeEntity<IChange>
                {
                    Change = new SetWordTextChange(word.Id, "test"),
                    CommitId = default,
                    EntityId = word.Id,
                    Index = 0
                }
            ]
        };
        return new ObjectSnapshot(word, commit, false);
    }

    private sealed class RenamedWordDbContext(
        DbContextOptions<RenamedWordDbContext> options,
        IOptions<HarmonyConfig> crdtConfig) : DbContext(options), ICrdtDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.UseCrdt(crdtConfig.Value);
            modelBuilder.Entity<Word>().ToTable("Word_Renamed");
        }

        public DbSet<Commit> Commits => Set<Commit>();
        public DbSet<ChangeEntity<IChange>> ChangeEntities => Set<ChangeEntity<IChange>>();
        public DbSet<ObjectSnapshot> Snapshots => Set<ObjectSnapshot>();
    }
}
