using System.Text.Json.Serialization;

namespace SIL.Harmony.Core;

public class ChangeEntity<TChange>
{
    [JsonConstructor]
    public ChangeEntity()
    {
    }

    public required int Index { get; set; }
    public required Guid CommitId { get; set; }
    public required Guid EntityId { get; set; }
    public required TChange Change { get; set; }
}
