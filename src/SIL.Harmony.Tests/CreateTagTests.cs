using SIL.Harmony.Refs;
using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Refs.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Tests;

public class CreateTagTests : DataModelTestBase
{
    public CreateTagTests() : base(configure: services =>
    {
        services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
    })
    {
    }

    [Fact]
    public async Task CanCreateAndReadTag()
    {
        var wordId = Guid.NewGuid();
        var tip = await DataModel.AddChange(_localClientId, SetWord(wordId, "at-tip"));
        var tagId = Guid.NewGuid();

        await DataModel.AddChange(_localClientId, new CreateTagChange(tagId, "release", tip.Id));

        var tag = await DataModel.GetLatest<Tag>(tagId);
        tag.Should().NotBeNull();
        tag!.Id.Should().Be(tagId);
        tag.Name.Should().Be("release");
        tag.TargetCommitId.Should().Be(tip.Id);
    }

    [Fact]
    public async Task DuplicateTagNamesAreAllowed()
    {
        var tip = await DataModel.AddChange(_localClientId, SetWord(Guid.NewGuid(), "x"));
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await DataModel.AddChange(_localClientId, new CreateTagChange(firstId, "dup", tip.Id));
        await DataModel.AddChange(_localClientId, new CreateTagChange(secondId, "dup", tip.Id));

        var both = DataModel.QueryLatest<Tag>().ToBlockingEnumerable(TestContext.Current.CancellationToken).ToArray();
        both.Should().HaveCount(2);
        both.Select(t => t.Name).Should().AllBe("dup");
    }
}
