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
    /// Marks that a commit's branch assignment was set deliberately (including to main).
    /// Distinguishes an explicit assignment from an unassigned commit — both of which
    /// have no <see cref="BranchIdKey"/> when the assignment is main — so the authoring
    /// interceptor knows to leave the commit alone rather than derive from the checkout.
    /// </summary>
    public const string BranchAssignedKey = "harmony.branchAssigned";

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
    /// True when the commit carries a deliberate branch assignment (see <see cref="BranchAssignedKey"/>).
    /// </summary>
    public static bool IsAssigned(CommitMetadata metadata) => metadata[BranchAssignedKey] is not null;

    /// <summary>
    /// Records an explicit per-call branch assignment on <paramref name="metadata"/> so it takes
    /// precedence over the current checkout when authoring through <see cref="DataModel.AddChange"/>.
    /// <see cref="BranchAssignment.Main"/> and <see cref="BranchAssignment.ToBranch"/> mark the commit;
    /// <see cref="BranchAssignment.FromCheckout"/> leaves it unmarked so the checkout is used.
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
