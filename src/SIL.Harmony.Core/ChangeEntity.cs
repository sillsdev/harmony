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
    /// The model version the change was authored under: the number of the latest model version that listed its type
    /// in the authoring app. Changes from before model versions existed are version 0.
    /// </summary>
    public int ChangeVersion { get; set; }
}
