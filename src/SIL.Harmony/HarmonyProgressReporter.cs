using SIL.Harmony.Changes;

namespace SIL.Harmony;

public class HarmonyProgressReporter
{
    private readonly IProgress<HarmonyProgress>? _progress;
    private readonly IProgress<HarmonyDetailedProgress>? _detailedProgress;
    private int? _totalChanges;

    public HarmonyProgressReporter(IProgress<HarmonyProgress> progress)
    {
        _progress = progress;
    }

    public HarmonyProgressReporter(IProgress<HarmonyDetailedProgress> detailedProgress)
    {
        _detailedProgress = detailedProgress;
    }

    public void ReportFetchingChanges() => Report(SyncStage.FetchingChanges);
    public void ReportFetchingChangesFinished() => Report(SyncStage.FetchingChangesFinished);
    public void ReportUploadingResources() => Report(SyncStage.UploadingResources);
    public void ReportUploadingResourcesFinished() => Report(SyncStage.UploadingResourcesFinished);
    public void ReportUploadingChanges(int? count = null) => Report(SyncStage.UploadingChanges, total: count);
    public void ReportUploadingChangesFinished() => Report(SyncStage.UploadingChangesFinished);

    public void ReportStartApplyingChanges(IEnumerable<Commit> commits)
    {
        if (_progress is null && _detailedProgress is null) return;
        _totalChanges = commits.Sum(c => c.ChangeEntities.Count);
        Report(SyncStage.ApplyingChanges, 0, _totalChanges);
    }

    public void ReportApplyingChange(int current, IChange change)
    {
        Report(SyncStage.ApplyingChanges, current, _totalChanges, change);
    }

    public void ReportApplyingChangesFinished() => Report(SyncStage.ApplyingChangesFinished, _totalChanges, _totalChanges);

    private void Report(SyncStage stage, int? current = null, int? total = null, IChange? change = null)
    {
        if (_progress is not null)
        {
            _progress.Report(new HarmonyProgress(stage, current, total));
        }
        else if (_detailedProgress is not null)
        {
            var status = GetStatus(stage, change, total);
            _detailedProgress.Report(new HarmonyDetailedProgress(stage, current, total, change, status, DateTimeOffset.Now));
        }
    }

    private static string GetStatus(SyncStage stage, IChange? change, int? total)
    {
        if (change != null) return $"Applying {change.GetType().Name}";
        return stage switch
        {
            SyncStage.FetchingChanges => "Fetching changes...",
            SyncStage.FetchingChangesFinished => "Finished fetching changes.",
            SyncStage.ApplyingChanges => "Applying changes...",
            SyncStage.ApplyingChangesFinished => "Finished applying changes.",
            SyncStage.UploadingResources => "Uploading resources...",
            SyncStage.UploadingResourcesFinished => "Finished uploading resources.",
            SyncStage.UploadingChanges => total.HasValue ? $"Uploading {total} changes to remote..." : "Uploading changes to remote...",
            SyncStage.UploadingChangesFinished => "Finished uploading changes.",
            _ => stage.ToString()
        };
    }
}
