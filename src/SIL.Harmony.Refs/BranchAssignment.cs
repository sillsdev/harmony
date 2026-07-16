namespace SIL.Harmony.Refs;

/// <summary>
/// Where a newly authored commit should be assigned.
/// </summary>
public readonly record struct BranchAssignment
{
    private BranchAssignment(BranchAssignmentKind kind, Guid? branchId)
    {
        Kind = kind;
        BranchId = branchId;
    }

    public BranchAssignmentKind Kind { get; }
    public Guid? BranchId { get; }

    /// <summary>Use the current local checkout (default).</summary>
    public static BranchAssignment FromCheckout { get; } = new(BranchAssignmentKind.FromCheckout, null);

    /// <summary>Force main line (no branch metadata).</summary>
    public static BranchAssignment Main { get; } = new(BranchAssignmentKind.Main, null);

    /// <summary>Force a specific branch.</summary>
    public static BranchAssignment ToBranch(Guid branchId) => new(BranchAssignmentKind.Branch, branchId);
}

public enum BranchAssignmentKind
{
    FromCheckout,
    Main,
    Branch
}
