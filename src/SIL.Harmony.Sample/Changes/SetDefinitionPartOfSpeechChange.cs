using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class SetDefinitionPartOfSpeechChange(Guid entityId, string partOfSpeech) : EditChange<Definition>(entityId), ISelfNamedType<SetDefinitionPartOfSpeechChange>
{
    public string PartOfSpeech { get; } = partOfSpeech;

    public override ValueTask ApplyChange(Definition entity, IChangeContext context)
    {
        entity.PartOfSpeech = PartOfSpeech;
        return ValueTask.CompletedTask;
    }
}