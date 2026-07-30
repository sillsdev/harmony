using SIL.Harmony.Changes;

namespace SIL.Harmony.Tests.Core;

/// <summary>
/// The per-client sync head is a millisecond timestamp, but commits carry a full-precision
/// <see cref="HybridDateTime"/> (DateTime + Counter). When the HLC clamps several commits onto one
/// millisecond, any commit sharing the head's millisecond must still be offered to a peer that only
/// holds an earlier commit at that millisecond. These tests pin that the filter never strands them.
/// </summary>
public class SyncHeadPrecisionTests
{
    private static readonly Guid ClientA = Guid.NewGuid();
    // A whole-second instant: ToUnixTimeMilliseconds() is exact, so a commit stamped here sits right on
    // the millisecond boundary the head truncates to. This is what the HLC produces when it clamps.
    private static readonly DateTimeOffset OnMs = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Commit Commit(HybridDateTime time)
    {
        var id = Guid.NewGuid();
        return new Commit(id)
        {
            ClientId = ClientA,
            HybridDateTime = time,
        };
    }

    private static SyncState LocalHead(params Commit[] commits) =>
        new(new Dictionary<Guid, long>
        {
            [ClientA] = commits.Max(c => c.DateTime.ToUnixTimeMilliseconds())
        });

    // A peer whose newest commit for ClientA is `head` reports this millisecond-only head over the wire.
    private static SyncState RemoteHead(Commit head) =>
        new(new Dictionary<Guid, long>
        {
            [ClientA] = head.DateTime.ToUnixTimeMilliseconds()
        });

    private static List<Guid> MissingIds(IEnumerable<Commit> all, SyncState remote)
    {
        var list = all.ToList();
        return list.GetMissingCommits<Commit, IChange>(LocalHead(list.ToArray()), remote)
            .Select(c => c.Id).ToList();
    }

    [Fact]
    public void OffersCounterSiblingWhenHeadSitsOnTheSharedMillisecond()
    {
        // Two commits with an identical DateTime, differing only by Counter (an HLC clamp).
        // The remote holds the first; its head is that shared millisecond.
        var c0 = Commit(new HybridDateTime(OnMs, 0));
        var c1 = Commit(new HybridDateTime(OnMs, 1));

        MissingIds([c0, c1], RemoteHead(c0)).Should().Contain(c1.Id);
    }

    [Fact]
    public void OffersStrandedCommitAfterHeadAdvancesPast_OnMillisecondBoundary()
    {
        // c1 shares c0's millisecond; c2 is a whole second later. The remote holds only c0.
        // Once the local head advances to c2, the DB pre-filter (DateTime > boundary) would drop c1
        // because its DateTime is exactly on the boundary. That is permanent, silent loss.
        var c0 = Commit(new HybridDateTime(OnMs, 0));
        var c1 = Commit(new HybridDateTime(OnMs, 1));
        var c2 = Commit(new HybridDateTime(OnMs.AddSeconds(5), 0));

        MissingIds([c0, c1, c2], RemoteHead(c0)).Should().Contain(c1.Id);
    }

    [Fact]
    public void OffersStrandedCommitAfterHeadAdvancesPast_WithSubMillisecondTicks()
    {
        // Same as above but the shared instant carries sub-millisecond ticks, so it clears the DB
        // pre-filter and is instead dropped by the in-memory `ms > head` guard. Different code path,
        // same loss.
        var shared = OnMs.AddTicks(3000); // +0.3ms, still truncates to OnMs
        var c0 = Commit(new HybridDateTime(shared, 0));
        var c1 = Commit(new HybridDateTime(shared, 1));
        var c2 = Commit(new HybridDateTime(OnMs.AddSeconds(5), 0));

        MissingIds([c0, c1, c2], RemoteHead(c0)).Should().Contain(c1.Id);
    }

    [Fact]
    public void OffersEveryCounterSiblingInAClampBurst()
    {
        // A whole catch-up window clamped onto one millisecond: only the first reached the remote.
        var burst = Enumerable.Range(0, 5)
            .Select(i => Commit(new HybridDateTime(OnMs, i)))
            .ToArray();

        var missing = MissingIds(burst, RemoteHead(burst[0]));
        missing.Should().Contain(burst.Skip(1).Select(c => c.Id));
    }
}
