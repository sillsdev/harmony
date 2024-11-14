using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests.ResourceTests;

public class WordResourceTests: DataModelTestBase
{
    private RemoteServiceMock _remoteServiceMock = new();
    private ResourceService _resourceService => _services.GetRequiredService<ResourceService>();
    private readonly Guid _entity1Id = Guid.NewGuid();

    private string CreateFile(string contents, [CallerMemberName] string fileName = "")
    {
        var filePath = Path.GetFullPath(fileName + ".txt");
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    [Fact]
    public async Task CanReferenceAResourceFromAWord()
    {
        await WriteNextChange(SetWord(_entity1Id, "test-value"));
        var imageFile = CreateFile("not image data");
        //set commit date for add local resource
        MockTimeProvider.SetNextDateTime(NextDate());
        var resourceId = await _resourceService.AddLocalResource(imageFile, Guid.NewGuid(), resourceService: _remoteServiceMock);
        await WriteNextChange(new AddWordImageChange(_entity1Id, resourceId));

        var word = await DataModel.GetLatest<Word>(_entity1Id);
        word.Should().NotBeNull();
        word!.ImageResourceId.Should().Be(resourceId);
        
        
        var localResource = await _resourceService.GetLocalResource(word.ImageResourceId!.Value);
        localResource.Should().NotBeNull();
        localResource!.LocalPath.Should().Be(imageFile);
        (await File.ReadAllTextAsync(localResource.LocalPath)).Should().Be("not image data");
    }
}