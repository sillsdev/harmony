using System.Text.Json.Serialization;

namespace Crdt.Core;

public class CommitMetadata
{
    //well known metadata
    public string? AuthorName { get; set; }
    public string? AuthorId { get; set; }
    public Dictionary<string, string?> ExtraMetadata { get; set; } = new();

    public string? this[string key]
    {
        get => ExtraMetadata.GetValueOrDefault(key);
        set => ExtraMetadata[key] = value;
    }
}
