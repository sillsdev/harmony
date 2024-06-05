using System.Text.Json.Serialization.Metadata;
using Crdt.Changes;
using Crdt.Db;
using Crdt.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crdt;

public class CrdtConfig
{
    /// <summary>
    /// recommended to increase query performance, as getting objects can just query the table for that object.
    /// it does however increase database size as now objects are stored both in snapshots and in their projected tables
    /// </summary>
    public bool EnableProjectedTables { get; set; } = true;
    public ChangeTypeListBuilder ChangeTypeListBuilder { get; } = new();
    public ObjectTypeListBuilder ObjectTypeListBuilder { get; } = new();

    public Action<JsonTypeInfo> MakeJsonTypeModifier()
    {
        return JsonTypeModifier;
    }

    public IJsonTypeInfoResolver MakeJsonTypeResolver()
    {
        return new DefaultJsonTypeInfoResolver
        {
            Modifiers = { MakeJsonTypeModifier() }
        };
    }

    private void JsonTypeModifier(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type == typeof(IChange))
        {
            foreach (var type in ChangeTypeListBuilder.Types)
            {
                typeInfo.PolymorphismOptions!.DerivedTypes.Add(type);
            }
        }

        if (typeInfo.Type == typeof(IObjectBase))
        {
            foreach (var type in ObjectTypeListBuilder.Types)
            {
                typeInfo.PolymorphismOptions!.DerivedTypes.Add(type);
            }
        }
    }
}

public class ChangeTypeListBuilder
{
    internal List<JsonDerivedType> Types { get; } = [];

    public ChangeTypeListBuilder Add<TDerived>() where TDerived : IChange, IPolyType
    {
        if (Types.Any(t => t.DerivedType == typeof(TDerived))) return this;
        Types.Add(new JsonDerivedType(typeof(TDerived), TDerived.TypeName));
        return this;
    }
}

public class ObjectTypeListBuilder
{
    internal List<JsonDerivedType> Types { get; } = [];

    internal List<Action<ModelBuilder, CrdtConfig>> ModelConfigurations { get; } = [];
    public List<Action<ModelConfigurationBuilder>> ModelConventions { get; } = [];

    public ObjectTypeListBuilder AddDbModelConvention(Action<ModelConfigurationBuilder> modelConvention)
    {
        ModelConventions.Add(modelConvention);
        return this;
    }

    public ObjectTypeListBuilder AddDbModelConfig(Action<ModelBuilder> modelConfiguration)
    {
        ModelConfigurations.Add((builder, _) => modelConfiguration(builder));
        return this;
    }

    public ObjectTypeListBuilder Add<TDerived>(Action<EntityTypeBuilder<TDerived>>? configureDb = null)
        where TDerived : class, IObjectBase
    {
        if (Types.Any(t => t.DerivedType == typeof(TDerived))) throw new InvalidOperationException($"Type {typeof(TDerived)} already added");
        Types.Add(new JsonDerivedType(typeof(TDerived), TDerived.TypeName));
        ModelConfigurations.Add((builder, config) =>
        {
            if (!config.EnableProjectedTables) return;
            var baseType = typeof(TDerived).BaseType;
            if (baseType is not null)
                builder.Ignore(baseType);
            var entity = builder.Entity<TDerived>();
            entity.HasBaseType((Type)null!);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id);
            entity.HasOne<ObjectSnapshot>()
                .WithOne()
                .HasForeignKey<TDerived>(ObjectSnapshot.ShadowRefName)
            //set null otherwise it will cascade delete, which would happen whenever snapshots are deleted
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(e => e.DeletedAt);
            entity.Ignore(e => e.TypeName);
            configureDb?.Invoke(entity);
        });
        return this;
    }
}
