namespace SIL.Harmony.Core;

/// <summary>
/// interface to facilitate downloading of resources, typically implemented in application code
/// the remote Id is opaque to the CRDT lib and could be a URL or some other identifier provided by the backend
/// the local path returned for the application code to use as required, it could be a URL if needed also.
/// </summary>
public interface IRemoteResourceService
{
    /// <summary>
    /// instructs application code to download a resource from the remote server
    /// the service is responsible for downloading the resource and returning the local path
    /// </summary>
    /// <param name="remoteId">ID used to identify the remote resource, could be a URL</param>
    /// <param name="localResourceCachePath">path defined by the CRDT config where the resource should be stored</param>
    /// <returns>download result containing the path to the downloaded file, this is stored in the local db and not synced</returns>
    Task<DownloadResult> DownloadResource(string remoteId, string localResourceCachePath);
    /// <summary>
    /// upload a resource to the remote server
    /// </summary>
    /// <param name="localPath">full path to the resource on the local machine</param>
    /// <returns>an upload result with the remote id, the id will be stored and transmitted to other clients so they can also download the resource</returns>
    Task<UploadResult> UploadResource(string localPath);
}

public record DownloadResult(string LocalPath);
public record UploadResult(string RemoteId);
