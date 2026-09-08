using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;
using SIL.Harmony.Config;

namespace SIL.Harmony.Db;

/// <summary>
/// Projects snapshots into the projected tables. Snapshots are still inserted through EF (unchanged),
/// but the projected tables are populated with hand-written raw SQL
/// `INSERT ... ON CONFLICT(pk) DO UPDATE` (one upsert command per entity row) instead of going
/// through EF's change tracker. Everything the SQL needs (table/column names, primary key, the
/// SnapshotId shadow FK, value converters) is derived from the EF model, so no per-entity code is
/// required.
/// </summary>
internal class FastProjection
{
    private readonly HarmonyConfig _crdtConfig;

    public FastProjection(IOptions<HarmonyConfig> crdtConfig)
    {
        _crdtConfig = crdtConfig.Value;
    }

    public async Task AddSnapshotsRawAsync(
        ICrdtDbContext dbContext,
        IReadOnlyCollection<ObjectSnapshot> snapshots,
        Func<IReadOnlyCollection<ObjectSnapshot>, ValueTask>? afterProjectedWrite = null)
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

            if (_crdtConfig.EnableProjectedTables)
            {
                var latest = LatestPerEntity(snapshots);
                await ProjectAsync(dbContext, latest);
                if (latest.Count > 0 && afterProjectedWrite is not null)
                {
                    await afterProjectedWrite(latest.Values);
                }
            }

            if (ownTransaction is not null) await ownTransaction.CommitAsync();
        }
        finally
        {
            if (ownTransaction is not null) await ownTransaction.DisposeAsync();
        }
    }

    private static Dictionary<Guid, ObjectSnapshot> LatestPerEntity(
        IReadOnlyCollection<ObjectSnapshot> snapshots)
    {
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
        return latest;
    }

    private async Task ProjectAsync(
        ICrdtDbContext dbContext,
        Dictionary<Guid, ObjectSnapshot> latest)
    {
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
            // within a single type's batch, order rows so a row referenced by another row's
            // self-FK (e.g. Word.AntonymId -> Word.Id) is inserted before the referencing row.
            live = OrderRowsBySelfReference(info, live);
            await UpsertPerQueryAsync(connection, transaction, info, live);
        }
    }

    /// <summary>
    /// Topologically orders same-type rows by their self-referencing FK so a referenced row is
    /// upserted before the row that points at it. Cyclic self-references (which cannot be ordered)
    /// are left in their original relative order and may still fail the FK check.
    /// </summary>
    private static List<ObjectSnapshot> OrderRowsBySelfReference(ProjectedTableInfo info, List<ObjectSnapshot> rows)
    {
        if (info.SelfReferenceProperties.Count == 0 || rows.Count < 2) return rows;

        var byKey = new Dictionary<object, ObjectSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            var key = GetClrValue(info.PrimaryKey.Property, row.Entity.DbObject);
            if (key is not null) byKey[key] = row;
        }

        var ordered = new List<ObjectSnapshot>(rows.Count);
        var visited = new HashSet<ObjectSnapshot>();

        void Visit(ObjectSnapshot row)
        {
            if (!visited.Add(row)) return; // also breaks self-reference cycles
            var dbObject = row.Entity.DbObject;
            foreach (var selfRef in info.SelfReferenceProperties)
            {
                var reference = GetClrValue(selfRef, dbObject);
                if (reference is null) continue;
                if (byKey.TryGetValue(reference, out var principal) && !ReferenceEquals(principal, row))
                    Visit(principal);
            }
            ordered.Add(row);
        }

        foreach (var row in rows) Visit(row);
        return ordered;
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
        var raw = GetClrValue(column.Property, dbObject);
        return column.Converter is null ? raw : column.Converter.ConvertToProvider(raw);
    }

    private static object? GetClrValue(IProperty property, object dbObject)
        => property.PropertyInfo is { } pi ? pi.GetValue(dbObject) : property.FieldInfo?.GetValue(dbObject);

    private static object ToParameterValue(object? value) => value ?? DBNull.Value;

    // ---- model metadata ---------------------------------------------------------------------

    private ProjectedTableInfo GetTableInfo(ICrdtDbContext dbContext, Type clrType, ISqlGenerationHelper sqlHelper)
    {
        return _crdtConfig.ProjectedTableInfoCache.GetOrAdd(
            (dbContext.Model, clrType),
            key => BuildTableInfo(dbContext, key.Type, sqlHelper));
    }

    private static ProjectedTableInfo BuildTableInfo(ICrdtDbContext dbContext, Type clrType, ISqlGenerationHelper sqlHelper)
    {
        var entityType = dbContext.Model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"No EF entity type found for projected type {clrType.Name}");
        // FastProjection sources every column value from the projected CLR instance, so it cannot
        // reproduce EF's write semantics for TPH inheritance / discriminator columns. Reject them
        // up front rather than silently writing wrong or null values.
        if (entityType.BaseType is not null
            || entityType.GetDerivedTypes().Any()
            || entityType.FindDiscriminatorProperty() is not null)
            throw new NotSupportedException(
                $"Fast projection does not support TPH inheritance or discriminator columns for projected type {clrType.Name}.");
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
            var isShadowSnapshotId = property.Name == ObjectSnapshot.ShadowRefName;
            // Any shadow property other than the SnapshotId FK has no CLR value to source from the
            // projected instance, so it would silently project as null. Reject rather than corrupt.
            if (!isShadowSnapshotId && property.IsShadowProperty())
                throw new NotSupportedException(
                    $"Fast projection cannot source shadow property '{property.Name}' on projected type {clrType.Name}; " +
                    $"only the '{ObjectSnapshot.ShadowRefName}' shadow FK is supported.");
            columns.Add(new ColumnInfo(
                sqlHelper.DelimitIdentifier(columnName),
                property,
                // Reproduce EF's relational write semantics by using the converter from the property's
                // relational type mapping, which includes converters supplied by the type mapping and
                // not just those returned by IProperty.GetValueConverter() (e.g. HasConversion).
                isShadowSnapshotId ? null : property.GetRelationalTypeMapping().Converter,
                isShadowSnapshotId,
                pkPropertyNames.Contains(property.Name)));
        }

        var pkColumns = columns.Where(c => c.IsPrimaryKey).ToList();
        if (pkColumns.Count != 1)
            throw new NotSupportedException($"Fast projection requires a single-column primary key for {clrType.Name}");
        var pk = pkColumns[0];

        // Self-referencing FKs whose principal key is this table's primary key (e.g. Word.AntonymId
        // -> Word.Id). Their dependent value can be compared to another row's PK to order inserts.
        var primaryKey = entityType.FindPrimaryKey();
        var selfReferenceProperties = entityType.GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType == entityType
                && fk.PrincipalKey == primaryKey
                && fk.Properties.Count == 1)
            .Select(fk => fk.Properties[0])
            .ToList();

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
            selfReferenceProperties,
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

    internal sealed record ColumnInfo(
        string DelimitedName,
        IProperty Property,
        ValueConverter? Converter,
        bool IsShadowSnapshotId,
        bool IsPrimaryKey);

    internal sealed record ProjectedTableInfo(
        IReadOnlyList<ColumnInfo> Columns,
        ColumnInfo PrimaryKey,
        IReadOnlyList<IProperty> SelfReferenceProperties,
        string InsertSql,
        string DeleteSql);
}
