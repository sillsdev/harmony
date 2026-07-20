using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Adapters;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Entities;
using SIL.Harmony.Resource;

namespace SIL.Harmony;

public delegate ValueTask BeforeSaveObjectDelegate(object obj, ObjectSnapshot snapshot);

public class CrdtConfig
{
    /// <summary>
    /// recommended to increase query performance, as getting objects can just query the table for that object.
    /// it does however increase database size as now objects are stored both in snapshots and in their projected tables
    /// </summary>
    public bool EnableProjectedTables { get; set; } = true;
    public BeforeSaveObjectDelegate BeforeSaveObject { get; set; } = (o, snapshot) => ValueTask.CompletedTask;
    /// <summary>
    /// after adding any commit validate the commit history, not great for performance but good for testing.
    /// </summary>
    public bool AlwaysValidateCommits { get; set; } = true;
    /// <summary>
    /// Selects which stored commits are applied when materializing snapshots.
    /// Default includes every commit (unchanged behaviour). Commits are still persisted and synced regardless.
    /// </summary>
    public ICommitMaterializationFilter CommitMaterializationFilter { get; set; } =
        IncludeAllCommitsFilter.Instance;
    /// <summary>
    /// When true, authoring while checked out on a tag writes to main instead of being rejected.
    /// Only relevant when using SIL.Harmony.Refs (RefsDataModel). Default false.
    /// </summary>
    public bool AllowAuthoringOnTagToMain { get; set; }
    public ChangeTypeListBuilder ChangeTypeListBuilder { get; } = new();
    public IEnumerable<Type> ChangeTypes => ChangeTypeListBuilder.Types.Select(t => t.DerivedType);
    public ObjectTypeListBuilder ObjectTypeListBuilder { get; } = new();
    public IEnumerable<Type> ObjectTypes => ObjectTypeListBuilder.AdapterProviders.SelectMany(p => p.GetRegistrations().Select(r => r.ObjectDbType));
    public JsonSerializerOptions JsonSerializerOptions => _lazyJsonSerializerOptions.Value;
    private readonly Lazy<JsonSerializerOptions> _lazyJsonSerializerOptions;
    private readonly Lazy<ChangeDiscriminatorMaps> _lazyChangeDiscriminatorMaps;

    public CrdtConfig()
    {
        _lazyChangeDiscriminatorMaps = new Lazy<ChangeDiscriminatorMaps>(BuildChangeDiscriminatorMaps);
        _lazyJsonSerializerOptions = new Lazy<JsonSerializerOptions>(CreateJsonSerializerOptions);
    }

    private JsonSerializerOptions CreateJsonSerializerOptions()
    {
        var changeDiscriminators = _lazyChangeDiscriminatorMaps.Value;

        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            TypeInfoResolver = MakeJsonTypeResolver()
        };
        options.Converters.Add(new PeekThenConcreteChangeConverter(changeDiscriminators.ByDiscriminator));
        return options;
    }

    private ChangeDiscriminatorMaps BuildChangeDiscriminatorMaps()
    {
        ChangeTypeListBuilder.Freeze();

        var knownChanges = new Dictionary<string, Type>(ChangeTypeListBuilder.Types.Count);
        var discriminators = new Dictionary<Type, string>(ChangeTypeListBuilder.Types.Count);
        foreach (var derived in ChangeTypeListBuilder.Types)
        {
            if (derived.TypeDiscriminator is not string discriminator)
                throw new InvalidOperationException(
                    $"Change type {derived.DerivedType} must use a string $type discriminator");

            knownChanges.Add(discriminator, derived.DerivedType);
            discriminators.Add(derived.DerivedType, discriminator);
        }

        return new ChangeDiscriminatorMaps(knownChanges, discriminators);
    }

    private sealed record ChangeDiscriminatorMaps(
        IReadOnlyDictionary<string, Type> ByDiscriminator,
        IReadOnlyDictionary<Type, string> ByType);

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
        var changeTypeDiscriminators = _lazyChangeDiscriminatorMaps.Value.ByType;

        // IChange polymorphism is owned by PeekThenConcreteChangeConverter — do not set PolymorphismOptions.
        if (typeInfo.Kind == JsonTypeInfoKind.Object
            && changeTypeDiscriminators.TryGetValue(typeInfo.Type, out var discriminator))
        {
            AddSyntheticTypeDiscriminator(typeInfo, discriminator);
        }

        if (ObjectTypeListBuilder.JsonTypes?.TryGetValue(typeInfo.Type, out var types) == true)
        {
            if (typeInfo.PolymorphismOptions is null) typeInfo.PolymorphismOptions = new();
            foreach (var type in types)
            {
                typeInfo.PolymorphismOptions!.DerivedTypes.Add(type);
            }
        }
    }

    /// <summary>
    /// Serialize-only <c>$type</c> on concrete change types so write stays a plain concrete serialize
    /// (converter Write does not inject the discriminator). Order forces <c>$type</c> first for the read path.
    /// </summary>
    private static void AddSyntheticTypeDiscriminator(JsonTypeInfo typeInfo, string discriminator)
    {
        var typeName = discriminator;
        var prop = typeInfo.CreateJsonPropertyInfo(typeof(string), CrdtConstants.ChangeDiscriminatorProperty);
        prop.Get = _ => typeName;
        prop.Order = int.MinValue;
        typeInfo.Properties.Add(prop);
    }

    public bool RemoteResourcesEnabled { get; private set; }
    public Type? RemoteResourceMetadataType { get; private set; }
    public string LocalResourceCachePath { get; set; } = Path.GetFullPath("./localResourceCache");
    public string FailedSyncOutputPath { get; set; } = Path.GetFullPath("./failedSyncs");
    public void AddRemoteResourceEntity<TMetadata>(string? cachePath = null)
        where TMetadata : class
    {
        RemoteResourcesEnabled = true;
        RemoteResourceMetadataType = typeof(TMetadata);
        LocalResourceCachePath = cachePath ?? LocalResourceCachePath;
        ObjectTypeListBuilder.DefaultAdapter().Add<RemoteResource<TMetadata>>(builder =>
        {
            builder.ToTable("RemoteResource");
            builder.Property(r => r.Metadata)
                .HasColumnType("jsonb")
                .HasConversion(
                    m => JsonSerializer.Serialize(m, (JsonSerializerOptions?)null),
                    json => string.IsNullOrEmpty(json)
                        ? null
                        : JsonSerializer.Deserialize<TMetadata>(json, (JsonSerializerOptions?)null)
                );
        });
        ChangeTypeListBuilder.Add<RemoteResourceUploadedChange<TMetadata>>();
        ChangeTypeListBuilder.Add<CreateRemoteResourceChange<TMetadata>>();
        ChangeTypeListBuilder.Add<CreateRemoteResourcePendingUploadChange<TMetadata>>();
        ChangeTypeListBuilder.Add<SetRemoteResourceMetadataChange<TMetadata>>();
        ChangeTypeListBuilder.Add<DeleteRemoteResourceChange<TMetadata>>();
        ObjectTypeListBuilder.ModelConfigurations.Add((builder, config) =>
        {
            var entity = builder.Entity<LocalResource>();
            entity.HasKey(lr => lr.Id);
            entity.Property(lr => lr.LocalPath);
        });
    }
}

public class ChangeTypeListBuilder
{
    private bool _frozen;

    /// <summary>
    /// we call freeze when the builder is used to create a json serializer options, as it is not possible to add new types after that.
    /// </summary>
    public void Freeze()
    {
        _frozen = true;
    }

    private void CheckFrozen()
    {
        if (_frozen) throw new InvalidOperationException($"{nameof(ChangeTypeListBuilder)} is frozen");
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

    /// <summary>
    /// we call freeze when the builder is used to create a json serializer options, as it is not possible to add new types after that.
    /// </summary>
    public void Freeze()
    {
        if (_frozen) return;
        _frozen = true;
        foreach (var registration in AdapterProviders.SelectMany(a => a.GetRegistrations()))
        {
            ModelConfigurations.Add((builder, config) =>
            {
                if (!config.EnableProjectedTables) return;
                var entity = registration.EntityBuilder(builder);
                entity.HasOne(typeof(ObjectSnapshot))
                    .WithOne()
                    .HasForeignKey(registration.ObjectDbType, ObjectSnapshot.ShadowRefName)
                    .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }

    internal void CheckFrozen()
    {
        if (_frozen) throw new InvalidOperationException($"{nameof(ObjectTypeListBuilder)} is frozen");
    }

    internal Dictionary<Type, List<JsonDerivedType>> JsonTypes { get; } = [];
    internal List<Action<ModelBuilder, CrdtConfig>> ModelConfigurations { get; } = [];
    internal List<IObjectAdapterProvider> AdapterProviders { get; } = [];

    public DefaultAdapterProvider DefaultAdapter()
    {
        CheckFrozen();
        if (AdapterProviders.OfType<DefaultAdapterProvider>().SingleOrDefault() is { } adapter) return adapter;
        adapter = new DefaultAdapterProvider(this);
        AdapterProviders.Add(adapter);
        return adapter;
    }

    /// <summary>
    /// add a custom adapter for a common interface
    /// this is required as CRDT objects must express their references and have an Id property
    /// using a custom adapter allows your model to not take a dependency on Harmony
    /// </summary>
    /// <typeparam name="TCommonInterface">
    /// A common interface that all objects in your application implement
    /// which System.Text.Json will deserialize your objects to, they must support polymorphic deserialization
    /// </typeparam>
    /// <typeparam name="TAdapter">
    /// This adapter will be serialized and stored in the database,
    /// it should include the object it is adapting otherwise Harmony will not work
    /// </typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">when another adapter has already been added or the config has been frozen</exception>
    public CustomAdapterProvider<TCommonInterface, TAdapter> CustomAdapter<TCommonInterface, TAdapter>()
        where TCommonInterface : class where TAdapter : class, ICustomAdapter<TAdapter, TCommonInterface>, IPolyType
    {
        CheckFrozen();
        if (AdapterProviders.OfType<CustomAdapterProvider<TCommonInterface, TAdapter>>().SingleOrDefault() is { } adapter) return adapter;
        adapter = new CustomAdapterProvider<TCommonInterface, TAdapter>(this);
        AdapterProviders.Add(adapter);
        return adapter;
    }

    internal IObjectBase Adapt(object obj)
    {
        if (AdapterProviders is [{ } defaultAdapter])
        {
            return defaultAdapter.Adapt(obj);
        }

        foreach (var objectAdapterProvider in AdapterProviders)
        {
            if (objectAdapterProvider.CanAdapt(obj))
            {
                return objectAdapterProvider.Adapt(obj);
            }
        }
        throw new ArgumentException($"Unable to adapt object of type {obj.GetType()}");
    }
}

