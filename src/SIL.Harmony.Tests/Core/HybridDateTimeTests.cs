using SIL.Harmony.Core;

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
}