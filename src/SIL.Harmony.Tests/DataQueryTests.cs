using SIL.Harmony.Sample.Models;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Tests;

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
        var entries = await DataModel.QueryLatest<Word>().ToArrayAsync();
        var entry = entries.Should().ContainSingle().Subject;
        entry.Text.Should().Be("entity1");
    }
}