﻿using System.Runtime.CompilerServices;
using SIL.Harmony.Resource;

namespace SIL.Harmony.Tests.ResourceTests;

public class RemoteResourcesTests : DataModelTestBase
{
    private RemoteServiceMock _remoteServiceMock = new();

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
        var resourceId = await DataModel.AddLocalResource(file, _localClientId, resourceService: null);
        return (resourceId, file);
    }

    [Fact]
    public async Task CreatingAResourceResultsInPendingLocalResources()
    {
        var (_, file) = await SetupLocalFile("contents");

        //act
        var pending = await DataModel.ListResourcesPendingUpload();


        pending.Should().ContainSingle().Which.LocalPath.Should().Be(file);
    }

    [Fact]
    public async Task ResourcesNotLocalShouldShowUpAsNotDownloaded()
    {
        var (resourceId, remoteId) = await SetupRemoteResource("test");

        //act
        var pending = await DataModel.ListResourcesPendingDownload();


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
        var resourceId =
            await DataModel.AddLocalResource(localFile, _localClientId, resourceService: _remoteServiceMock);


        var resource = await DataModel.GetLatest<RemoteResource>(resourceId);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(resource.RemoteId);
        _remoteServiceMock.ReadFile(resource.RemoteId).Should().Be(fileContents);
        var pendingUpload = await DataModel.ListResourcesPendingUpload();
        pendingUpload.Should().BeEmpty();
    }

    [Fact]
    public async Task WillUploadMultiplePendingLocalFilesAtOnce()
    {
        await SetupLocalFile("file1", "file1");
        await SetupLocalFile("file2", "file2");

        //act
        await DataModel.UploadPendingResources(_localClientId, _remoteServiceMock);


        _remoteServiceMock.ListFiles()
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
        var localResource = await DataModel.DownloadResource(resourceId, _remoteServiceMock);


        localResource.Id.Should().Be(resourceId);
        var actualFileContents = await File.ReadAllTextAsync(localResource.LocalPath);
        actualFileContents.Should().Be(fileContents);
        var pendingDownloads = await DataModel.ListResourcesPendingDownload();
        pendingDownloads.Should().BeEmpty();
    }

    [Fact]
    public async Task CanGetALocalResourceGivenAnId()
    {
        var file = CreateFile("resource");
        //because resource service is null the file is not uploaded
        var resourceId = await DataModel.AddLocalResource(file, _localClientId, resourceService: null);

        //act
        var localResource = await DataModel.GetLocalResource(resourceId);


        localResource.Should().NotBeNull();
        localResource!.LocalPath.Should().Be(file);
    }

    [Fact]
    public async Task LocalResourceIsNullIfNotDownloaded()
    {
        var (resourceId, _) = await SetupRemoteResource("test");
        var localResource = await DataModel.GetLocalResource(resourceId);
        localResource.Should().BeNull();
    }
}
