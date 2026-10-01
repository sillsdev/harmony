using System.IO.Hashing;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Core;

public record struct SimpleCommit(Guid ClientId, Guid CommitId, DateTimeOffset DateTime);

public static class QueryHelpers
{
    public static async Task<SyncState> GetSyncState(this IQueryable<CommitBase> commits)
    {
        //one query, so the count, hash and timestamp can't disagree about which commits exist
        var simpleCommits = await commits.AsNoTracking()
            .Select(c => new SimpleCommit(c.ClientId, c.Id, c.HybridDateTime.DateTime))
            .ToArrayAsync();

        return new SyncState(BuildSyncState(simpleCommits));
    }

    public static ClientState[] BuildSyncState(IEnumerable<SimpleCommit> simpleCommits)
    {
        var builders = new Dictionary<Guid, ClientStateBuilder>();
        foreach (var (clientId, commitId, dateTime) in simpleCommits)
        {
            if (!builders.TryGetValue(clientId, out var builder))
                builders[clientId] = builder = new ClientStateBuilder { ClientId = clientId };
            builder.Add(commitId, dateTime);
        }

        return builders.Values.Select(b => b.Build()).ToArray();
    }

    public static async Task<ChangesResult<TCommit>> GetChanges<TCommit, TChange>(this IQueryable<TCommit> commits,
        SyncState remoteState) where TCommit : CommitBase<TChange>
    {
        var localState = await commits.AsNoTracking().GetSyncState();
        return new ChangesResult<TCommit>(
            await GetCommitsMissingFromRemote<TCommit, TChange>(commits, localState, remoteState).ToArrayAsync(),
            localState);
    }

    public static async IAsyncEnumerable<TCommit> GetCommitsMissingFromRemote<TCommit, TChange>(
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
        //an older peer reports a head and nothing else, so timestamps are all we can compare
        if (remote.OnlyHasTimestamp)
            return local.MaxTimestamp > remote.MaxTimestamp ? SyncPlan.SendAfterRemoteTimestamp : SyncPlan.SendNothing;
        //local and remote agree on this client, nothing to sync.
        //the hash alone isn't enough: a commit id stored twice XORs back out of it, so the count has to match too.
        if (local == remote)
            return SyncPlan.SendNothing;
        if (local.MaxTimestamp > remote.MaxTimestamp)
            return SyncPlan.SendAfterRemoteTimestamp;
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

    public static IEnumerable<TCommit> GetCommitsMissingFromRemote<TCommit, TChange>(
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
