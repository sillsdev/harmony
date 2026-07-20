using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Refs;
using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Refs.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests.Refs;

public class MergeBranchTests : DataModelTestBase
{
    private readonly RefsDataModel _refs;

    public MergeBranchTests() : base(configure: services =>
    {
        services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
        services.AddHarmonyRefsDataModel();
    })
    {
        _refs = _services.GetRequiredService<RefsDataModel>();
    }

    [Fact]
    public async Task MergeInterleavesBranchCommitsIntoMainByAuthorTime()
    {
        var branchId = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, branchId, "feature");

        // main: 1, 2, 3 — branch: A, B, C interleaved by time on the same entity
        await AddAt(_localClientId, NextDate(), SetWord(wordId, "1"));
        await _refs.CheckoutBranch(branchId);
        await AddAt(_localClientId, NextDate(), SetWord(wordId, "A"), BranchMeta(branchId));
        await _refs.CheckoutMain();
        await AddAt(_localClientId, NextDate(), SetWord(wordId, "2"));
        await _refs.CheckoutBranch(branchId);
        await AddAt(_localClientId, NextDate(), SetWord(wordId, "B"), BranchMeta(branchId));
        await _refs.CheckoutMain();
        await AddAt(_localClientId, NextDate(), SetWord(wordId, "3"));
        await _refs.CheckoutBranch(branchId);
        await AddAt(_localClientId, NextDate(), SetWord(wordId, "C"), BranchMeta(branchId));

        await _refs.CheckoutMain();
        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("3");

        var merge = await _refs.MergeBranch(_localClientId, branchId);
        RefMetadata.GetBranchId(merge.Metadata).Should().BeNull();
        merge.ChangeEntities.Should().ContainSingle(ce => ce.Change is MergeBranchChange);

        (await DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("C");

        var branchScoped = await DbContext.Commits.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
        branchScoped.Where(c => RefMetadata.GetBranchId(c.Metadata) == branchId)
            .Should().HaveCount(3);

        var deleted = await DataModel.GetLatest<Branch>(branchId);
        deleted!.DeletedAt.Should().NotBeNull();
        DataModel.QueryLatest<Branch>().ToBlockingEnumerable(TestContext.Current.CancellationToken)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task MergeExpandsReplayFromEarliestBranchCommit()
    {
        var branchId = Guid.NewGuid();
        var earlyId = Guid.NewGuid();
        var lateId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, branchId, "feature");

        await _refs.CheckoutBranch(branchId);
        var earlyBranch = await AddAt(_localClientId, NextDate(), SetWord(earlyId, "early-branch"),
            BranchMeta(branchId));
        await _refs.CheckoutMain();
        await AddAt(_localClientId, NextDate(), SetWord(lateId, "late-main"));

        (await DataModel.GetLatest<Word>(earlyId)).Should().BeNull();
        (await DataModel.GetLatest<Word>(lateId))!.Text.Should().Be("late-main");

        await _refs.MergeBranch(_localClientId, branchId);

        (await DataModel.GetLatest<Word>(earlyId))!.Text.Should().Be("early-branch");
        (await DataModel.GetLatest<Word>(lateId))!.Text.Should().Be("late-main");

        var storedEarly = await DbContext.Commits.AsNoTracking()
            .SingleAsync(c => c.Id == earlyBranch.Id, TestContext.Current.CancellationToken);
        RefMetadata.GetBranchId(storedEarly.Metadata).Should().Be(branchId);
    }

    private static CommitMetadata BranchMeta(Guid branchId)
    {
        var metadata = new CommitMetadata();
        RefMetadata.SetBranchId(metadata, branchId);
        return metadata;
    }

    private async Task<Commit> AddAt(
        Guid clientId,
        DateTimeOffset dateTime,
        IChange change,
        CommitMetadata? commitMetadata = null)
    {
        MockTimeProvider.SetNextDateTime(dateTime);
        return await DataModel.AddChange(clientId, change, commitMetadata);
    }
}
