namespace SIL.Harmony.Resource;

/// <summary>
/// thrown by <see cref="ResourceService{TMetadata}.UploadPendingResources"/> when a pending resource fails to upload.
/// resources uploaded before the failure are still saved; <see cref="Uploaded"/> and <see cref="Remaining"/> report how far the batch got.
/// </summary>
public class ResourceUploadException(int uploaded, int remaining, Exception innerException)
    : Exception($"Failed to upload pending resources: {uploaded} uploaded, {remaining} remaining", innerException)
{
    public int Uploaded { get; } = uploaded;
    public int Remaining { get; } = remaining;
}
