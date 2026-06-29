using SIL.Harmony.Changes;

namespace SIL.Harmony;

public class HarmonyProgressReporter
{
    private readonly IProgress<HarmonyProgress>? _progress;
    private readonly IProgress<HarmonyDetailedProgress>? _detailedProgress;

    public HarmonyProgressReporter(IProgress<HarmonyProgress> progress)
    {
        _progress = progress;
    }

    public HarmonyProgressReporter(IProgress<HarmonyDetailedProgress> detailedProgress)
    {
        _detailedProgress = detailedProgress;
    }

    public void Report(SyncStage stage, int? current = null, int? total = null, IChange? change = null)
    {
        if (_progress is not null)
        {
            _progress.Report(new HarmonyProgress(stage, current, total));
        }
        else if (_detailedProgress is not null)
        {
            var status = change != null ? $"Applying {change.GetType().Name}" : stage.ToString();
            _detailedProgress.Report(new HarmonyDetailedProgress(stage, current, total, change, status, DateTimeOffset.Now));
        }
    }
}
