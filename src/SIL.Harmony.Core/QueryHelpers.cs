using System.IO.Hashing;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Core;

public record struct SimpleCommit(Guid ClientId, Guid CommitId);

public static class QueryHelpers
{
    public static async Task<SyncState> GetSyncState(this IQueryable<CommitBase> commits)
    {
        var dict = await commits.AsNoTracking().GroupBy(c => c.ClientId)
            .Select(g => new { ClientId = g.Key, DateTime = g.Max(c => c.HybridDateTime.DateTime) })
            .AsAsyncEnumerable() //this is so the ticks are calculated server side instead of the db
            .ToDictionaryAsync(c => c.ClientId,
                c => new ClientStateBuilder
                {
                    ClientId = c.ClientId,
                    Timestamp = c.DateTime.ToUnixTimeMilliseconds()
                });
        var simpleCommits = await commits.AsNoTracking()
            .OrderBy(c => c.ClientId).ThenBy(c => c.Id)
            .Select(c => new SimpleCommit(c.ClientId, c.Id))
            .ToArrayAsync();

        return new SyncState(BuildSyncState(simpleCommits, dict));
    }

    public static ClientState[] BuildSyncState(SimpleCommit[] simpleCommits, Dictionary<Guid, ClientStateBuilder> dict)
    {
        //commits should be ordered by client id, so if we can avoid looking up the builder every loop it should be faster.
        ClientStateBuilder? currentBuilder = null;
        Span<byte> hash = stackalloc byte[16];
        foreach (var (clientId, commitId) in simpleCommits)
        {
            if (currentBuilder == null || currentBuilder.ClientId != clientId)
            {
                currentBuilder = dict.GetValueOrDefault(clientId) ?? (dict[clientId] = new ClientStateBuilder() { ClientId = clientId });
            }
            currentBuilder.Count++;
            if (!commitId.TryWriteBytes(hash))
                throw new InvalidOperationException("Commit ID is too large to fit in a 16-byte buffer.");
            currentBuilder.Hash.Append(hash);
        }

        return dict.Values.Select(b => b.Build()).ToArray();
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
        foreach (var localClientState in localState.ClientStates)
        {
            var clientCommits = commits.Where(c => c.ClientId == localClientState.ClientId).DefaultOrder();
            var remoteClientState = remoteState.GetClientState(localClientState.ClientId);
            switch (PlanFor(localClientState, remoteClientState))
            {
                case SyncPlan.SendNothing:
                    break;
                case SyncPlan.SendAll:
                    await foreach (var commit in clientCommits.AsAsyncEnumerable())
                        yield return commit;
                    break;
                case SyncPlan.SendAfterRemoteTimestamp:
                    var after = DateTimeOffset.FromUnixTimeMilliseconds(remoteClientState!.MaxTimestamp);
                    await foreach (var commit in clientCommits
                                       .Where(c => c.HybridDateTime.DateTime > after)
                                       .AsAsyncEnumerable())
                    {
                        if (IsAfter(commit, remoteClientState.MaxTimestamp))
                            yield return commit;
                    }
                    break;
            }
        }
    }

    private enum SyncPlan
    {
        SendNothing,
        SendAll,
        SendAfterRemoteTimestamp
    }

    private static SyncPlan PlanFor(ClientState local, ClientState? remote)
    {
        //the remote has never seen this client, so push everything
        if (remote is null)
            return SyncPlan.SendAll;
        if (local.MaxTimestamp > remote.MaxTimestamp)
            return SyncPlan.SendAfterRemoteTimestamp;
        //local and remote agree on this client, nothing to sync
        if (local.Hash == remote.Hash)
            return SyncPlan.SendNothing;
        //same head but different commits: whoever has at least as many pushes everything.
        //if we have fewer we assume the remote has ours, which may be a false positive we catch next sync.
        return local.CommitCount >= remote.CommitCount ? SyncPlan.SendAll : SyncPlan.SendNothing;
    }

    //the db keeps sub-millisecond precision, so re-check against the millisecond value the remote reported
    private static bool IsAfter(CommitBase commit, long afterMs) =>
        commit.DateTime.ToUnixTimeMilliseconds() > afterMs;

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
        foreach (var localClientState in localState.ClientStates)
        {
            ClientState? remoteClientState = remoteState.GetClientState(localClientState.ClientId);
            var clientCommits = commits.Where(c => c.ClientId == localClientState.ClientId).DefaultOrder();
            switch (PlanFor(localClientState, remoteClientState))
            {
                case SyncPlan.SendNothing:
                    break;
                case SyncPlan.SendAll:
                    foreach (var commit in clientCommits)
                        yield return commit;
                    break;
                case SyncPlan.SendAfterRemoteTimestamp:
                    foreach (var commit in clientCommits.Where(c => IsAfter(c, remoteClientState!.MaxTimestamp)))
                        yield return commit;
                    break;
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
