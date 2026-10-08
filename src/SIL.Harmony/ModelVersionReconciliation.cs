using SIL.Harmony.Config;

namespace SIL.Harmony;

/// <param name="StoredVersion">the model version stored in the database before the call, 0 when none was stored</param>
/// <param name="CurrentVersion">the model version of the current config, which is now stored</param>
/// <param name="FullRegeneration">true when all snapshots were regenerated, because of a major version or a downgrade</param>
/// <param name="ReplayedFrom">the oldest commit snapshots were rebuilt from, null when nothing was replayed</param>
public record ModelVersionReconcileResult(
    int StoredVersion,
    int CurrentVersion,
    bool FullRegeneration,
    Commit? ReplayedFrom)
{
    public bool Changed => StoredVersion != CurrentVersion;
}

/// <summary>
/// The model versions that were applied to the snapshots of a database, stored in the local state.
/// Names aren't stored, so renaming a version doesn't count as a modification.
/// </summary>
internal record StoredModelVersions(int Version, StoredModelVersion[] Versions)
{
    public const string LocalStateKey = "harmony:modelVersions";
    public static readonly StoredModelVersions None = new(0, []);

    public static StoredModelVersions From(IReadOnlyList<ModelVersion> versions)
    {
        return new StoredModelVersions(versions.Count,
            versions.Select(v => new StoredModelVersion(v.Major, v.Changes.ToArray())).ToArray());
    }
}

internal record StoredModelVersion(bool Major, ModelVersionChange[] Changes)
{
    public bool SameAs(ModelVersion version)
    {
        return Major == version.Major && Changes.SequenceEqual(version.Changes);
    }
}
