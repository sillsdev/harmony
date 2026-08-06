using System.Text.Json;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;
using SIL.Harmony.Tests.Adapter;

namespace SIL.Harmony.Tests;

public class ConfigTests
{
    [Fact]
    public void CanGetEntityTypes()
    {
        var config = new HarmonyConfig();
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
        var config = new HarmonyConfig();
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
        var config = new HarmonyConfig();
        config.ConfigureJsonOptions(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        config.JsonSerializerOptions.PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);
    }

    [Fact]
    public void ConfigureJsonOptions_composes_multiple_callbacks()
    {
        var config = new HarmonyConfig();
        config.ConfigureJsonOptions(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        config.ConfigureJsonOptions(o => o.WriteIndented = true);

        config.JsonSerializerOptions.PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);
        config.JsonSerializerOptions.WriteIndented.Should().BeTrue();
    }

    [Fact]
    public void ConfigureJsonOptions_throws_after_freeze()
    {
        var config = new HarmonyConfig();
        _ = config.JsonSerializerOptions;

        var act = () => config.ConfigureJsonOptions(o => o.WriteIndented = true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JsonOptionsBuilder* frozen*");
    }

    [Fact]
    public void ChangeTypeListBuilder_DuplicateAdd_IsIdempotent()
    {
        var config = new HarmonyConfig();
        config.ChangeTypeListBuilder.Add<SetWordTextChange>();
        config.ChangeTypeListBuilder.Add<SetWordTextChange>();

        config.ChangeTypes.Should().ContainSingle(t => t.Type == typeof(SetWordTextChange));
    }

    [Fact]
    public void ChangeTypeListBuilder_AddAfterFreeze_Throws()
    {
        var config = new HarmonyConfig();
        //building the serializer options freezes the change type builder
        _ = config.JsonSerializerOptions;

        var act = () => config.ChangeTypeListBuilder.Add<SetWordTextChange>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ChangeTypeListBuilder*frozen*");
    }

    [Fact]
    public void ObjectTypeListBuilder_AddAfterFreeze_Throws()
    {
        var config = new HarmonyConfig();
        config.ObjectTypeListBuilder.DefaultAdapter().Add<Word>();
        config.ObjectTypeListBuilder.Freeze(); //happens during EF model build in a real setup

        var act = () => config.ObjectTypeListBuilder.DefaultAdapter();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ObjectTypeListBuilder*frozen*");
    }

    [Fact]
    public void DefaultAdapter_CalledTwice_ReturnsSameInstance()
    {
        var config = new HarmonyConfig();
        var first = config.ObjectTypeListBuilder.DefaultAdapter();
        var second = config.ObjectTypeListBuilder.DefaultAdapter();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Adapt_ObjectImplementingIObjectBase_ReturnsIt()
    {
        var config = new HarmonyConfig();
        config.ObjectTypeListBuilder.DefaultAdapter().Add<Word>();
        var word = new Word { Id = Guid.NewGuid(), Text = "hello" };

        config.ObjectTypeListBuilder.Adapt(word).Should().BeSameAs(word);
    }

    [Fact]
    public void Adapt_NonObjectBase_Throws()
    {
        var config = new HarmonyConfig();
        config.ObjectTypeListBuilder.DefaultAdapter().Add<Word>();

        var act = () => config.ObjectTypeListBuilder.Adapt("not an entity");

        act.Should().Throw<ArgumentException>().WithMessage("*does not implement*IObjectBase*");
    }

    [Fact]
    public void Adapt_NoProviderMatches_Throws()
    {
        var config = new HarmonyConfig();
        //two providers, so Adapt takes the multi-provider dispatch path rather than the single-adapter fast path
        config.ObjectTypeListBuilder.DefaultAdapter().Add<Word>();
        config.ObjectTypeListBuilder
            .CustomAdapter<CustomObjectAdapterTests.IMyCustomInterface, CustomObjectAdapterTests.MyClassAdapter>()
            .Add<CustomObjectAdapterTests.MyClass>();

        var act = () => config.ObjectTypeListBuilder.Adapt(new object());

        act.Should().Throw<ArgumentException>().WithMessage("*Unable to adapt*");
    }
}
