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
}