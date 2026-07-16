using SIL.Harmony.Changes;

namespace SIL.Harmony.Refs;

/// <summary>
/// Thin authoring/checkout wrapper over <see cref="DataModel"/>.
/// Checkout lives on <see cref="CheckoutMaterializationFilter"/> (single source of truth);
/// changing it rematerializes snapshots for the new view.
/// </summary>
public class RefsDataModel(DataModel dataModel, CheckoutMaterializationFilter filter)
{
    public DataModel DataModel { get; } = dataModel;

    public RefCheckout Checkout => filter.Checkout;

    public async Task CheckoutMain()
    {
        if (Checkout is MainCheckout) return;
        filter.Checkout = RefCheckout.Main;
        await DataModel.RegenerateSnapshots();
    }

    public async Task CheckoutBranch(Guid branchId)
    {
        if (Checkout is BranchCheckout current && current.BranchId == branchId) return;
        filter.Checkout = RefCheckout.ForBranch(branchId);
        await DataModel.RegenerateSnapshots();
    }

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
