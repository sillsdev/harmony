using System.Text.Json;
using SIL.Harmony.Changes;

namespace SIL.Harmony.Tests.Syncable;

public class SyncStateTests
{
    [Fact]
    public async Task BuildSyncState_TypicalUsage()
    {
        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();

        var clientStates = await QueryHelpers.BuildSyncState(Enumerable.Range(1, 10_000)
            .Select(i => new SimpleCommit(i <= 5000 ? clientId1 : clientId2, Guid.NewGuid(),
                DateTimeOffset.UnixEpoch.AddMilliseconds(i)))
            .ToAsyncEnumerable());

        clientStates.Should().HaveCount(2);
        ClientState clientState1 = clientStates.Should().ContainSingle(s => s.ClientId == clientId1).Subject;
        clientState1.MaxTimestamp.Should().Be(5000);
        clientState1.CommitCount.Should().Be(5000);
        clientState1.Hash.Should().NotBe(0);

        ClientState clientState2 = clientStates.Should().ContainSingle(s => s.ClientId == clientId2).Subject;
        clientState2.MaxTimestamp.Should().Be(10_000);
        clientState2.CommitCount.Should().Be(5000);
        clientState2.Hash.Should().NotBe(0);
    }

    [Fact]
    public async Task BuildSyncState_HashIgnoresCommitOrder()
    {
        var clientId = Guid.NewGuid();
        var commits = Enumerable.Range(1, 100)
            .Select(i => new SimpleCommit(clientId, Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMilliseconds(i)))
            .ToArray();

        var forwards = (await QueryHelpers.BuildSyncState(commits.ToAsyncEnumerable())).Should().ContainSingle().Subject;
        var backwards = (await QueryHelpers.BuildSyncState(commits.Reverse().ToAsyncEnumerable())).Should().ContainSingle().Subject;
        backwards.Should().Be(forwards);
    }

    [Fact]
    public async Task BuildSyncState_ADuplicateCommitCancelsOutOfTheHashButNotTheCount()
    {
        var clientId = Guid.NewGuid();
        var commit1 = NewCommit(clientId, DateTimeOffset.UnixEpoch.AddMilliseconds(1));
        var commit2 = NewCommit(clientId, DateTimeOffset.UnixEpoch.AddMilliseconds(2));
        //backdated, so the duplicate doesn't move the head either
        var duplicated = NewCommit(clientId, DateTimeOffset.UnixEpoch);

        var without = (await BuildSyncState(commit1, commit2)).Should().ContainSingle().Subject;
        var withTwice = (await BuildSyncState(commit1, commit2, duplicated, duplicated)).Should().ContainSingle().Subject;

        //hash and head both match a state that is missing a commit; only the count tells them apart,
        //which is why PlanFor compares whole states before deciding two clients agree
        withTwice.Hash.Should().Be(without.Hash);
        withTwice.MaxTimestamp.Should().Be(without.MaxTimestamp);
        withTwice.CommitCount.Should().NotBe(without.CommitCount);
        withTwice.Should().NotBe(without);

        //so the side holding the duplicate pushes everything rather than calling the two in sync
        Commit[] localCommits = [commit1, commit2, duplicated];
        localCommits.GetCommitsMissingFromRemote<Commit, IChange>(new SyncState([withTwice]), new SyncState([without]))
            .Should().BeEquivalentTo(localCommits);
    }

    private static Commit NewCommit(Guid clientId, DateTimeOffset dateTime) =>
        new(Guid.NewGuid()) { ClientId = clientId, HybridDateTime = new HybridDateTime(dateTime, 0) };

    private static Task<ClientState[]> BuildSyncState(params Commit[] commits) =>
        QueryHelpers.BuildSyncState(commits.Select(c => new SimpleCommit(c.ClientId, c.Id, c.DateTime)).ToAsyncEnumerable());

    [Fact]
    public void CanDeserializeFromClientHeadsOnly()
    {
        var json = JsonSerializer.Serialize(new
        {
            ClientHeads = new Dictionary<Guid, long>
            {
                [Guid.NewGuid()] = 5,
                [Guid.NewGuid()] = 10,
            }
        });

        var syncState = JsonSerializer.Deserialize<SyncState>(json);
        syncState.Should().NotBeNull();
        syncState.ClientHeads.Should().HaveCount(2);
        syncState.ClientStates.Should().HaveCount(2);
        syncState.ClientStates.Should().AllSatisfy(s => s.OnlyHasTimestamp.Should().BeTrue());
        syncState.ClientStates.Should().ContainSingle(s => s.MaxTimestamp == 5);
        ClientState clientState = syncState.ClientStates.Should().ContainSingle(s => s.MaxTimestamp == 10).Subject;
        clientState.ClientId.Should().NotBe(Guid.Empty);
        clientState.Hash.Should().Be(0);
        clientState.CommitCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTripsThroughJson(bool camelCase)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = camelCase ? JsonNamingPolicy.CamelCase : null };
        var clientId = Guid.NewGuid();
        var syncState = new SyncState([new ClientState(clientId, MaxTimestamp: 5, CommitCount: 2, Hash: 123)]);

        var json = JsonSerializer.Serialize(syncState, options);
        var roundTripped = JsonSerializer.Deserialize<SyncState>(json, options);

        var propertyNames = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name);
        propertyNames.Should().Equal(camelCase
            ? ["clientHeads", "clientStates"]
            : ["ClientHeads", "ClientStates"]);
        roundTripped.Should().NotBeNull();
        roundTripped.ClientHeads.Should().Equal(new Dictionary<Guid, long> { [clientId] = 5 });
        roundTripped.ClientStates.Should().Equal(syncState.ClientStates);
    }

    [Fact]
    public async Task GetCommitsMissingFromRemote_SendsOnlyNewerCommitsToARemoteThatIsBehind()
    {
        var clientId = Guid.NewGuid();
        Commit[] commits = [.. Enumerable.Range(1, 3).Select(i => NewCommit(clientId, DateTimeOffset.UnixEpoch.AddMilliseconds(i)))];
        var localState = new SyncState(await BuildSyncState(commits));
        //the remote has the same commits up to the first one, so it is not missing that one
        var remoteState = new SyncState(await BuildSyncState(commits[0]));

        commits.GetCommitsMissingFromRemote<Commit, IChange>(localState, remoteState)
            .Should().Equal(commits[1], commits[2]);
    }

    [Theory]
    [InlineData(0, new[] { 1, 2 })]
    [InlineData(1, new[] { 2 })]
    [InlineData(2, new int[0])]
    public async Task GetCommitsMissingFromRemote_SendsCommitsAfterTheHeadOfAPeerThatOnlyReportsHeads(int remoteHeadIndex, int[] expectedIndexes)
    {
        var clientId = Guid.NewGuid();
        Commit[] commits = [.. Enumerable.Range(1, 3).Select(i => NewCommit(clientId, DateTimeOffset.UnixEpoch.AddMilliseconds(i)))];
        var localState = new SyncState(await BuildSyncState(commits));
        //an older peer only sends ClientHeads, so it has no commit count or hash to compare
        var remoteState = new SyncState(new Dictionary<Guid, long>
        {
            [clientId] = commits[remoteHeadIndex].DateTime.ToUnixTimeMilliseconds()
        });

        commits.GetCommitsMissingFromRemote<Commit, IChange>(localState, remoteState)
            .Should().Equal(expectedIndexes.Select(i => commits[i]));
    }
}
