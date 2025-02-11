﻿using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class AddAntonymReferenceChange(Guid entityId, Guid antonymId)
    : EditChange<Word>(entityId), ISelfNamedType<AddAntonymReferenceChange>
{
    public Guid AntonymId { get; set; } = antonymId;

    public override async ValueTask ApplyChange(Word entity, IChangeContext context)
    {
        //if the word being referenced was deleted before this change was applied (could happen after a sync)
        //then we don't want to apply the change
        //if the change was already applied,
        //then this reference is removed via Word.RemoveReference after the change which deletes the Antonym, see SnapshotWorker.MarkDeleted
        if (!await context.IsObjectDeleted(AntonymId))
            entity.AntonymId = AntonymId;
    }
}
