using System.Text.Json;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;
using SIL.Harmony.Tests.Adapter;

namespace SIL.Harmony.Tests;

public class ConfigTests
{
    [Fact]
    public void CanGetEntityTypes()
    {
        var config = new CrdtConfig();
        config.ObjectTypeListBuilder.DefaultAdapter()
            .Add<Word>()
            .Add<Definition>();
        config.ObjectTypeListBuilder
            .CustomAdapter<CustomObjectAdapterTests.IMyCustomInterface, CustomObjectAdapterTests.MyClassAdapter>()
            .Add<CustomObjectAdapterTests.MyClass>();
        var types = config.ObjectTypes.ToArray();
        types.Should().BeEquivalentTo([typeof(Word), typeof(Definition), typeof(CustomObjectAdapterTests.MyClass)]);
    }

    [Fact]
    public void CanGetChangeTypes()
    {
        var config = new CrdtConfig();
        config.ChangeTypeListBuilder.Add<NewDefinitionChange>();
        config.ChangeTypeListBuilder.Add<SetWordTextChange>();
        var types = config.ChangeTypes.ToArray();
        types.Should().BeEquivalentTo([typeof(NewDefinitionChange), typeof(SetWordTextChange)]);
    }

    [Fact]
    public void CanAddChangeTypesAfterReadingJsonSerializerOptions()
    {
        // Mirrors a consumer that reads JsonSerializerOptions mid-configuration (to layer its own
        // TypeInfoResolver modifier onto ours) and then keeps registering change types.
        var config = new CrdtConfig();
        config.ChangeTypeListBuilder.Add<NewDefinitionChange>();

        _ = config.JsonSerializerOptions;

        config.ChangeTypeListBuilder.Add<SetWordTextChange>();

        var options = config.JsonSerializerOptions;
        var entityId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var json = JsonSerializer.Serialize<IChange>(new SetWordTextChange(entityId, "hello"), options);

        var roundTripped = JsonSerializer.Deserialize<IChange>(json, options);
        roundTripped.Should().BeOfType<SetWordTextChange>()
            .Which.Text.Should().Be("hello");
    }
}
