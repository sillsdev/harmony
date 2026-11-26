using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Db;

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

    /// <summary>snapshots whose entity holds a reference to <paramref name="entityId"/></summary>
    IAsyncEnumerable<ObjectSnapshot> WhereReferences(Guid entityId, bool includeDeleted) =>
        Where(ReferencesPredicate(entityId, includeDeleted));

    IAsyncEnumerable<ObjectSnapshot> WhereTypeName(string typeName, bool includeDeleted) =>
        Where(TypeNamePredicate(typeName, includeDeleted));

    static Expression<Func<ObjectSnapshot, bool>> ReferencesPredicate(Guid entityId, bool includeDeleted) =>
        s => (includeDeleted || !s.EntityIsDeleted) && s.References.Contains(entityId);

    static Expression<Func<ObjectSnapshot, bool>> TypeNamePredicate(string typeName, bool includeDeleted) =>
        s => (includeDeleted || !s.EntityIsDeleted) && s.TypeName == typeName;
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

    public IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicate) =>
        Where(_currentSnapshots, predicate);

    public IAsyncEnumerable<ObjectSnapshot> WhereReferences(Guid entityId, bool includeDeleted) =>
        Where(CurrentSnapshotsQuery(dbContext, upToInclusive, CandidateFilter.References(entityId)),
            ISnapshotView.ReferencesPredicate(entityId, includeDeleted));

    public IAsyncEnumerable<ObjectSnapshot> WhereTypeName(string typeName, bool includeDeleted) =>
        Where(CurrentSnapshotsQuery(dbContext, upToInclusive, CandidateFilter.TypeName(typeName)),
            ISnapshotView.TypeNamePredicate(typeName, includeDeleted));

    /// <param name="currentSnapshots">the current snapshots, or a narrower query that still returns every match</param>
    private async IAsyncEnumerable<ObjectSnapshot> Where(IQueryable<ObjectSnapshot> currentSnapshots,
        Expression<Func<ObjectSnapshot, bool>> predicate)
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

        await foreach (var snapshot in currentSnapshots.Where(predicate)
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
        var found = await CurrentSnapshotsQuery(dbContext, upToInclusive, CandidateFilter.EntityIds(entityIds))
            .Include(s => s.Commit)
            .ToDictionaryAsync(s => s.EntityId);
        foreach (var entityId in entityIds)
        {
            _cache[entityId] = found.GetValueOrDefault(entityId);
        }
    }

    /// <summary>
    /// Narrows <see cref="CurrentSnapshotsQuery(ICrdtDbContext, Commit?, CandidateFilter?)"/> before it picks each
    /// entity's newest snapshot. That grouping is the expensive part, and SQLite won't push an outer filter into it.
    /// </summary>
    /// <param name="Sql">a condition on the snapshot row "s", with {8} for <see cref="Arg"/></param>
    internal sealed record CandidateFilter(string Sql, object Arg)
    {
        /// <summary>
        /// Entities with any snapshot whose references contain the id, a superset of those whose newest one does,
        /// so callers still filter the newest. The JSON array holds each id as EF writes a Guid, which is how EF binds
        /// the parameter, so the raw text match finds every snapshot json_each would.
        /// </summary>
        public static CandidateFilter References(Guid entityId) => AnySnapshot(
            "instr(\"References\", {8}) > 0", entityId);

        /// <summary>an entity id belongs to one type, but callers still filter the newest snapshot</summary>
        public static CandidateFilter TypeName(string typeName) => AnySnapshot("\"TypeName\" = {8}", typeName);

        //bound as one JSON parameter; EF 10+ would otherwise emit one parameter per id and overflow SQLite's limit
        public static CandidateFilter EntityIds(IReadOnlyCollection<Guid> entityIds) =>
            new("\"s\".\"EntityId\" IN (SELECT \"value\" FROM json_each({8}))", entityIds.ToArray());

        private static CandidateFilter AnySnapshot(string snapshotCondition, object arg) =>
            new($"\"s\".\"EntityId\" IN (SELECT \"EntityId\" FROM \"Snapshots\" WHERE {snapshotCondition})", arg);
    }

    internal static IQueryable<ObjectSnapshot> CurrentSnapshotsQuery(ICrdtDbContext dbContext, Commit? upToInclusive,
        CandidateFilter? candidates = null)
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
        // The separator '!' (0x21) has to sort below '.' and every digit: SQLite trims trailing zeros off the datetime, so
        // "00:00:00" vs "00:00:00.5" is decided by the separator.
        var candidateSql = candidates is null
            ? ""
            : $"AND {candidates.Sql}";
        var sql = $$"""
             SELECT "s".*,
                    max("c"."DateTime" || '!' || printf('%020d', "c"."Counter") || '!' || "c"."Id")
             FROM "Snapshots" AS "s"
                      INNER JOIN "Commits" AS "c" ON "s"."CommitId" = "c"."Id"
             WHERE ({0} IS NULL
                OR ("c"."DateTime" < {1} OR ("c"."DateTime" = {2} AND "c"."Counter" < {3}) OR
                    ("c"."DateTime" = {4} AND "c"."Counter" = {5} AND "c"."Id" < {6}) OR "c"."Id" = {7}))
               {{candidateSql}}
             GROUP BY "s"."EntityId"
             """;
        object?[] args =
        [
            ignoreAfterDate, ignoreAfterDate, ignoreAfterDate, ignoreAfterCounter, ignoreAfterDate, ignoreAfterCounter,
            ignoreAfterCommitId, ignoreAfterCommitId, .. candidates is null ? Array.Empty<object>() : [candidates.Arg]
        ];
        return dbContext.Set<ObjectSnapshot>().FromSql(FormattableStringFactory.Create(sql, args)).AsNoTracking();
    }
}
