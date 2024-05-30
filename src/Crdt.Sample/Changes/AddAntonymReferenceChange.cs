using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;

namespace Crdt.Sample.Changes;

public class AddAntonymReferenceChange(Guid entityId, Guid antonymId)
    : EditChange<Word>(entityId), ISelfNamedType<AddAntonymReferenceChange>
{
    public Guid AntonymId { get; set; } = antonymId;

    public override async ValueTask ApplyChange(Word entity, ChangeContext context)
    {
        //if the word being referenced was deleted before this change was applied (could happen after a sync)
        //then we don't want to apply the change
        //if the change was already applied,
        //then this reference is removed via Word.RemoveReference after the change which deletes the Antonym, see SnapshotWorker.MarkDeleted
        if (!await context.IsObjectDeleted(AntonymId))
            entity.AntonymId = AntonymId;
    }
}
