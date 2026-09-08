using System.Globalization;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests.PropertyBased;

/// <summary>
/// An immutable, deterministic description of a single change, mintable into a real
/// <see cref="IChange"/>. Kept as data (not a live <see cref="IChange"/>) so a schedule can be
/// replayed and fed to many engines reproducibly. <see cref="Signature"/> is the type-specific
/// content string used to compare projected entities across engines (see
/// <see cref="HarmonyEngineHarness.Read"/>).
/// </summary>
public abstract record ChangeSpec(Guid EntityId)
{
    public abstract IChange ToChange();
}

public sealed record SetWordTextSpec(Guid EntityId, string Text) : ChangeSpec(EntityId)
{
    public override IChange ToChange() => new SetWordTextChange(EntityId, Text);
}

public sealed record NewWordSpec(Guid EntityId, string Text) : ChangeSpec(EntityId)
{
    public override IChange ToChange() => new NewWordChange(EntityId, Text);
}

public sealed record SetWordNoteSpec(Guid EntityId, string Note) : ChangeSpec(EntityId)
{
    public override IChange ToChange() => new SetWordNoteChange(EntityId, Note);
}

public sealed record DeleteWordSpec(Guid EntityId) : ChangeSpec(EntityId)
{
    public override IChange ToChange() => new DeleteChange<Word>(EntityId);
}

public sealed record SetAntonymSpec(Guid EntityId, Guid AntonymId) : ChangeSpec(EntityId)
{
    public override IChange ToChange() => new SetAntonymReferenceChange(EntityId, AntonymId);
}

public sealed record NewDefinitionSpec(Guid EntityId, Guid WordId, string Text, string PartOfSpeech, double Order)
    : ChangeSpec(EntityId)
{
    public override IChange ToChange() => new NewDefinitionChange(EntityId)
    {
        WordId = WordId,
        Text = Text,
        PartOfSpeech = PartOfSpeech,
        Order = Order,
    };
}

public sealed record SetDefinitionPartOfSpeechSpec(Guid EntityId, string PartOfSpeech) : ChangeSpec(EntityId)
{
    public override IChange ToChange() => new SetDefinitionPartOfSpeechChange(EntityId, PartOfSpeech);
}

public sealed record DeleteDefinitionSpec(Guid EntityId) : ChangeSpec(EntityId)
{
    public override IChange ToChange() => new DeleteChange<Definition>(EntityId);
}

/// <summary>Type-specific projected-content signatures, used to compare entities across engines.</summary>
public static class EntitySignature
{
    public static string Of(object dbObject) => dbObject switch
    {
        Word w => $"W:{w.Text}|{w.Note}|{w.AntonymId}|{w.ImageResourceId}",
        Definition d =>
            $"D:{d.Text}|{d.PartOfSpeech}|{d.Order.ToString(CultureInfo.InvariantCulture)}|{d.OneWordDefinition}|{d.WordId}",
        _ => dbObject.ToString() ?? "",
    };
}
