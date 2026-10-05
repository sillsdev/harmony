namespace SIL.Harmony.Config;

/// <summary>
/// One step in the ordered list of model versions an app declares on <see cref="HarmonyConfig.ModelVersionBuilder"/>.
/// Released versions must never be modified, only new versions added at the end.
/// </summary>
/// <param name="Name">describes the version for people, it is not stored in the database</param>
/// <param name="Major">when true, upgrading to this version regenerates all snapshots</param>
/// <param name="ChangeTypes">discriminators of the change types this version added or modified, sorted</param>
public sealed record ModelVersion(string Name, bool Major, IReadOnlyList<string> ChangeTypes)
{
    public bool Equals(ModelVersion? other)
    {
        return other is not null && Name == other.Name && Major == other.Major
               && ChangeTypes.SequenceEqual(other.ChangeTypes, StringComparer.Ordinal);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Name, Major, ChangeTypes.Count);
    }
}
