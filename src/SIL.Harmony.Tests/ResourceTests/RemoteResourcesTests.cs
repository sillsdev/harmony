using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Resource;

namespace SIL.Harmony.Tests.ResourceTests;

public class RemoteResourcesTests : DataModelTestBase
{
    private RemoteServiceMock _remoteServiceMock = new();
    private ResourceService _resourceService => _services.GetRequiredService<ResourceService>();

    public RemoteResourcesTests()
    {
    }

    private string CreateFile(string contents, [CallerMemberName] string fileName = "")
    {
        var filePath = Path.GetFullPath(fileName + ".txt");
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    private async Task<(Guid resourceId, string remoteId)> SetupRemoteResource(string fileContents)
    {
        var remoteId = _remoteServiceMock.CreateRemoteResource(fileContents);
        var resourceId = Guid.NewGuid();
        await DataModel.AddChange(_localClientId, new CreateRemoteResourceChange(resourceId, remoteId));
        return (resourceId, remoteId);
    }

    private async Task<(Guid resourceId, string localPath)> SetupLocalFile(string contents, [CallerMemberName] string fileName = "")
    {
        var file = CreateFile(contents, fileName);
        //because resource service is null the file is not uploaded
        var crdtResource = await _resourceService.AddLocalResource(file, _localClientId, resourceService: null);
        return (crdtResource.Id, file);
    }

    [Fact]
    public async Task CreatingAResourceResultsInPendingLocalResources()
    {
        var (_, file) = await SetupLocalFile("contents");

        //act
        var pending = await _resourceService.ListResourcesPendingUpload();


        pending.Should().ContainSingle().Which.LocalPath.Should().Be(file);
    }

    [Fact]
    public async Task ResourcesNotLocalShouldShowUpAsNotDownloaded()
    {
        var (resourceId, remoteId) = await SetupRemoteResource("test");

        //act
        var pending = await _resourceService.ListResourcesPendingDownload();


        var remoteResource = pending.Should().ContainSingle().Subject;
        remoteResource.RemoteId.Should().Be(remoteId);
        remoteResource.Id.Should().Be(resourceId);
    }

    [Fact]
    public async Task CanUploadFileToRemote()
    {
        var fileContents = "resource";
        var localFile = CreateFile(fileContents);

        //act
        var crdtResource = await _resourceService.AddLocalResource(localFile, _localClientId, resourceService: _remoteServiceMock);


        var resource = await DataModel.GetLatest<RemoteResource>(crdtResource.Id);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(resource.RemoteId);
        _remoteServiceMock.ReadFile(resource.RemoteId).Should().Be(fileContents);
        var pendingUpload = await _resourceService.ListResourcesPendingUpload();
        pendingUpload.Should().BeEmpty();
    }

    [Fact]
    public async Task IfUploadingFailsTheResourceIsStillAddedAsPendingUpload()
    {
        var fileContents = "resource";
        var localFile = CreateFile(fileContents);

        //todo setup a mock that throws an exception when uploading
        _remoteServiceMock.ThrowOnUpload(localFile);
        
        //act
        var crdtResource = await _resourceService.AddLocalResource(localFile, _localClientId, resourceService: _remoteServiceMock);

        var harmonyResource = await _resourceService.GetResource(crdtResource.Id);
        harmonyResource.Should().NotBeNull();
        harmonyResource.Id.Should().Be(crdtResource.Id);
        harmonyResource.RemoteId.Should().BeNull();
        harmonyResource.LocalPath.Should().Be(localFile);
        var pendingUpload = await _resourceService.ListResourcesPendingUpload();
        pendingUpload.Should().ContainSingle().Which.Id.Should().Be(crdtResource.Id);
    }

    [Fact]
    public async Task WillUploadMultiplePendingLocalFilesAtOnce()
    {
        await SetupLocalFile("file1", "file1");
        await SetupLocalFile("file2", "file2");

        //act
        await _resourceService.UploadPendingResources(_localClientId, _remoteServiceMock);


        _remoteServiceMock.ListRemoteFiles()
            .Select(Path.GetFileName)
            .Should()
            .Contain(["file1.txt", "file2.txt"]);
    }

    [Fact]
    public async Task CanDownloadFileFromRemote()
    {
        var fileContents = "resource";
        var (resourceId, _) = await SetupRemoteResource(fileContents);

        //act
        var localResource = await _resourceService.DownloadResource(resourceId, _remoteServiceMock);


        localResource.Id.Should().Be(resourceId);
        var actualFileContents = await File.ReadAllTextAsync(localResource.LocalPath);
        actualFileContents.Should().Be(fileContents);
        var pendingDownloads = await _resourceService.ListResourcesPendingDownload();
        pendingDownloads.Should().BeEmpty();
    }

    [Fact]
    public async Task CanGetALocalResourceGivenAnId()
    {
        var file = CreateFile("resource");
        //because resource service is null the file is not uploaded
        var crdtResource = await _resourceService.AddLocalResource(file, _localClientId, resourceService: null);

        //act
        var localResource = await _resourceService.GetLocalResource(crdtResource.Id);


        localResource.Should().NotBeNull();
        localResource!.LocalPath.Should().Be(file);
    }

    [Fact]
    public async Task LocalResourceIsNullIfNotDownloaded()
    {
        var (resourceId, _) = await SetupRemoteResource("test");
        var localResource = await _resourceService.GetLocalResource(resourceId);
        localResource.Should().BeNull();
    }

    [Fact]
    public async Task CanListAllResources()
    {
        var (localResourceId, localResourcePath) = await SetupLocalFile("localOnly", "localOnly.txt");
        var (remoteResourceId, remoteId) = await SetupRemoteResource("remoteOnly");
        var localAndRemoteResource = await _resourceService.AddLocalResource(CreateFile("localAndRemove"), _localClientId, resourceService: _remoteServiceMock);

        var crdtResources = await _resourceService.AllResources();
        crdtResources.Should().BeEquivalentTo(
            [
                new HarmonyResource
                {
                    Id = localResourceId,
                    LocalPath = localResourcePath,
                    RemoteId = null
                },
                new HarmonyResource
                {
                    Id = remoteResourceId,
                    LocalPath = null, 
                    RemoteId = remoteId
                },
                localAndRemoteResource
            ]);
    }

    [Fact]
    public async Task CanGetAResourceGivenAnId()
    {
        var (localResourceId, localResourcePath) = await SetupLocalFile("localOnly", "localOnly.txt");
        var (remoteResourceId, remoteId) = await SetupRemoteResource("remoteOnly");
        var localAndRemoteResource = await _resourceService.AddLocalResource(CreateFile("localAndRemove"),
            _localClientId,
            resourceService: _remoteServiceMock);
        
        (await _resourceService.GetResource(localResourceId)).Should().BeEquivalentTo(new HarmonyResource
        {
            Id = localResourceId,
            LocalPath = localResourcePath,
            RemoteId = null
        });
        (await _resourceService.GetResource(remoteResourceId)).Should().BeEquivalentTo(new HarmonyResource
        {
            Id = remoteResourceId,
            LocalPath = null,
            RemoteId = remoteId
        });
        (await _resourceService.GetResource(localAndRemoteResource.Id)).Should().BeEquivalentTo(localAndRemoteResource);
        (await _resourceService.GetResource(Guid.NewGuid())).Should().BeNull();
    }
}
