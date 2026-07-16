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

    /// <summary>
    /// Transient authoring-time marker: distinguishes an explicit assignment (including to main,
    /// which has no <see cref="BranchIdKey"/>) from an unassigned commit, so the authoring
    /// interceptor knows to leave the commit alone rather than derive from the checkout. It is
    /// consumed (removed) by the interceptor via <see cref="ConsumeAssignment"/> before the commit
    /// is persisted, so it never lands in stored or synced metadata.
    /// </summary>
    private const string BranchAssignedKey = "harmony.branchAssigned";

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

    /// <summary>
    /// Reads and clears the transient assignment marker, returning whether the commit carried a
    /// deliberate assignment. Called once by the authoring interceptor so the marker is consumed
    /// before persistence and never synced.
    /// </summary>
    internal static bool ConsumeAssignment(CommitMetadata metadata) =>
        metadata.ExtraMetadata.Remove(BranchAssignedKey);

    /// <summary>
    /// Records an explicit per-call branch assignment on <paramref name="metadata"/> so it takes
    /// precedence over the current checkout when authoring through <see cref="DataModel.AddChange"/>.
    /// <see cref="BranchAssignment.Main"/> and <see cref="BranchAssignment.ToBranch"/> mark the commit;
    /// <see cref="BranchAssignment.FromCheckout"/> leaves it unmarked so the checkout is used.
    /// The marker is transient — the authoring interceptor consumes it, so it never persists.
    /// Returns the same metadata for fluent use.
    /// </summary>
    public static CommitMetadata SetAssignment(CommitMetadata metadata, BranchAssignment assignment)
    {
        switch (assignment.Kind)
        {
            case BranchAssignmentKind.Main:
                SetBranchId(metadata, null);
                metadata[BranchAssignedKey] = bool.TrueString;
                break;
            case BranchAssignmentKind.Branch:
                SetBranchId(metadata, assignment.BranchId);
                metadata[BranchAssignedKey] = bool.TrueString;
                break;
            case BranchAssignmentKind.FromCheckout:
                // Leave unmarked: the interceptor derives the branch id from the checkout.
                break;
        }

        return metadata;
    }
}
