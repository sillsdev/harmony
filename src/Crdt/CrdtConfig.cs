using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Crdt.Changes;
using Crdt.Db;
using Crdt.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crdt;

public class CrdtConfig
{
    public bool EnableProjectedTables { get; set; } = false;
    public ChangeTypeListBuilder ChangeTypeListBuilder { get; } = new();
    public ObjectTypeListBuilder ObjectTypeListBuilder { get; } = new();
    public JsonSerializerOptions JsonSerializerOptions => _lazyJsonSerializerOptions.Value;
    private readonly Lazy<JsonSerializerOptions> _lazyJsonSerializerOptions;

    public CrdtConfig()
    {
        _lazyJsonSerializerOptions = new Lazy<JsonSerializerOptions>(() => new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            TypeInfoResolver = MakeJsonTypeResolver()
        });
    }

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
        ChangeTypeListBuilder.Freeze();
        ObjectTypeListBuilder.Freeze();
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
    private bool _frozen;

    public void Freeze()
    {
        _frozen = true;
    }

    private void CheckFrozen()
    {
        if (_frozen) throw new InvalidOperationException("ObjectTypeListBuilder is frozen");
    }
    internal List<JsonDerivedType> Types { get; } = [];

    public ChangeTypeListBuilder Add<TDerived>() where TDerived : IChange, IPolyType
    {
        CheckFrozen();
        if (Types.Any(t => t.DerivedType == typeof(TDerived))) return this;
        Types.Add(new JsonDerivedType(typeof(TDerived), TDerived.TypeName));
        return this;
    }
}

public class ObjectTypeListBuilder
{
    private bool _frozen;
    public void Freeze()
    {
        _frozen = true;
    }

    private void CheckFrozen()
    {
        if (_frozen) throw new InvalidOperationException("ObjectTypeListBuilder is frozen");
    }

    internal List<JsonDerivedType> Types { get; } = [];

    internal List<Action<ModelBuilder, CrdtConfig>> ModelConfigurations { get; } = [];

    public ObjectTypeListBuilder AddDbModelConfig(Action<ModelBuilder> modelConfiguration)
    {
        CheckFrozen();
        ModelConfigurations.Add((builder, _) => modelConfiguration(builder));
        return this;
    }

  
    public ObjectTypeListBuilder Add<TDerived>(Action<EntityTypeBuilder<TDerived>>? configureDb = null)
        where TDerived : class, IObjectBase
    {
        CheckFrozen();
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
