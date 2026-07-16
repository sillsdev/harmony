namespace SIL.Harmony.Refs;

/// <summary>
/// Well-known <see cref="CommitMetadata"/> keys for the refs layer.
/// </summary>
public static class RefMetadata
{
    /// <summary>
    /// Immutable branch assignment for a commit. Absent or empty means main line.
    /// </summary>
    public const string BranchIdKey = "harmony.branchId";

    public static Guid? GetBranchId(CommitMetadata metadata)
    {
        var value = metadata[BranchIdKey];
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public static void SetBranchId(CommitMetadata metadata, Guid? branchId)
    {
        if (branchId is null)
        {
            metadata.ExtraMetadata.Remove(BranchIdKey);
            return;
        }

        metadata[BranchIdKey] = branchId.Value.ToString();
    }
}
