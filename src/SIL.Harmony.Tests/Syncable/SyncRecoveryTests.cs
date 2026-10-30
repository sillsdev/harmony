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

    private static readonly DateTimeOffset Date = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    //behind has fewer and older commits from the shared client, so PlanFor withholds the create but still sends a third client's edit to it
    private static async Task<Commit[]> SetUpEditOnWithheldCreate(SyncableTestContext behind, SyncableTestContext ahead)
    {
        var sharedClient = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        var create = CreateCommit(sharedClient, Date.AddDays(-1), SetWord("word", wordId));
        var edit = CreateCommit(Guid.NewGuid(), Date, new SetWordNoteChange(wordId, "note"));
        Commit[] newer = [SetWordCommit("newer1", sharedClient, Date.AddDays(1)), SetWordCommit("newer2", sharedClient, Date.AddDays(2))];
        await behind.Syncable.AddRangeFromSync([create, edit]);
        await ahead.Syncable.AddRangeFromSync(newer);
        return [create, edit, .. newer];
    }

    private static async Task Sync(SyncableTestContext local, SyncableTestContext remote, int times)
    {
        //only the end state is guaranteed; syncs before the last may throw
        for (var i = 1; i < times; i++)
        {
            try { await local.Syncable.SyncWith(remote.Syncable); }
            catch (SyncException) { }
        }
        await local.Syncable.SyncWith(remote.Syncable);
    }

    private static async Task ShouldHaveCommits(SyncableTestContext context, Commit[] commits,
        [CallerArgumentExpression(nameof(context))] string side = "")
    {
        var changes = await context.Syncable.GetChanges(new([], []));
        changes.MissingFromClient.Select(c => c.Id).Should().Contain(commits.Select(c => c.Id), $"{side} should have every commit");
    }

    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendData), MemberType = typeof(SyncableTestHelpers))]
    public async Task Sync_RecoversWithinTwoSyncs_WhenRemoteWithholdsCreateForAnotherClientsEdit(ISyncableTestBackend remoteBackend)
    {
        await using var local = await new DataModelSyncBackend().CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();
        var commits = await SetUpEditOnWithheldCreate(behind: remote, ahead: local);
        var sync = () => local.Syncable.SyncWith(remote.Syncable);

        await sync.Should().ThrowExactlyAsync<SyncException>()
            .Where(e => e.PullError is NotSupportedException && e.PushError == null && e.Applied.MissingFromRemote.Length == 2);
        await sync.Should().NotThrowAsync();

        await ShouldHaveCommits(local, commits);
        await ShouldHaveCommits(remote, commits);
    }

    [Fact]
    public async Task SyncMany_RecoversWithinTwoSyncs_WhenRemoteWithholdsCreateForAnotherClientsEdit()
    {
        await using var local = await new DataModelSyncBackend().CreateAsync();
        await using var remote = await new DataModelSyncBackend().CreateAsync();
        var commits = await SetUpEditOnWithheldCreate(behind: remote, ahead: local);
        var sync = () => local.Syncable.SyncMany([remote.Syncable]);

        await sync.Should().ThrowExactlyAsync<SyncException>()
            .Where(e => e.PullError is NotSupportedException && e.PushError == null);
        await sync.Should().NotThrowAsync();

        await ShouldHaveCommits(local, commits);
        await ShouldHaveCommits(remote, commits);
    }

    [Fact]
    public async Task Sync_ReportsBothErrors_WhenBothDirectionsFail()
    {
        await using var local = await new DataModelSyncBackend().CreateAsync();
        await using var remote = await new DataModelSyncBackend().CreateAsync();
        await SetUpEditOnWithheldCreate(behind: remote, ahead: local);
        await SetUpEditOnWithheldCreate(behind: local, ahead: remote);

        var sync = () => local.Syncable.SyncWith(remote.Syncable);

        await sync.Should().ThrowExactlyAsync<SyncException>()
            .Where(e => e.PullError is NotSupportedException && e.PushError is NotSupportedException);
    }

    [Fact(Skip = "Known sync blocker: each side withholds a create the other needs, so both directions throw on every sync. Revisit when we know more about missing commits.")]
    public async Task Sync_RecoversWithinTwoSyncs_WhenBothWithholdCreatesForOtherClientsEdits()
    {
        await using var local = await new DataModelSyncBackend().CreateAsync();
        await using var remote = await new DataModelSyncBackend().CreateAsync();
        Commit[] commits =
        [
            .. await SetUpEditOnWithheldCreate(behind: remote, ahead: local),
            .. await SetUpEditOnWithheldCreate(behind: local, ahead: remote)
        ];

        await Sync(local, remote, times: 2);

        await ShouldHaveCommits(local, commits);
        await ShouldHaveCommits(remote, commits);
    }

    [Fact(Skip = "Known sync blocker: the side withholding the create is ahead on the shared client, so it only sends newer commits and the other side's push never changes that. Revisit when we know more about missing commits.")]
    public async Task Sync_RecoversWithinThreeSyncs_WhenTheSideWithholdingTheCreateIsAhead()
    {
        await using var local = await new DataModelSyncBackend().CreateAsync();
        await using var remote = await new DataModelSyncBackend().CreateAsync();
        var sharedClient = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        var create = CreateCommit(sharedClient, Date.AddDays(-1), SetWord("word", wordId));
        var edit = CreateCommit(Guid.NewGuid(), Date, new SetWordNoteChange(wordId, "note"));
        var remoteNewer = SetWordCommit("remote", sharedClient, Date.AddDays(5));
        var localCommit = SetWordCommit("local", sharedClient, Date.AddDays(2));
        await remote.Syncable.AddRangeFromSync([create, edit, remoteNewer]);
        await local.Syncable.AddRangeFromSync([localCommit]);

        await Sync(local, remote, times: 3);

        await ShouldHaveCommits(local, [create, edit, remoteNewer, localCommit]);
        await ShouldHaveCommits(remote, [create, edit, remoteNewer, localCommit]);
    }

    [Fact(Skip = "Known sync blocker: no reachable client has the create for this edit. Revisit when we know more about missing commits.")]
    public async Task Sync_Completes_WhenNeitherSideHasTheCreateForAnEdit()
    {
        await using var device = await new DataModelSyncBackend().CreateAsync();
        await using var hub = await new JsonSyncableSyncBackend().CreateAsync();
        //the create only exists on some third client, so no sync between these two can deliver it
        var edit = CreateCommit(Guid.NewGuid(), Date, new SetWordNoteChange(Guid.NewGuid(), "note"));
        await hub.Syncable.AddRangeFromSync([edit]);

        await device.Syncable.SyncWith(hub.Syncable);

        await ShouldHaveCommits(device, [edit]);
    }
}
