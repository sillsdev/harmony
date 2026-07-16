using SIL.Harmony.Refs;
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

        var act = () => DataModel.AddChange(_localClientId, SetWord(Guid.NewGuid(), "nope"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MoveTagWhileCheckedOutRematerializes()
    {
        var wordId = Guid.NewGuid();
        var first = await DataModel.AddChange(_localClientId, SetWord(wordId, "first"));
        var second = await DataModel.AddChange(_localClientId, SetWord(wordId, "second"));
        var tagId = Guid.NewGuid();
        await _refs.CreateTag(_localClientId, tagId, "moving", first.Id);

        await _refs.CheckoutTag(tagId);
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("first");

        await _refs.MoveTag(_localClientId, tagId, second.Id);

        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("second");
        (await DataModel.GetLatest<RefTag>(tagId))!.TargetCommitId.Should().Be(second.Id);
        _refs.Checkout.Should().BeOfType<TagCheckout>().Which.TagId.Should().Be(tagId);
    }

    [Fact]
    public async Task AuthoringOnTagCheckoutWritesToMainWhenConfigured()
    {
        // Fresh model with AllowAuthoringOnTagToMain enabled via CrdtConfig.
        await using var withConfig = new DataModelTestBase(configure: services =>
        {
            services.Configure<CrdtConfig>(config =>
            {
                config.AddHarmonyRefs();
                config.AllowAuthoringOnTagToMain = true;
            });
            services.AddHarmonyRefsDataModel();
        });
        var refs = withConfig.GetRequiredService<RefsDataModel>();
        refs.AllowAuthoringOnTagToMain.Should().BeTrue();

        var tip = await withConfig.DataModel.AddChange(withConfig.LocalClientId, withConfig.SetWord(Guid.NewGuid(), "at-tag"));
        var tagId = Guid.NewGuid();
        await refs.CreateTag(withConfig.LocalClientId, tagId, "v1", tip.Id);
        await refs.CheckoutTag(tagId);

        var newWordId = Guid.NewGuid();
        await withConfig.DataModel.AddChange(withConfig.LocalClientId, withConfig.SetWord(newWordId, "authored-to-main"));

        // The change was authored to main (no branch id), so it is visible on the main checkout.
        await refs.CheckoutMain();
        (await withConfig.DataModel.GetLatest<Word>(newWordId))!.Text.Should().Be("authored-to-main");
    }

    [Fact]
    public async Task TagOnBranchTipShowsBranchViewAsOfTip()
    {
        var branchId = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, branchId, "feature");
        await DataModel.AddChange(_localClientId, SetWord(wordId, "main"),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));

        await _refs.CheckoutBranch(branchId);
        var branchTip = await DataModel.AddChange(_localClientId, SetWord(wordId, "on-branch"));

        var tagId = Guid.NewGuid();
        await _refs.CheckoutMain();
        await _refs.CreateTag(_localClientId, tagId, "wip", branchTip.Id);
        await DataModel.AddChange(_localClientId, SetWord(wordId, "later-main"),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));

        await _refs.CheckoutTag(tagId);
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("on-branch");
    }
}
