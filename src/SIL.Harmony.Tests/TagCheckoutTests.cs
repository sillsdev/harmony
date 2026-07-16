using SIL.Harmony.Refs;
using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Sample.Models;
using Microsoft.Extensions.DependencyInjection;
using RefTag = SIL.Harmony.Refs.Entities.Tag;

namespace SIL.Harmony.Tests;

public class TagCheckoutTests : DataModelTestBase
{
    private readonly RefsDataModel _refs;

    public TagCheckoutTests() : base(configure: services =>
    {
        services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
        services.AddHarmonyRefsDataModel();
    })
    {
        _refs = _services.GetRequiredService<RefsDataModel>();
    }

    [Fact]
    public async Task TagCheckoutShowsStateAsOfTip()
    {
        var wordId = Guid.NewGuid();
        var tip = await DataModel.AddChange(_localClientId, SetWord(wordId, "at-tag"));
        var tagId = Guid.NewGuid();
        await _refs.CreateTag(_localClientId, tagId, "v1", tip.Id);
        await DataModel.AddChange(_localClientId, SetWord(wordId, "after-tag"));

        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("after-tag");

        await _refs.CheckoutTag(tagId);
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("at-tag");

        await _refs.CheckoutMain();
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("after-tag");
    }

    [Fact]
    public async Task AuthoringOnTagCheckoutIsRejectedByDefault()
    {
        var tip = await DataModel.AddChange(_localClientId, SetWord(Guid.NewGuid(), "x"));
        var tagId = Guid.NewGuid();
        await _refs.CreateTag(_localClientId, tagId, "v1", tip.Id);
        await _refs.CheckoutTag(tagId);

        var act = () => _refs.AddChange(_localClientId, SetWord(Guid.NewGuid(), "nope"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MoveTagWhileCheckedOutRematerializesAndNotifies()
    {
        var wordId = Guid.NewGuid();
        var first = await DataModel.AddChange(_localClientId, SetWord(wordId, "first"));
        var second = await DataModel.AddChange(_localClientId, SetWord(wordId, "second"));
        var tagId = Guid.NewGuid();
        await _refs.CreateTag(_localClientId, tagId, "moving", first.Id);

        RefCheckoutChangedEventArgs? notified = null;
        _refs.CheckoutChanged += (_, args) => notified = args;

        await _refs.CheckoutTag(tagId);
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("first");

        await _refs.MoveTag(_localClientId, tagId, second.Id);

        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("second");
        (await DataModel.GetLatest<RefTag>(tagId))!.TargetCommitId.Should().Be(second.Id);
        notified.Should().NotBeNull();
        notified!.Checkout.Should().BeOfType<TagCheckout>().Which.TagId.Should().Be(tagId);
        notified.TipCommitId.Should().Be(second.Id);
    }

    [Fact]
    public async Task TagOnBranchTipShowsBranchViewAsOfTip()
    {
        var branchId = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        await DataModel.AddChange(_localClientId, new CreateBranchChange(branchId, "feature"));
        await _refs.AddChange(_localClientId, SetWord(wordId, "main"), BranchAssignment.Main);

        await _refs.CheckoutBranch(branchId);
        var branchTip = await _refs.AddChange(_localClientId, SetWord(wordId, "on-branch"));

        var tagId = Guid.NewGuid();
        await _refs.CheckoutMain();
        await _refs.CreateTag(_localClientId, tagId, "wip", branchTip.Id);
        await _refs.AddChange(_localClientId, SetWord(wordId, "later-main"), BranchAssignment.Main);

        await _refs.CheckoutTag(tagId);
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("on-branch");
    }
}
