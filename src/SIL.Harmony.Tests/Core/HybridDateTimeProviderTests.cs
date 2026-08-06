namespace SIL.Harmony.Tests.Core;

/// <summary>
/// Exercises the real <see cref="HybridDateTimeProvider"/> clock logic. The rest of the suite injects a
/// MockTimeProvider that stubs this behaviour out, so without these tests the hybrid-clock guarantees
/// (monotonic timestamps when the wall clock goes backward, advancing past synced commits) have no coverage.
/// </summary>
public class HybridDateTimeProviderTests
{
    private sealed class SettableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset _baseTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (HybridDateTimeProvider provider, SettableTimeProvider clock) NewProvider(HybridDateTime lastDateTime)
    {
        var clock = new SettableTimeProvider { Now = _baseTime };
        return (new HybridDateTimeProvider(clock, lastDateTime), clock);
    }

    [Fact]
    public void GetDateTime_UsesWallClockWhenItIsAheadOfLastTime()
    {
        var (provider, clock) = NewProvider(new HybridDateTime(_baseTime.AddHours(-1), 5));
        clock.Now = _baseTime;

        var result = provider.GetDateTime();

        result.DateTime.Should().Be(_baseTime);
        result.Counter.Should().Be(0, "a forward clock resets the counter");
    }

    [Fact]
    public void GetDateTime_WhenClockGoesBackward_ReusesLastTimeAndIncrementsCounter()
    {
        //lastDateTime is in the future relative to the wall clock (clock was reset backwards)
        var (provider, clock) = NewProvider(new HybridDateTime(_baseTime.AddHours(1), 0));
        clock.Now = _baseTime;

        var first = provider.GetDateTime();
        first.DateTime.Should().Be(_baseTime.AddHours(1), "the newer timestamp is kept, not the regressed wall clock");
        first.Counter.Should().Be(1);

        //still behind, so the counter keeps climbing to stay monotonic
        var second = provider.GetDateTime();
        second.DateTime.Should().Be(_baseTime.AddHours(1));
        second.Counter.Should().Be(2);

        (second > first).Should().BeTrue();
    }

    [Fact]
    public void GetDateTime_WhenClockEqualsLastTime_IncrementsCounter()
    {
        var (provider, clock) = NewProvider(new HybridDateTime(_baseTime, 0));
        clock.Now = _baseTime;

        var result = provider.GetDateTime();

        result.DateTime.Should().Be(_baseTime);
        result.Counter.Should().Be(1, "equal timestamps must still advance via the counter");
    }

    [Fact]
    public void TakeLatestTime_AdvancesToNewestReceivedTime()
    {
        var (provider, clock) = NewProvider(new HybridDateTime(_baseTime, 0));

        provider.TakeLatestTime([
            new HybridDateTime(_baseTime.AddHours(-1), 9),
            new HybridDateTime(_baseTime.AddHours(2), 3),
            new HybridDateTime(_baseTime.AddHours(1), 0),
        ]);

        //next local write must sort after the newest received commit even though the wall clock is older
        clock.Now = _baseTime;
        var next = provider.GetDateTime();
        next.DateTime.Should().Be(_baseTime.AddHours(2));
        next.Counter.Should().Be(4);
    }

    [Fact]
    public void TakeLatestTime_IgnoresTimesOlderThanCurrent()
    {
        var (provider, clock) = NewProvider(new HybridDateTime(_baseTime.AddHours(5), 2));

        provider.TakeLatestTime([
            new HybridDateTime(_baseTime, 0),
            new HybridDateTime(_baseTime.AddHours(1), 0),
        ]);

        //current time was newer than everything received, so it is unchanged
        clock.Now = _baseTime;
        var next = provider.GetDateTime();
        next.DateTime.Should().Be(_baseTime.AddHours(5));
        next.Counter.Should().Be(3);
    }
}
