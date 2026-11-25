using System.Diagnostics;

namespace SIL.Harmony.Core;

public static class HarmonyActivitySource
{
    public const string ActivitySourceName = "SIL.Harmony";
    internal static readonly ActivitySource Source =
        new(ActivitySourceName, typeof(HarmonyActivitySource).Assembly.GetName().Version?.ToString());
}
