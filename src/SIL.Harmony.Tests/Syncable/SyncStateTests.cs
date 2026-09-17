namespace SIL.Harmony.Tests.Syncable;

public class SyncStateTests
{
    [Fact]
    public void BuildSyncState_TypicalUsage()
    {
        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();

        var dict = new Dictionary<Guid, ClientStateBuilder>()
        {
            [clientId1] = new ClientStateBuilder() { ClientId = clientId1, Timestamp = 5},
            [clientId2] = new ClientStateBuilder() { ClientId = clientId2, Timestamp = 10},
        };

        var clientStates = QueryHelpers.BuildSyncState(Enumerable.Range(1, 10_000)
                .Select(i => new SimpleCommit(i <= 5000 ? clientId1 : clientId2, Guid.NewGuid()))
                .ToArray(),
            dict);

        clientStates.Should().HaveCount(2);
        ClientState clientState = clientStates.Should().ContainSingle(s => s.ClientId == clientId1).Subject;
        clientState.MaxTimestamp.Should().Be(5);
        clientState.CommitCount.Should().Be(5000);
        clientState.Hash.Should().NotBe(0);

        ClientState clientState2 = clientStates.Should().ContainSingle(s => s.ClientId == clientId2).Subject;
        clientState2.MaxTimestamp.Should().Be(10);
        clientState2.CommitCount.Should().Be(5000);
        clientState2.Hash.Should().NotBe(0);
    }
}
