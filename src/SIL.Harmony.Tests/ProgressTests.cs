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
        Assert.Contains(progressReports, p => p.Stage == SyncStage.ApplyingChangesFinished);
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
        Assert.Contains(progressReports, p => p.Stage == SyncStage.ApplyingChangesFinished && p.Status == "Finished applying changes.");
        Assert.All(progressReports, p => Assert.NotEmpty(p.Status));
    }

    [Fact]
    public async Task SyncWith_ReportsProgress()
    {
        var model1 = DataModel;
        var testBase2 = new DataModelTestBase();
        var model2 = testBase2.DataModel;

        var clientId = Guid.NewGuid();
        // remote changes to be downloaded by model1
        await model2.AddChanges(clientId, [new SetTagChange(Guid.NewGuid(), "Tag 1"), new SetTagChange(Guid.NewGuid(), "Tag 2")]);
        // local changes to be uploaded to model2
        await model1.AddChanges(clientId, [new SetTagChange(Guid.NewGuid(), "Tag 3")]);

        var progressReports = new ConcurrentQueue<HarmonyDetailedProgress>();
        var progress = new Progress<HarmonyDetailedProgress>(p =>
        {
            _output.WriteLine($"Sync Progress: {p.Current}/{p.Total} - {p.Status} ({p.Stage})");
            progressReports.Enqueue(p);
        });
        var reporter = new HarmonyProgressReporter(progress);

        await model1.SyncWith(model2, reporter);

        await Task.Delay(500);

        _output.WriteLine($"Reports count: {progressReports.Count}");
        foreach(var report in progressReports)
        {
            _output.WriteLine($"- {report.Stage}: {report.Current}/{report.Total} {report.Status}");
        }

        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.Stage == SyncStage.FetchingChanges);
        Assert.Contains(progressReports, p => p.Stage == SyncStage.FetchingChangesFinished);
        // We expect UploadingChanges happens when sending local changes to remote
        Assert.Contains(progressReports, p => p.Stage == SyncStage.UploadingChanges && p.Total == 1 && p.Status.Contains("Uploading 1 changes"));
        Assert.Contains(progressReports, p => p.Stage == SyncStage.UploadingChangesFinished);
    }
}
