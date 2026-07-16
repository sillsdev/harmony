using SIL.Harmony.Changes;

namespace SIL.Harmony.Refs;

/// <summary>
/// Thin authoring/checkout wrapper over <see cref="DataModel"/>.
/// Checkout is local and not synced.
/// </summary>
public class RefsDataModel(DataModel dataModel)
{
    public DataModel DataModel { get; } = dataModel;

    public RefCheckout Checkout { get; private set; } = RefCheckout.Main;

    public void CheckoutMain() => Checkout = RefCheckout.Main;

    public void CheckoutBranch(Guid branchId) => Checkout = RefCheckout.ForBranch(branchId);

    public Task<Commit> AddChange(
        Guid clientId,
        IChange change,
        BranchAssignment assignment = default,
        CommitMetadata? commitMetadata = null)
    {
        if (assignment == default) assignment = BranchAssignment.FromCheckout;
        return DataModel.AddChange(clientId, change, ApplyAssignment(commitMetadata, assignment));
    }

    public Task<Commit> AddChanges(
        Guid clientId,
        IEnumerable<IChange> changes,
        BranchAssignment assignment = default,
        CommitMetadata? commitMetadata = null)
    {
        if (assignment == default) assignment = BranchAssignment.FromCheckout;
        return DataModel.AddChanges(clientId, changes, ApplyAssignment(commitMetadata, assignment));
    }

    private CommitMetadata ApplyAssignment(CommitMetadata? commitMetadata, BranchAssignment assignment)
    {
        var metadata = commitMetadata ?? new CommitMetadata();
        var branchId = ResolveBranchId(assignment);
        RefMetadata.SetBranchId(metadata, branchId);
        return metadata;
    }

    private Guid? ResolveBranchId(BranchAssignment assignment) => assignment.Kind switch
    {
        BranchAssignmentKind.Main => null,
        BranchAssignmentKind.Branch => assignment.BranchId,
        BranchAssignmentKind.FromCheckout => Checkout switch
        {
            BranchCheckout branch => branch.BranchId,
            _ => null
        },
        _ => null
    };
}
