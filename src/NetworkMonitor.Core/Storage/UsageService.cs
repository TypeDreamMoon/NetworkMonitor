namespace NetworkMonitor.Core.Storage;

/// <summary>Builds today / this week / this month / total reports from the minute ledger.</summary>
public sealed class UsageService(UsageStore store)
{
    private readonly UsageStore _store = store;

    /// <summary>When set, only these interface keys are counted; null/empty means all.</summary>
    public IReadOnlyCollection<string>? InterfaceKeys { get; set; }

    public UsageReport Build(DateTimeOffset now, TimeZoneInfo? timeZone = null, int seriesDays = 30,
        IReadOnlyCollection<string>? keys = null)
    {
        keys ??= InterfaceKeys;
        timeZone ??= TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var today = DateOnly.FromDateTime(localNow.DateTime);

        var weekStart = StartOfWeek(today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var tomorrow = today.AddDays(1);

        long todayFrom = ToBucket(today, timeZone);
        long tomorrowBucket = ToBucket(tomorrow, timeZone);
        long weekFrom = ToBucket(weekStart, timeZone);
        long monthFrom = ToBucket(monthStart, timeZone);

        var seriesStart = today.AddDays(-(seriesDays - 1));
        var daily = _store.DailyLocal(ToBucket(seriesStart, timeZone), tomorrowBucket, timeZone, keys);
        var byDay = daily.ToDictionary(d => d.Date);

        var series = new List<DailyUsage>(seriesDays);
        for (int i = 0; i < seriesDays; i++)
        {
            var day = seriesStart.AddDays(i);
            series.Add(byDay.TryGetValue(day, out var usage) ? usage : new DailyUsage(day, 0, 0));
        }

        return new UsageReport
        {
            Today = _store.SumRange(todayFrom, tomorrowBucket, keys),
            Week = _store.SumRange(weekFrom, tomorrowBucket, keys),
            Month = _store.SumRange(monthFrom, tomorrowBucket, keys),
            Total = _store.SumAll(keys),
            FirstTrackedDate = _store.FirstTrackedLocal(timeZone),
            DailySeries = series,
        };
    }

    public static DateOnly StartOfWeek(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); // Monday

    /// <summary>Local midnight -> UTC minute bucket, with a DST-invalid-time guard.</summary>
    public static long ToBucket(DateOnly localDate, TimeZoneInfo timeZone)
    {
        var midnight = DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);

        var utc = TimeZoneInfo.ConvertTimeToUtc(midnight, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds() / 60;
    }
}
