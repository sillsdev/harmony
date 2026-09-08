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

        public ValueTask OnProjectedEntitiesChanged(ProjectedEntityBatch batch)
        {
            Invocations.Add(Record(batch));
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
                new OrderRecordingInterceptor(order, interceptor));
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

    private sealed class OrderRecordingInterceptor(
        List<string> order,
        RecordingInterceptor inner) : IProjectedEntityInterceptor
    {
        public ValueTask OnProjectedEntitiesChanged(ProjectedEntityBatch batch)
        {
            order.Add("di");
            return inner.OnProjectedEntitiesChanged(batch);
        }
    }
}
