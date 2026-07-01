using SIL.Harmony.Changes;

namespace SIL.Harmony;

public record struct HarmonyDetailedProgress(SyncStage Stage, int? Current, int? Total, IChange? Change, string Status, DateTimeOffset DateTime);
