using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Db;

internal interface ISnapshotView
{
    ValueTask<ObjectSnapshot?> GetAsync(Guid entityId);
    /// <param name="candidates">entities that include every match, which lets a database view skip the rest cheaply</param>
    IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicate, CandidateFilter? candidates = null);
    /// <summary>every entity's snapshot; enumerating it preloads the whole view</summary>
    IAsyncEnumerable<ObjectSnapshot> All();
    /// <summary>fetches these entities in one query so later <see cref="GetAsync"/> calls don't each hit the database</summary>
    Task PreloadAsync(IReadOnlyCollection<Guid> entityIds);
    /// <summary>fetches every entity in one query, so every later read is a cache hit</summary>
    Task PreloadAllAsync();

    /// <summary>snapshots whose entity holds a reference to <paramref name="entityId"/></summary>
    IAsyncEnumerable<ObjectSnapshot> WhereReferences(Guid entityId, bool includeDeleted) =>
        Where(s => (includeDeleted || !s.EntityIsDeleted) && s.References.Contains(entityId),
            CandidateFilter.References(entityId));

    /// <param name="typeName">the type's JSON name, as stored in <see cref="ObjectSnapshot.TypeName"/></param>
    IAsyncEnumerable<ObjectSnapshot> WhereTypeName(string typeName, bool includeDeleted) =>
        Where(s => (includeDeleted || !s.EntityIsDeleted) && s.TypeName == typeName,
            CandidateFilter.TypeName(typeName));
}

/// <summary>
/// Narrows <see cref="DbSnapshotView.CurrentSnapshotsQuery"/> before it picks each entity's newest snapshot. That
/// grouping is the expensive part, and SQLite won't push an outer filter into it.
/// </summary>
/// <param name="Sql">a condition on the snapshot row "s", with <see cref="ValuePlaceholder"/> for <see cref="Value"/></param>
internal sealed record CandidateFilter(string Sql, object Value)
{
    public const string ValuePlaceholder = "{3}";

    /// <summary>
    /// Entities with any snapshot whose references contain the id. The JSON array holds each id as EF writes a Guid,
    /// which is how EF binds the parameter, so the text match finds every snapshot json_each would.
    /// </summary>
    public static CandidateFilter References(Guid entityId) =>
        AnySnapshot($"instr(\"References\", {ValuePlaceholder}) > 0", entityId);

    public static CandidateFilter TypeName(string typeName) => AnySnapshot($"\"TypeName\" = {ValuePlaceholder}", typeName);

    //bound as one JSON parameter; EF 10+ would otherwise emit one parameter per id and overflow SQLite's limit
    public static CandidateFilter EntityIds(IReadOnlyCollection<Guid> entityIds) =>
        new($"\"s\".\"EntityId\" IN (SELECT \"value\" FROM json_each({ValuePlaceholder}))", entityIds.ToArray());

    private static CandidateFilter AnySnapshot(string snapshotCondition, object value) =>
        new($"\"s\".\"EntityId\" IN (SELECT \"EntityId\" FROM \"Snapshots\" WHERE {snapshotCondition})", value);
}

/// <summary>the view before the first commit</summary>
internal sealed class EmptySnapshotView : ISnapshotView
{
    public static readonly EmptySnapshotView Instance = new();

    private EmptySnapshotView()
    {
    }

    public ValueTask<ObjectSnapshot?> GetAsync(Guid entityId) => ValueTask.FromResult<ObjectSnapshot?>(null);

    public IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicate, CandidateFilter? candidates = null) =>
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

    public async IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicate,
        CandidateFilter? candidates = null)
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

        var currentSnapshots = candidates is null ? _currentSnapshots : CurrentSnapshotsQuery(dbContext, upToInclusive, candidates);
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
        // Format items, not named parameters: EF binds each argument through its type mapping, which is what stores a
        // Guid as the text the columns hold. Each argument is bound once however often its item appears.
        const string date = "{0}", counter = "{1}", commitId = "{2}";
        var sql = $$"""
             SELECT "s".*,
                    max("c"."DateTime" || '!' || printf('%020d', "c"."Counter") || '!' || "c"."Id")
             FROM "Snapshots" AS "s"
                      INNER JOIN "Commits" AS "c" ON "s"."CommitId" = "c"."Id"
             WHERE ({{date}} IS NULL
                OR ("c"."DateTime" < {{date}} OR ("c"."DateTime" = {{date}} AND "c"."Counter" < {{counter}}) OR
                    ("c"."DateTime" = {{date}} AND "c"."Counter" = {{counter}} AND "c"."Id" < {{commitId}}) OR "c"."Id" = {{commitId}}))
               {{(candidates is null ? "" : "AND " + candidates.Sql)}}
             GROUP BY "s"."EntityId"
             """;
        object?[] args = candidates is null
            ? [ignoreAfterDate, ignoreAfterCounter, ignoreAfterCommitId]
            : [ignoreAfterDate, ignoreAfterCounter, ignoreAfterCommitId, candidates.Value];
        return dbContext.Set<ObjectSnapshot>().FromSql(FormattableStringFactory.Create(sql, args)).AsNoTracking();
    }
}
