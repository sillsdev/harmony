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
        config.ChangeTypeListBuilder.Add<DeleteChange<Word>>();
        var types = config.ChangeTypes.ToArray();
        types.Should().BeEquivalentTo([
            new RegisteredChangeType(typeof(NewDefinitionChange), nameof(NewDefinitionChange)),
            new RegisteredChangeType(typeof(SetWordTextChange), nameof(SetWordTextChange)),
            new RegisteredChangeType(typeof(DeleteChange<Word>), "delete:Word"),
        ]);
    }

    [Fact]
    public void ConfigureJsonOptions_applies_callback()
    {
        var config = new CrdtConfig();
        config.ConfigureJsonOptions(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        config.JsonSerializerOptions.PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);
    }

    [Fact]
    public void ConfigureJsonOptions_composes_multiple_callbacks()
    {
        var config = new CrdtConfig();
        config.ConfigureJsonOptions(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        config.ConfigureJsonOptions(o => o.WriteIndented = true);

        config.JsonSerializerOptions.PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);
        config.JsonSerializerOptions.WriteIndented.Should().BeTrue();
    }

    [Fact]
    public void ConfigureJsonOptions_throws_after_freeze()
    {
        var config = new CrdtConfig();
        _ = config.JsonSerializerOptions;

        var act = () => config.ConfigureJsonOptions(o => o.WriteIndented = true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JsonOptionsBuilder* frozen*");
    }
}
