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

public class ProjectionUnsupportedModelTests
{
    /// <summary>
    /// FastProjection sources every column value from the projected CLR instance, so a shadow
    /// property other than the SnapshotId FK has no value to write and would silently project as
    /// null. Such a model must be rejected rather than silently corrupting data.
    /// </summary>
    [Fact]
    public async Task RejectsShadowPropertyOtherThanSnapshotId()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await using var services = new ServiceCollection()
            .AddCrdtDataSample(builder => builder.UseSqlite(connection, true))
            .BuildServiceProvider();
        // realize the container so the shared config/singletons exist
        services.GetRequiredService<SampleDbContext>();

        var config = services.GetRequiredService<IOptions<HarmonyConfig>>();
        var fastProjection = services.GetRequiredService<FastProjection>();

        var options = new DbContextOptionsBuilder<ShadowPropertyDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new ShadowPropertyDbContext(options, config);
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var project = async () => await fastProjection.AddSnapshotsRawAsync(context, [WordSnapshot()]);

        await project.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*ExtraShadow*");
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

    private sealed class ShadowPropertyDbContext(
        DbContextOptions<ShadowPropertyDbContext> options,
        IOptions<HarmonyConfig> crdtConfig) : DbContext(options), ICrdtDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.UseCrdt(crdtConfig.Value);
            modelBuilder.Entity<Word>().Property<string>("ExtraShadow");
        }

        public DbSet<Commit> Commits => Set<Commit>();
        public DbSet<ChangeEntity<IChange>> ChangeEntities => Set<ChangeEntity<IChange>>();
        public DbSet<ObjectSnapshot> Snapshots => Set<ObjectSnapshot>();
    }
}
