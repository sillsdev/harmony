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
    /// <summary>
    /// The version of the change type when the change was authored: how many model versions list its type.
    /// Changes from before model versions existed are version 0.
    /// </summary>
    public int Version { get; set; }
}
