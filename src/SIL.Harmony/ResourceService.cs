using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Helpers;
using SIL.Harmony.Resource;

namespace SIL.Harmony;

public class ResourceService<TMetadata> where TMetadata : class
{
    private readonly CrdtRepositoryFactory _crdtRepositoryFactory;
    private readonly IOptions<HarmonyConfig> _crdtConfig;
    private readonly DataModel _dataModel;
    private readonly ILogger<ResourceService<TMetadata>> _logger;

    internal ResourceService(CrdtRepositoryFactory crdtRepositoryFactory, IOptions<HarmonyConfig> crdtConfig,
        DataModel dataModel, ILogger<ResourceService<TMetadata>> logger)
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
        Guid resourceId,
        string remoteId,
        TMetadata? metadata = null,
        CommitMetadata? commitMetadata = null)
    {
        ValidateResourcesSetup();
        var localResource = new LocalResource
        {
            Id = resourceId,
            LocalPath = Path.GetFullPath(resourcePath)
        };
        if (!localResource.FileExists()) throw new FileNotFoundException(localResource.LocalPath);

        await _dataModel.AddChange(clientId,
            new CreateRemoteResourceChange<TMetadata>(localResource.Id, remoteId, metadata),
            commitMetadata);
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        await repo.AddLocalResource(localResource);
    }

    /// <summary>
    /// add and upload a local resource
    /// </summary>
    /// <param name="resourcePath">path to the resource on the local machine</param>
    /// <param name="clientId">id of the client</param>
    /// <param name="metadata">metadata for the resource, this metadata will be overridden by the remote service if returned by <see cref="IRemoteResourceService{TMetadata}.UploadResource"/></param>
    /// <param name="id">id of the resource</param>
    /// <param name="resourceService">service to upload the resource to the remote server</param>
    /// <param name="commitMetadata">metadata for the commit that records this change, for example the author</param>
    /// <returns>the HarmonyResource created</returns>
    public async Task<HarmonyResource<TMetadata>> AddLocalResource(string resourcePath,
        Guid clientId,
        TMetadata? metadata = null,
        Guid id = default,
        IRemoteResourceService<TMetadata>? resourceService = null,
        CommitMetadata? commitMetadata = null)
    {
        ValidateResourcesSetup();
        var localResource = new LocalResource
        {
            Id = id == default ? Guid.NewGuid() : id,
            LocalPath = Path.GetFullPath(resourcePath)
        };
        if (!localResource.FileExists()) throw new FileNotFoundException(localResource.LocalPath);
        UploadResult<TMetadata>? uploadResult = null;
        if (resourceService is not null)
        {
            try
            {
                uploadResult = await resourceService.UploadResource(localResource.Id, localResource.LocalPath, metadata);
                metadata = uploadResult.Metadata ?? metadata;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error uploading resource {resourcePath}, resource will be marked as pending upload",
                    localResource.LocalPath);
            }
        }

        if (uploadResult is not null)
        {
            await _dataModel.AddChange(clientId,
                new CreateRemoteResourceChange<TMetadata>(localResource.Id, uploadResult.RemoteId, metadata),
                commitMetadata);
        }
        else
        {
            await _dataModel.AddChange(clientId,
                new CreateRemoteResourcePendingUploadChange<TMetadata>(localResource.Id, metadata),
                commitMetadata);
        }

        await _crdtRepositoryFactory.Execute(repo => repo.AddLocalResource(localResource));
        return new HarmonyResource<TMetadata>
        {
            Id = localResource.Id,
            RemoteId = uploadResult?.RemoteId,
            LocalPath = localResource.LocalPath,
            Metadata = metadata
        };
    }

    public async Task SetResourceMetadata(Guid resourceId, Guid clientId, TMetadata metadata, CommitMetadata? commitMetadata = null)
    {
        ValidateResourcesSetup();
        await _dataModel.AddChange(clientId, new SetRemoteResourceMetadataChange<TMetadata>(resourceId, metadata), commitMetadata);
    }

    public async Task<HarmonyResource<TMetadata>[]> ListResourcesPendingUpload()
    {
        ValidateResourcesSetup();
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var remoteResources = await repo.GetCurrentObjects<RemoteResource<TMetadata>>()
            .Where(r => r.RemoteId == null && r.DeletedAt == null).ToDictionaryAsync(r => r.Id);
        var localResource = repo.LocalResourcesByIds(remoteResources.Keys);
        return await localResource.Select(l => new HarmonyResource<TMetadata>(l, remoteResources.GetValueOrDefault(l.Id))).ToArrayAsync();
    }

    public async Task UploadPendingResources(Guid clientId, IRemoteResourceService<TMetadata> remoteResourceService, CommitMetadata? commitMetadata = null)
    {
        ValidateResourcesSetup();
        var pendingUploads = await ListResourcesPendingUpload();
        if (pendingUploads is []) return;
        var changes = new List<IChange>(pendingUploads.Length);
        try
        {
            foreach (var resource in pendingUploads)
            {
                //we know that local path is not null because we filtered for resources that are not uploaded yet
                var uploadResult = await remoteResourceService.UploadResource(resource.Id, resource.LocalPath!, resource.Metadata);
                changes.Add(new RemoteResourceUploadedChange<TMetadata>(resource.Id, uploadResult.RemoteId, uploadResult.Metadata));
            }
        }
        finally
        {
            //if upload throws at any point we will at least save the changes that did get made.
            await _dataModel.AddChanges(clientId, changes, commitMetadata);
        }
    }

    public async Task UploadPendingResource(Guid resourceId, Guid clientId, IRemoteResourceService<TMetadata> remoteResourceService, CommitMetadata? commitMetadata = null)
    {
        ValidateResourcesSetup();
        var resource = await GetResource(resourceId) ??
                            throw new ArgumentException($"unable to find local resource with id {resourceId}");
        await UploadPendingResource(resource, clientId, remoteResourceService, commitMetadata);
    }

    public async Task UploadPendingResource(HarmonyResource<TMetadata> resource, Guid clientId,
        IRemoteResourceService<TMetadata> remoteResourceService,
        CommitMetadata? commitMetadata = null)
    {
        ValidateResourcesSetup();
        if (resource is not { Local: true, Remote: false }) throw new ArgumentException("Resource is not pending upload");
        var uploadResult = await remoteResourceService.UploadResource(resource.Id, resource.LocalPath, resource.Metadata);
        await _dataModel.AddChange(clientId,
            new RemoteResourceUploadedChange<TMetadata>(resource.Id, uploadResult.RemoteId, uploadResult.Metadata),
            commitMetadata);
    }

    public async Task<RemoteResource<TMetadata>[]> ListResourcesPendingDownload()
    {
        ValidateResourcesSetup();
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var localResourceIds = repo.LocalResourceIds();
        var remoteResources = await repo.GetCurrentObjects<RemoteResource<TMetadata>>()
            .Where(r => r.RemoteId != null && !localResourceIds.Contains(r.Id) && r.DeletedAt == null)
            .ToArrayAsync();
        return remoteResources;
    }

    public async Task<LocalResource> DownloadResource(Guid resourceId, IRemoteResourceService<TMetadata> remoteResourceService)
    {
        ValidateResourcesSetup();
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await DownloadResourceInternal(repo,
            await repo.GetCurrent<RemoteResource<TMetadata>>(resourceId) ??
            throw new EntityNotFoundException("Unable to find remote resource"),
            remoteResourceService
        );
    }

    public async Task<LocalResource> DownloadResource(RemoteResource<TMetadata> remoteResource,
        IRemoteResourceService<TMetadata> remoteResourceService)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        return await DownloadResourceInternal(repo, remoteResource, remoteResourceService);
    }

    private async Task<LocalResource> DownloadResourceInternal(CrdtRepository repo,
        RemoteResource<TMetadata> remoteResource,
        IRemoteResourceService<TMetadata> remoteResourceService)
    {
        ValidateResourcesSetup();
        ArgumentNullException.ThrowIfNull(remoteResource.RemoteId);
        var downloadResult = await remoteResourceService.DownloadResource(remoteResource.RemoteId,
            _crdtConfig.Value.LocalResourceCachePath);
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

    public async Task<HarmonyResource<TMetadata>[]> AllResources()
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var remoteResources = await repo.GetCurrentObjects<RemoteResource<TMetadata>>().Where(r => r.DeletedAt == null).ToArrayAsync();
        var localResources = await repo.LocalResources().ToArrayAsync();
        return remoteResources
            .FullOuterJoin<RemoteResource<TMetadata>, LocalResource, Guid, HarmonyResource<TMetadata>>(
                localResources,
                r => r.Id,
                l => l.Id,
                (r, l, _) => new HarmonyResource<TMetadata>(l, r)).ToArray();
    }

    public async Task<HarmonyResource<TMetadata>?> GetResource(Guid resourceId)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        var remoteResource = await repo.GetCurrent<RemoteResource<TMetadata>>(resourceId);
        var localResource = await repo.GetLocalResource(resourceId);
        if (remoteResource is { DeletedAt: not null }) remoteResource = null;
        if (localResource is null && remoteResource is null) return null;
        return new HarmonyResource<TMetadata>(localResource, remoteResource);
    }

    public async Task DeleteResource(Guid clientId, Guid resourceId, CommitMetadata? commitMetadata = null)
    {
        await _dataModel.AddChange(clientId, new DeleteRemoteResourceChange<TMetadata>(resourceId), commitMetadata);
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        await repo.DeleteLocalResource(resourceId);
    }
}
