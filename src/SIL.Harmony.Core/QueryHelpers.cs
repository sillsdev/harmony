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
            var clientCommits = commits.Where(c => c.ClientId == localClientState.ClientId);
            var remoteClientState = remoteState.GetClientState(localClientState.ClientId);
            if (SendCommitsAfterTimestamp(localClientState, remoteClientState) is { } afterTimestamp)
            {
                await foreach (var commit in clientCommits
                                   .Where(c => c.HybridDateTime.DateTime > afterTimestamp)
                                   .DefaultOrder()
                                   .AsAsyncEnumerable())
                {
                    if (commit.DateTime.ToUnixTimeMilliseconds() > afterTimestamp.ToUnixTimeMilliseconds())
                        yield return commit;
                }
                continue;
            }

            if (ShouldSendAllCommits(localClientState, remoteClientState))
            {
                await foreach (var commit in clientCommits.DefaultOrder().AsAsyncEnumerable())
                    yield return commit;
            }
        }
    }

    private static DateTimeOffset? SendCommitsAfterTimestamp(ClientState localClientState, ClientState? remoteClientState)
    {
        if (remoteClientState is null)
            return null;
        if (localClientState.MaxTimestamp > remoteClientState.MaxTimestamp)
            return DateTimeOffset.FromUnixTimeMilliseconds(remoteClientState.MaxTimestamp);
        return null;
    }

    private static bool ShouldSendAllCommits(ClientState localClientState, ClientState? remoteClientState)
    {
        //remote does not have this client, so push everything
        if (remoteClientState is null)
        {
            return true;
        }
        //local and remote agree on this client, nothing to sync
        if (localClientState.Hash == remoteClientState.Hash)
        {
            return false;
        }

        //the local client is missing commits from the remote, don't send anything.
        //this could be a false positive, but we'll catch those on the next sync.
        if (localClientState.CommitCount < remoteClientState.CommitCount)
        {
            return false;
        }

        //the hashes don't match and we have more or the same commit counts than remote, so just send everything
        return true;
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
        foreach (var localClientState in localState.ClientStates)
        {
            ClientState? remoteClientState = remoteState.GetClientState(localClientState.ClientId);
            foreach (var commit in GetMissingCommitsForClient(
                         commits.Where(c => c.ClientId == localClientState.ClientId), localClientState, remoteClientState))
            {
                yield return commit;
            }
        }
    }

    private static IEnumerable<TCommit> GetMissingCommitsForClient<TCommit>(
        IEnumerable<TCommit> clientCommits,
        ClientState localClientState,
        ClientState? remoteClientState) where TCommit : CommitBase
    {
        if (SendCommitsAfterTimestamp(localClientState, remoteClientState) is { } afterTimestamp)
        {
            foreach (var commit in clientCommits
                         .Where(c => c.HybridDateTime.DateTime > afterTimestamp)
                         .DefaultOrder())
            {
                if (commit.DateTime.ToUnixTimeMilliseconds() > afterTimestamp.ToUnixTimeMilliseconds())
                    yield return commit;
            }
            yield break;
        }

        if (ShouldSendAllCommits(localClientState, remoteClientState))
        {
            foreach (var commit in clientCommits.DefaultOrder())
                yield return commit;
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
