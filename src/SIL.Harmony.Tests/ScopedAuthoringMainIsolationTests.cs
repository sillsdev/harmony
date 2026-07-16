using SIL.Harmony.Refs;
using SIL.Harmony.Sample.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Tests;

public class ScopedAuthoringMainIsolationTests : DataModelTestBase
{
    private readonly RefsDataModel _refs;

    public ScopedAuthoringMainIsolationTests() : base(configure: services =>
    {
        services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
        services.AddHarmonyRefsDataModel();
    })
    {
        _refs = _services.GetRequiredService<RefsDataModel>();
    }

    [Fact]
    public async Task BranchAuthoringIsHiddenOnMainCheckout()
    {
        var branchId = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, branchId, "feature");

        await _refs.CheckoutBranch(branchId);
        await DataModel.AddChange(_localClientId, SetWord(wordId, "on-branch"));

        await _refs.CheckoutMain();
        var word = await DataModel.GetLatest<Word>(wordId);
        word.Should().BeNull();
        DataModel.QueryLatest<Word>().ToBlockingEnumerable(TestContext.Current.CancellationToken)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task BranchAuthoringSetsImmutableBranchMetadata()
    {
        var branchId = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, branchId, "feature");

        await _refs.CheckoutBranch(branchId);
        var commit = await DataModel.AddChange(_localClientId, SetWord(wordId, "on-branch"));

        RefMetadata.GetBranchId(commit.Metadata).Should().Be(branchId);

        // later commits do not rewrite earlier assignment
        await DataModel.AddChange(_localClientId, SetWord(wordId, "again"));
        var stored = await DbContext.Commits.AsNoTracking().SingleAsync(c => c.Id == commit.Id, TestContext.Current.CancellationToken);
        RefMetadata.GetBranchId(stored.Metadata).Should().Be(branchId);
    }

    [Fact]
    public async Task AuthoringOverrideCanForceMainOrAnotherBranch()
    {
        var featureId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var mainWordId = Guid.NewGuid();
        var otherWordId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, featureId, "feature");
        await _refs.CreateBranch(_localClientId, otherId, "other");

        await _refs.CheckoutBranch(featureId);

        var mainCommit = await DataModel.AddChange(
            _localClientId,
            SetWord(mainWordId, "forced-main"),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));
        RefMetadata.GetBranchId(mainCommit.Metadata).Should().BeNull();
        (await DataModel.GetLatest<Word>(mainWordId))!.Text.Should().Be("forced-main");

        var otherCommit = await DataModel.AddChange(
            _localClientId,
            SetWord(otherWordId, "other-branch"),
            RefMetadata.SetAssignment(new(), BranchAssignment.ToBranch(otherId)));
        RefMetadata.GetBranchId(otherCommit.Metadata).Should().Be(otherId);
        (await DataModel.GetLatest<Word>(otherWordId)).Should().BeNull();
    }
}
