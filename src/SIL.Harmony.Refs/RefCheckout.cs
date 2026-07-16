namespace SIL.Harmony.Refs;

/// <summary>
/// Local checkout selection. Not synced; affects authoring defaults and materialization views.
/// </summary>
public abstract record RefCheckout
{
    public static RefCheckout Main { get; } = new MainCheckout();

    public static RefCheckout ForBranch(Guid branchId) => new BranchCheckout(branchId);

    public static RefCheckout ForTag(Guid tagId) => new TagCheckout(tagId);
}

public sealed record MainCheckout : RefCheckout;

public sealed record BranchCheckout(Guid BranchId) : RefCheckout;

public sealed record TagCheckout(Guid TagId) : RefCheckout;
