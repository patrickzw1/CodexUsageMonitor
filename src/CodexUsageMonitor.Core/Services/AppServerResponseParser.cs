using System.Text.Json;
using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.Core.Services;

public sealed record QuotaParseResult(
    QuotaSnapshot? Snapshot,
    bool GeneralFiveHourComplete,
    bool GeneralWeeklyComplete,
    bool SparkComplete,
    bool ResetCreditCountComplete,
    bool ResetCreditDetailsComplete);

public sealed record OfficialUsageParseResult(
    OfficialUsageSnapshot? Snapshot,
    bool SummaryComplete,
    bool DailyUsageComplete);

public static class AppServerResponseParser
{
    private const long MaxWindowDurationMinutes = 60L * 24 * 31;

    public static AccountSnapshot? ParseAccount(JsonElement result)
    {
        if (!result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var planType = ReadString(account, "planType");
        return string.IsNullOrWhiteSpace(planType)
            ? null
            : new AccountSnapshot(ReadString(account, "type") ?? "unknown", planType);
    }

    public static QuotaParseResult ParseQuotaDetailed(JsonElement result)
    {
        JsonElement? generalElement = null;
        JsonElement? sparkElement = null;

        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets)
            && buckets.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in buckets.EnumerateObject())
            {
                var limitName = ReadString(property.Value, "limitName");
                if (property.Name.Equals("codex", StringComparison.OrdinalIgnoreCase))
                {
                    generalElement = property.Value.Clone();
                }

                if (!string.IsNullOrWhiteSpace(limitName)
                    && limitName.Contains("spark", StringComparison.OrdinalIgnoreCase))
                {
                    sparkElement = property.Value.Clone();
                }
            }
        }

        if (generalElement is null
            && result.TryGetProperty("rateLimits", out var legacy)
            && legacy.ValueKind == JsonValueKind.Object)
        {
            generalElement = legacy.Clone();
        }

        var general = generalElement.HasValue ? ParseBucket(generalElement.Value, "codex") : null;
        var spark = sparkElement.HasValue ? ParseBucket(sparkElement.Value, "spark") : null;

        int? availableCount = null;
        var countComplete = false;
        var detailsComplete = false;
        var credits = new List<ResetCreditSnapshot>();
        if (result.TryGetProperty("rateLimitResetCredits", out var resetSummary)
            && resetSummary.ValueKind == JsonValueKind.Object)
        {
            availableCount = ReadNonNegativeInt(resetSummary, "availableCount");
            countComplete = availableCount.HasValue;
            if (resetSummary.TryGetProperty("credits", out var creditRows))
            {
                detailsComplete = creditRows.ValueKind == JsonValueKind.Array;
                if (detailsComplete)
                {
                    foreach (var row in creditRows.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Object)
                        {
                            detailsComplete = false;
                            continue;
                        }

                        var id = ReadString(row, "id");
                        var grantedAt = ReadUnixSeconds(row, "grantedAt");
                        var status = ReadString(row, "status");
                        var resetType = ReadString(row, "resetType");
                        var expiresAt = ReadUnixSeconds(row, "expiresAt");
                        var hasInvalidExpiry = row.TryGetProperty("expiresAt", out var expiryValue)
                                               && expiryValue.ValueKind is not JsonValueKind.Null
                                               && expiresAt is null;
                        if (string.IsNullOrWhiteSpace(id)
                            || grantedAt is null
                            || string.IsNullOrWhiteSpace(status)
                            || string.IsNullOrWhiteSpace(resetType)
                            || hasInvalidExpiry)
                        {
                            detailsComplete = false;
                            continue;
                        }

                        credits.Add(new ResetCreditSnapshot(
                            id,
                            status,
                            resetType,
                            grantedAt.Value,
                            expiresAt,
                            ReadString(row, "title"),
                            ReadString(row, "description")));
                    }

                    if (availableCount is { } expectedAvailable
                        && credits.Count(item => item.Status.Equals("available", StringComparison.OrdinalIgnoreCase)) < expectedAvailable)
                    {
                        detailsComplete = false;
                    }
                }
            }
        }

        var snapshot = general is null && spark is null && availableCount is null && !result.TryGetProperty("rateLimitResetCredits", out _)
            ? null
            : new QuotaSnapshot(general, spark, availableCount, detailsComplete, credits);
        return new QuotaParseResult(
            snapshot,
            general?.FiveHour is not null && IsCompleteWindow(general.FiveHour),
            general?.Weekly is not null && IsCompleteWindow(general.Weekly),
            spark is not null
            && (spark.FiveHour is not null || spark.Weekly is not null)
            && (spark.FiveHour is null || IsCompleteWindow(spark.FiveHour))
            && (spark.Weekly is null || IsCompleteWindow(spark.Weekly)),
            countComplete,
            countComplete && detailsComplete);
    }

    public static OfficialUsageParseResult ParseOfficialUsageDetailed(JsonElement result)
    {
        long? lifetime = null;
        long? peak = null;
        int? currentStreak = null;
        int? longestStreak = null;
        var hasSummaryObject = result.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object;
        if (hasSummaryObject)
        {
            lifetime = ReadNonNegativeLong(summary, "lifetimeTokens");
            peak = ReadNonNegativeLong(summary, "peakDailyTokens");
            currentStreak = ReadNonNegativeInt(summary, "currentStreakDays");
            longestStreak = ReadNonNegativeInt(summary, "longestStreakDays");
        }

        var daily = new List<DailyUsagePoint>();
        var dailyRowsValid = true;
        var hasDailyArray = result.TryGetProperty("dailyUsageBuckets", out var buckets)
                            && buckets.ValueKind == JsonValueKind.Array;
        if (hasDailyArray)
        {
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object)
                {
                    dailyRowsValid = false;
                    continue;
                }

                var dateText = ReadString(bucket, "startDate");
                var tokens = ReadLong(bucket, "tokens");
                if (DateOnly.TryParse(dateText, out var date) && tokens is >= 0)
                {
                    daily.Add(new DailyUsagePoint(date, tokens.Value));
                }
                else
                {
                    dailyRowsValid = false;
                }
            }
        }

        var recognized = hasSummaryObject || hasDailyArray;
        var snapshot = recognized
            ? new OfficialUsageSnapshot(lifetime, peak, currentStreak, longestStreak, daily)
            : null;
        return new OfficialUsageParseResult(
            snapshot,
            hasSummaryObject
            && lifetime.HasValue
            && peak.HasValue
            && currentStreak.HasValue
            && longestStreak.HasValue,
            hasDailyArray && daily.Count > 0 && dailyRowsValid);
    }

    private static QuotaBucketSnapshot ParseBucket(JsonElement element, string fallbackId)
    {
        RateLimitWindowSnapshot? fiveHour = null;
        RateLimitWindowSnapshot? weekly = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var window = ParseWindow(value);
            if (window.WindowDurationMinutes == 300)
            {
                fiveHour = window;
            }
            else if (window.WindowDurationMinutes == 10_080)
            {
                weekly = window;
            }
        }

        return new QuotaBucketSnapshot(
            ReadString(element, "limitId") ?? fallbackId,
            ReadString(element, "limitName"),
            ReadString(element, "planType"),
            fiveHour,
            weekly);
    }

    private static RateLimitWindowSnapshot ParseWindow(JsonElement element)
        => new(
            ReadPercentage(element, "usedPercent"),
            ReadWindowDuration(element, "windowDurationMins"),
            ReadUnixSeconds(element, "resetsAt"));

    private static bool IsCompleteWindow(RateLimitWindowSnapshot? window)
        => window?.UsedPercent.HasValue == true
           && window.WindowDurationMinutes is > 0 and <= MaxWindowDurationMinutes
           && window.ResetsAt.HasValue;

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static long? ReadLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static long? ReadWindowDuration(JsonElement element, string name)
    {
        var value = ReadLong(element, name);
        return value is > 0 and <= MaxWindowDurationMinutes ? value : null;
    }

    private static int? ReadNonNegativeInt(JsonElement element, string name)
    {
        var value = ReadInt(element, name);
        return value is >= 0 ? value : null;
    }

    private static long? ReadNonNegativeLong(JsonElement element, string name)
    {
        var value = ReadLong(element, name);
        return value is >= 0 ? value : null;
    }

    private static int? ReadPercentage(JsonElement element, string name)
    {
        var value = ReadInt(element, name);
        return value is >= 0 and <= 100 ? value : null;
    }

    private static DateTimeOffset? ReadUnixSeconds(JsonElement element, string name)
    {
        var seconds = ReadLong(element, name);
        if (!seconds.HasValue
            || seconds.Value < DateTimeOffset.MinValue.ToUnixTimeSeconds()
            || seconds.Value > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
    }
}
