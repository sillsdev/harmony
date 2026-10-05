using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Resource;

namespace SIL.Harmony.Config;

public delegate ValueTask BeforeSaveObjectDelegate(object obj, ObjectSnapshot snapshot);
public delegate ValueTask ProjectedEntitiesChangedDelegate(ProjectedEntityBatch batch);

public class HarmonyConfig
{
    /// <summary>
    /// recommended to increase query performance, as getting objects can just query the table for that object.
    /// it does however increase database size as now objects are stored both in snapshots and in their projected tables
    /// </summary>
    public bool EnableProjectedTables { get; set; } = true;
    public BeforeSaveObjectDelegate BeforeSaveObject { get; set; } = (o, snapshot) => ValueTask.CompletedTask;
    internal static readonly ProjectedEntitiesChangedDelegate DefaultOnProjectedEntitiesChanged =
        static _ => ValueTask.CompletedTask;

    public int PrefetchSnapshotsBreakpoint { get; set; } = 220; //not exactly sure the right number, but 200 is slower with the query, in release builds

    public ProjectedEntitiesChangedDelegate OnProjectedEntitiesChanged { get; set; } =
        DefaultOnProjectedEntitiesChanged;

    /// <summary>
    /// After adding any commit validate the commit history.
    /// Not great for performance but good for testing, so off by default.
    /// </summary>
    public bool AlwaysValidateCommits { get; set; }
    /// <summary>
    /// Bounds how far back a replay of an out-of-order commit resumes, in changes. Lower keeps more snapshots (more
    /// storage, cheaper replay); higher keeps fewer. The only dial in the snapshot checkpoint design.
    /// </summary>
    public int MaxChangesBetweenSnapshotCheckpoints { get; set; } = 100;
    /// <summary>
    /// Controls how an unknown <see cref="IChange"/> <c>$type</c> is handled during deserialization.
    /// Defaults to <see cref="UnknownChangeHandling.Throw"/>; set to <see cref="UnknownChangeHandling.Fallback"/>
    /// to preserve unknown changes as <see cref="OpaqueChange"/>.
    /// </summary>
    public UnknownChangeHandling UnknownChangeHandling { get; set; } = UnknownChangeHandling.Throw;
    public ChangeTypeListBuilder ChangeTypeListBuilder { get; } = new();
    public IReadOnlyList<RegisteredChangeType> ChangeTypes => ChangeTypeListBuilder.Types;
    /// <summary>
    /// The ordered list of model versions, see <see cref="ModelVersionBuilder"/>.
    /// </summary>
    public ModelVersionBuilder ModelVersionBuilder { get; } = new();
    public IReadOnlyList<ModelVersion> ModelVersions => ModelVersionBuilder.Versions;
    /// <summary>the number of model versions, 0 when the app declares none</summary>
    public int CurrentModelVersion => ModelVersions.Count;
    private readonly Lazy<FrozenDictionary<string, int>> _lazyChangeVersions;
    public ObjectTypeListBuilder ObjectTypeListBuilder { get; } = new();
    public IEnumerable<Type> ObjectTypes => ObjectTypeListBuilder.AdapterProviders.SelectMany(p => p.GetRegistrations().Select(r => r.ObjectDbType));
    public JsonSerializerOptions JsonSerializerOptions => _lazyJsonSerializerOptions.Value;
    private readonly JsonOptionsBuilder _jsonOptionsBuilder = new();
    private readonly Lazy<JsonSerializerOptions> _lazyJsonSerializerOptions;
    private readonly Lazy<ChangeDiscriminatorMaps> _lazyChangeDiscriminatorMaps;

    /// <summary>
    /// Cache of derived projected-table SQL metadata, used by <see cref="FastProjection"/>. Stored on
    /// the config so it's shared across repositories and db contexts. Keyed by <see cref="IModel"/>
    /// as well as CLR type because the cached metadata holds model-specific <c>IProperty</c>
    /// instances, table/column names, converters, and provider-delimited SQL: a single config can be
    /// paired with more than one EF model (multiple <see cref="ICrdtDbContext"/> types or providers),
    /// so keying by CLR type alone could hand one model metadata built from another.
    /// </summary>
    internal ConcurrentDictionary<(IModel Model, Type Type), FastProjection.ProjectedTableInfo> ProjectedTableInfoCache { get; } = new();

    public HarmonyConfig()
    {
        _lazyChangeDiscriminatorMaps = new Lazy<ChangeDiscriminatorMaps>(BuildChangeDiscriminatorMaps);
        _lazyChangeVersions = new Lazy<FrozenDictionary<string, int>>(BuildChangeVersions);
        _lazyJsonSerializerOptions = new Lazy<JsonSerializerOptions>(CreateJsonSerializerOptions);
    }

    private JsonSerializerOptions CreateJsonSerializerOptions()
    {

        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        ConfigureExternalJsonOptions(options);
        return options;
    }

    /// <summary>
    /// Configures <see cref="JsonSerializerOptions"/> for Harmony's serialization of <see cref="IChange"/> and <see cref="IObject"/> types.
    /// Also applies any callbacks registered via <see cref="ConfigureJsonOptions(Action{JsonSerializerOptions})"/>.
    /// </summary>
    public void ConfigureExternalJsonOptions(JsonSerializerOptions options)
    {
        var changeDiscriminators = _lazyChangeDiscriminatorMaps.Value;
        options.TypeInfoResolver = options.TypeInfoResolver?.WithAddedModifier(MakeJsonTypeModifier())
            ?? MakeJsonTypeResolver();
        options.Converters.Add(new PeekThenConcreteChangeConverter(changeDiscriminators.ByDiscriminator, UnknownChangeHandling));
        _jsonOptionsBuilder.ApplyTo(options);
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

    private FrozenDictionary<string, int> BuildChangeVersions()
    {
        ModelVersionBuilder.Freeze();
        return ModelVersions.SelectMany(v => v.ChangeTypes)
            .CountBy(t => t, StringComparer.Ordinal)
            .ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// The version a change of this type is authored with: how many model versions list the type.
    /// </summary>
    /// <param name="discriminator">the change type's discriminator, its <c>$type</c></param>
    public int ChangeVersion(string discriminator)
    {
        return _lazyChangeVersions.Value.GetValueOrDefault(discriminator);
    }

    internal int ChangeVersion(IChange change)
    {
        var discriminator = change is OpaqueChange opaque
            ? opaque.TypeName
            : _lazyChangeDiscriminatorMaps.Value.ByType.GetValueOrDefault(change.GetType());
        return discriminator is null ? 0 : ChangeVersion(discriminator);
    }

    /// <summary>
    /// A stable text form of <see cref="ModelVersions"/>, one line per version.
    /// Use it in a snapshot test so a released version can't be modified by accident: the snapshot may only grow at the end.
    /// </summary>
    public string DescribeModelVersions()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < ModelVersions.Count; i++)
        {
            var version = ModelVersions[i];
            builder.Append(i + 1)
                .Append(version.Major ? " major " : " ")
                .Append(version.Name)
                .Append(": ")
                .AppendJoin(", ", version.ChangeTypes)
                .Append('\n');
        }
        return builder.ToString();
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
