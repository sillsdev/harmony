namespace SIL.Harmony.Core;

public enum SyncStage
{
    FetchingChanges,
    FetchingChangesFinished,
    ApplyingChanges,
    ApplyingChangesFinished,
    UploadingResources,
    UploadingResourcesFinished,
    UploadingChanges,
    UploadingChangesFinished
}
