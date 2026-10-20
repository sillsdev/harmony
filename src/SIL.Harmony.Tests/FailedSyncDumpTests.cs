using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Config;

namespace SIL.Harmony.Tests;

public sealed class FailedSyncDumpTests : DataModelTestBase, IDisposable
{
    private static readonly string OutputPath =
        Path.Combine(Path.GetTempPath(), $"harmony-failed-sync-{Guid.NewGuid():N}");

    public FailedSyncDumpTests() : base(configure: services =>
        services.Configure<HarmonyConfig>(config => config.FailedSyncOutputPath = OutputPath))
    {
    }

    public void Dispose()
    {
        if (Directory.Exists(OutputPath)) Directory.Delete(OutputPath, true);
    }

    [Fact]
    public async Task AddRangeFromSync_OnDbUpdateException_DumpsFailedImportAndRethrows()
    {
        await DbContext.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_commits BEFORE INSERT ON Commits BEGIN SELECT RAISE(ABORT, 'rejected'); END",
            TestContext.Current.CancellationToken);
        var commit = NextCommit(SetWord(Guid.NewGuid(), "word"));

        var act = () => ((ISyncable)DataModel).AddRangeFromSync([commit]);

        await act.Should().ThrowAsync<DbUpdateException>();
        var dump = await File.ReadAllTextAsync(Path.Combine(OutputPath, "last-failed-import.json"),
            TestContext.Current.CancellationToken);
        dump.Should().Contain("rejected").And.Contain(commit.Id.ToString());
    }
}
