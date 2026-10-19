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
    /// The model version the change was authored under: the number of the authoring app's latest model version,
    /// whether or not that version lists the change's type. Changes from before model versions existed are version 0.
    /// </summary>
    public int AuthoredModelVersion { get; set; }
}
