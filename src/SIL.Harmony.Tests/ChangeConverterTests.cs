using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;

namespace SIL.Harmony.Tests;

public class ChangeConverterTests
{
    private static JsonSerializerOptions SampleOptions() =>
        new ServiceCollection()
            .AddCrdtDataSample(":memory:")
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
}
