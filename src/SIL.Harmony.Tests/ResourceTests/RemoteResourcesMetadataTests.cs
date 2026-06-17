using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Resource;
using SIL.Harmony.Sample;
namespace SIL.Harmony.Tests.ResourceTests;
public class RemoteResourcesMetadataTests : DataModelTestBase
{
    private RemoteServiceMock _remoteServiceMock = new();
    private ResourceService<MediaMetadata> _resourceService =>
        _services.GetRequiredService<ResourceService<MediaMetadata>>();
    private string CreateFile(string contents, [CallerMemberName] string fileName = "")
    {
        var filePath = Path.GetFullPath(fileName + ".txt");
        File.WriteAllText(filePath, contents);
        return filePath;
    }
    private static MediaMetadata SampleMetadata(string fileName = "photo.jpg") =>
        new(fileName, "image/jpeg", 102400);
    [Fact]
    public async Task CreateWithUpload_IncludesMetadata()
    {
        var metadata = SampleMetadata();
        var localFile = CreateFile("image data");
        var resource = await _resourceService.AddLocalResource(localFile, _localClientId, metadata,
            resourceService: _remoteServiceMock);
        resource.Metadata.Should().BeEquivalentTo(metadata);
        var stored = await DataModel.GetLatest<RemoteResource<MediaMetadata>>(resource.Id);
        stored!.Metadata.Should().BeEquivalentTo(metadata);
        (await _resourceService.GetResource(resource.Id))!.Metadata.Should().BeEquivalentTo(metadata);
    }
    [Fact]
    public async Task CreatePendingUpload_IncludesMetadata()
    {
        var metadata = SampleMetadata("pending.mp4");
        var localFile = CreateFile("video data");
        var resource = await _resourceService.AddLocalResource(localFile, _localClientId, metadata,
            resourceService: null);
        resource.Metadata.Should().BeEquivalentTo(metadata);
        var stored = await DataModel.GetLatest<RemoteResource<MediaMetadata>>(resource.Id);
        stored!.Metadata.Should().BeEquivalentTo(metadata);
    }
    [Fact]
    public async Task AllResources_IncludesMetadata()
    {
        var metadata = SampleMetadata();
        var localFile = CreateFile("list test");
        await _resourceService.AddLocalResource(localFile, _localClientId, metadata, resourceService: _remoteServiceMock);
        var all = await _resourceService.AllResources();
        all.Should().ContainSingle().Which.Metadata.Should().BeEquivalentTo(metadata);
    }
    [Fact]
    public async Task SetResourceMetadata_UpdatesAndSyncs()
    {
        var metadata = SampleMetadata();
        var localFile = CreateFile("sync test");
        var resource = await _resourceService.AddLocalResource(localFile, _localClientId, metadata,
            resourceService: _remoteServiceMock);
        var remoteClient = ForkDatabase();
        var updated = metadata with { FileName = "renamed.jpg" };
        await _resourceService.SetResourceMetadata(resource.Id, _localClientId, updated);
        await DataModel.SyncWith(remoteClient.DataModel);
        (await _resourceService.GetResource(resource.Id))!.Metadata.Should().BeEquivalentTo(updated);
        (await remoteClient.DataModel.GetLatest<RemoteResource<MediaMetadata>>(resource.Id))!.Metadata
            .Should().BeEquivalentTo(updated);
    }
    [Fact]
    public async Task CreateWithoutMetadata_DeserializesWithNullMetadata()
    {
        var resourceId = Guid.NewGuid();
        var remoteId = _remoteServiceMock.CreateRemoteResource("legacy");
        await DataModel.AddChange(_localClientId,
            new CreateRemoteResourceChange<MediaMetadata>(resourceId, remoteId));
        var stored = await DataModel.GetLatest<RemoteResource<MediaMetadata>>(resourceId);
        stored!.Metadata.Should().BeNull();
        stored.RemoteId.Should().Be(remoteId);
    }
}

