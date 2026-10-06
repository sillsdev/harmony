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

        //local has fewer commits than remote at the same head, so on its own it would assume remote already has commit0
        var commit0 = SetWordCommit("word0", local.ClientId, date.Subtract(TimeSpan.FromDays(1)));
        await local.Syncable.AddRangeFromSync([commit0]);
        var commitA = SetWordCommit("wordA", local.ClientId, date.Subtract(TimeSpan.FromDays(2)));
        var commitB = SetWordCommit("wordB", local.ClientId, date.Subtract(TimeSpan.FromDays(3)));
        await remote.Syncable.AddRangeFromSync([commitA, commitB]);

        await local.Syncable.SyncWith(remote.Syncable);
        await ShouldHaveCommit(remote, commit0.Id);
        await ShouldHaveCommit(local, commitA.Id);
        await ShouldHaveCommit(local, commitB.Id);
    }

    private static readonly DateTimeOffset Date = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    //behind gets fewer, older commits from a shared client than ahead, so it withholds the create but still sends the edit
    private static async Task<Commit[]> WithholdCreateButSendEdit(SyncableTestContext behind, SyncableTestContext ahead)
    {
        var sharedClient = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        var create = CreateCommit(sharedClient, Date.AddDays(-1), SetWord("word", wordId));
        var edit = CreateCommit(Guid.NewGuid(), Date, new SetWordNoteChange(wordId, "note"));
        Commit[] newer = [SetWordCommit("newer1", sharedClient, Date.AddDays(1)), SetWordCommit("newer2", sharedClient, Date.AddDays(2))];
        await behind.Syncable.AddRangeFromSync([create, edit]);
        await ahead.Syncable.AddRangeFromSync(newer);
        return [create, edit, ..newer];
    }

    private static async Task ShouldHaveCommits(SyncableTestContext context, Commit[] commits,
        [CallerArgumentExpression(nameof(context))] string side = "")
    {
        var changes = await context.Syncable.GetChanges(new([], []));
        changes.MissingFromClient.Select(c => c.Id).Should().Contain(commits.Select(c => c.Id), $"{side} should have every commit");
    }

    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendData), MemberType = typeof(SyncableTestHelpers))]
    public async Task Sync_Converges_WhenRemoteWithholdsCreateForAnotherClientsEdit(ISyncableTestBackend remoteBackend)
    {
        await using var local = await new DataModelSyncBackend().CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();
        var commits = await WithholdCreateButSendEdit(behind: remote, ahead: local);

        await local.Syncable.SyncWith(remote.Syncable);

        await ShouldHaveCommits(local, commits);
        await ShouldHaveCommits(remote, commits);
    }

    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendData), MemberType = typeof(SyncableTestHelpers))]
    public async Task Sync_Converges_WhenLocalWithholdsCreateForAnotherClientsEdit(ISyncableTestBackend localBackend)
    {
        await using var local = await localBackend.CreateAsync();
        await using var remote = await new DataModelSyncBackend().CreateAsync();
        var commits = await WithholdCreateButSendEdit(behind: local, ahead: remote);

        await local.Syncable.SyncWith(remote.Syncable);

        await ShouldHaveCommits(local, commits);
        await ShouldHaveCommits(remote, commits);
    }

    [Fact]
    public async Task Sync_Converges_WhenBothWithholdCreatesForOtherClientsEdits()
    {
        await using var local = await new DataModelSyncBackend().CreateAsync();
        await using var remote = await new DataModelSyncBackend().CreateAsync();
        Commit[] commits =
        [
            ..await WithholdCreateButSendEdit(behind: remote, ahead: local),
            ..await WithholdCreateButSendEdit(behind: local, ahead: remote)
        ];

        await local.Syncable.SyncWith(remote.Syncable);

        await ShouldHaveCommits(local, commits);
        await ShouldHaveCommits(remote, commits);
    }

    [Theory]
    [MemberData(nameof(SyncableTestHelpers.BackendPairData), MemberType = typeof(SyncableTestHelpers))]
    public async Task Sync_Converges_WhenBehindSideHasACommitTheOtherLacks(ISyncableTestBackend localBackend, ISyncableTestBackend remoteBackend)
    {
        await using var local = await localBackend.CreateAsync();
        await using var remote = await remoteBackend.CreateAsync();
        var client = Guid.NewGuid();
        Commit[] localCommits = [SetWordCommit("a1", client, Date.AddDays(1)), SetWordCommit("a2", client, Date.AddDays(2)), SetWordCommit("a3", client, Date.AddDays(5))];
        var remoteCommit = SetWordCommit("b1", client, Date.AddDays(3));
        await local.Syncable.AddRangeFromSync(localCommits);
        await remote.Syncable.AddRangeFromSync([remoteCommit]);

        await local.Syncable.SyncWith(remote.Syncable);

        await ShouldHaveCommits(local, [..localCommits, remoteCommit]);
        await ShouldHaveCommits(remote, [..localCommits, remoteCommit]);
    }

    [Fact]
    public async Task Sync_AsksTheRemoteOnce_WhenEitherSideIsSimplyBehind()
    {
        await using var local = await new DataModelSyncBackend().CreateAsync();
        await using var remoteContext = await new DataModelSyncBackend().CreateAsync();
        var remote = new CountingSyncable(remoteContext.Syncable);
        var client = Guid.NewGuid();
        await local.Syncable.AddRangeFromSync([SetWordCommit("a1", client, Date)]);
        await local.Syncable.SyncWith(remote);
        await remote.AddRangeFromSync([SetWordCommit("a2", client, Date.AddDays(1))]);
        await local.Syncable.SyncWith(remote);
        await local.Syncable.AddRangeFromSync([SetWordCommit("a3", client, Date.AddDays(2))]);
        await local.Syncable.SyncWith(remote);

        remote.GetChangesCalls.Should().Be(3);
    }

    [Fact]
    public async Task SyncMany_Converges_WhenRemotesHoldDifferentCommitsOfOneClient()
    {
        await using var local = await new DataModelSyncBackend().CreateAsync();
        await using var remote1 = await new DataModelSyncBackend().CreateAsync();
        await using var remote2 = await new DataModelSyncBackend().CreateAsync();
        var client = Guid.NewGuid();
        Commit[] commits1 = [SetWordCommit("a1", client, Date.AddDays(1)), SetWordCommit("a2", client, Date.AddDays(2)), SetWordCommit("a3", client, Date.AddDays(5))];
        Commit[] commits2 = [SetWordCommit("b1", client, Date.AddDays(3))];
        await remote1.Syncable.AddRangeFromSync(commits1);
        await remote2.Syncable.AddRangeFromSync(commits2);

        await local.Syncable.SyncMany([remote1.Syncable, remote2.Syncable]);

        await ShouldHaveCommits(local, [..commits1, ..commits2]);
        await ShouldHaveCommits(remote1, [..commits1, ..commits2]);
        await ShouldHaveCommits(remote2, [..commits1, ..commits2]);
    }

    private sealed class CountingSyncable(ISyncable inner) : ISyncable
    {
        public int GetChangesCalls { get; private set; }

        public Task<ChangesResult<Commit>> GetChanges(SyncState otherHeads)
        {
            GetChangesCalls++;
            return inner.GetChanges(otherHeads);
        }

        public Task AddRangeFromSync(IEnumerable<Commit> commits) => inner.AddRangeFromSync(commits);
        public Task<SyncState> GetSyncState() => inner.GetSyncState();
        public Task<SyncResults> SyncWith(ISyncable remoteModel) => inner.SyncWith(remoteModel);
        public Task SyncMany(ISyncable[] remotes) => inner.SyncMany(remotes);
        public ValueTask<bool> ShouldSync() => inner.ShouldSync();
    }
}
