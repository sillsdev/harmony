using SIL.Harmony.Core;

namespace SIL.Harmony.Tests.ResourceTests;

public class RemoteServiceMock : IResourceService
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

    public Task<UploadResult> UploadResource(string localPath)
    {
        var remoteId = Path.Combine(RemotePath, Path.GetFileName(localPath));
        File.Copy(localPath, remoteId);
        return Task.FromResult(new UploadResult(remoteId));
    }

    public string ReadFile(string remoteId)
    {
        return File.ReadAllText(remoteId);
    }

    public IEnumerable<string> ListFiles()
    {
        return Directory.GetFiles(RemotePath);
    }
}
