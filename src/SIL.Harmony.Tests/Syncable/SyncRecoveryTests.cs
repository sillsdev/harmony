using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;

namespace SIL.Harmony.Tests.Syncable;


/// <summary>
/// Tests recovering from sync where a commit has been lost.
/// </summary>
public class SyncRecoveryTests
{
    private static IChange SetWord(string text, Guid? entityId = null)
    {
        return new SetWordTextChange(entityId ?? Guid.NewGuid(), text);
    }

    private static Commit SetWordCommit(string text, Guid clientId, DateTimeOffset? dateTime = null)
    {
        return CreateCommit(clientId, dateTime ?? DateTimeOffset.Now, SetWord(text));
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

    private async Task ShouldHaveCommit(SyncableTestContext context, Guid commitId)
    {
        var changes = await context.Syncable.GetChanges(new([]));
        changes.MissingFromClient.Should().ContainSingle(c => c.Id == commitId);
    }

    /// <summary>
    /// this test catches the case where one client has an out of order commit
    /// </summary>
    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendPairData), MemberType = typeof(SyncableTestHelpers))]
    public async Task Sync_CanRecoverFromOutOfOrderCommitFromSameClient(ISyncableTestBackend localBackend,
        ISyncableTestBackend remoteBackend)
    {
        var date = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var local = await localBackend.CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();

        var commit1 = SetWordCommit("word", local.ClientId, date);
        await local.Syncable.AddRangeFromSync([commit1]);
        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit1.Id);

        //add an old commit using the client id previously synced
        var commit0 = SetWordCommit("word", local.ClientId, date.Subtract(TimeSpan.FromDays(1)));
        await local.Syncable.AddRangeFromSync([commit0]);

        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit0.Id);
    }

    /// <summary>
    /// this test catches the case where both clients have out of order commits from the same client
    /// </summary>
    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendPairData), MemberType = typeof(SyncableTestHelpers))]
    public async Task Sync_CanRecoverBothClientsHavingOutOfOrderCommitFromSameClient(ISyncableTestBackend localBackend, ISyncableTestBackend remoteBackend)
    {
        var date = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var local = await localBackend.CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();

        var commit1 = SetWordCommit("word", local.ClientId, date);
        await local.Syncable.AddRangeFromSync([commit1]);
        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit1.Id);

        //add an old commit using the client id previously synced
        var commit0 = SetWordCommit("word0", local.ClientId, date.Subtract(TimeSpan.FromDays(1)));
        await local.Syncable.AddRangeFromSync([commit0]);
        var commitA = SetWordCommit("worA", local.ClientId, date.Subtract(TimeSpan.FromDays(2)));
        await remote.Syncable.AddRangeFromSync([commitA]);

        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit0.Id);
        await ShouldHaveCommit(local, commitA.Id);
    }
}
