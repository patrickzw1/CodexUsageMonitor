namespace CodexUsageMonitor.Core.Models;

public sealed record AccountSnapshot(string AccountType, string PlanType);

public sealed record RateLimitWindowSnapshot(
    int? UsedPercent,
    long? WindowDurationMinutes,
    DateTimeOffset? ResetsAt)
{
    public int? RemainingPercent => UsedPercent.HasValue
        ? Math.Clamp(100 - UsedPercent.Value, 0, 100)
        : null;
}

public sealed record QuotaBucketSnapshot(
    string LimitId,
    string? LimitName,
    string? PlanType,
    RateLimitWindowSnapshot? FiveHour,
    RateLimitWindowSnapshot? Weekly);

public sealed record ResetCreditSnapshot(
    string Id,
    string Status,
    string ResetType,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt,
    string? Title,
    string? Description);

public sealed record QuotaSnapshot(
    QuotaBucketSnapshot? General,
    QuotaBucketSnapshot? Spark,
    int? ResetCreditCount,
    bool ResetCreditDetailsComplete,
    IReadOnlyList<ResetCreditSnapshot> ResetCredits);

public sealed record DailyUsagePoint(DateOnly Date, long Tokens)
{
    public DateTimeOffset? CapturedAt { get; init; }
    public bool IsCached { get; init; }
}

public sealed record OfficialUsageSnapshot(
    long? LifetimeTokens,
    long? PeakDailyTokens,
    int? CurrentStreakDays,
    int? LongestStreakDays,
    IReadOnlyList<DailyUsagePoint> DailyUsage);

public sealed record AppServerCompleteness(
    bool Account,
    bool GeneralFiveHourQuota,
    bool GeneralWeeklyQuota,
    bool SparkQuota,
    bool ResetCreditCount,
    bool ResetCreditDetails,
    bool UsageSummary,
    bool DailyUsage)
{
    public bool GeneralQuota => GeneralFiveHourQuota || GeneralWeeklyQuota;

    public static AppServerCompleteness None { get; } = new(false, false, false, false, false, false, false, false);
}

public sealed record AppServerSnapshot(
    AccountSnapshot? Account,
    QuotaSnapshot? Quota,
    OfficialUsageSnapshot? Usage,
    DateTimeOffset CapturedAt,
    string? Error)
{
    public AppServerCompleteness Completeness { get; init; } = AppServerCompleteness.None;
    public bool HasDailyUsageResponse { get; init; }
    public int SuccessfulMethodCount { get; init; }
    public bool ProcessFailure { get; init; }
}

public sealed record TokenUsageEvent(
    string SourceId,
    string EventKey,
    DateTimeOffset TimestampUtc,
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long TotalTokens);

public sealed record TokenPrice(
    string Model,
    decimal InputPerMillion,
    decimal CachedInputPerMillion,
    decimal OutputPerMillion);

public sealed record PricingSnapshot(
    IReadOnlyDictionary<string, TokenPrice> Prices,
    DateTimeOffset UpdatedAt,
    Uri Source,
    bool IsLive,
    string? StatusMessage);

public sealed record ModelUsageSummary(
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long TotalTokens,
    decimal? EstimatedCostUsd)
{
    public double CacheHitPercent => InputTokens <= 0
        ? 0
        : Math.Clamp((double)CachedInputTokens / InputTokens * 100, 0, 100);
}

public sealed record TokenComposition(
    long UncachedInputTokens,
    long CachedInputTokens,
    long VisibleOutputTokens,
    long ReasoningTokens)
{
    public long TotalTokens
    {
        get
        {
            try
            {
                return checked(UncachedInputTokens + CachedInputTokens + VisibleOutputTokens + ReasoningTokens);
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }
    }

    public double UncachedInputPercent => Percentage(UncachedInputTokens);
    public double CachedInputPercent => Percentage(CachedInputTokens);
    public double VisibleOutputPercent => Percentage(VisibleOutputTokens);
    public double ReasoningPercent => Percentage(ReasoningTokens);

    private double Percentage(long value) => TotalTokens <= 0 ? 0 : (double)value / TotalTokens * 100;
}

public sealed record UsageAggregation(
    IReadOnlyList<ModelUsageSummary> Models,
    TokenComposition Composition,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    decimal? EstimatedCacheSavingsUsd,
    double CacheHitPercent)
{
    public decimal? EstimatedCostWithoutCachingUsd { get; init; }
}

public enum ResetHistoryKind
{
    Granted,
    Used,
    Expired
}

public sealed record ResetHistoryItem(
    ResetHistoryKind Kind,
    DateTimeOffset OccurredAt,
    int Count,
    bool IsInferred,
    string? CreditId);

public sealed record WeeklyPace(
    double? ProjectedUsedPercent,
    bool? WillExhaust,
    double? ElapsedPercent)
{
    public static WeeklyPace Unavailable { get; } = new(null, null, null);
}

public sealed record DashboardSnapshot(
    AccountSnapshot? Account,
    QuotaSnapshot? Quota,
    DateTimeOffset? QuotaUpdatedAt,
    bool IsQuotaStale,
    OfficialUsageSnapshot? OfficialUsage,
    DateTimeOffset? OfficialUsageUpdatedAt,
    bool IsOfficialUsageStale,
    UsageAggregation LocalMonthUsage,
    UsageAggregation LocalTodayUsage,
    UsageAggregation LocalSevenDayUsage,
    UsageAggregation LocalThirtyDayUsage,
    PricingSnapshot Pricing,
    IReadOnlyList<ResetHistoryItem> ResetHistory,
    WeeklyPace WeeklyPace,
    DateTimeOffset RefreshedAt,
    string? Warning)
{
    public DashboardPartitionFreshness Freshness { get; init; } = DashboardPartitionFreshness.Empty;
}

public sealed record DashboardPartitionFreshness(
    DateTimeOffset? GeneralQuotaUpdatedAt,
    bool IsGeneralQuotaStale,
    DateTimeOffset? SparkQuotaUpdatedAt,
    bool IsSparkQuotaStale,
    DateTimeOffset? ResetCreditsUpdatedAt,
    bool IsResetCreditsStale,
    DateTimeOffset? UsageSummaryUpdatedAt,
    bool IsUsageSummaryStale,
    DateTimeOffset? DailyUsageUpdatedAt,
    bool IsDailyUsageStale)
{
    public DateTimeOffset? GeneralFiveHourQuotaUpdatedAt { get; init; }
    public bool IsGeneralFiveHourQuotaStale { get; init; }
    public DateTimeOffset? GeneralWeeklyQuotaUpdatedAt { get; init; }
    public bool IsGeneralWeeklyQuotaStale { get; init; }
    public bool HasCurrentDailyUsageResponse { get; init; }

    public static DashboardPartitionFreshness Empty { get; } = new(
        null, false, null, false, null, false, null, false, null, false);
}
