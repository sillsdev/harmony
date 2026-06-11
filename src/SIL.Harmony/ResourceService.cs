using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Helpers;
using SIL.Harmony.Resource;

namespace SIL.Harmony;

public class ResourceService
{
    private readonly CrdtRepositoryFactory _crdtRepositoryFactory;
    private readonly IOptions<CrdtConfig> _crdtConfig;
    private readonly DataModel _dataModel;
    private readonly ILogger<ResourceService> _logger;

    internal ResourceService(CrdtRepositoryFactory crdtRepositoryFactory, IOptions<CrdtConfig> crdtConfig, DataModel dataModel, ILogger<ResourceService> logger)
    {
        _crdtRepositoryFactory = crdtRepositoryFactory;
        _crdtConfig = crdtConfig;
        _dataModel = dataModel;
        _logger = logger;
    }

    private void ValidateResourcesSetup()
    {
        if (!_crdtConfig.Value.RemoteResourcesEnabled) throw new RemoteResourceNotEnabledException();
    }

    public async Task AddExistingRemoteResource(string resourcePath,
        Guid clientId,
        Guid resourceId, string remoteId)
    {
        ValidateResourcesSetup();
        var localResource = new LocalResource
        {
            Id = resourceId,
            LocalPath = Path.GetFullPath(resourcePath)
        };
        if (!localResource.FileExists()) throw new FileNotFoundException(localResource.LocalPath);

        await _dataModel.AddChange(clientId, new CreateRemoteResourceChange(localResource.Id, remoteId));
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        await repo.AddLocalResource(localResource);
    }

    public async Task<HarmonyResource> AddLocalResource(string resourcePath,
        Guid clientId,
        Guid id = default,
        IRemoteResourceService? resourceService = null)
    {
        ValidateResourcesSetup();
        var localResource = new LocalResource
        {
            Id = id == default ? Guid.NewGuid() : id,
            LocalPath = Path.GetFullPath(resourcePath)
        };
        if (!localResource.FileExists()) throw new FileNotFoundException(localResource.LocalPath);
        UploadResult? uploadResult = null;
        if (resourceService is not null)
        {
            try
            {

                uploadResult = await resourceService.UploadResource(localResource.Id, localResource.LocalPath);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error uploading resource {resourcePath}, resource will be marked as pending upload", localResource.LocalPath);
            }
        }

        if (uploadResult is not null)
        {
            await _dataModel.AddChange(clientId, new CreateRemoteResourceChange(localResource.Id, uploadResult.RemoteId));
        }
        else
        {
            await _dataModel.AddChange(clientId, new CreateRemoteResourcePendingUploadChange(localResource.Id));
        }

        await _crdtRepositoryFactory.Execute(repo => repo.AddLocalResource(localResource));
        return new HarmonyResource
        {
            Id = localResource.Id,
            RemoteId = uploadResult?.RemoteId,
            LocalPath = localResource.LocalPath
        };
    }

    public async Task<LocalResource[]> ListResourcesPendingUpload()
    {
        ValidateResourcesSetup();
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var remoteResources = await repo.GetCurrentObjects<RemoteResource>().Where(r => r.RemoteId == null).ToArrayAsync();
        var localResource = repo.LocalResourcesByIds(remoteResources.Select(r => r.Id));
        return await localResource.ToArrayAsync();
    }

    public async Task UploadPendingResources(Guid clientId, IRemoteResourceService remoteResourceService)
    {
        ValidateResourcesSetup();
        var pendingUploads = await ListResourcesPendingUpload();
        if (pendingUploads is []) return;
        var changes = new List<IChange>(pendingUploads.Length);
        try
        {
            foreach (var localResource in pendingUploads)
            {
                var uploadResult = await remoteResourceService.UploadResource(localResource.Id, localResource.LocalPath);
                changes.Add(new RemoteResourceUploadedChange(localResource.Id, uploadResult.RemoteId));
            }
        }
        finally
        {
            //if upload throws at any point we will at least save the changes that did get made.
            await _dataModel.AddChanges(clientId, changes);
        }
    }

    public async Task UploadPendingResource(Guid resourceId, Guid clientId, IRemoteResourceService remoteResourceService)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var localResource = await repo.GetLocalResource(resourceId) ??
                            throw new ArgumentException($"unable to find local resource with id {resourceId}");
        ValidateResourcesSetup();
        await UploadPendingResource(localResource, clientId, remoteResourceService);
    }

    public async Task UploadPendingResource(LocalResource localResource, Guid clientId, IRemoteResourceService remoteResourceService)
    {
        ValidateResourcesSetup();
        var uploadResult = await remoteResourceService.UploadResource(localResource.Id, localResource.LocalPath);
        await _dataModel.AddChange(clientId, new RemoteResourceUploadedChange(localResource.Id, uploadResult.RemoteId));
    }

    public async Task<RemoteResource[]> ListResourcesPendingDownload()
    {
        ValidateResourcesSetup();
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var localResourceIds = repo.LocalResourceIds();
        var remoteResources = await repo.GetCurrentObjects<RemoteResource>()
            .Where(r => r.RemoteId != null && !localResourceIds.Contains(r.Id))
            .ToArrayAsync();
        return remoteResources;
    }

    public async Task<LocalResource> DownloadResource(Guid resourceId, IRemoteResourceService remoteResourceService)
    {
        ValidateResourcesSetup();
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await DownloadResourceInternal(repo,
            await repo.GetCurrent<RemoteResource>(resourceId) ??
            throw new EntityNotFoundException("Unable to find remote resource"),
            remoteResourceService
        );
    }

    public async Task<LocalResource> DownloadResource(RemoteResource remoteResource,
        IRemoteResourceService remoteResourceService)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await DownloadResourceInternal(repo, remoteResource, remoteResourceService);
    }

    private async Task<LocalResource> DownloadResourceInternal(CrdtRepository repo,
        RemoteResource remoteResource,
        IRemoteResourceService remoteResourceService)
    {
        ValidateResourcesSetup();
        ArgumentNullException.ThrowIfNull(remoteResource.RemoteId);
        var downloadResult = await remoteResourceService.DownloadResource(remoteResource.RemoteId, _crdtConfig.Value.LocalResourceCachePath);
        var localResource = new LocalResource
        {
            Id = remoteResource.Id,
            LocalPath = downloadResult.LocalPath
        };
        await repo.AddLocalResource(localResource);
        return localResource;
    }

    public async Task<LocalResource?> GetLocalResource(Guid resourceId)
    {
        return await _crdtRepositoryFactory.Execute(repo => repo.GetLocalResource(resourceId));
    }

    public async Task<HarmonyResource[]> AllResources()
    {
        return (await AllResourcesInternal()).ToArray();
    }

    private async Task<IEnumerable<HarmonyResource>> AllResourcesInternal()
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var remoteResources = await repo.GetCurrentObjects<RemoteResource>().ToArrayAsync();
        var localResources = await repo.LocalResources().ToArrayAsync();
        return remoteResources.FullOuterJoin<RemoteResource, LocalResource, Guid, HarmonyResource>(localResources,
            r => r.Id,
            l => l.Id,
            (r, l, id) => new HarmonyResource
            {
                Id = id,
                RemoteId = r?.RemoteId,
                LocalPath = l?.LocalPath
            });
    }

    public async Task<HarmonyResource?> GetResource(Guid resourceId)
    {
        var resources = await AllResourcesInternal();
        return resources.FirstOrDefault(r => r.Id == resourceId);
    }

    public async Task DeleteResource(Guid clientId, Guid resourceId)
    {
        await _dataModel.AddChange(clientId, new DeleteChange<RemoteResource>(resourceId));
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        await repo.DeleteLocalResource(resourceId);
    }
}
