using SIL.Harmony.Refs;
using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Sample.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Tests;

/// <summary>
/// Ticket 05: with refs registered, an active tag checkout rolls forward automatically after any
/// apply — through <see cref="DataModel.SyncWith"/> for a synced tag move and through
/// <see cref="DataModel.AddChange"/> for a local one — driven by the post-apply listener rather than
/// a <see cref="RefsDataModel"/> wrapper call. Roll-forward is skipped when the tip did not move.
/// </summary>
public class TagRollForwardListenerTests : IAsyncLifetime
{
    private readonly DataModelTestBase _client1;
    private readonly DataModelTestBase _client2;
    private readonly RefsDataModel _refs1;
    private readonly RefsDataModel _refs2;

    public TagRollForwardListenerTests()
    {
        static void Configure(IServiceCollection services)
        {
            services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
            services.AddHarmonyRefsDataModel();
        }

        _client1 = new DataModelTestBase(configure: Configure);
        _client2 = new DataModelTestBase(configure: Configure);
        _refs1 = _client1.GetRequiredService<RefsDataModel>();
        _refs2 = _client2.GetRequiredService<RefsDataModel>();
    }

    public async ValueTask InitializeAsync()
    {
        await _client1.InitializeAsync();
        await _client2.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _client1.DisposeAsync();
        await _client2.DisposeAsync();
    }

    [Fact]
    public async Task SyncedTagMoveRollsForwardActiveCheckout_ViaDataModelSyncWith()
    {
        var wordId = Guid.NewGuid();
        var first = await _client1.DataModel.AddChange(_client1.LocalClientId, _client1.SetWord(wordId, "first"));
        var second = await _client1.DataModel.AddChange(_client1.LocalClientId, _client1.SetWord(wordId, "second"));
        var tagId = Guid.NewGuid();
        await _refs1.CreateTag(_client1.LocalClientId, tagId, "release", first.Id);

        await _client1.DataModel.SyncWith(_client2.DataModel);

        await _refs2.CheckoutTag(tagId);
        (await _client2.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("first");

        // Move the tag on client1, then sync through the plain DataModel API — the listener, not a
        // RefsDataModel.SyncWith wrapper, must roll client2's active tag view forward.
        await _refs1.MoveTag(_client1.LocalClientId, tagId, second.Id);
        await _client2.DataModel.SyncWith(_client1.DataModel);

        (await _client2.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("second");
    }

    [Fact]
    public async Task LocalTagMoveRollsForwardActiveCheckout_ViaListener()
    {
        var wordId = Guid.NewGuid();
        var first = await _client1.DataModel.AddChange(_client1.LocalClientId, _client1.SetWord(wordId, "first"));
        var second = await _client1.DataModel.AddChange(_client1.LocalClientId, _client1.SetWord(wordId, "second"));
        var tagId = Guid.NewGuid();
        await _refs1.CreateTag(_client1.LocalClientId, tagId, "release", first.Id);

        await _refs1.CheckoutTag(tagId);
        (await _client1.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("first");

        // Author the move straight through DataModel (explicit main assignment, so it is allowed on a
        // tag checkout and lands on main). Only the post-apply listener can roll the view forward here.
        await _client1.DataModel.AddChange(
            _client1.LocalClientId,
            new MoveTagChange(tagId, second.Id),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));

        (await _client1.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("second");
    }

    [Fact(Timeout = 30_000)]
    public async Task LocalTagMoveRollsForwardOnPersistentDatabase()
    {
        // A file-backed database uses a stable, shared apply lock (unlike :memory:, which normalizes
        // to a fresh lock per repository). The lock is not reentrant, so if the post-apply
        // notification fired while the apply lock was held, the listener's roll-forward would deadlock
        // trying to take that same lock. This exercises that path; a regression would hang and trip
        // the timeout.
        var dbPath = Path.Combine(Path.GetTempPath(), $"harmony-rollforward-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnection($"Data Source={dbPath}");
        var client = new DataModelTestBase(connection, configure: services =>
        {
            services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
            services.AddHarmonyRefsDataModel();
        });
        try
        {
            var refs = client.GetRequiredService<RefsDataModel>();

            var wordId = Guid.NewGuid();
            var first = await client.DataModel.AddChange(client.LocalClientId, client.SetWord(wordId, "first"));
            var second = await client.DataModel.AddChange(client.LocalClientId, client.SetWord(wordId, "second"));
            var tagId = Guid.NewGuid();
            await refs.CreateTag(client.LocalClientId, tagId, "release", first.Id);

            await refs.CheckoutTag(tagId);
            (await client.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("first");

            await client.DataModel.AddChange(
                client.LocalClientId,
                new MoveTagChange(tagId, second.Id),
                RefMetadata.SetAssignment(new(), BranchAssignment.Main));

            (await client.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("second");
        }
        finally
        {
            await client.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task NoRollForwardWhenActiveTagTipDidNotMove()
    {
        var wordId = Guid.NewGuid();
        var laterId = Guid.NewGuid();
        var first = await _client1.DataModel.AddChange(_client1.LocalClientId, _client1.SetWord(wordId, "first"));
        var tagId = Guid.NewGuid();
        await _refs1.CreateTag(_client1.LocalClientId, tagId, "release", first.Id);

        await _refs1.CheckoutTag(tagId);
        (await _client1.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("first");

        // A main-line commit that does not touch the tag: the tip is unchanged, so the view stays
        // pinned at the tag tip and the new commit remains invisible in the tag view.
        await _client1.DataModel.AddChange(
            _client1.LocalClientId,
            _client1.SetWord(laterId, "after-tip"),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));

        (await _client1.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("first");
        (await _client1.DataModel.GetLatest<Word>(laterId)).Should().BeNull();
    }
}

/// <summary>
/// Regression: with refs not registered, <see cref="DataModel"/>'s apply paths (local author + sync)
/// behave exactly as before — no listener runs.
/// </summary>
public class CoreOnlyApplyTests : IAsyncLifetime
{
    private readonly DataModelTestBase _client1 = new();
    private readonly DataModelTestBase _client2 = new();

    public async ValueTask InitializeAsync()
    {
        await _client1.InitializeAsync();
        await _client2.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _client1.DisposeAsync();
        await _client2.DisposeAsync();
    }

    [Fact]
    public async Task AuthorAndSyncBehaveAsBeforeWithoutRefs()
    {
        var wordId = Guid.NewGuid();
        await _client1.DataModel.AddChange(_client1.LocalClientId, _client1.SetWord(wordId, "hello"));

        await _client1.DataModel.SyncWith(_client2.DataModel);

        (await _client2.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("hello");
    }
}
