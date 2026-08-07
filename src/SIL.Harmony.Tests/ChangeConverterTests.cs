using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;

namespace SIL.Harmony.Tests;

public class ChangeConverterTests
{
    private static JsonSerializerOptions SampleOptions(UnknownChangeHandling handling = UnknownChangeHandling.Fallback) =>
        new ServiceCollection()
            .AddCrdtDataSample(":memory:")
            .Configure<HarmonyConfig>(c => c.UnknownChangeHandling = handling)
            .BuildServiceProvider()
            .GetRequiredService<JsonSerializerOptions>();

    [Fact]
    public void Happy_path_deserializes_to_concrete_change()
    {
        var options = SampleOptions();
        var entityId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        IChange change = new SetWordTextChange(entityId, "hello");

        var json = JsonSerializer.Serialize(change, options);
        var roundTripped = JsonSerializer.Deserialize<IChange>(json, options);

        roundTripped.Should().BeOfType<SetWordTextChange>()
            .Which.Text.Should().Be("hello");
        roundTripped!.EntityId.Should().Be(entityId);
        json.Should().StartWith("{\"$type\":\"SetWordTextChange\"");
    }

    [Fact]
    public void Unknown_type_deserializes_to_OpaqueChange()
    {
        var options = SampleOptions();
        var json = """
            {"$type":"SetWordPriorityChange","EntityId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","Priority":7}
            """;

        var change = JsonSerializer.Deserialize<IChange>(json, options);

        var opaque = change.Should().BeOfType<OpaqueChange>().Subject;
        opaque.TypeName.Should().Be("SetWordPriorityChange");
        opaque.RawJson.GetProperty("Priority").GetInt32().Should().Be(7);
        opaque.SupportsNewEntity().Should().BeFalse();
        opaque.SupportsApplyChange().Should().BeFalse();
        opaque.EntityType.Should().BeNull();
    }

    [Fact]
    public void Unknown_type_throws_when_fallback_disabled()
    {
        var options = SampleOptions(UnknownChangeHandling.Throw);
        var json = """
            {"$type":"SetWordPriorityChange","EntityId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","Priority":7}
            """;

        var act = () => JsonSerializer.Deserialize<IChange>(json, options);

        act.Should().Throw<JsonException>().WithMessage("*SetWordPriorityChange*");
    }

    [Fact]
    public void OpaqueChange_round_trips_original_discriminator()
    {
        var options = SampleOptions();
        var json = """
            {"$type":"SetWordPriorityChange","EntityId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","Priority":7}
            """;

        var change = JsonSerializer.Deserialize<IChange>(json, options)!;
        var rewritten = JsonSerializer.Serialize(change, options);

        rewritten.Should().Contain("\"$type\":\"SetWordPriorityChange\"");
        rewritten.Should().Contain("\"Priority\":7");
        rewritten.Should().NotContain("OpaqueChange");
    }

    [Fact]
    public void Mixed_commit_round_trips_known_and_opaque_changes()
    {
        var options = SampleOptions();
        var entityId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var commit = new Commit
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = new HybridDateTime(DateTimeOffset.UtcNow, 0),
        };
        commit.ChangeEntities.Add(new ChangeEntity<IChange>
        {
            Index = 0,
            CommitId = commit.Id,
            EntityId = entityId,
            Change = new SetWordTextChange(entityId, "hello")
        });

        var json = JsonSerializer.Serialize(commit, options);
        // Inject an unknown change as if from a newer client.
        json = json.Replace(
            "\"ChangeEntities\":[",
            """
            "ChangeEntities":[{"Index":1,"CommitId":"00000000-0000-0000-0000-000000000000","EntityId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","Change":{"$type":"SetWordPriorityChange","Priority":3}},
            """);

        var roundTripped = JsonSerializer.Deserialize<Commit>(json, options)!;
        roundTripped.ChangeEntities.Should().HaveCount(2);
        roundTripped.ChangeEntities.Select(c => c.Change.GetType())
            .Should().BeEquivalentTo([typeof(OpaqueChange), typeof(SetWordTextChange)]);
    }

    [Fact]
    public void Requires_type_as_first_property()
    {
        var options = SampleOptions();
        var json = """
            {"Text":"hello","EntityId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","$type":"SetWordTextChange"}
            """;

        var act = () => JsonSerializer.Deserialize<IChange>(json, options);
        act.Should().Throw<JsonException>().WithMessage("*first property*");
    }

    [Fact]
    public void Deserialize_NonObjectToken_ThrowsExpectedStartObject()
    {
        var options = SampleOptions();

        var act = () => JsonSerializer.Deserialize<IChange>("[1,2,3]", options);
        act.Should().Throw<JsonException>().WithMessage("*StartObject*");
    }

    [Fact]
    public void Deserialize_EmptyObject_ThrowsExpectedPropertyName()
    {
        var options = SampleOptions();

        var act = () => JsonSerializer.Deserialize<IChange>("{}", options);
        act.Should().Throw<JsonException>().WithMessage("*property name*");
    }

    [Fact]
    public void Deserialize_NonStringDiscriminator_Throws()
    {
        var options = SampleOptions();
        var json = """{"$type":5,"EntityId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}""";

        var act = () => JsonSerializer.Deserialize<IChange>(json, options);
        act.Should().Throw<JsonException>().WithMessage("*string*discriminator*");
    }

    [Fact]
    public void OpaqueChange_ParsesEntityIdFromRawJson()
    {
        var options = SampleOptions();
        var entityId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var json = $$"""{"$type":"SetWordPriorityChange","EntityId":"{{entityId}}","Priority":7}""";

        var change = JsonSerializer.Deserialize<IChange>(json, options);

        change.Should().BeOfType<OpaqueChange>().Which.EntityId.Should().Be(entityId);
    }

    [Fact]
    public void OpaqueChange_MissingEntityId_DefaultsToEmpty()
    {
        var options = SampleOptions();
        var json = """{"$type":"SetWordPriorityChange","Priority":7}""";

        var change = JsonSerializer.Deserialize<IChange>(json, options);

        change.Should().BeOfType<OpaqueChange>().Which.EntityId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void OpaqueChange_PreservesNestedJson_OnRoundTrip()
    {
        var options = SampleOptions();
        var json = """
            {"$type":"SetWordPriorityChange","EntityId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","Data":{"nested":[1,2],"flag":true}}
            """;

        var change = JsonSerializer.Deserialize<IChange>(json, options)!;
        var opaque = change.Should().BeOfType<OpaqueChange>().Subject;
        opaque.RawJson.GetProperty("Data").GetProperty("flag").GetBoolean().Should().BeTrue();

        var rewritten = JsonSerializer.Serialize(change, options);
        rewritten.Should().Contain("\"nested\":[1,2]");
        rewritten.Should().Contain("\"flag\":true");
    }

    private static OpaqueChange NewOpaque() => new()
    {
        TypeName = "UnknownChange",
        RawJson = JsonDocument.Parse("{}").RootElement.Clone(),
    };

    [Fact]
    public void OpaqueChange_EntityType_IsNull()
    {
        //an unknown change has no known entity type on this client
        NewOpaque().EntityType.Should().BeNull();
    }

    [Fact]
    public async Task OpaqueChange_NewEntity_Throws()
    {
        var commit = new Commit
        {
            ClientId = Guid.NewGuid(),
            HybridDateTime = new HybridDateTime(DateTimeOffset.UtcNow, 0),
        };
        var act = async () => await NewOpaque().NewEntity(commit, null!);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task OpaqueChange_ApplyChange_IsNoOp()
    {
        //an unknown change applied via sync must not mutate anything or throw
        await NewOpaque().ApplyChange(null!, null!);
    }
}
