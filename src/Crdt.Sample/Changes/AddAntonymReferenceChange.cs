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
        if (!await context.IsObjectDeleted(AntonymId))
            entity.AntonymId = AntonymId;
    }
}
