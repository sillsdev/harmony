using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class AddWordImageChange(Guid entityId, Guid imageId) : EditChange<Word>(entityId), ISelfNamedType<AddWordImageChange>
{
    public Guid ImageId { get; } = imageId;

    public override async ValueTask ApplyChange(Word entity, ChangeContext context)
    {
        if (!await context.IsObjectDeleted(ImageId)) entity.ImageResourceId = ImageId;
    }
}