using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SIL.Harmony.Changes;

/// <summary>
/// Owns <see cref="IChange"/> discrimination. Requires <c>$type</c> as the first JSON property
/// (matching synthetic write order from <see cref="Config.HarmonyConfig"/>).
/// Known discriminators deserialize via cached concrete <see cref="JsonTypeInfo"/>;
/// unknown → <see cref="OpaqueChange"/> preserving the raw payload.
/// </summary>
internal sealed class PeekThenConcreteChangeConverter : JsonConverter<IChange>
{
    private readonly KnownType[] _known;
    private readonly byte[] _discriminatorPropertyUtf8;

    public PeekThenConcreteChangeConverter(IReadOnlyDictionary<string, Type> known)
    {
        _discriminatorPropertyUtf8 = Encoding.UTF8.GetBytes(CrdtConstants.ChangeDiscriminatorProperty);
        _known = known.Select(kv => new KnownType(Encoding.UTF8.GetBytes(kv.Key), kv.Value)).ToArray();
    }

    public override IChange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject");

        // Checkpoint: restore for full-object deserialize / opaque capture after peeking $type.
        var checkpoint = reader;

        if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException("Expected property name");

        if (!reader.ValueTextEquals(_discriminatorPropertyUtf8))
            throw new JsonException(
                $"IChange requires \"{CrdtConstants.ChangeDiscriminatorProperty}\" as the first property");

        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected string {CrdtConstants.ChangeDiscriminatorProperty} discriminator");

        if (!TryFindKnown(ref reader, out var knownIndex, out var unknownTypeName))
        {
            reader = checkpoint;
            return ReadOpaque(ref reader, unknownTypeName!);
        }

        ref var known = ref _known[knownIndex];
        var typeInfo = known.EnsureTypeInfo(options);

        // Real change types use parameterized constructors / get-only props — let STJ materialize.
        reader = checkpoint;
        return (IChange)(JsonSerializer.Deserialize(ref reader, typeInfo)
            ?? throw new JsonException($"null {known.ClrType.Name}"));
    }

    public override void Write(Utf8JsonWriter writer, IChange value, JsonSerializerOptions options)
    {
        if (value is OpaqueChange opaque)
        {
            opaque.RawJson.WriteTo(writer);
            return;
        }

        // Concrete runtime type: synthetic $type comes from JsonTypeInfo modifier.
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    private static OpaqueChange ReadOpaque(ref Utf8JsonReader reader, string typeName)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement.Clone();
        return new OpaqueChange
        {
            TypeName = typeName,
            EntityId = element.TryGetProperty(nameof(IChange.EntityId), out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetGuid()
                : default,
            RawJson = element
        };
    }

    private bool TryFindKnown(ref Utf8JsonReader reader, out int index, out string? unknownTypeName)
    {
        for (var i = 0; i < _known.Length; i++)
        {
            if (reader.ValueTextEquals(_known[i].Utf8Discriminator))
            {
                index = i;
                unknownTypeName = null;
                return true;
            }
        }

        index = -1;
        unknownTypeName = reader.GetString();
        return false;
    }

    private struct KnownType
    {
        public KnownType(byte[] utf8Discriminator, Type clrType)
        {
            Utf8Discriminator = utf8Discriminator;
            ClrType = clrType;
        }

        public byte[] Utf8Discriminator { get; }
        public Type ClrType { get; }
        private JsonTypeInfo? _typeInfo;

        public JsonTypeInfo EnsureTypeInfo(JsonSerializerOptions options)
        {
            if (_typeInfo is not null && ReferenceEquals(_typeInfo.Options, options))
                return _typeInfo;

            _typeInfo = options.GetTypeInfo(ClrType);
            return _typeInfo;
        }
    }
}
