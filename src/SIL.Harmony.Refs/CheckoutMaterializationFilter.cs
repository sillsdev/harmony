using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Db;
using SIL.Harmony.Refs.Changes;

namespace SIL.Harmony.Refs;

/// <summary>
/// Materializes commits visible for the current local checkout:
/// main line (no branch id), commits for incorporated (merged) branches on main,
/// plus commits scoped to the checked-out branch when on a branch,
/// or commits at-or-before a tag tip with visibility evaluated at that tip.
/// </summary>
public sealed class CheckoutMaterializationFilter : ICommitMaterializationFilter, IMaterializationApplyWindow
{
    private readonly HashSet<Guid> _incorporatedBranchIds = [];
    private Commit? _asOfTip;

    public RefCheckout Checkout { get; set; } = RefCheckout.Main;

    public bool HasAsOfTip => _asOfTip is not null;

    /// <summary>
    /// The commit the current tag checkout is pinned to, or null when not pinned to a tip.
    /// Lets the roll-forward listener skip rematerialization when the tag tip has not moved.
    /// </summary>
    public Guid? AsOfTipId => _asOfTip?.Id;

    /// <summary>
    /// When set (tag checkout), only commits at or before this tip are included,
    /// and merge incorporation is evaluated only through this tip.
    /// </summary>
    public void SetAsOfTip(Commit tip) => _asOfTip = tip;

    public void ClearAsOfTip() => _asOfTip = null;

    public bool Include(Commit commit)
    {
        // Keep the active tag entity materializable even when create/move commits are after the tip.
        if (Checkout is TagCheckout tag && TouchesTag(commit, tag.TagId))
            return true;

        if (_asOfTip is { } tip && commit.CompareKey.CompareTo(tip.CompareKey) > 0)
            return false;

        var branchId = RefMetadata.GetBranchId(commit.Metadata);
        if (branchId is null) return true;

        return Checkout switch
        {
            BranchCheckout branch => branchId == branch.BranchId || _incorporatedBranchIds.Contains(branchId.Value),
            TagCheckout when _asOfTip is { } asOfTip && RefMetadata.GetBranchId(asOfTip.Metadata) is Guid tipBranch =>
                branchId == tipBranch || _incorporatedBranchIds.Contains(branchId.Value),
            _ => _incorporatedBranchIds.Contains(branchId.Value)
        };
    }

    async Task<SortedSet<Commit>> IMaterializationApplyWindow.PrepareApplyWindowAsync(
        CrdtRepository repo,
        SortedSet<Commit> commitsToApply)
    {
        await RefreshIncorporatedBranchIds(repo);

        var mergedInWindow = commitsToApply
            .SelectMany(MergesIn)
            .Select(m => m.EntityId)
            .ToHashSet();
        if (mergedInWindow.Count == 0) return commitsToApply;

        // CurrentCommits is DefaultOrder (oldest first). First hit is the window start:
        // earliest merged-branch commit, or the apply-window head if that is older.
        var applyMinId = commitsToApply.Min!.Id;
        var allCommits = await repo.CurrentCommits()
            .Include(c => c.ChangeEntities)
            .ToListAsync();

        for (var i = 0; i < allCommits.Count; i++)
        {
            var commit = allCommits[i];
            var branchId = RefMetadata.GetBranchId(commit.Metadata);
            if (commit.Id == applyMinId ||
                (branchId is Guid id && mergedInWindow.Contains(id)))
            {
                return allCommits.Skip(i).ToSortedSet();
            }
        }

        return commitsToApply;
    }

    private async Task RefreshIncorporatedBranchIds(CrdtRepository repo)
    {
        _incorporatedBranchIds.Clear();
        // Scoping to the tip bounds GetEntityIdsForChangeType to merges at or before it (see issue 15),
        // so a single query serves both the tag (as-of) and unscoped cases.
        if (_asOfTip is { } tip) repo = repo.GetScopedRepository(tip);
        _incorporatedBranchIds.UnionWith(await repo.GetEntityIdsForChangeType<MergeBranchChange>());
    }

    private static IEnumerable<MergeBranchChange> MergesIn(Commit commit) =>
        commit.ChangeEntities
            .Select(ce => ce.Change)
            .OfType<MergeBranchChange>();

    private static bool TouchesTag(Commit commit, Guid tagId) =>
        commit.ChangeEntities.Any(ce => ce.EntityId == tagId &&
            (ce.Change is CreateTagChange or MoveTagChange));
}
