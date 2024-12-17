namespace SIL.Harmony.Resource;

/// <summary>
/// a non CRDT object that tracks local resource files
/// </summary>
public class LocalResource
{
    public required Guid Id { get; set; }
    //could probably be a URL if working in an electron context, not sure what would be best here. It depends on the app
    public required string LocalPath { get; set; }

    public bool FileExists()
    {
        return File.Exists(LocalPath);
    }
}
