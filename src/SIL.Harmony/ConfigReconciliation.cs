using SIL.Harmony.Config;

namespace SIL.Harmony;

/// <summary>
/// What <see cref="DataModel.ReconcileConfigChanges"/> does when the database has no stored config,
/// for example a database created before config tracking existed.
/// </summary>
public enum MissingConfigBehavior
{
    /// <summary>
    /// Replay from the oldest commit with a change type known to the current config, which is usually all of history.
    /// Safe for any database, but expensive for big ones.
    /// </summary>
    ReplayAll,
    /// <summary>
    /// Accept the current config as the config the data was written with: store it and don't replay.
    /// Only use this when every client that wrote to the database used <see cref="UnknownChangeHandling.Throw"/>,
    /// or knew the same change types as the current config. Otherwise snapshots may be missing changes.
    /// </summary>
    AssumeCurrentConfig,
}

/// <param name="ConfigChanged">false when the stored config matched the current one and nothing was done</param>
/// <param name="AddedChangeTypes">change types the current config knows that the stored config did not</param>
/// <param name="RemovedChangeTypes">change types the stored config knew that the current config does not</param>
/// <param name="ReplayedFrom">the oldest commit snapshots were rebuilt from, null when nothing was replayed</param>
public record ConfigReconcileResult(
    bool ConfigChanged,
    IReadOnlyList<string> AddedChangeTypes,
    IReadOnlyList<string> RemovedChangeTypes,
    Commit? ReplayedFrom)
{
    internal static readonly ConfigReconcileResult Unchanged = new(false, [], [], null);
}

/// <summary>
/// The parts of the <see cref="HarmonyConfig"/> that affect which snapshots are generated, stored in the local state.
/// </summary>
internal record StoredHarmonyConfig(int Version, string[] ChangeTypes)
{
    public const string LocalStateKey = "harmony:config";
    public const int CurrentVersion = 1;

    public static StoredHarmonyConfig From(HarmonyConfig config)
    {
        return new StoredHarmonyConfig(CurrentVersion,
            config.ChangeTypes.Select(t => t.Discriminator).Order(StringComparer.Ordinal).ToArray());
    }

    public bool SameAs(StoredHarmonyConfig other)
    {
        return Version == other.Version && ChangeTypes.SequenceEqual(other.ChangeTypes, StringComparer.Ordinal);
    }
}
