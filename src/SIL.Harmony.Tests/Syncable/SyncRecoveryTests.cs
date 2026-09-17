using System.Runtime.CompilerServices;
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

    private async Task ShouldHaveCommit(SyncableTestContext context,
        Guid commitId,
        [CallerArgumentExpression(nameof(context))] string direction = "",
        [CallerArgumentExpression(nameof(commitId))] string commit = "")
    {
        var changes = await context.Syncable.GetChanges(new([], []));
        changes.MissingFromClient.Should()
            .ContainSingle(c => c.Id == commitId, $"client {direction} should have {commit}");
    }

    private async Task ShouldNotHaveCommit(SyncableTestContext context,
        Guid commitId,
        [CallerArgumentExpression(nameof(context))] string direction = "",
        [CallerArgumentExpression(nameof(commitId))] string commit = "")
    {
        var changes = await context.Syncable.GetChanges(new([], []));
        changes.MissingFromClient.Should()
            .NotContain(c => c.Id == commitId, $"client {direction} should not have {commit}");
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

        var commit1 = SetWordCommit("word1", local.ClientId, date);
        await local.Syncable.AddRangeFromSync([commit1]);
        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit1.Id);

        //add an old commit using the client id previously synced
        var commit0 = SetWordCommit("word0", local.ClientId, date.Subtract(TimeSpan.FromDays(1)));
        await local.Syncable.AddRangeFromSync([commit0]);

        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit0.Id);
    }

    /// <summary>
    /// this test catches the case where both clients have out of order commits from the same client
    /// </summary>
    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendPairData), MemberType = typeof(SyncableTestHelpers))]
    public async Task Sync_CanRecoverBothClientsHavingOneOutOfOrderCommitFromSameClient(ISyncableTestBackend localBackend, ISyncableTestBackend remoteBackend)
    {
        var date = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var local = await localBackend.CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();

        var commit1 = SetWordCommit("word", local.ClientId, date);
        await local.Syncable.AddRangeFromSync([commit1]);
        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit1.Id);

        //add an old commit to both clients
        var commit0 = SetWordCommit("word0", local.ClientId, date.Subtract(TimeSpan.FromDays(1)));
        await local.Syncable.AddRangeFromSync([commit0]);
        var commitA = SetWordCommit("worA", local.ClientId, date.Subtract(TimeSpan.FromDays(2)));
        await remote.Syncable.AddRangeFromSync([commitA]);

        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit0.Id);
        await ShouldHaveCommit(local, commitA.Id);
    }

    /// <summary>
    /// this test catches the case where both clients have out of order commits from the same client
    /// </summary>
    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendPairData), MemberType = typeof(SyncableTestHelpers))]
    public async Task Sync_CanRecoverBothClientsHavingAsymmetricCountOfOutOfOrderCommitFromSameClient(ISyncableTestBackend localBackend, ISyncableTestBackend remoteBackend)
    {
        var date = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var local = await localBackend.CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();

        var commit1 = SetWordCommit("word", local.ClientId, date);
        await local.Syncable.AddRangeFromSync([commit1]);
        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit1.Id);

        //add an old commit to local, which won't get synced on the first try because remote has more commits
        var commit0 = SetWordCommit("word0", local.ClientId, date.Subtract(TimeSpan.FromDays(1)));
        await local.Syncable.AddRangeFromSync([commit0]);
        //adding 2 commits to remote, means that local will not send commit0 to remote (because it has less commits than the remote). This is expected.
        var commitA = SetWordCommit("wordA", local.ClientId, date.Subtract(TimeSpan.FromDays(2)));
        var commitB = SetWordCommit("wordB", local.ClientId, date.Subtract(TimeSpan.FromDays(3)));
        await remote.Syncable.AddRangeFromSync([commitA, commitB]);

        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldNotHaveCommit(remote, commit0.Id);
        await ShouldHaveCommit(local, commitA.Id);
        await ShouldHaveCommit(local, commitB.Id);

        //after a second sync, commit0 should be sent to remote
        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit0.Id);
    }
}
