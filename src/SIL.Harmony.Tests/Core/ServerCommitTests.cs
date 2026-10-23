using System.Text.Json;
using SIL.Harmony.Core;

namespace SIL.Harmony.Tests.Core;

public class ServerCommitTests
{
    // ASP.NET Core's defaults, which sync servers use to read uploaded commits
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private const string ValidJson = """
        {
          "id": "11111111-1111-1111-1111-111111111111",
          "clientId": "22222222-2222-2222-2222-222222222222",
          "hybridDateTime": { "dateTime": "2026-01-01T00:00:00+00:00", "counter": 3 },
          "changeEntities": [
            {
              "index": 0,
              "commitId": "11111111-1111-1111-1111-111111111111",
              "entityId": "33333333-3333-3333-3333-333333333333",
              "change": { "$type": "SomeChange", "text": "hello" }
            }
          ]
        }
        """;

    [Fact]
    public void CanRoundTripThroughJson()
    {
        var commit = JsonSerializer.Deserialize<ServerCommit>(ValidJson, WebOptions)!;
        commit.ClientId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        commit.HybridDateTime.Counter.Should().Be(3);

        var json = JsonSerializer.Serialize(commit, WebOptions);
        var roundTripped = JsonSerializer.Deserialize<ServerCommit>(json, WebOptions);
        JsonSerializer.Serialize(roundTripped, WebOptions).Should().Be(json);
    }

    [Theory]
    [InlineData("clientId")]
    [InlineData("hybridDateTime")]
    public void RejectsMissingRequiredProperty(string property)
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ValidJson)!;
        json.Remove(property);

        var act = () => JsonSerializer.Deserialize<ServerCommit>(JsonSerializer.Serialize(json), WebOptions);

        act.Should().Throw<JsonException>().WithMessage($"*{property}*");
    }
}
