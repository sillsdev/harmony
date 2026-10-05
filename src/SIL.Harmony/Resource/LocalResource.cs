namespace SIL.Harmony.Resource;

/// <summary>
/// a non CRDT object that tracks local resource files
/// </summary>
public class LocalResource
{
    public required Guid Id { get; set; }
    /// <summary>
    /// absolute path to the file on the local machine, resolved by Harmony when read from the database
    /// </summary>
    public required string LocalPath { get; set; }

    public bool FileExists()
    {
        return File.Exists(LocalPath);
    }
}
