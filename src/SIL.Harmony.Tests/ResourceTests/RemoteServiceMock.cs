namespace SIL.Harmony.Tests.ResourceTests;

public class RemoteServiceMock : IRemoteResourceService
{
    public static readonly string RemotePath = Directory.CreateTempSubdirectory("RemoteServiceMock").FullName;

    /// <summary>
    /// directly creates a remote resource
    /// </summary>
    /// <returns>the remote id</returns>
    public string CreateRemoteResource(string contents)
    {
        var filePath = Path.Combine(RemotePath, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    public Task<DownloadResult> DownloadResource(string remoteId, string localResourceCachePath)
    {
        var fileName = Path.GetFileName(remoteId);
        var localPath = Path.Combine(localResourceCachePath, fileName);
        Directory.CreateDirectory(localResourceCachePath);
        File.Copy(remoteId, localPath);
        return Task.FromResult(new DownloadResult(localPath));
    }
    
    private readonly Queue<string> _throwOnUpload = new();

    public async Task<UploadResult> UploadResource(Guid resourceId, string localPath)
    {
        await Task.Yield();//yield back to the scheduler to emulate how exceptions are thrown
        if (_throwOnUpload.TryPeek(out var throwOnUpload))
        {
            if (throwOnUpload == localPath)
            {
                _throwOnUpload.Dequeue();
                throw new Exception($"Simulated upload failure for {localPath}");
            }
        }
        var remoteId = Path.Combine(RemotePath, Path.GetFileName(localPath));
        File.Copy(localPath, remoteId);
        return new UploadResult(remoteId);
    }

    public void ThrowOnUpload(string localPath)
    {
        _throwOnUpload.Enqueue(localPath);
    }

    public string ReadFile(string remoteId)
    {
        return File.ReadAllText(remoteId);
    }

    public IEnumerable<string> ListRemoteFiles()
    {
        return Directory.GetFiles(RemotePath);
    }
}
