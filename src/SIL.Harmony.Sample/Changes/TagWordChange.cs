using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class TagWordChange(WordTag wordTag) : CreateChange<WordTag>(wordTag.Id == Guid.Empty ? Guid.NewGuid() : wordTag.Id), ISelfNamedType<TagWordChange>
{
    public Guid WordId { get; } = wordTag.WordId;
    public Guid TagId { get; } = wordTag.TagId;

    public async override ValueTask<WordTag> NewEntity(Commit commit, IChangeContext context)
    {
        var isDuplicate = await IsDuplicate(context);
        return new WordTag()
        {
            Id = EntityId,
            WordId = WordId,
            TagId = TagId,
            DeletedAt = isDuplicate ? commit.DateTime : null
        };
    }

    private async Task<bool> IsDuplicate(IChangeContext context)
    {
        await foreach (var tag in context.GetObjectsReferencing(WordId).OfType<WordTag>())
        {
            if (tag.TagId == TagId)
            {
                return true;
            }
        }
        return false;
    }
}
