namespace SIL.Harmony.Core;

public record struct HarmonyProgress(SyncStage Stage, int? Current, int? Total);
