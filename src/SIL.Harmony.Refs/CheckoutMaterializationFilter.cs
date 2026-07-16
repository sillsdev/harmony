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
    private (DateTimeOffset, long, Guid)? _asOfCompareKey;
    private Guid? _tipBranchId;

    public RefCheckout Checkout { get; set; } = RefCheckout.Main;

    public bool HasAsOfTip => _asOfCompareKey is not null;

    /// <summary>
    /// When set (tag checkout), only commits at or before this compare key are included,
    /// and merge incorporation is evaluated only through this tip.
    /// </summary>
    public void SetAsOfTip(Commit tip)
    {
        _asOfCompareKey = tip.CompareKey;
        _tipBranchId = RefMetadata.GetBranchId(tip.Metadata);
    }

    public void ClearAsOfTip()
    {
        _asOfCompareKey = null;
        _tipBranchId = null;
    }

    public bool Include(Commit commit)
    {
        // Keep the active tag entity materializable even when create/move commits are after the tip.
        if (Checkout is TagCheckout tag && TouchesTag(commit, tag.TagId))
            return true;

        if (_asOfCompareKey is { } asOf && commit.CompareKey.CompareTo(asOf) > 0)
            return false;

        var branchId = RefMetadata.GetBranchId(commit.Metadata);
        if (branchId is null) return true;

        return Checkout switch
        {
            BranchCheckout branch => branchId == branch.BranchId || _incorporatedBranchIds.Contains(branchId.Value),
            TagCheckout when _tipBranchId is Guid tipBranch =>
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
        if (_asOfCompareKey is { } asOf)
        {
            // Only merges at or before the tip count for tag (as-of) visibility.
            var commits = await repo.CurrentCommits()
                .Include(c => c.ChangeEntities)
                .ToListAsync();
            foreach (var commit in commits)
            {
                if (commit.CompareKey.CompareTo(asOf) > 0) break;
                foreach (var merge in MergesIn(commit))
                    _incorporatedBranchIds.Add(merge.EntityId);
            }
        }
        else
        {
            _incorporatedBranchIds.UnionWith(await repo.GetEntityIdsForChangeType<MergeBranchChange>());
        }
    }

    private static IEnumerable<MergeBranchChange> MergesIn(Commit commit) =>
        commit.ChangeEntities
            .Select(ce => ce.Change)
            .OfType<MergeBranchChange>();

    private static bool TouchesTag(Commit commit, Guid tagId) =>
        commit.ChangeEntities.Any(ce => ce.EntityId == tagId &&
            (ce.Change is CreateTagChange or MoveTagChange));
}
