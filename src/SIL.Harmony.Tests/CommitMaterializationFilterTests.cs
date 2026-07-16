using SIL.Harmony.Changes;
using SIL.Harmony.Core;
using SIL.Harmony.Sample.Models;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Tests;

public class CommitMaterializationFilterTests : DataModelTestBase
{
    public CommitMaterializationFilterTests() : base(configure: services =>
    {
        services.Configure<CrdtConfig>(config =>
        {
            config.CommitMaterializationFilter = new ExcludeMarkedCommitsFilter();
        });
    })
    {
    }

    [Fact]
    public async Task ExcludedCommitDoesNotAffectGetLatest()
    {
        var entityId = Guid.NewGuid();

        await DataModel.AddChange(_localClientId, SetWord(entityId, "included"));
        var excluded = await DataModel.AddChange(
            _localClientId,
            SetWord(entityId, "excluded"),
            new CommitMetadata { ["materialize"] = "false" });

        var word = await DataModel.GetLatest<Word>(entityId);
        word!.Text.Should().Be("included");
        // Excluded commits are still stored; only materialization skips them
        DbContext.Commits.Should().Contain(c => c.Id == excluded.Id);
    }

    [Fact]
    public async Task LateArrivingIncludedCommitReplaysFilteredHistory()
    {
        var entityId = Guid.NewGuid();

        var first = await DataModel.AddChange(_localClientId, SetWord(entityId, "first"));
        await DataModel.AddChange(
            _localClientId,
            SetWord(entityId, "skipped"),
            new CommitMetadata { ["materialize"] = "false" });

        (await DataModel.GetLatest<Word>(entityId))!.Text.Should().Be("first");

        var late = new Commit
        {
            ClientId = _localClientId,
            HybridDateTime = new HybridDateTime(first.DateTime.AddHours(1), 0),
            Metadata = new CommitMetadata()
        };
        late.ChangeEntities.Add(new ChangeEntity<IChange>
        {
            Change = SetWord(entityId, "late"),
            Index = 0,
            CommitId = late.Id,
            EntityId = entityId
        });
        await AddCommitsViaSync([late]);

        var word = await DataModel.GetLatest<Word>(entityId);
        // Included apply order: first → late; skipped never materializes
        word!.Text.Should().Be("late");
    }

    [Fact]
    public async Task LateArrivingExcludedCommitDoesNotDuplicateLaterSnapshots()
    {
        var entityId = Guid.NewGuid();

        var first = await DataModel.AddChange(_localClientId, SetWord(entityId, "first"));
        await DataModel.AddChange(_localClientId, SetWord(entityId, "third"));
        (await DataModel.GetLatest<Word>(entityId))!.Text.Should().Be("third");
        var snapshotCountBefore = DbContext.Snapshots.Count(s => s.EntityId == entityId);

        var excluded = new Commit
        {
            ClientId = _localClientId,
            HybridDateTime = new HybridDateTime(first.DateTime.AddHours(1), 0),
            Metadata = new CommitMetadata { ["materialize"] = "false" }
        };
        excluded.ChangeEntities.Add(new ChangeEntity<IChange>
        {
            Change = SetWord(entityId, "excluded-middle"),
            Index = 0,
            CommitId = excluded.Id,
            EntityId = entityId
        });
        await AddCommitsViaSync([excluded]);

        (await DataModel.GetLatest<Word>(entityId))!.Text.Should().Be("third");
        DbContext.Snapshots.Count(s => s.EntityId == entityId).Should().Be(snapshotCountBefore);
        DbContext.Commits.Should().Contain(c => c.Id == excluded.Id);
    }

    /// <summary>
    /// Excludes commits marked with ExtraMetadata materialize=false.
    /// </summary>
    private sealed class ExcludeMarkedCommitsFilter : ICommitMaterializationFilter
    {
        public bool Include(Commit commit) =>
            commit.Metadata["materialize"] != "false";
    }
}
