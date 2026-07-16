using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Refs;
using SIL.Harmony.Refs.Entities;

namespace SIL.Harmony.Tests.Refs;

public class CreateTagTests : DataModelTestBase
{
    private readonly RefsDataModel _refs;

    public CreateTagTests() : base(configure: services =>
    {
        services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
        services.AddHarmonyRefsDataModel();
    })
    {
        _refs = _services.GetRequiredService<RefsDataModel>();
    }

    [Fact]
    public async Task CanCreateAndReadTag()
    {
        var wordId = Guid.NewGuid();
        var tip = await DataModel.AddChange(_localClientId, SetWord(wordId, "at-tip"));
        var tagId = Guid.NewGuid();

        await _refs.CreateTag(_localClientId, tagId, "release", tip.Id);

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
        await _refs.CreateTag(_localClientId, firstId, "dup", tip.Id);
        await _refs.CreateTag(_localClientId, secondId, "dup", tip.Id);

        var both = _refs.ListTags()
            .ToBlockingEnumerable(TestContext.Current.CancellationToken)
            .ToArray();
        both.Should().HaveCount(2);
        both.Select(t => t.Name).Should().AllBe("dup");
    }
}
