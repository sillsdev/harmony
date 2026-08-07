namespace SIL.Harmony.Tests.Core;

public class HybridDateTimeTests
{
    [Fact]
    public void Equals_TrueWhenTheSame()
    {
        var dateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);
        var otherDateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);

        (dateTime == otherDateTime).Should().BeTrue();
    }

    [Fact]
    public void Equals_FalseWhenDifferentDateTime()
    {
        var dateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);
        var otherDateTime = new HybridDateTime(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);

        (dateTime != otherDateTime).Should().BeTrue();
    }

    [Fact]
    public void Equals_FalseWhenDifferentCounter()
    {
        var dateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);
        var otherDateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 1);

        dateTime.Should().NotBe(otherDateTime);
    }

    [Fact]
    public void Constructor_ThrowsArgumentOutOfRangeExceptionWhenCounterIsNegative()
    {
        Action action = () => new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), -1);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CompareTo_ReturnsOneWhenOtherIsNull()
    {
        var dateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);
        dateTime.CompareTo(null).Should().Be(1);
    }

    [Fact]
    public void CompareTo_ReturnsNegativeOneWhenThisIsLessThanOther()
    {
        var dateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);
        var otherDateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero), 0);

        var result = dateTime.CompareTo(otherDateTime);
        result.Should().BeLessThan(0);
    }

    [Fact]
    public void CompareTo_ReturnsZeroWhenThisIsEqualToOther()
    {
        var dateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);
        var otherDateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);

        var result = dateTime.CompareTo(otherDateTime);
        result.Should().Be(0);
    }

    [Fact]
    public void CompareTo_ReturnsOneWhenThisIsGreaterThanOther()
    {
        var dateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero), 0);
        var otherDateTime = new HybridDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), 0);

        var result = dateTime.CompareTo(otherDateTime);
        result.Should().Be(1);
    }

    [Fact]
    public void CompareTo_BreaksTiesByCounterWhenDateTimeIsEqual()
    {
        var instant = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lower = new HybridDateTime(instant, 1);
        var higher = new HybridDateTime(instant, 2);

        lower.CompareTo(higher).Should().BeLessThan(0);
        higher.CompareTo(lower).Should().BeGreaterThan(0);
    }

    [Theory]
    //same instant, ordered by counter
    [InlineData("2000-01-01", 1, "2000-01-01", 2, true)]
    [InlineData("2000-01-01", 2, "2000-01-01", 1, false)]
    //different instant, counter is irrelevant
    [InlineData("2000-01-01", 9, "2000-01-02", 0, true)]
    [InlineData("2000-01-02", 0, "2000-01-01", 9, false)]
    public void Operators_OrderByDateTimeThenCounter(string leftDate, long leftCounter, string rightDate, long rightCounter, bool leftIsSmaller)
    {
        var left = new HybridDateTime(DateTimeOffset.Parse(leftDate + "T00:00:00Z"), leftCounter);
        var right = new HybridDateTime(DateTimeOffset.Parse(rightDate + "T00:00:00Z"), rightCounter);

        (left < right).Should().Be(leftIsSmaller);
        (left <= right).Should().Be(leftIsSmaller);
        (left > right).Should().Be(!leftIsSmaller);
        (left >= right).Should().Be(!leftIsSmaller);
    }

    [Fact]
    public void Operators_LessOrEqualAndGreaterOrEqual_TrueWhenEqual()
    {
        var instant = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var left = new HybridDateTime(instant, 3);
        var right = new HybridDateTime(instant, 3);

        (left <= right).Should().BeTrue();
        (left >= right).Should().BeTrue();
        (left < right).Should().BeFalse();
        (left > right).Should().BeFalse();
    }
}
