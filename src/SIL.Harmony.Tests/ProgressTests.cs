using System.Collections.Concurrent;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SIL.Harmony.Tests;

public class ProgressTests : DataModelTestBase
{
    [Fact]
    public async Task AddManyChanges_ReportsProgress()
    {
        var clientId = Guid.NewGuid();
        var changes = Enumerable.Range(0, 10).Select(i => new SetTagChange(Guid.NewGuid(), $"Tag {i}")).ToArray();
        var progressReports = new ConcurrentQueue<HarmonyProgress>();
        var progress = new Progress<HarmonyProgress>(p => progressReports.Enqueue(p));

        await DataModel.AddManyChanges(clientId, changes, () => new CommitMetadata(), 2, progress);

        // Wait a bit for Progress to report (it's often asynchronous)
        await Task.Delay(100);

        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.Current == 10 && p.Total == 10);
        Assert.All(progressReports, p => Assert.NotEmpty(p.Status));
        Assert.All(progressReports, p => Assert.True(p.Current <= p.Total));
    }

    [Fact]
    public async Task SyncWith_ReportsProgress()
    {
        var model1 = DataModel;
        var testBase2 = new DataModelTestBase();
        var model2 = testBase2.DataModel;

        var clientId = Guid.NewGuid();
        await model2.AddChanges(clientId, [new SetTagChange(Guid.NewGuid(), "Tag 1"), new SetTagChange(Guid.NewGuid(), "Tag 2")]);

        var progressReports = new ConcurrentQueue<HarmonyProgress>();
        var progress = new Progress<HarmonyProgress>(p => progressReports.Enqueue(p));

        await model1.SyncWith(model2, progress);

        await Task.Delay(100);

        Assert.NotEmpty(progressReports);
        // model2 has 2 changes, model1 should report progress for those 2 changes
        Assert.Contains(progressReports, p => p.Current == 2 && p.Total == 2);
    }
}
