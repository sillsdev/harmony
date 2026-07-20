using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Refs;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests.Refs;

/// <summary>
/// Ticket 04: authoring through <see cref="DataModel"/> directly (no RefsDataModel
/// wrapper) transparently applies the current checkout's branch assignment, and a
/// per-call override via <see cref="RefMetadata.SetAssignment"/> takes precedence.
/// </summary>
public class TransparentAuthoringTests : DataModelTestBase
{
    private readonly RefsDataModel _refs;

    public TransparentAuthoringTests() : base(configure: services =>
    {
        services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
        services.AddHarmonyRefsDataModel();
    })
    {
        _refs = _services.GetRequiredService<RefsDataModel>();
    }

    [Fact]
    public async Task DirectAuthoringOnBranchCheckoutStampsBranchAndIsHiddenOnMain()
    {
        var branchId = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, branchId, "feature");

        await _refs.CheckoutBranch(branchId);
        var commit = await DataModel.AddChange(_localClientId, SetWord(wordId, "on-branch"));

        RefMetadata.GetBranchId(commit.Metadata).Should().Be(branchId);
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("on-branch");

        await _refs.CheckoutMain();
        (await DataModel.GetLatest<Word>(wordId)).Should().BeNull();
    }

    [Fact]
    public async Task DirectAuthoringOnMainCheckoutAuthorsToMain()
    {
        var wordId = Guid.NewGuid();
        var commit = await DataModel.AddChange(_localClientId, SetWord(wordId, "on-main"));

        RefMetadata.GetBranchId(commit.Metadata).Should().BeNull();
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("on-main");
    }

    [Fact]
    public async Task DirectAuthoringOnTagCheckoutThrowsByDefault()
    {
        var tip = await DataModel.AddChange(_localClientId, SetWord(Guid.NewGuid(), "at-tag"));
        var tagId = Guid.NewGuid();
        await _refs.CreateTag(_localClientId, tagId, "v1", tip.Id);
        await _refs.CheckoutTag(tagId);

        var act = () => DataModel.AddChange(_localClientId, SetWord(Guid.NewGuid(), "nope"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DirectAuthoringOnTagCheckoutWritesToMainWhenConfigured()
    {
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

        var tip = await withConfig.DataModel.AddChange(withConfig.LocalClientId, withConfig.SetWord(Guid.NewGuid(), "at-tag"));
        var tagId = Guid.NewGuid();
        await refs.CreateTag(withConfig.LocalClientId, tagId, "v1", tip.Id);
        await refs.CheckoutTag(tagId);

        var newWordId = Guid.NewGuid();
        var commit = await withConfig.DataModel.AddChange(withConfig.LocalClientId, withConfig.SetWord(newWordId, "authored-to-main"));
        RefMetadata.GetBranchId(commit.Metadata).Should().BeNull();

        await refs.CheckoutMain();
        (await withConfig.DataModel.GetLatest<Word>(newWordId))!.Text.Should().Be("authored-to-main");
    }

    [Fact]
    public async Task PerCallOverrideForcesMainOrAnotherBranchRegardlessOfCheckout()
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

    [Fact]
    public async Task AllRegisteredInterceptorsAreInvoked()
    {
        await using var tb = new DataModelTestBase(configure: services =>
        {
            services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
            services.AddHarmonyRefsDataModel();
            services.AddScoped<RecordingInterceptor>();
            services.AddScoped<ICommitInterceptor>(sp => sp.GetRequiredService<RecordingInterceptor>());
        });
        var recording = tb.GetRequiredService<RecordingInterceptor>();
        var refs = tb.GetRequiredService<RefsDataModel>();

        var branchId = Guid.NewGuid();
        await refs.CreateBranch(tb.LocalClientId, branchId, "feature");
        await refs.CheckoutBranch(branchId);
        var commit = await tb.DataModel.AddChange(tb.LocalClientId, tb.SetWord(Guid.NewGuid(), "x"));

        // The extra interceptor ran alongside the checkout interceptor...
        recording.Count.Should().BeGreaterThan(0);
        // ...and the checkout interceptor still applied the branch assignment.
        RefMetadata.GetBranchId(commit.Metadata).Should().Be(branchId);
    }

    [Fact]
    public async Task ExplicitAssignmentMarkerIsNotPersisted()
    {
        var featureId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, featureId, "feature");
        await _refs.CheckoutBranch(featureId);

        var wordId = Guid.NewGuid();
        var commit = await DataModel.AddChange(
            _localClientId,
            SetWord(wordId, "forced-main"),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));

        // Explicit main leaves no branch id and no transient marker behind.
        var stored = await DbContext.Commits.AsNoTracking()
            .SingleAsync(c => c.Id == commit.Id, TestContext.Current.CancellationToken);
        RefMetadata.GetBranchId(stored.Metadata).Should().BeNull();
        stored.Metadata.ExtraMetadata.Should().BeEmpty();
    }

    private sealed class RecordingInterceptor : ICommitInterceptor
    {
        public int Count { get; private set; }
        public void OnCommitAuthored(Commit commit) => Count++;
    }

    [Fact]
    public async Task PerCallOverrideToMainOnTagCheckoutDoesNotThrow()
    {
        await using var withConfig = new DataModelTestBase(configure: services =>
        {
            services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
            services.AddHarmonyRefsDataModel();
        });
        var refs = withConfig.GetRequiredService<RefsDataModel>();

        var tip = await withConfig.DataModel.AddChange(withConfig.LocalClientId, withConfig.SetWord(Guid.NewGuid(), "at-tag"));
        var tagId = Guid.NewGuid();
        await refs.CreateTag(withConfig.LocalClientId, tagId, "v1", tip.Id);
        await refs.CheckoutTag(tagId);

        var wordId = Guid.NewGuid();
        // Explicit main assignment bypasses the tag-authoring rejection even though
        // AllowAuthoringOnTagToMain is false, because the caller opted in per-call.
        var commit = await withConfig.DataModel.AddChange(
            withConfig.LocalClientId,
            withConfig.SetWord(wordId, "explicit-main"),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));
        RefMetadata.GetBranchId(commit.Metadata).Should().BeNull();
    }
}

/// <summary>
/// Regression: with refs not registered, authoring through <see cref="DataModel"/>
/// behaves exactly as before — no interceptor, no branch metadata.
/// </summary>
public class CoreOnlyAuthoringTests : DataModelTestBase
{
    [Fact]
    public async Task AuthoringWithoutRefsAppliesNoBranchMetadata()
    {
        var wordId = Guid.NewGuid();
        var commit = await DataModel.AddChange(_localClientId, SetWord(wordId, "plain"));

        commit.Metadata.ExtraMetadata.Should().BeEmpty();
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("plain");
    }
}
