using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Resource;

namespace SIL.Harmony.Config;

public delegate ValueTask BeforeSaveObjectDelegate(object obj, ObjectSnapshot snapshot);

public class HarmonyConfig
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
    public ChangeTypeListBuilder ChangeTypeListBuilder { get; } = new();
    public IReadOnlyList<RegisteredChangeType> ChangeTypes => ChangeTypeListBuilder.Types;
    public ObjectTypeListBuilder ObjectTypeListBuilder { get; } = new();
    public IEnumerable<Type> ObjectTypes => ObjectTypeListBuilder.AdapterProviders.SelectMany(p => p.GetRegistrations().Select(r => r.ObjectDbType));
    public JsonSerializerOptions JsonSerializerOptions => _lazyJsonSerializerOptions.Value;
    private readonly JsonOptionsBuilder _jsonOptionsBuilder = new();
    private readonly Lazy<JsonSerializerOptions> _lazyJsonSerializerOptions;
    private readonly Lazy<ChangeDiscriminatorMaps> _lazyChangeDiscriminatorMaps;

    /// <summary>
    /// Cache of derived projected-table SQL metadata (keyed by projected CLR type), used by
    /// <see cref="FastProjection"/>. Stored on the config so it's shared across repositories and
    /// db contexts and only built once per type.
    /// </summary>
    internal ConcurrentDictionary<Type, FastProjection.ProjectedTableInfo> ProjectedTableInfoCache { get; } = new();

    public HarmonyConfig()
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
        _jsonOptionsBuilder.ApplyTo(options);
        return options;
    }

    /// <summary>
    /// Registers a callback to customize <see cref="JsonSerializerOptions"/> before they are frozen.
    /// Callbacks run after Harmony's type resolver and change converter are configured.
    /// Replacing <see cref="JsonSerializerOptions.TypeInfoResolver"/> or removing the change converter will break serialization.
    /// </summary>
    public void ConfigureJsonOptions(Action<JsonSerializerOptions> configure)
    {
        _jsonOptionsBuilder.Configure(configure);
    }

    private ChangeDiscriminatorMaps BuildChangeDiscriminatorMaps()
    {
        ChangeTypeListBuilder.Freeze();

        var knownChanges = new Dictionary<string, Type>(ChangeTypeListBuilder.Types.Count);
        var discriminators = new Dictionary<Type, string>(ChangeTypeListBuilder.Types.Count);
        foreach (var changeType in ChangeTypeListBuilder.Types)
        {
            knownChanges.Add(changeType.Discriminator, changeType.Type);
            discriminators.Add(changeType.Type, changeType.Discriminator);
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
