using Microsoft.EntityFrameworkCore;

namespace Crdt.Core;

public static class QueryHelpers
{
    public static async Task<SyncState> GetSyncState(this IQueryable<CommitBase> commits)
    {
        var dict = await commits.GroupBy(c => c.ClientId)
            .Select(g => new { ClientId = g.Key, DateTime = g.Max(c => c.HybridDateTime.DateTime) })
            .AsAsyncEnumerable()//this is so the ticks are calculated server side instead of the db
            .ToDictionaryAsync(c => c.ClientId, c => c.DateTime.ToUnixTimeMilliseconds());
        return new SyncState(dict);
    }

    public static async Task<ChangesResult<TCommit>> GetChanges<TCommit, TChange>(this IQueryable<TCommit> commits, SyncState remoteState) where TCommit : CommitBase<TChange>
    {
        var newHistory = new List<TCommit>();
        var localSyncState = await commits.GetSyncState();
        foreach (var (clientId, localTimestamp) in localSyncState.ClientHeads)
        {
            if (!remoteState.ClientHeads.TryGetValue(clientId, out var otherTimestamp))
            {
                //todo slow, it would be better if we could query on client id and get latest changes per client
                //client is new to the other history
                newHistory.AddRange(await commits.Include(c => c.ChangeEntities).DefaultOrder()
                    .Where(c => c.ClientId == clientId)
                    .ToArrayAsync());
            }
            else if (localTimestamp > otherTimestamp)
            {
                var otherDt = DateTimeOffset.FromUnixTimeMilliseconds(otherTimestamp);
                //todo even slower we want to also filter out changes that are already in the other history
                //client has newer history than the other history
                newHistory.AddRange(await commits.Include(c => c.ChangeEntities).DefaultOrder()
                    .Where(c => c.ClientId == clientId && c.HybridDateTime.DateTime > otherDt)
                    .ToArrayAsync());
            }
        }

        return new(newHistory.ToArray(), localSyncState);
    }

    public static IQueryable<T> DefaultOrder<T>(this IQueryable<T> queryable) where T: CommitBase
    {
        return queryable
            .OrderBy(c => c.HybridDateTime.DateTime)
            .ThenBy(c => c.HybridDateTime.Counter)
            .ThenBy(c => c.Id);
    }
}
