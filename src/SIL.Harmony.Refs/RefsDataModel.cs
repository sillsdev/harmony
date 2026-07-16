using SIL.Harmony.Changes;
using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Refs.Entities;

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

    /// <summary>
    /// When true, authoring on a tag checkout writes to main. Default is false (rejected).
    /// </summary>
    public bool AllowAuthoringOnTagToMain { get; set; }

    /// <summary>
    /// Fired when the active checkout tip advances (tag move / roll-forward).
    /// </summary>
    public event EventHandler<RefCheckoutChangedEventArgs>? CheckoutChanged;

    public async Task CheckoutMain()
    {
        if (Checkout is MainCheckout && !filter.HasAsOfTip) return;
        filter.ClearAsOfTip();
        filter.Checkout = RefCheckout.Main;
        await DataModel.RegenerateSnapshots();
    }

    public async Task CheckoutBranch(Guid branchId)
    {
        if (Checkout is BranchCheckout current && current.BranchId == branchId && !filter.HasAsOfTip)
            return;

        filter.ClearAsOfTip();
        filter.Checkout = RefCheckout.ForBranch(branchId);
        await DataModel.RegenerateSnapshots();
    }

    public async Task CheckoutTag(Guid tagId)
    {
        var tip = await ResolveTagTip(tagId);
        filter.Checkout = RefCheckout.ForTag(tagId);
        filter.SetAsOfTip(tip);
        await DataModel.RegenerateSnapshots();
    }

    public Task<Commit> CreateTag(Guid clientId, Guid tagId, string name, Guid targetCommitId) =>
        DataModel.AddChange(clientId, new CreateTagChange(tagId, name, targetCommitId),
            ApplyAssignment(null, BranchAssignment.Main));

    public async Task<Commit> MoveTag(Guid clientId, Guid tagId, Guid targetCommitId)
    {
        var commit = await DataModel.AddChange(clientId, new MoveTagChange(tagId, targetCommitId),
            ApplyAssignment(null, BranchAssignment.Main));
        if (Checkout is TagCheckout tag && tag.TagId == tagId)
            await RollForwardActiveTag(tagId);
        return commit;
    }

    /// <summary>
    /// Incorporates <paramref name="branchId"/> into main visibility and deletes the branch entity.
    /// Authored as a main-line commit; materialization expands from the earliest branch commit.
    /// </summary>
    public async Task<Commit> MergeBranch(Guid clientId, Guid branchId)
    {
        await CheckoutMain();
        return await AddChange(clientId, new MergeBranchChange(branchId), BranchAssignment.Main);
    }

    /// <summary>
    /// Syncs via <see cref="DataModel.SyncWith"/> then rolls forward an active tag checkout if the tip moved.
    /// </summary>
    public async Task<SyncResults> SyncWith(ISyncable remote)
    {
        Guid? tipBefore = null;
        if (Checkout is TagCheckout active)
        {
            try { tipBefore = (await ResolveTagTip(active.TagId)).Id; }
            catch (InvalidOperationException) { /* tag not projected yet */ }
        }

        var results = await DataModel.SyncWith(remote);

        if (Checkout is TagCheckout checkedOut)
        {
            var tipAfter = await ResolveTagTip(checkedOut.TagId);
            if (tipBefore != tipAfter.Id)
                await RollForwardActiveTag(checkedOut.TagId);
        }

        return results;
    }

    /// <summary>
    /// After sync (or external apply), refresh tag checkout if the tip moved.
    /// </summary>
    public async Task RefreshCheckoutAfterSync()
    {
        if (Checkout is not TagCheckout tag) return;
        await RollForwardActiveTag(tag.TagId);
    }

    public Task<Commit> AddChange(
        Guid clientId,
        IChange change,
        BranchAssignment assignment = default,
        CommitMetadata? commitMetadata = null)
    {
        EnsureCanAuthor();
        if (assignment == default) assignment = BranchAssignment.FromCheckout;
        return DataModel.AddChange(clientId, change, ApplyAssignment(commitMetadata, assignment));
    }

    public Task<Commit> AddChanges(
        Guid clientId,
        IEnumerable<IChange> changes,
        BranchAssignment assignment = default,
        CommitMetadata? commitMetadata = null)
    {
        EnsureCanAuthor();
        if (assignment == default) assignment = BranchAssignment.FromCheckout;
        return DataModel.AddChanges(clientId, changes, ApplyAssignment(commitMetadata, assignment));
    }

    private void EnsureCanAuthor()
    {
        if (Checkout is not TagCheckout) return;
        if (AllowAuthoringOnTagToMain) return;
        throw new InvalidOperationException(
            "Authoring is not allowed while checked out on a tag. Set AllowAuthoringOnTagToMain to write to main, or checkout main/branch first.");
    }

    private async Task RollForwardActiveTag(Guid tagId)
    {
        var tip = await ResolveTagTip(tagId);
        filter.Checkout = RefCheckout.ForTag(tagId);
        filter.SetAsOfTip(tip);
        await DataModel.RegenerateSnapshots();
        CheckoutChanged?.Invoke(this, new RefCheckoutChangedEventArgs(filter.Checkout, tip.Id));
    }

    private async Task<Commit> ResolveTagTip(Guid tagId)
    {
        var tag = await DataModel.GetLatest<Tag>(tagId)
                  ?? throw new InvalidOperationException($"Tag {tagId} was not found.");
        return await DataModel.GetCommit(tag.TargetCommitId);
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
            TagCheckout when AllowAuthoringOnTagToMain => null,
            _ => null
        },
        _ => null
    };
}
