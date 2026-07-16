namespace SIL.Harmony.Refs;

/// <summary>
/// Local checkout selection. Not synced; only affects authoring defaults and (later) materialization views.
/// </summary>
public abstract record RefCheckout
{
    public static RefCheckout Main { get; } = new MainCheckout();

    public static RefCheckout ForBranch(Guid branchId) => new BranchCheckout(branchId);
}

public sealed record MainCheckout : RefCheckout;

public sealed record BranchCheckout(Guid BranchId) : RefCheckout;
