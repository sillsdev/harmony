
namespace Crdt.Core;

/// <summary>
/// Hybrid date time represents a hybrid logical clock that combines a datetime and a counter.
/// <a href="https://martinfowler.com/articles/patterns-of-distributed-systems/hybrid-clock.html">read more here</a>
/// It's use is primarily to avoid conflicts due to a users clock getting reset, we still want their changes to come
/// after the most recent change they made.
/// </summary>
public record HybridDateTime : IComparable<HybridDateTime>
{
    private HybridDateTime()
    {
    }
    public HybridDateTime(DateTimeOffset dateTime, long counter)
    {
        DateTime = dateTime;
        Counter = counter;
    }

    public static HybridDateTime ForTestingNow => new(DateTimeOffset.UtcNow, 0);
    public DateTimeOffset DateTime { get; init; }
    public long Counter { get; init; }

    public int CompareTo(HybridDateTime? other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (ReferenceEquals(null, other)) return 1;
        var dateTimeComparison = DateTime.CompareTo(other.DateTime);
        if (dateTimeComparison != 0) return dateTimeComparison;
        return Counter.CompareTo(other.Counter);
    }

    public static bool operator <(HybridDateTime left, HybridDateTime right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(HybridDateTime left, HybridDateTime right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(HybridDateTime left, HybridDateTime right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(HybridDateTime left, HybridDateTime right)
    {
        return left.CompareTo(right) >= 0;
    }
}

public interface IHybridDateTimeProvider
{
    HybridDateTime GetDateTime();
    void TakeLatestTime(IEnumerable<HybridDateTime> times);
}

public class HybridDateTimeProvider(TimeProvider timeProvider, HybridDateTime lastDateTime) : IHybridDateTimeProvider
{
    public static readonly HybridDateTime DefaultLastDateTime = new(DateTimeOffset.MinValue, 0);
    private readonly object _lockObject = new();

    public HybridDateTime GetDateTime()
    {
        var now = new HybridDateTime(timeProvider.GetUtcNow(), 0);
        lock (_lockObject)
        {
            if (now <= lastDateTime)
            {
                now = new HybridDateTime(lastDateTime.DateTime, lastDateTime.Counter + 1);
            }

            lastDateTime = now;
        }

        return now;
    }

    public void TakeLatestTime(IEnumerable<HybridDateTime> times)
    {
        var max = times.Max();
        if (max is null) return;
        lock (_lockObject)
        {
            if (max > lastDateTime)
            {
                lastDateTime = max;
            }
        }
    }
}
