using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Core;
using SIL.Harmony.Db;
using SIL.Harmony.Resource;

namespace SIL.Harmony;

public class ResourceService
{
    private readonly CrdtRepository _crdtRepository;
    private readonly IOptions<CrdtConfig> _crdtConfig;
    private readonly DataModel _dataModel;

    internal ResourceService(CrdtRepository crdtRepository, IOptions<CrdtConfig> crdtConfig, DataModel dataModel)
    {
        _crdtRepository = crdtRepository;
        _crdtConfig = crdtConfig;
        _dataModel = dataModel;
    }

    private void ValidateResourcesSetup()
    {
        if (!_crdtConfig.Value.RemoteResourcesEnabled) throw new RemoteResourceNotEnabledException();
    }

    public async Task<Guid> AddLocalResource(string resourcePath, Guid clientId, Guid id = default, IRemoteResourceService? resourceService = null)
    {
        ValidateResourcesSetup();
        var localResource = new LocalResource
        {
            Id = id == default ? Guid.NewGuid() : id,
            LocalPath = Path.GetFullPath(resourcePath)
        };
        if (!localResource.FileExists()) throw new FileNotFoundException(localResource.LocalPath);
        await using var transaction = await _crdtRepository.BeginTransactionAsync();
        await _crdtRepository.AddLocalResource(localResource);
        if (resourceService is not null)
        {
            var uploadResult = await resourceService.UploadResource(localResource.LocalPath);
            await _dataModel.AddChange(clientId, new CreateRemoteResourceChange(localResource.Id, uploadResult.RemoteId));
        }
        else
        {
            await _dataModel.AddChange(clientId, new CreateRemoteResourcePendingUploadChange(localResource.Id));
        }
        await transaction.CommitAsync();
        return localResource.Id;
    }

    public async Task<LocalResource[]> ListResourcesPendingUpload()
    {
        ValidateResourcesSetup();
        var remoteResources = await _dataModel.QueryLatest<RemoteResource>().Where(r => r.RemoteId == null).ToArrayAsync();
        var localResource = _crdtRepository.LocalResourcesByIds(remoteResources.Select(r => r.Id));
        return await localResource.ToArrayAsync();
    }

    public async Task UploadPendingResources(Guid clientId, IRemoteResourceService remoteResourceService)
    {
        ValidateResourcesSetup();
        var pendingUploads = await ListResourcesPendingUpload();
        var changes = new List<IChange>(pendingUploads.Length);
        try
        {
            foreach (var localResource in pendingUploads)
            {
                var uploadResult = await remoteResourceService.UploadResource(localResource.LocalPath);
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
        var localResource = await _crdtRepository.GetLocalResource(resourceId) ??
                            throw new ArgumentException($"unable to find local resource with id {resourceId}");
        ValidateResourcesSetup();
        await UploadPendingResource(localResource, clientId, remoteResourceService);
    }

    public async Task UploadPendingResource(LocalResource localResource, Guid clientId, IRemoteResourceService remoteResourceService)
    {
        ValidateResourcesSetup();
        var uploadResult = await remoteResourceService.UploadResource(localResource.LocalPath);
        await _dataModel.AddChange(clientId, new RemoteResourceUploadedChange(localResource.Id, uploadResult.RemoteId));
    }

    public async Task<RemoteResource[]> ListResourcesPendingDownload()
    {
        ValidateResourcesSetup();
        var localResourceIds = _crdtRepository.LocalResourceIds();
        var remoteResources = await _dataModel.QueryLatest<RemoteResource>()
            .Where(r => r.RemoteId != null && !localResourceIds.Contains(r.Id))
            .ToArrayAsync();
        return remoteResources;
    }

    public async Task<LocalResource> DownloadResource(Guid resourceId, IRemoteResourceService remoteResourceService)
    {
        ValidateResourcesSetup();
        return await DownloadResource(await _dataModel.GetLatest<RemoteResource>(resourceId) ?? throw new EntityNotFoundException("Unable to find remote resource"), remoteResourceService);
    }

    public async Task<LocalResource> DownloadResource(RemoteResource remoteResource, IRemoteResourceService remoteResourceService)
    {
        ValidateResourcesSetup();
        ArgumentNullException.ThrowIfNull(remoteResource.RemoteId);
        var downloadResult = await remoteResourceService.DownloadResource(remoteResource.RemoteId, _crdtConfig.Value.LocalResourceCachePath);
        var localResource = new LocalResource
        {
            Id = remoteResource.Id,
            LocalPath = downloadResult.LocalPath
        };
        await _crdtRepository.AddLocalResource(localResource);
        return localResource;
    }

    public async Task<LocalResource?> GetLocalResource(Guid resourceId)
    {
        return await _crdtRepository.GetLocalResource(resourceId);
    }
}