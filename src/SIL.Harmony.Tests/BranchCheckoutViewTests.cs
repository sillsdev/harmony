using SIL.Harmony.Refs;
using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Sample.Models;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Tests;

public class BranchCheckoutViewTests : DataModelTestBase
{
    private readonly RefsDataModel _refs;

    public BranchCheckoutViewTests() : base(configure: services =>
    {
        services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
        services.AddHarmonyRefsDataModel();
    })
    {
        _refs = _services.GetRequiredService<RefsDataModel>();
    }

    [Fact]
    public async Task BranchCheckoutShowsMainPlusBranchCommits()
    {
        var branchId = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        await DataModel.AddChange(_localClientId, new CreateBranchChange(branchId, "feature"));
        await _refs.AddChange(_localClientId, SetWord(wordId, "main"), BranchAssignment.Main);

        await _refs.CheckoutBranch(branchId);
        await _refs.AddChange(_localClientId, SetWord(wordId, "on-branch"));

        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("on-branch");

        await _refs.CheckoutMain();
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("main");
    }

    [Fact]
    public async Task OtherBranchCommitsStayInvisible()
    {
        var featureId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var featureWordId = Guid.NewGuid();
        var otherWordId = Guid.NewGuid();
        await DataModel.AddChange(_localClientId, new CreateBranchChange(featureId, "feature"));
        await DataModel.AddChange(_localClientId, new CreateBranchChange(otherId, "other"));

        await _refs.CheckoutBranch(featureId);
        await _refs.AddChange(_localClientId, SetWord(featureWordId, "feature-word"));

        await _refs.CheckoutBranch(otherId);
        await _refs.AddChange(_localClientId, SetWord(otherWordId, "other-word"));

        await _refs.CheckoutBranch(featureId);
        (await DataModel.GetLatest<Word>(featureWordId))!.Text.Should().Be("feature-word");
        (await DataModel.GetLatest<Word>(otherWordId)).Should().BeNull();
    }

    [Fact]
    public async Task BranchAndMainCommitsInterleaveByAuthorTime()
    {
        var branchId = Guid.NewGuid();
        var earlyId = Guid.NewGuid();
        var lateId = Guid.NewGuid();
        await DataModel.AddChange(_localClientId, new CreateBranchChange(branchId, "feature"));

        // t1 main
        await _refs.AddChange(_localClientId, SetWord(earlyId, "early-main"), BranchAssignment.Main);
        await _refs.CheckoutBranch(branchId);
        // t2 branch (between main commits chronologically once late main is written)
        await _refs.AddChange(_localClientId, SetWord(Guid.NewGuid(), "mid-branch"));
        await _refs.CheckoutMain();
        // t3 main
        await _refs.AddChange(_localClientId, SetWord(lateId, "late-main"), BranchAssignment.Main);

        await _refs.CheckoutBranch(branchId);
        var words = DataModel.QueryLatest<Word>()
            .ToBlockingEnumerable(TestContext.Current.CancellationToken)
            .Select(w => w.Text)
            .OrderBy(t => t)
            .ToArray();

        // All three domain commits visible on branch; main-only would miss mid-branch
        words.Should().BeEquivalentTo(["early-main", "late-main", "mid-branch"]);

        await _refs.CheckoutMain();
        var mainWords = DataModel.QueryLatest<Word>()
            .ToBlockingEnumerable(TestContext.Current.CancellationToken)
            .Select(w => w.Text)
            .OrderBy(t => t)
            .ToArray();
        mainWords.Should().BeEquivalentTo(["early-main", "late-main"]);
    }
}
