using System.Text.Json.Serialization;

namespace SIL.Harmony.Core;

public class CommitMetadata
{
    //well known metadata
    public string? AuthorName { get; set; }
    public string? AuthorId { get; set; }
    public string? ClientVersion { get; set; }
    /// <summary>
    /// used to store application specific metadata
    /// </summary>
    public Dictionary<string, string?> ExtraMetadata { get; set; } = new();

    public string? this[string key]
    {
        get => ExtraMetadata.GetValueOrDefault(key);
        set => ExtraMetadata[key] = value;
    }
}
