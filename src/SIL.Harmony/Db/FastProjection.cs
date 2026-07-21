using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace SIL.Harmony.Db;

/// <summary>
/// Fast projection path used by the FAST benchmark build. Snapshots are still inserted through EF
/// (unchanged), but the projected tables are populated with hand-written raw SQL
/// `INSERT ... ON CONFLICT(pk) DO UPDATE` (one upsert command per entity row) instead of going
/// through EF's change tracker. Everything the SQL needs (table/column names, primary key, the
/// SnapshotId shadow FK, value converters) is derived from the EF model, so no per-entity code is
/// required.
/// </summary>
internal static class FastProjection
{
    private static readonly ConcurrentDictionary<Type, ProjectedTableInfo> TableInfoCache = new();

    public static async Task AddSnapshotsRawAsync(
        ICrdtDbContext dbContext,
        IReadOnlyCollection<ObjectSnapshot> snapshots,
        bool enableProjectedTables)
    {
        // AddSnapshots is normally called inside a caller-managed transaction; reuse it so the raw
        // SQL runs on the same connection/transaction as the snapshot insert. When called outside a
        // transaction (e.g. direct repository tests) we open and commit our own.
        var ownTransaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync()
            : null;
        try
        {
            // 1. persist the snapshot rows exactly as before
            dbContext.AddRange(snapshots);
            await dbContext.SaveChangesAsync();

            if (enableProjectedTables)
            {
                await ProjectAsync(dbContext, snapshots);
            }

            if (ownTransaction is not null) await ownTransaction.CommitAsync();
        }
        finally
        {
            if (ownTransaction is not null) await ownTransaction.DisposeAsync();
        }
    }

    private static async Task ProjectAsync(
        ICrdtDbContext dbContext,
        IReadOnlyCollection<ObjectSnapshot> snapshots)
    {
        // 2. dedup to the latest snapshot per entity (a batch can contain several snapshots for the
        // same entity - intermediate + latest - and possibly a deleted and undeleted one).
        var latest = new Dictionary<Guid, ObjectSnapshot>();
        foreach (var snapshot in snapshots)
        {
            if (latest.TryGetValue(snapshot.EntityId, out var existing) &&
                existing.Commit.CompareKey.CompareTo(snapshot.Commit.CompareKey) >= 0)
            {
                continue;
            }
            latest[snapshot.EntityId] = snapshot;
        }

        var connection = dbContext.Database.GetDbConnection();
        var transaction = dbContext.Database.CurrentTransaction!.GetDbTransaction();
        var sqlHelper = dbContext.Database.GetService<ISqlGenerationHelper>();

        // 3. group by projected CLR type and order the types so FK parents are written first
        var byType = latest.Values
            .GroupBy(s => s.Entity.DbObject.GetType())
            .ToDictionary(g => g.Key, g => g.ToList());
        var orderedTypes = OrderTypesByDependency(dbContext.Model, byType.Keys);

        // deletes first (like the slow path) so a unique value can be freed and re-inserted within the
        // same batch; children before parents (reverse dependency order) to satisfy FK constraints.
        for (var i = orderedTypes.Count - 1; i >= 0; i--)
        {
            var deleted = byType[orderedTypes[i]].Where(s => s.EntityIsDeleted).ToList();
            if (deleted.Count == 0) continue;
            var info = GetTableInfo(dbContext, orderedTypes[i], sqlHelper);
            await DeletePerQueryAsync(connection, transaction, info, deleted);
        }

        // upserts: parents first
        foreach (var type in orderedTypes)
        {
            var live = byType[type].Where(s => !s.EntityIsDeleted).ToList();
            if (live.Count == 0) continue;
            var info = GetTableInfo(dbContext, type, sqlHelper);
            await UpsertPerQueryAsync(connection, transaction, info, live);
        }
    }

    private static async Task UpsertPerQueryAsync(
        DbConnection connection, DbTransaction transaction, ProjectedTableInfo info, List<ObjectSnapshot> rows)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = info.InsertSql;
        var parameters = new DbParameter[info.Columns.Count];
        for (var i = 0; i < info.Columns.Count; i++)
        {
            var p = command.CreateParameter();
            p.ParameterName = "@p" + i;
            command.Parameters.Add(p);
            parameters[i] = p;
        }
        command.Prepare();

        foreach (var snapshot in rows)
        {
            var dbObject = snapshot.Entity.DbObject;
            for (var i = 0; i < info.Columns.Count; i++)
            {
                parameters[i].Value = ToParameterValue(GetProviderValue(info.Columns[i], snapshot, dbObject));
            }
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task DeletePerQueryAsync(
        DbConnection connection, DbTransaction transaction, ProjectedTableInfo info, List<ObjectSnapshot> rows)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = info.DeleteSql;
        var p = command.CreateParameter();
        p.ParameterName = "@p0";
        command.Parameters.Add(p);
        command.Prepare();

        foreach (var snapshot in rows)
        {
            p.Value = ToParameterValue(GetProviderValue(info.PrimaryKey, snapshot, snapshot.Entity.DbObject));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static object? GetProviderValue(ColumnInfo column, ObjectSnapshot snapshot, object dbObject)
    {
        if (column.IsShadowSnapshotId) return snapshot.Id;
        var raw = column.Property.PropertyInfo is { } pi
            ? pi.GetValue(dbObject)
            : column.Property.FieldInfo?.GetValue(dbObject);
        var converter = column.Property.GetValueConverter();
        return converter is null ? raw : converter.ConvertToProvider(raw);
    }

    private static object ToParameterValue(object? value) => value ?? DBNull.Value;

    // ---- model metadata ---------------------------------------------------------------------

    private static ProjectedTableInfo GetTableInfo(ICrdtDbContext dbContext, Type clrType, ISqlGenerationHelper sqlHelper)
    {
        return TableInfoCache.GetOrAdd(clrType, t => BuildTableInfo(dbContext, t, sqlHelper));
    }

    private static ProjectedTableInfo BuildTableInfo(ICrdtDbContext dbContext, Type clrType, ISqlGenerationHelper sqlHelper)
    {
        var entityType = dbContext.Model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"No EF entity type found for projected type {clrType.Name}");
        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException($"No table name found for projected type {clrType.Name}");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);
        var pkPropertyNames = (entityType.FindPrimaryKey()?.Properties
                ?? throw new InvalidOperationException($"No primary key found for projected type {clrType.Name}"))
            .Select(p => p.Name)
            .ToHashSet();

        var columns = new List<ColumnInfo>();
        foreach (var property in entityType.GetProperties())
        {
            var columnName = property.GetColumnName(storeObject);
            if (columnName is null) continue; // not mapped to this table
            columns.Add(new ColumnInfo(
                sqlHelper.DelimitIdentifier(columnName),
                property,
                property.Name == ObjectSnapshot.ShadowRefName,
                pkPropertyNames.Contains(property.Name)));
        }

        var pkColumns = columns.Where(c => c.IsPrimaryKey).ToList();
        if (pkColumns.Count != 1)
            throw new NotSupportedException($"Fast projection requires a single-column primary key for {clrType.Name}");
        var pk = pkColumns[0];

        var delimitedTable = sqlHelper.DelimitIdentifier(tableName, schema);
        var columnList = string.Join(",", columns.Select(c => c.DelimitedName));
        var parameterList = string.Join(",", columns.Select((_, i) => "@p" + i));
        var setClause = string.Join(",", columns.Where(c => !c.IsPrimaryKey)
            .Select(c => $"{c.DelimitedName}=excluded.{c.DelimitedName}"));
        var onConflict = string.IsNullOrEmpty(setClause)
            ? $"ON CONFLICT ({pk.DelimitedName}) DO NOTHING"
            : $"ON CONFLICT ({pk.DelimitedName}) DO UPDATE SET {setClause}";

        return new ProjectedTableInfo(
            columns,
            pk,
            InsertSql: $"INSERT INTO {delimitedTable} ({columnList}) VALUES ({parameterList}) {onConflict};",
            DeleteSql: $"DELETE FROM {delimitedTable} WHERE {pk.DelimitedName}=@p0;");
    }

    /// <summary>
    /// Post-order DFS over FK edges so principal (parent) types come before dependents. Self
    /// references and FKs to non-projected types (e.g. the SnapshotId FK to Snapshots) are ignored.
    /// </summary>
    private static List<Type> OrderTypesByDependency(IModel model, IEnumerable<Type> types)
    {
        var typeSet = types.ToHashSet();
        var ordered = new List<Type>(typeSet.Count);
        var visited = new HashSet<Type>();

        void Visit(Type type)
        {
            if (!visited.Add(type)) return;
            var entityType = model.FindEntityType(type);
            if (entityType is not null)
            {
                foreach (var fk in entityType.GetForeignKeys())
                {
                    var principal = fk.PrincipalEntityType.ClrType;
                    if (principal != type && typeSet.Contains(principal)) Visit(principal);
                }
            }
            ordered.Add(type);
        }

        foreach (var type in typeSet) Visit(type);
        return ordered;
    }

    private sealed record ColumnInfo(
        string DelimitedName,
        IProperty Property,
        bool IsShadowSnapshotId,
        bool IsPrimaryKey);

    private sealed record ProjectedTableInfo(
        IReadOnlyList<ColumnInfo> Columns,
        ColumnInfo PrimaryKey,
        string InsertSql,
        string DeleteSql);
}
