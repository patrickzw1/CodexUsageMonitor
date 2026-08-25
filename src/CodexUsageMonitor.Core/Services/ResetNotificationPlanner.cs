using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.Core.Services;

public sealed record ResetNotificationAlert(int DaysRemaining, DateTimeOffset ExpiresAt, IReadOnlyList<string> CreditIds);

public static class ResetNotificationPlanner
{
    public static IReadOnlyList<ResetNotificationAlert> CreateAlerts(
        IEnumerable<ResetCreditSnapshot> credits,
        DateTimeOffset now)
    {
        return credits
            .Where(item => item.Status.Equals("available", StringComparison.OrdinalIgnoreCase) && item.ExpiresAt.HasValue)
            .Select(item => new
            {
                item.Id,
                ExpiresAt = item.ExpiresAt!.Value,
                Days = (int)Math.Ceiling((item.ExpiresAt.Value - now).TotalDays)
            })
            .Where(item => item.ExpiresAt > now)
            .Where(item => item.Days is 7 or 3 or 1 or 0)
            .GroupBy(item => new { item.Days, item.ExpiresAt })
            .Select(group => new ResetNotificationAlert(
                group.Key.Days,
                group.Key.ExpiresAt,
                group.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray()))
            .OrderBy(item => item.ExpiresAt)
            .ToArray();
    }
}
