namespace SIL.Harmony.Config;

/// <summary>
/// One step in the ordered list of model versions an app declares on <see cref="HarmonyConfig.ModelVersionBuilder"/>.
/// Released versions must never be modified, only new versions added at the end.
/// </summary>
/// <param name="Number">the position of the version in the list, starting at 0</param>
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

/// <summary>
/// A change type that a model version added or modified. Changes of the type are authored with that version's number
/// from then on, see <see cref="ChangeEntity{TChange}.ChangeVersion"/>.
/// </summary>
/// <param name="ChangeType">the discriminator of the change type, its <c>$type</c></param>
/// <param name="InvalidateFrom">
/// on upgrade, changes of this type authored with this model version or later are invalidated, and the snapshots
/// are rebuilt from the checkpoint before the oldest of them. Usually the version's own number, lower when changes
/// authored by earlier versions must be invalidated too.
/// </param>
public sealed record ModelVersionChange(string ChangeType, int InvalidateFrom);
