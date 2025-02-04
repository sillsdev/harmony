namespace SIL.Harmony.Tests.Mocks;

public class MockTimeProvider: IHybridDateTimeProvider
{
    public static HybridDateTime Time(int hour, int counter = 0) => new(new DateTime(2022, 1, 1, 0, 0, 0).AddHours(hour), counter);
    private HybridDateTime? _nextDateTime;
    public void SetNextDateTime(DateTimeOffset dateTimeOffset)
    {
        _nextDateTime = new HybridDateTime(dateTimeOffset, 0);
    }
    public HybridDateTime GetDateTime()
    {
        if (_nextDateTime is null) return new HybridDateTime(DateTimeOffset.UtcNow, 0);
        var result = _nextDateTime;
        _nextDateTime = null;
        return result;
    }

    public void TakeLatestTime(IEnumerable<HybridDateTime> times)
    {
    }
}
