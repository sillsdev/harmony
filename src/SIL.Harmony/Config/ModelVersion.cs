namespace SIL.Harmony.Config;

/// <summary>
/// One step in the ordered list of model versions an app declares on <see cref="HarmonyConfig.ModelVersionBuilder"/>.
/// Released versions must never be modified, only new versions added at the end.
/// </summary>
/// <param name="Number">the position of the version in the list, starting at 1</param>
/// <param name="Name">describes the version for people, it is not stored in the database</param>
/// <param name="Major">when true, upgrading to this version regenerates all snapshots</param>
/// <param name="Changes">the change types this version added or modified, sorted by type</param>
public sealed record ModelVersion(int Number, string Name, bool Major, IReadOnlyList<ModelVersionChange> Changes)
{
    public bool Equals(ModelVersion? other)
    {
        return other is not null && Number == other.Number && Name == other.Name && Major == other.Major
               && Changes.SequenceEqual(other.Changes);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Number, Name, Major, Changes.Count);
    }
}

/// <summary>a change type that a model version added or modified</summary>
/// <param name="ChangeType">the discriminator of the change type, its <c>$type</c></param>
/// <param name="Version">the version changes of this type are authored with, from this model version on</param>
/// <param name="InvalidateFrom">
/// on upgrade, changes of this type with this version or higher are replayed.
/// Usually <paramref name="Version"/>, lower when an earlier model version forgot to list the type.
/// </param>
public sealed record ModelVersionChange(string ChangeType, int Version, int InvalidateFrom);
