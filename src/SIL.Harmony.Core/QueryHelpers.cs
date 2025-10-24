using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Core;

public static class QueryHelpers
{
    public static async Task<SyncState> GetSyncState(this IQueryable<CommitBase> commits)
    {
        var dict = await commits.AsNoTracking().GroupBy(c => c.ClientId)
            .Select(g => new { ClientId = g.Key, DateTime = g.Max(c => c.HybridDateTime.DateTime) })
            .AsAsyncEnumerable()//this is so the ticks are calculated server side instead of the db
            .ToDictionaryAsync(c => c.ClientId, c => c.DateTime.ToUnixTimeMilliseconds());
        return new SyncState(dict);
    }

    public static async Task<ChangesResult<TCommit>> GetChanges<TCommit, TChange>(this IQueryable<TCommit> commits,
        SyncState remoteState) where TCommit : CommitBase<TChange>
    {
        var localState = await commits.AsNoTracking().GetSyncState();
        return new ChangesResult<TCommit>(
            await GetMissingCommits<TCommit, TChange>(commits, localState, remoteState).ToArrayAsync(),
            localState);
    }

    public static async IAsyncEnumerable<TCommit> GetMissingCommits<TCommit, TChange>(
        this IQueryable<TCommit> commits,
        SyncState localState,
        SyncState remoteState, bool includeChangeEntities = true) where TCommit : CommitBase<TChange>
    {
        commits = commits.AsNoTracking();
        if (includeChangeEntities) commits = commits.Include(c => c.ChangeEntities);
        foreach (var (clientId, localTimestamp) in localState.ClientHeads)
        {
            //client is new to the other history
            if (!remoteState.ClientHeads.TryGetValue(clientId, out var otherTimestamp))
            {
                //todo slow, it would be better if we could query on client id and get latest changes per client
                await foreach (var commit in commits
                                   .DefaultOrder()
                                   .Where(c => c.ClientId == clientId)
                                   .AsAsyncEnumerable())
                {
                    yield return commit;
                }
            }
            //client has newer history than the other history
            else if (localTimestamp > otherTimestamp)
            {
                var otherDt = DateTimeOffset.FromUnixTimeMilliseconds(otherTimestamp);
                //todo even slower because we need to filter out changes that are already in the other history
                await foreach (var commit in commits
                                   .DefaultOrder()
                                   .Where(c => c.ClientId == clientId && c.HybridDateTime.DateTime > otherDt)
                                   .AsAsyncEnumerable())
                {
                    if (commit.DateTime.ToUnixTimeMilliseconds() > otherTimestamp)
                        yield return commit;
                }
            }
        }
    }

    private static readonly IComparer<CommitBase> CommitComparer =
        Comparer<CommitBase>.Create((a, b) => a.CompareKey.CompareTo(b.CompareKey));

    public static SortedSet<T> ToSortedSet<T>(this IEnumerable<T> queryable) where T : CommitBase
    {
        return new SortedSet<T>(queryable, CommitComparer);
    }

    public static async Task<SortedSet<T>> ToSortedSetAsync<T>(this IQueryable<T> queryable) where T : CommitBase
    {
        var set = new SortedSet<T>(CommitComparer);
        await foreach (var item in queryable.AsAsyncEnumerable())
            set.Add(item);
        return set;
    }

    public static IQueryable<T> DefaultOrder<T>(this IQueryable<T> queryable) where T: CommitBase
    {
        return queryable
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ThenBy(c => c.HybridDateTime.Counter)
            .ThenBy(c => c.Id);
    }

    public static IEnumerable<T> DefaultOrder<T>(this IEnumerable<T> queryable) where T: CommitBase
    {
        return queryable
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ThenBy(c => c.HybridDateTime.Counter)
            .ThenBy(c => c.Id);
    }

    public static IQueryable<T> DefaultOrderDescending<T>(this IQueryable<T> queryable) where T: CommitBase
    {
        return queryable
            .OrderByDescending(c => c.HybridDateTime.DateTime)
            .ThenByDescending(c => c.HybridDateTime.Counter)
            .ThenByDescending(c => c.Id);
    }

    public static IQueryable<T> WhereAfter<T>(this IQueryable<T> queryable, T after) where T : CommitBase
    {
        return queryable.Where(c => after.HybridDateTime.DateTime < c.HybridDateTime.DateTime
        || (after.HybridDateTime.DateTime == c.HybridDateTime.DateTime && after.HybridDateTime.Counter < c.HybridDateTime.Counter)
        || (after.HybridDateTime.DateTime == c.HybridDateTime.DateTime && after.HybridDateTime.Counter == c.HybridDateTime.Counter && after.Id < c.Id));
    }

    public static IQueryable<T> WhereBefore<T>(this IQueryable<T> queryable, T before, bool inclusive = false) where T : CommitBase
    {
        if (inclusive)
        {

            return queryable.Where(c => c.HybridDateTime.DateTime < before.HybridDateTime.DateTime
                                        || (c.HybridDateTime.DateTime == before.HybridDateTime.DateTime &&
                                            c.HybridDateTime.Counter < before.HybridDateTime.Counter)
                                        || (c.HybridDateTime.DateTime == before.HybridDateTime.DateTime &&
                                            c.HybridDateTime.Counter == before.HybridDateTime.Counter &&
                                            c.Id < before.Id)
                                        || c.Id == before.Id);
        }
        return queryable.Where(c => c.HybridDateTime.DateTime < before.HybridDateTime.DateTime
        || (c.HybridDateTime.DateTime == before.HybridDateTime.DateTime && c.HybridDateTime.Counter < before.HybridDateTime.Counter)
        || (c.HybridDateTime.DateTime == before.HybridDateTime.DateTime && c.HybridDateTime.Counter == before.HybridDateTime.Counter && c.Id < before.Id));
    }
}
