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
            long? remoteTimestamp = remoteState.ClientHeads.TryGetValue(clientId, out var otherTimestamp)
                ? otherTimestamp
                : null;
            var clientCommits = commits.Where(c => c.ClientId == clientId);
            if (remoteTimestamp is null)
            {
                await foreach (var commit in clientCommits.DefaultOrder().AsAsyncEnumerable())
                    yield return commit;
            }
            else if (localTimestamp > remoteTimestamp)
            {
                var otherDt = DateTimeOffset.FromUnixTimeMilliseconds(remoteTimestamp.Value);
                await foreach (var commit in clientCommits
                                   .Where(c => c.HybridDateTime.DateTime > otherDt)
                                   .DefaultOrder()
                                   .AsAsyncEnumerable())
                {
                    if (commit.DateTime.ToUnixTimeMilliseconds() > remoteTimestamp)
                        yield return commit;
                }
            }
        }
    }

    public static SortedSet<T> ToSortedSet<T>(this IEnumerable<T> queryable) where T : CommitBase
    {
        return [.. queryable];
    }

    public static async Task<SortedSet<T>> ToSortedSetAsync<T>(this IQueryable<T> queryable) where T : CommitBase
    {
        var set = new SortedSet<T>();
        await foreach (var item in queryable.AsAsyncEnumerable())
        {
            set.Add(item);
        }
        return set;
    }

    public static IEnumerable<TCommit> GetMissingCommits<TCommit, TChange>(
        this IEnumerable<TCommit> commits,
        SyncState localState,
        SyncState remoteState) where TCommit : CommitBase<TChange>
    {
        foreach (var (clientId, localTimestamp) in localState.ClientHeads)
        {
            long? remoteTimestamp = remoteState.ClientHeads.TryGetValue(clientId, out var otherTimestamp)
                ? otherTimestamp
                : null;
            foreach (var commit in GetMissingCommitsForClient(
                         commits.Where(c => c.ClientId == clientId), localTimestamp, remoteTimestamp))
            {
                yield return commit;
            }
        }
    }

    private static IEnumerable<TCommit> GetMissingCommitsForClient<TCommit>(
        IEnumerable<TCommit> clientCommits,
        long localTimestamp,
        long? remoteTimestamp) where TCommit : CommitBase
    {
        if (remoteTimestamp is null)
        {
            foreach (var commit in clientCommits.DefaultOrder())
                yield return commit;
        }
        else if (localTimestamp > remoteTimestamp)
        {
            var otherDt = DateTimeOffset.FromUnixTimeMilliseconds(remoteTimestamp.Value);
            foreach (var commit in clientCommits
                         .Where(c => c.HybridDateTime.DateTime > otherDt)
                         .DefaultOrder())
            {
                if (commit.DateTime.ToUnixTimeMilliseconds() > remoteTimestamp)
                    yield return commit;
            }
        }
    }

    public static IQueryable<T> DefaultOrder<T>(this IQueryable<T> queryable) where T : CommitBase
    {
        return queryable
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ThenBy(c => c.HybridDateTime.Counter)
            .ThenBy(c => c.Id);
    }

    public static IEnumerable<T> DefaultOrder<T>(this IEnumerable<T> queryable) where T : CommitBase
    {
        return queryable
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ThenBy(c => c.HybridDateTime.Counter)
            .ThenBy(c => c.Id);
    }

    public static IQueryable<T> DefaultOrderDescending<T>(this IQueryable<T> queryable) where T : CommitBase
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
