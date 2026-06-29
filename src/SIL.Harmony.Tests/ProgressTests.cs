using System.Collections.Concurrent;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SIL.Harmony.Tests;

public class ProgressTests : DataModelTestBase
{
    private readonly ITestOutputHelper _output;

    public ProgressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AddManyChanges_ReportsProgress()
    {
        var clientId = Guid.NewGuid();
        var changes = Enumerable.Range(0, 10).Select(i => new SetTagChange(Guid.NewGuid(), $"Tag {i}")).ToArray();
        var progressReports = new ConcurrentQueue<HarmonyProgress>();
        var progress = new Progress<HarmonyProgress>(p =>
        {
            _output.WriteLine($"Progress: {p.Current}/{p.Total} - {p.Stage}");
            progressReports.Enqueue(p);
        });
        var reporter = new HarmonyProgressReporter(progress);

        await DataModel.AddManyChanges(clientId, changes, () => new CommitMetadata(), 2, reporter);

        // Wait a bit for Progress to report (it's often asynchronous)
        await Task.Delay(100);

        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.Current == 10 && p.Total == 10 && p.Stage == SyncStage.ApplyingChanges);
    }

    [Fact]
    public async Task AddManyChanges_ReportsDetailedProgress()
    {
        var clientId = Guid.NewGuid();
        var changes = Enumerable.Range(0, 10).Select(i => new SetTagChange(Guid.NewGuid(), $"Tag {i}")).ToArray();
        var progressReports = new ConcurrentQueue<HarmonyDetailedProgress>();
        var progress = new Progress<HarmonyDetailedProgress>(p =>
        {
            _output.WriteLine($"Detailed Progress: {p.Current}/{p.Total} - {p.Status} @ {p.DateTime}");
            progressReports.Enqueue(p);
        });
        var reporter = new HarmonyProgressReporter(progress);

        await DataModel.AddManyChanges(clientId, changes, () => new CommitMetadata(), 2, reporter);

        await Task.Delay(100);

        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.Current == 10 && p.Total == 10 && p.Change is SetTagChange);
        Assert.All(progressReports, p => Assert.NotEmpty(p.Status));
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
        var progress = new Progress<HarmonyProgress>(p =>
        {
            _output.WriteLine($"Sync Progress: {p.Current}/{p.Total} - {p.Stage}");
            progressReports.Enqueue(p);
        });
        var reporter = new HarmonyProgressReporter(progress);

        await model1.SyncWith(model2, reporter);

        await Task.Delay(100);

        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.Stage == SyncStage.FetchingChanges);
        Assert.Contains(progressReports, p => p.Current == 2 && p.Total == 2 && p.Stage == SyncStage.ApplyingChanges);
    }
}
