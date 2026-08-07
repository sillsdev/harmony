using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests.Syncable;

public class SyncableTests
{
    public static IEnumerable<object[]> SyncableBackends => SyncableTestHelpers.BackendData;

    [Theory]
    [MemberData(nameof(SyncableBackends))]
    public async Task GetSyncState_EmptySyncable(ISyncableTestBackend backend)
    {
        await using var context = await backend.CreateAsync();
        var state = await context.Syncable.GetSyncState();
        state.ClientHeads.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SyncableBackends))]
    public async Task GetSyncState_ReflectsWrittenCommit(ISyncableTestBackend backend)
    {
        await using var context = await backend.CreateAsync();
        var wordCommit = SetWordCommit("x", context.ClientId);
        await context.Syncable.AddRangeFromSync([wordCommit]);

        var state = await context.Syncable.GetSyncState();
        state.ClientHeads.Should().ContainKey(context.ClientId);
        state.ClientHeads[context.ClientId].Should()
            .Be(wordCommit.HybridDateTime.DateTime.ToUnixTimeMilliseconds());
    }

    private static Commit SetWordCommit(string text, Guid clientId, DateTimeOffset? dateTime = null)
    {
        return CreateCommit(clientId, dateTime ?? DateTimeOffset.Now, SetWord(text));
    }

    private static Commit SetWordCommit(Guid entityId, string text, Guid clientId, DateTimeOffset? dateTime = null)
    {
        return CreateCommit(clientId, dateTime ?? DateTimeOffset.Now, SetWord(text, entityId));
    }

    private static Commit CreateCommit(Guid clientId, DateTimeOffset dateTime, params IChange[] changes)
    {
        var commitId = Guid.NewGuid();
        return new Commit(commitId)
        {
            ClientId = clientId,
            HybridDateTime = new HybridDateTime(dateTime, 0),
            ChangeEntities = changes.Select((change, index) => new ChangeEntity<IChange>
            {
                Index = index,
                CommitId = commitId,
                EntityId = change.EntityId,
                Change = change
            }).ToList()
        };
    }

    private static IChange SetWord(string text, Guid? entityId = null)
    {
        return new SetWordTextChange(entityId ?? Guid.NewGuid(), text);
    }

    private static IChange NewDefinition(Guid wordId, string text, string partOfSpeech, Guid definitionId)
    {
        return new NewDefinitionChange(definitionId)
        {
            WordId = wordId,
            Text = text,
            PartOfSpeech = partOfSpeech,
            Order = 0
        };
    }

    [Theory]
    [MemberData(nameof(SyncableBackends))]
    public async Task GetChanges_ReturnsAllForEmptyRemote(ISyncableTestBackend backend)
    {
        await using var context = await backend.CreateAsync();
        var commit = SetWordCommit("entity1", context.ClientId);
        await context.Syncable.AddRangeFromSync([commit]);

        var (missing, _) = await context.Syncable.GetChanges(new SyncState([]));
        missing.Should().ContainSingle(c => c.Id == commit.Id);
    }

    [Theory]
    [MemberData(nameof(SyncableBackends))]
    public async Task GetChanges_ReturnsOnlyNewCommits(ISyncableTestBackend backend)
    {
        await using var context = await backend.CreateAsync();
        var first = SetWordCommit("first", context.ClientId, DateTimeOffset.UnixEpoch.AddDays(1));
        var second = SetWordCommit("second", context.ClientId, DateTimeOffset.UnixEpoch.AddDays(2));
        await context.Syncable.AddRangeFromSync([first, second]);

        var remoteState = new SyncState(new Dictionary<Guid, long>
        {
            [context.ClientId] = first.HybridDateTime.DateTime.ToUnixTimeMilliseconds()
        });
        var (missing, _) = await context.Syncable.GetChanges(remoteState);
        missing.Should().ContainSingle(c => c.Id == second.Id);
    }

    [Theory]
    [MemberData(nameof(SyncableBackends))]
    public async Task AddRangeFromSync_IsIdempotent(ISyncableTestBackend backend)
    {
        await using var context = await backend.CreateAsync();
        var commit = SetWordCommit("entity1", context.ClientId);
        await context.Syncable.AddRangeFromSync([commit]);
        var stateBefore = await context.Syncable.GetSyncState();

        await context.Syncable.AddRangeFromSync([commit]);
        var stateAfter = await context.Syncable.GetSyncState();

        stateAfter.ClientHeads.Should().BeEquivalentTo(stateBefore.ClientHeads);
        var changes = await context.Syncable.GetChanges(new SyncState([]));
        changes.MissingFromClient.Should().HaveCount(1);
    }

    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendPairData), MemberType = typeof(SyncableTestHelpers))]
    public async Task SyncWith_ReturnsMissingCommits(ISyncableTestBackend localBackend, ISyncableTestBackend remoteBackend)
    {
        await using var local = await localBackend.CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();
        var commit = SetWordCommit("entity1", local.ClientId);
        await local.Syncable.AddRangeFromSync([commit]);

        var syncResults = await local.Syncable.SyncWith(remote.Syncable);
        syncResults.MissingFromRemote.Should().ContainSingle(c => c.Id == commit.Id);
    }

    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendPairData), MemberType = typeof(SyncableTestHelpers))]
    public async Task CanSyncSimpleChange(ISyncableTestBackend localBackend, ISyncableTestBackend remoteBackend)
    {
        await using var local = await localBackend.CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();
        var entity1Id = Guid.NewGuid();
        var entity2Id = Guid.NewGuid();
        var localCommit = SetWordCommit(entity1Id, "entity1", local.ClientId);
        await local.Syncable.AddRangeFromSync([localCommit]);
        await local.MirrorToReadModelAsync([localCommit]);
        (await local.ReadModel.GetLatest<Word>(entity1Id))!.Text.Should().Be("entity1");
        var remoteCommit = SetWordCommit(entity2Id, "entity2", remote.ClientId);
        await remote.Syncable.AddRangeFromSync([remoteCommit]);
        await remote.MirrorToReadModelAsync([remoteCommit]);
        (await remote.ReadModel.GetLatest<Word>(entity2Id))!.Text.Should().Be("entity2");

        var syncResults = await local.Syncable.SyncWith(remote.Syncable);
        await SyncableTestHelpers.MirrorToReadModelsAsync(local, remote, syncResults);

        var client1Snapshot = await local.ReadModel.GetProjectSnapshot();
        var client2Snapshot = await remote.ReadModel.GetProjectSnapshot();
        client1Snapshot.LastCommitHash.Should().Be(client2Snapshot.LastCommitHash);
        var client2Entity1 = await remote.ReadModel.GetBySnapshotId<Word>(client2Snapshot.Snapshots[entity1Id].Id);
        client2Entity1.Text.Should().Be("entity1");
        var client1Entity2 = await local.ReadModel.GetBySnapshotId<Word>(client1Snapshot.Snapshots[entity2Id].Id);
        client1Entity2.Text.Should().Be("entity2");
    }

    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendPairData), MemberType = typeof(SyncableTestHelpers))]
    public async Task CanSyncMultipleTimes(ISyncableTestBackend localBackend, ISyncableTestBackend remoteBackend)
    {
        await using var local = await localBackend.CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();
        var entity1Id = Guid.NewGuid();
        var entity2Id = Guid.NewGuid();
        var firstLocalCommit = SetWordCommit(entity1Id, "entity1", local.ClientId, DateTimeOffset.UnixEpoch.AddDays(1));
        await local.Syncable.AddRangeFromSync([firstLocalCommit]);
        await local.MirrorToReadModelAsync([firstLocalCommit]);
        var firstSyncResults = await local.Syncable.SyncWith(remote.Syncable);
        await SyncableTestHelpers.MirrorToReadModelsAsync(local, remote, firstSyncResults);

        var remoteCommit = SetWordCommit(entity2Id, "entity2", remote.ClientId, DateTimeOffset.UnixEpoch.AddDays(2));
        await remote.Syncable.AddRangeFromSync([remoteCommit]);
        await remote.MirrorToReadModelAsync([remoteCommit]);
        var secondLocalCommit = SetWordCommit(entity1Id, "entity1.1", local.ClientId, DateTimeOffset.UnixEpoch.AddDays(3));
        await local.Syncable.AddRangeFromSync([secondLocalCommit]);
        await local.MirrorToReadModelAsync([secondLocalCommit]);

        var secondSyncResults = await local.Syncable.SyncWith(remote.Syncable);
        await SyncableTestHelpers.MirrorToReadModelsAsync(local, remote, secondSyncResults);

        var client2Entity = await remote.ReadModel.GetLatest<Word>(entity1Id);
        client2Entity!.Text.Should().Be("entity1.1");
        var client1Entity = await local.ReadModel.GetLatest<Word>(entity1Id);
        client1Entity!.Text.Should().Be("entity1.1");
    }

    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendPairData), MemberType = typeof(SyncableTestHelpers))]
    public async Task CanSync_AddDependentWithMultipleChanges(ISyncableTestBackend localBackend, ISyncableTestBackend remoteBackend)
    {
        await using var local = await localBackend.CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();
        var entity1Id = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var commits = new[]
        {
            CreateCommit(local.ClientId, DateTimeOffset.UnixEpoch.AddDays(1), SetWord("entity1", entity1Id)),
            CreateCommit(local.ClientId, DateTimeOffset.UnixEpoch.AddDays(2), NewDefinition(entity1Id, "def1", "pos1", definitionId)),
            CreateCommit(local.ClientId, DateTimeOffset.UnixEpoch.AddDays(3),
                new SetDefinitionPartOfSpeechChange(definitionId, "pos2"))
        };
        await local.Syncable.AddRangeFromSync(commits);
        await local.MirrorToReadModelAsync(commits);

        var syncResults = await remote.Syncable.SyncWith(local.Syncable);
        await SyncableTestHelpers.MirrorToReadModelsAsync(remote, local, syncResults);

        remote.ReadModel.QueryLatest<Definition>().ToBlockingEnumerable(TestContext.Current.CancellationToken).Should()
            .BeEquivalentTo(local.ReadModel.QueryLatest<Definition>().ToBlockingEnumerable(TestContext.Current.CancellationToken));
    }
}
