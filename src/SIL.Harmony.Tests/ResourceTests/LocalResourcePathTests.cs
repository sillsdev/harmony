using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Resource;
using SIL.Harmony.Sample;

namespace SIL.Harmony.Tests.ResourceTests;

public class LocalResourcePathTests : DataModelTestBase
{
    private readonly string _cacheRoot = Directory.CreateTempSubdirectory("harmony-cache").FullName;
    private readonly string _outsideRoot = Directory.CreateTempSubdirectory("harmony-outside").FullName;
    private readonly RemoteServiceMock _remoteServiceMock = new();
    private readonly Guid _clientId = Guid.NewGuid();

    private ResourceService<MediaMetadata> ResourceService =>
        _services.GetRequiredService<ResourceService<MediaMetadata>>();

    public LocalResourcePathTests()
    {
        //the config object is read on every call, so setting it here applies to the whole test
        HarmonyConfig.LocalResourceCachePath = _cacheRoot;
        HarmonyConfig.StoreLocalResourcePathsRelativeToCache = true;
    }

    private static string CreateFile(string directory, string name = "file.txt")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "contents");
        return path;
    }

    //bypasses the repository so the assertion sees what is actually in the table
    private async Task<string> StoredPath(Guid id)
    {
        var row = await DbContext.Set<LocalResource>().AsNoTracking()
            .SingleAsync(r => r.Id == id, TestContext.Current.CancellationToken);
        return row.LocalPath;
    }

    [Fact]
    public async Task FileUnderCacheIsStoredRelativeButReturnedAbsolute()
    {
        var file = CreateFile(Path.Combine(_cacheRoot, "project"));

        var resource = await ResourceService.AddLocalResource(file, _clientId, resourceService: null);

        resource.LocalPath.Should().Be(file);
        (await StoredPath(resource.Id)).Should().Be("project/file.txt");
        var fetched = await ResourceService.GetLocalResource(resource.Id);
        fetched!.LocalPath.Should().Be(file);
        fetched.FileExists().Should().BeTrue();
    }

    [Fact]
    public async Task FileOutsideCacheIsStoredAbsolute()
    {
        var file = CreateFile(_outsideRoot);

        var resource = await ResourceService.AddLocalResource(file, _clientId, resourceService: null);

        (await StoredPath(resource.Id)).Should().Be(file);
        (await ResourceService.GetLocalResource(resource.Id))!.LocalPath.Should().Be(file);
    }

    [Fact]
    public async Task FlagOffStoresAbsolutePathsEvenUnderCache()
    {
        HarmonyConfig.StoreLocalResourcePathsRelativeToCache = false;
        var file = CreateFile(Path.Combine(_cacheRoot, "project"));

        var resource = await ResourceService.AddLocalResource(file, _clientId, resourceService: null);

        (await StoredPath(resource.Id)).Should().Be(file);
        (await ResourceService.GetLocalResource(resource.Id))!.LocalPath.Should().Be(file);
    }

    [Fact]
    public async Task ResourcesSurviveTheCacheDirectoryMoving()
    {
        var file = CreateFile(Path.Combine(_cacheRoot, "project"));
        var resource = await ResourceService.AddLocalResource(file, _clientId, resourceService: null);

        //emulate iOS handing the app a new container: the files move, the database rows do not
        var newRoot = Path.Combine(_outsideRoot, "new-container");
        Directory.Move(_cacheRoot, newRoot);
        Directory.CreateDirectory(_cacheRoot);//so dispose can clean up
        HarmonyConfig.LocalResourceCachePath = newRoot;

        var fetched = await ResourceService.GetLocalResource(resource.Id);
        fetched!.LocalPath.Should().Be(Path.Combine(newRoot, "project", "file.txt"));
        fetched.FileExists().Should().BeTrue();
        var listed = await ResourceService.ListResourcesPendingUpload();
        listed.Should().ContainSingle().Which.LocalPath.Should().Be(fetched.LocalPath);
        var all = await ResourceService.AllResources();
        all.Should().ContainSingle().Which.LocalPath.Should().Be(fetched.LocalPath);
    }

    [Fact]
    public async Task DownloadedFileIsStoredRelativeToCache()
    {
        var remoteId = _remoteServiceMock.CreateRemoteResource("remote contents");
        var resourceId = Guid.NewGuid();
        await DataModel.AddChange(_clientId, new CreateRemoteResourceChange<MediaMetadata>(resourceId, remoteId));

        var local = await ResourceService.DownloadResource(resourceId, _remoteServiceMock);

        local.LocalPath.Should().Be(Path.Combine(_cacheRoot, Path.GetFileName(remoteId)));
        (await StoredPath(resourceId)).Should().Be(Path.GetFileName(remoteId));
    }

    [Fact]
    public async Task RelativeDownloadResultResolvesAgainstCache()
    {
        var resourceId = Guid.NewGuid();
        await DataModel.AddChange(_clientId, new CreateRemoteResourceChange<MediaMetadata>(resourceId, "remote"));
        var file = CreateFile(Path.Combine(_cacheRoot, "sub"), "downloaded.txt");
        var service = new PathReturningRemoteService(Path.Combine("sub", "downloaded.txt"));

        var local = await ResourceService.DownloadResource(resourceId, service);

        local.LocalPath.Should().Be(file);
        (await StoredPath(resourceId)).Should().Be("sub/downloaded.txt");
    }

    [Fact]
    public async Task DownloadReturningMissingFileThrows()
    {
        var resourceId = Guid.NewGuid();
        await DataModel.AddChange(_clientId, new CreateRemoteResourceChange<MediaMetadata>(resourceId, "remote"));
        var service = new PathReturningRemoteService(Path.Combine(_cacheRoot, "does-not-exist.txt"));

        await FluentActions.Awaiting(() => ResourceService.DownloadResource(resourceId, service))
            .Should().ThrowAsync<FileNotFoundException>();
        (await ResourceService.GetLocalResource(resourceId)).Should().BeNull();
    }

    private class PathReturningRemoteService(string path) : IRemoteResourceService<MediaMetadata>
    {
        public Task<DownloadResult> DownloadResource(string remoteId, string localResourceCachePath) =>
            Task.FromResult(new DownloadResult(path));

        public Task<UploadResult<MediaMetadata>> UploadResource(Guid resourceId, string localPath, MediaMetadata? metadata = null) =>
            throw new NotSupportedException();
    }
}
