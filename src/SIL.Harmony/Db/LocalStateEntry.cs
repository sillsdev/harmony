namespace SIL.Harmony.Db;

/// <summary>
/// a non CRDT key value pair that is local to this database and is never synced.
/// Keys starting with <c>harmony:</c> are used by Harmony itself, apps should use their own prefix.
/// </summary>
public class LocalStateEntry
{
    public required string Key { get; set; }
    /// <summary>json encoded value</summary>
    public required string Value { get; set; }
}
