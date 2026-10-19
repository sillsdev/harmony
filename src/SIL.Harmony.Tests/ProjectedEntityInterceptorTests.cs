using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class ProjectedEntityInterceptorTests
{
    private sealed class RecordingInterceptor : IProjectedEntityInterceptor
    {
        public List<IReadOnlyList<(Guid Id, ProjectedChangeKind Kind, string? WordText)>> Invocations { get; } = [];
        public List<ProjectedEntityBatch> Batches { get; } = [];

        public ValueTask OnProjectedEntitiesChanged(ProjectedEntityBatch batch)
        {
            Invocations.Add(Record(batch));
            Batches.Add(batch);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingInterceptor : IProjectedEntityInterceptor
    {
        public ValueTask OnProjectedEntitiesChanged(ProjectedEntityBatch batch) =>
            throw new InvalidOperationException("projected interceptor failed");
    }

    private static IReadOnlyList<(Guid Id, ProjectedChangeKind Kind, string? WordText)> Record(
        ProjectedEntityBatch batch)
    {
        var recorded = new List<(Guid Id, ProjectedChangeKind Kind, string? WordText)>(batch.Changes.Count);
        foreach (var change in batch.Changes)
        {
            string? text = null;
            if (change.ClrType == typeof(Word))
                text = ((Word)change.Entity).Text;
            recorded.Add((change.EntityId, change.Kind, text));
        }

        return recorded;
    }

    private static DataModelTestBase CreateWithInterceptor(RecordingInterceptor interceptor) =>
        new(configure: services =>
        {
            services.AddScoped<IProjectedEntityInterceptor>(_ => interceptor);
        });

    private static ProjectedEntityChange LastChangeFor(RecordingInterceptor interceptor, Guid entityId) =>
        interceptor.Batches[^1].Changes.Single(c => c.EntityId == entityId);

    [Fact]
    public async Task Create_notifies_once_with_upsert()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));

        interceptor.Invocations.Should().ContainSingle();
        interceptor.Invocations[0].Should().BeEquivalentTo([(id, ProjectedChangeKind.Upsert, "a")]);
    }

    [Fact]
    public async Task Update_notifies_upsert_with_latest_text()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));
        await fixture.WriteNextChange(new SetWordTextChange(id, "b"));

        interceptor.Invocations.Should().HaveCount(2);
        interceptor.Invocations[1].Should().BeEquivalentTo([(id, ProjectedChangeKind.Upsert, "b")]);
    }

    [Fact]
    public async Task Delete_notifies_delete()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));
        await fixture.WriteNextChange(new DeleteChange<Word>(id));

        interceptor.Invocations.Should().HaveCount(2);
        interceptor.Invocations[1].Should().BeEquivalentTo([(id, ProjectedChangeKind.Delete, "a")]);
    }

    [Fact]
    public async Task Two_Updates_in_same_AddSnapshots_notifies_only_once()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(
        [
            new NewWordChange(id, "a"),
            new SetWordTextChange(id, "b"),
        ]);

        interceptor.Invocations.Should().ContainSingle();
        interceptor.Invocations[0].Should().BeEquivalentTo([(id, ProjectedChangeKind.Upsert, "b")]);
    }

    [Fact]
    public async Task Create_then_delete_in_same_AddSnapshots_notifies_delete_only()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(
        [
            new NewWordChange(id, "a"),
            new DeleteChange<Word>(id),
        ]);

        interceptor.Invocations.Should().ContainSingle();
        interceptor.Invocations[0].Should().BeEquivalentTo([(id, ProjectedChangeKind.Delete, "a")]);
    }

    [Fact]
    public async Task Delete_then_revive_notifies_upsert()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));
        await fixture.WriteNextChange(new DeleteChange<Word>(id));
        await fixture.WriteNextChange(new NewWordChange(id, "Undeleted"));

        interceptor.Invocations.Should().HaveCount(3);
        interceptor.Invocations[2].Should().BeEquivalentTo([(id, ProjectedChangeKind.Upsert, "Undeleted")]);
    }

    [Fact]
    public async Task Config_delegate_notifies_create()
    {
        var invocations = new List<IReadOnlyList<(Guid Id, ProjectedChangeKind Kind, string? WordText)>>();
        var fixture = new DataModelTestBase(configure: services =>
        {
            services.Configure<HarmonyConfig>(config =>
            {
                config.OnProjectedEntitiesChanged = batch =>
                {
                    invocations.Add(Record(batch));
                    return ValueTask.CompletedTask;
                };
            });
        });
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));

        invocations.Should().ContainSingle();
        invocations[0].Should().BeEquivalentTo([(id, ProjectedChangeKind.Upsert, "a")]);
    }

    [Fact]
    public async Task Di_then_config_see_the_same_batch()
    {
        var interceptor = new RecordingInterceptor();
        var configInvocations = new List<IReadOnlyList<(Guid Id, ProjectedChangeKind Kind, string? WordText)>>();
        var order = new List<string>();
        var fixture = new DataModelTestBase(configure: services =>
        {
            services.AddScoped<IProjectedEntityInterceptor>(_ =>
                new OrderRecordingInterceptor(order, interceptor, "di"));
            services.Configure<HarmonyConfig>(config =>
            {
                config.OnProjectedEntitiesChanged = batch =>
                {
                    order.Add("config");
                    configInvocations.Add(Record(batch));
                    return ValueTask.CompletedTask;
                };
            });
        });
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));

        interceptor.Invocations.Should().ContainSingle();
        configInvocations.Should().ContainSingle();
        interceptor.Invocations[0].Should().BeEquivalentTo(configInvocations[0]);
        interceptor.Invocations[0].Should().BeEquivalentTo([(id, ProjectedChangeKind.Upsert, "a")]);
        order.Should().Equal("di", "config");
    }

    [Fact]
    public async Task Throw_rolls_back_the_save()
    {
        var fixture = new DataModelTestBase(configure: services =>
        {
            services.AddScoped<IProjectedEntityInterceptor, ThrowingInterceptor>();
        });
        var id = Guid.NewGuid();

        var act = async () => await fixture.WriteNextChange(new NewWordChange(id, "a"));
        await act.Should().ThrowAsync<InvalidOperationException>();

        fixture.DataModel.QueryLatest<Word>()
            .ToBlockingEnumerable(TestContext.Current.CancellationToken)
            .Where(w => w.Id == id)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task EnableProjectedTables_false_does_not_notify()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = new DataModelTestBase(configure: services =>
        {
            services.AddScoped<IProjectedEntityInterceptor>(_ => interceptor);
            services.Configure<HarmonyConfig>(config => config.EnableProjectedTables = false);
        });
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));

        interceptor.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Previous_is_null_for_a_create()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));

        LastChangeFor(interceptor, id).Previous.Should().BeNull();
    }

    [Fact]
    public async Task Previous_is_the_old_value_for_an_update()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));
        await fixture.WriteNextChange(new SetWordTextChange(id, "b"));

        LastChangeFor(interceptor, id).Previous.Should().BeOfType<Word>().Which.Text.Should().Be("a");
    }

    [Fact]
    public async Task Previous_is_the_live_entity_for_a_delete()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));
        await fixture.WriteNextChange(new DeleteChange<Word>(id));

        var change = LastChangeFor(interceptor, id);
        change.Kind.Should().Be(ProjectedChangeKind.Delete);
        change.Previous.Should().BeOfType<Word>().Which.Text.Should().Be("a");
    }

    [Fact]
    public async Task Previous_is_null_when_reviving_a_deleted_entity()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));
        await fixture.WriteNextChange(new DeleteChange<Word>(id));
        await fixture.WriteNextChange(new NewWordChange(id, "Undeleted"));

        LastChangeFor(interceptor, id).Previous.Should().BeNull();
    }

    [Fact]
    public async Task Previous_is_set_for_an_entity_deleted_by_cascade()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(wordId, "a"));
        await fixture.WriteNextChange(new NewDefinitionChange(definitionId)
        {
            WordId = wordId,
            Text = "def",
            PartOfSpeech = "noun",
            Order = 1
        });
        await fixture.WriteNextChange(new DeleteChange<Word>(wordId));

        var change = LastChangeFor(interceptor, definitionId);
        change.Kind.Should().Be(ProjectedChangeKind.Delete);
        change.Previous.Should().BeOfType<Definition>().Which.Text.Should().Be("def");
    }

    [Fact]
    public async Task Previous_is_the_pre_batch_state_when_two_commits_change_the_entity_in_one_batch()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        await fixture.WriteNextChange(new NewWordChange(id, "a"));
        await fixture.DataModel.AddManyChanges(Guid.NewGuid(),
            [new SetWordTextChange(id, "b"), new SetWordTextChange(id, "c")],
            () => null,
            changesPerCommitMax: 1);

        var change = LastChangeFor(interceptor, id);
        change.Snapshot.Commit.ChangeEntities.Should().ContainSingle("each change got its own commit");
        ((Word)change.Entity).Text.Should().Be("c");
        change.Previous.Should().BeOfType<Word>().Which.Text.Should().Be("a");
    }

    [Fact]
    public async Task Previous_is_the_projected_state_before_an_out_of_order_commit_rewinds_history()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();

        var create = await fixture.WriteNextChange(new NewWordChange(id, "a"));
        var latest = await fixture.WriteChangeAt(create.DateTime.AddHours(2), new SetWordTextChange(id, "c"));
        //lands between the two, so the replay deletes the "c" snapshot and resumes from the checkpoint at "a", passing through "b"
        await fixture.WriteChangeBefore(latest, new SetWordTextChange(id, "b"));

        interceptor.Batches.Should().HaveCount(3);
        var change = LastChangeFor(interceptor, id);
        ((Word)change.Entity).Text.Should().Be("c");
        change.Previous.Should().BeOfType<Word>().Which.Text.Should().Be("c");
    }

    [Fact]
    public async Task Previous_is_the_projected_state_when_a_commit_before_all_history_replays_from_nothing()
    {
        var interceptor = new RecordingInterceptor();
        var fixture = CreateWithInterceptor(interceptor);
        var id = Guid.NewGuid();
        var earlierId = Guid.NewGuid();

        var create = await fixture.WriteNextChange(new NewWordChange(id, "a"));
        await fixture.WriteNextChange(new SetWordTextChange(id, "c"));
        //no checkpoint precedes it, so every snapshot and projected row is deleted and all of history replays
        await fixture.WriteChangeBefore(create, new NewWordChange(earlierId, "earlier"));

        LastChangeFor(interceptor, id).Previous.Should().BeOfType<Word>().Which.Text.Should().Be("c");
        LastChangeFor(interceptor, earlierId).Previous.Should().BeNull();
    }

    private sealed class OrderRecordingInterceptor(
        List<string> order,
        RecordingInterceptor inner,
        string name) : IProjectedEntityInterceptor
    {
        public ValueTask OnProjectedEntitiesChanged(ProjectedEntityBatch batch)
        {
            order.Add(name);
            return inner.OnProjectedEntitiesChanged(batch);
        }
    }
}
