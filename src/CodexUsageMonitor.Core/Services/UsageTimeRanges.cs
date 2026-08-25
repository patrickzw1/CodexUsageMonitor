namespace CodexUsageMonitor.Core.Services;

public sealed record UsageTimeRangeSet(
    DateTimeOffset MonthStartUtc,
    DateTimeOffset MonthEndUtc,
    DateTimeOffset TodayStartUtc,
    DateTimeOffset TomorrowStartUtc,
    DateTimeOffset SevenDayStartUtc,
    DateTimeOffset ThirtyDayStartUtc);

public static class UsageTimeRanges
{
    public static UsageTimeRangeSet Create(DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);

        return new UsageTimeRangeSet(
            StartOfLocalDayUtc(monthStart, timeZone),
            StartOfLocalDayUtc(monthEnd, timeZone),
            StartOfLocalDayUtc(today, timeZone),
            StartOfLocalDayUtc(today.AddDays(1), timeZone),
            StartOfLocalDayUtc(today.AddDays(-6), timeZone),
            StartOfLocalDayUtc(today.AddDays(-29), timeZone));
    }

    public static DateTimeOffset StartOfLocalDayUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
