using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Db;

/// <summary>
/// Read only access to each entity's newest snapshot at a point in time.
/// A view remembers what it has been asked, so repeating a read is free.
/// </summary>
internal interface ISnapshotView
{
    ValueTask<ObjectSnapshot?> GetAsync(Guid entityId);
    IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicate);
    /// <summary>every entity's snapshot; enumerating it preloads the whole view</summary>
    IAsyncEnumerable<ObjectSnapshot> All();
    /// <summary>fetches these entities in one query so later <see cref="GetAsync"/> calls don't each hit the database</summary>
    Task PreloadAsync(IReadOnlyCollection<Guid> entityIds);
    /// <summary>fetches every entity in one query, so every later read is a cache hit</summary>
    Task PreloadAllAsync();
}

internal static class SnapshotViewExtensions
{
    public static IAsyncEnumerable<ObjectSnapshot> WhereReferences(this ISnapshotView snapshots,
        Guid entityId,
        bool includeDeleted)
    {
        return snapshots.Where(s => (includeDeleted || !s.EntityIsDeleted) && s.References.Contains(entityId));
    }
}

/// <summary>the view before the first commit</summary>
internal sealed class EmptySnapshotView : ISnapshotView
{
    public static readonly EmptySnapshotView Instance = new();

    private EmptySnapshotView()
    {
    }

    public ValueTask<ObjectSnapshot?> GetAsync(Guid entityId) => ValueTask.FromResult<ObjectSnapshot?>(null);

    public IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicate) =>
        AsyncEnumerable.Empty<ObjectSnapshot>();

    public IAsyncEnumerable<ObjectSnapshot> All() => AsyncEnumerable.Empty<ObjectSnapshot>();

    public Task PreloadAsync(IReadOnlyCollection<Guid> entityIds) => Task.CompletedTask;

    public Task PreloadAllAsync() => Task.CompletedTask;
}

/// <param name="upToInclusive">null means the current table</param>
internal sealed class DbSnapshotView(ICrdtDbContext dbContext, Commit? upToInclusive) : ISnapshotView
{
    private readonly IQueryable<ObjectSnapshot> _currentSnapshots = CurrentSnapshotsQuery(dbContext, upToInclusive);
    /// <summary>a null value is an entity known to have no snapshot</summary>
    private readonly Dictionary<Guid, ObjectSnapshot?> _cache = [];
    /// <summary>set once <see cref="PreloadAllAsync"/> has run: from then on anything missing from <see cref="_cache"/> does not exist</summary>
    private bool _complete;

    public async ValueTask<ObjectSnapshot?> GetAsync(Guid entityId)
    {
        if (_cache.TryGetValue(entityId, out var snapshot)) return snapshot;
        if (_complete) return null;
        var query = dbContext.Snapshots.AsNoTracking();
        if (upToInclusive is not null)
            query = query.WhereBefore(upToInclusive, inclusive: true);
        snapshot = await query
            .Include(s => s.Commit)
            .DefaultOrder()
            .LastOrDefaultAsync(s => s.EntityId == entityId);
        _cache[entityId] = snapshot;
        return snapshot;
    }

    public async IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicate)
    {
        if (_complete)
        {
            var matches = predicate.Compile();
            foreach (var snapshot in _cache.Values)
            {
                if (snapshot is not null && matches(snapshot)) yield return snapshot;
            }

            yield break;
        }

        //Include so a match has its Commit loaded either way; the cached branch above always does
        await foreach (var snapshot in _currentSnapshots.Where(predicate)
            .Include(s => s.Commit).AsAsyncEnumerable())
        {
            yield return snapshot;
        }
    }

    public async IAsyncEnumerable<ObjectSnapshot> All()
    {
        await PreloadAllAsync();
        foreach (var snapshot in _cache.Values)
        {
            if (snapshot is not null) yield return snapshot;
        }
    }

    public async Task PreloadAllAsync()
    {
        if (_complete) return;
        await foreach (var snapshot in _currentSnapshots.Include(s => s.Commit).AsAsyncEnumerable())
        {
            _cache[snapshot.EntityId] = snapshot;
        }

        _complete = true;
    }

    public async Task PreloadAsync(IReadOnlyCollection<Guid> entityIds)
    {
        if (_complete) return;
        //EF.Parameter forces a single JSON parameter; without it EF 10+ emits one parameter per id and overflows SQLite's parameter limit
        var found = await _currentSnapshots
            .Include(s => s.Commit)
            .Where(s => EF.Parameter(entityIds).Contains(s.EntityId))
            .ToDictionaryAsync(s => s.EntityId);
        foreach (var entityId in entityIds)
        {
            _cache[entityId] = found.GetValueOrDefault(entityId);
        }
    }

    internal static IQueryable<ObjectSnapshot> CurrentSnapshotsQuery(ICrdtDbContext dbContext, Commit? upToInclusive)
    {
        var ignoreAfterDate = upToInclusive?.HybridDateTime.DateTime.UtcDateTime;
        var ignoreAfterCounter = upToInclusive?.HybridDateTime.Counter;
        var ignoreAfterCommitId = upToInclusive?.Id;
        // Newest snapshot per entity in a single grouped pass (SQLite only, not valid on Postgres).
        // Scanning via IX_Snapshots_EntityId arrives pre-grouped and max() streams, so nothing sorts.
        // With exactly one max(), SQLite returns the bare "s".* columns from the row that produced it
        // (https://sqlite.org/lang_select.html#bareagg). The commit order (DateTime, Counter, Id) is
        // packed into one sortable text key (Counter zero-padded to cover the long range); the trailing
        // max(...) column is unmapped and ignored by EF.
        // The separator has to sort below '.' and every digit: SQLite trims trailing zeros off the datetime, so
        // "00:00:00" vs "00:00:00.5" is decided by the separator, and '|' (0x7C) ranked the earlier commit first.
        return dbContext.Set<ObjectSnapshot>().FromSql(
            $"""
             SELECT "s".*,
                    max("c"."DateTime" || '!' || printf('%020d', "c"."Counter") || '!' || "c"."Id")
             FROM "Snapshots" AS "s"
                      INNER JOIN "Commits" AS "c" ON "s"."CommitId" = "c"."Id"
             WHERE {ignoreAfterDate} IS NULL
                OR ("c"."DateTime" < {ignoreAfterDate} OR ("c"."DateTime" = {ignoreAfterDate} AND "c"."Counter" < {ignoreAfterCounter}) OR
                    ("c"."DateTime" = {ignoreAfterDate} AND "c"."Counter" = {ignoreAfterCounter} AND "c"."Id" < {ignoreAfterCommitId}) OR "c"."Id" = {ignoreAfterCommitId})
             GROUP BY "s"."EntityId"
             """).AsNoTracking();
    }
}
