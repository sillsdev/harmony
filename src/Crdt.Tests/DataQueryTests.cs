using Crdt.Sample.Models;
using Microsoft.EntityFrameworkCore;

namespace Crdt.Tests;

public class DataQueryTests: DataModelTestBase
{
    private readonly Guid _entity1Id = Guid.NewGuid();
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await WriteNextChange(SetWord(_entity1Id, "entity1"));
    }

    [Fact]
    public async Task CanQueryLatestData()
    {
        var entries = await DataModel.GetLatestObjects<Word>().ToArrayAsync();
        var entry = entries.Should().ContainSingle().Subject;
        entry.Text.Should().Be("entity1");
    }
}