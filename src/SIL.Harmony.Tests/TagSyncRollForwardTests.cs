using SIL.Harmony.Refs;
using SIL.Harmony.Sample.Models;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Tests;

public class TagSyncRollForwardTests : IAsyncLifetime
{
    private readonly DataModelTestBase _client1;
    private readonly DataModelTestBase _client2;
    private readonly RefsDataModel _refs1;
    private readonly RefsDataModel _refs2;

    public TagSyncRollForwardTests()
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
    public async Task SyncMoveTagRollsForwardActiveCheckout()
    {
        var wordId = Guid.NewGuid();
        var first = await _client1.DataModel.AddChange(_client1.LocalClientId, _client1.SetWord(wordId, "first"));
        var second = await _client1.DataModel.AddChange(_client1.LocalClientId, _client1.SetWord(wordId, "second"));
        var tagId = Guid.NewGuid();
        await _refs1.CreateTag(_client1.LocalClientId, tagId, "release", first.Id);

        await _client1.DataModel.SyncWith(_client2.DataModel);

        await _refs2.CheckoutTag(tagId);
        (await _client2.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("first");

        await _refs1.MoveTag(_client1.LocalClientId, tagId, second.Id);
        await _client2.DataModel.SyncWith(_client1.DataModel);

        (await _client2.DataModel.GetLatest<Word>(wordId))!.Text.Should().Be("second");
    }
}
