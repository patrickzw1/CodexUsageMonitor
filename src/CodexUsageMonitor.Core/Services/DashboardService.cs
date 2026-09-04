using System.Collections.Concurrent;
using System.Text.Json;
using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.Core.Services;

public sealed class DashboardService : IDashboardService, IUpdateCheckSettingsStore
{
    private static readonly TimeSpan[] AppServerBackoff =
    {
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2)
    };

    private readonly UsageHistoryRepository _repository;
    private readonly IRolloutUsageScanner _rolloutScanner;
    private readonly ICodexAppServerClient _appServerClient;
    private readonly IPriceCatalogService _priceCatalogService;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeZoneInfo _timeZone;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private PricingSnapshot _latestPricing = PriceCatalogService.CreateBuiltInSnapshot();
    private bool _initialized;
    private int _consecutiveAppServerFailures;
    private DateTimeOffset _nextAppServerAttemptUtc = DateTimeOffset.MinValue;

    public DashboardService(
        UsageHistoryRepository repository,
        IRolloutUsageScanner rolloutScanner,
        ICodexAppServerClient appServerClient,
        IPriceCatalogService priceCatalogService,
        Func<DateTimeOffset>? utcNow = null,
        TimeZoneInfo? timeZone = null)
    {
        _repository = repository;
        _rolloutScanner = rolloutScanner;
        _appServerClient = appServerClient;
        _priceCatalogService = priceCatalogService;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public static DashboardService CreateDefault()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageMonitor");
        var repository = new UsageHistoryRepository(Path.Combine(appData, "usage.db"));
        var scanner = new RolloutUsageScanner(new RolloutParser(), repository);
        var appServer = new CodexAppServerClient(new CodexExecutableLocator());
        var pricing = new PriceCatalogService(PriceCatalogService.CreateDefaultHttpClient(), Path.Combine(appData, "prices.json"));
        return new DashboardService(repository, scanner, appServer, pricing);
    }

    public Task<DashboardSnapshot> RefreshAsync(
        bool forceAppServer = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RefreshCoreAsync(forceAppServer, cancellationToken);
    }

    public async Task<UsageAggregation> QueryLocalUsageAsync(
        DateOnly startInclusive,
        DateOnly endInclusive,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_utcNow(), _timeZone).DateTime);
        if (startInclusive > endInclusive)
        {
            throw new ArgumentException("The start date must not be later than the end date.", nameof(startInclusive));
        }

        if (endInclusive > today)
        {
            throw new ArgumentOutOfRangeException(nameof(endInclusive), "The end date must not be in the future.");
        }

        var fromUtc = UsageTimeRanges.StartOfLocalDayUtc(startInclusive, _timeZone);
        var toUtc = UsageTimeRanges.StartOfLocalDayUtc(endInclusive.AddDays(1), _timeZone);
        var events = await _repository.QueryEventsAsync(fromUtc, toUtc, cancellationToken);
        return UsageCalculator.Aggregate(events, _latestPricing.Prices);
    }

    private async Task<DashboardSnapshot> RefreshCoreAsync(
        bool forceAppServer,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var nowUtc = _utcNow();
        var warnings = new ConcurrentQueue<string>();

        var scanTask = Task.Run(() => _rolloutScanner.RefreshAsync(cancellationToken), cancellationToken);
        var pricingTask = ReadPricingAsync(warnings, cancellationToken);
        var appServerTask = ReadAppServerAsync(nowUtc, forceAppServer, warnings, cancellationToken);

        await ReadScanResultAsync(scanTask, warnings, cancellationToken);
        var pricing = await pricingTask;
        _latestPricing = pricing;
        var appServer = await appServerTask;

        var storedQuota = await _repository.GetLatestQuotaSnapshotAsync(cancellationToken);
        var completeness = appServer.Completeness;
        var account = completeness.Account && appServer.Account is not null
            ? appServer.Account
            : storedQuota?.Account ?? appServer.Account;
        var currentQuota = appServer.Quota;
        var storedQuotaValue = storedQuota?.Quota;

        var general = MergeGeneralQuota(
            currentQuota?.General,
            storedQuotaValue?.General,
            completeness.GeneralFiveHourQuota,
            completeness.GeneralWeeklyQuota,
            nowUtc);
        var spark = completeness.SparkQuota
            ? currentQuota?.Spark
            : storedQuotaValue?.Spark ?? currentQuota?.Spark;
        var resetCount = completeness.ResetCreditCount
            ? currentQuota?.ResetCreditCount
            : storedQuotaValue?.ResetCreditCount ?? currentQuota?.ResetCreditCount;
        var authoritativeNoResetCredits = completeness.ResetCreditCount
                                          && currentQuota?.ResetCreditCount == 0;
        var useCurrentResetDetails = completeness.ResetCreditDetails;
        var resetCredits = authoritativeNoResetCredits
            ? []
            : useCurrentResetDetails
            ? currentQuota?.ResetCredits ?? []
            : storedQuotaValue?.ResetCredits.Count > 0
                ? storedQuotaValue.ResetCredits
                : currentQuota?.ResetCredits ?? [];
        var resetDetailsComplete = authoritativeNoResetCredits
            || useCurrentResetDetails
            || !completeness.ResetCreditDetails && storedQuotaValue?.ResetCreditDetailsComplete == true;
        var quota = general is null && spark is null && resetCount is null && resetCredits.Count == 0
            ? null
            : new QuotaSnapshot(general, spark, resetCount, resetDetailsComplete, resetCredits);
        var quotaPlan = completeness.GeneralQuota && !string.IsNullOrWhiteSpace(currentQuota?.General?.PlanType)
            ? currentQuota.General.PlanType
            : completeness.Account
                ? appServer.Account?.PlanType
                : general?.PlanType ?? account?.PlanType;
        quota = ApplyPlanRules(quotaPlan, quota);

        var accountUpdatedAt = completeness.Account && appServer.Account is not null
            ? appServer.CapturedAt
            : storedQuota?.AccountCapturedAt;
        var generalFiveHourUpdatedAt = completeness.GeneralFiveHourQuota
            ? appServer.CapturedAt
            : storedQuota?.GeneralFiveHourCapturedAt;
        var generalWeeklyUpdatedAt = completeness.GeneralWeeklyQuota
            ? appServer.CapturedAt
            : storedQuota?.GeneralWeeklyCapturedAt;
        var sparkUpdatedAt = completeness.SparkQuota ? appServer.CapturedAt : storedQuota?.SparkCapturedAt;
        var resetUpdatedAt = authoritativeNoResetCredits || completeness.ResetCreditDetails
            ? appServer.CapturedAt
            : storedQuota?.ResetCreditsCapturedAt;
        var generalFiveHourStale = !completeness.GeneralFiveHourQuota
                                   && storedQuotaValue?.General?.FiveHour is not null
                                   && general?.FiveHour is not null;
        var generalWeeklyStale = !completeness.GeneralWeeklyQuota
                                 && storedQuotaValue?.General?.Weekly is not null
                                 && general?.Weekly is not null;
        var sparkStale = !completeness.SparkQuota && storedQuotaValue?.Spark is not null && quota?.Spark is not null;
        var resetStale = !authoritativeNoResetCredits
                         && !(completeness.ResetCreditCount && completeness.ResetCreditDetails)
                         && storedQuotaValue is not null
                         && (storedQuotaValue.ResetCreditCount.HasValue || storedQuotaValue.ResetCredits.Count > 0);

        if (quota is not null
            && (completeness.Account
                || completeness.GeneralQuota
                || completeness.SparkQuota
                || completeness.ResetCreditCount
                || completeness.ResetCreditDetails))
        {
            await _repository.SaveQuotaSnapshotPartitionsAsync(
                account,
                quota,
                appServer.CapturedAt,
                accountUpdatedAt,
                generalFiveHourUpdatedAt,
                generalWeeklyUpdatedAt,
                sparkUpdatedAt,
                resetUpdatedAt,
                authoritativeNoResetCredits
                    ? new QuotaSnapshot(null, null, 0, true, [])
                    : completeness.ResetCreditCount || completeness.ResetCreditDetails ? currentQuota : null,
                cancellationToken);
        }

        AddPartitionWarning(warnings, "通用 5 小时额度", generalFiveHourStale, generalFiveHourUpdatedAt);
        AddPartitionWarning(warnings, "周额度", generalWeeklyStale, generalWeeklyUpdatedAt);
        AddPartitionWarning(warnings, "Spark 额度", sparkStale, sparkUpdatedAt);
        AddPartitionWarning(warnings, "重置卡", resetStale, resetUpdatedAt);
        if (quota is null)
        {
            warnings.Enqueue("额度不可用");
        }

        var storedUsage = await _repository.GetLatestOfficialUsageAsync(cancellationToken);
        if (appServer.Usage is not null
            && (completeness.UsageSummary || completeness.DailyUsage))
        {
            await _repository.SaveOfficialUsagePartitionsAsync(
                appServer.Usage,
                appServer.CapturedAt,
                completeness.UsageSummary,
                completeness.DailyUsage,
                cancellationToken);
            storedUsage = await _repository.GetLatestOfficialUsageAsync(cancellationToken);
        }

        var currentUsage = appServer.Usage;
        var summarySource = completeness.UsageSummary
            ? currentUsage
            : storedUsage?.Usage ?? currentUsage;
        // 官方按日历史在保存后统一读取；部分响应只修正返回日期，不丢弃其他官方历史。
        var currentDays = completeness.DailyUsage
            ? (currentUsage?.DailyUsage ?? []).Select(day => day.Date).ToHashSet()
            : [];
        var dailySource = storedUsage?.Usage ?? currentUsage;
        var officialUsage = summarySource is null && dailySource is null
            ? null
            : new OfficialUsageSnapshot(
                summarySource?.LifetimeTokens,
                summarySource?.PeakDailyTokens,
                summarySource?.CurrentStreakDays,
                summarySource?.LongestStreakDays,
                (dailySource?.DailyUsage ?? []).Select(day => day with { IsCached = !currentDays.Contains(day.Date) }).ToArray());
        var summaryUpdatedAt = completeness.UsageSummary ? appServer.CapturedAt : storedUsage?.SummaryCapturedAt;
        var dailyUpdatedAt = completeness.DailyUsage ? appServer.CapturedAt : storedUsage?.DailyUsageCapturedAt;
        var summaryStale = !completeness.UsageSummary && storedUsage?.SummaryCapturedAt is not null;
        var dailyStale = officialUsage?.DailyUsage.Any(day => day.IsCached) == true;
        AddPartitionWarning(warnings, "官方汇总", summaryStale, summaryUpdatedAt);
        AddPartitionWarning(warnings, "官方每日用量", dailyStale, dailyUpdatedAt);

        var quotaUpdatedAt = Latest(generalFiveHourUpdatedAt, generalWeeklyUpdatedAt) ?? storedQuota?.CapturedAt;
        var quotaStale = generalFiveHourStale || generalWeeklyStale || sparkStale || resetStale;
        var officialUsageUpdatedAt = dailyUpdatedAt ?? summaryUpdatedAt;
        var officialUsageStale = summaryStale || dailyStale;

        var ranges = UsageTimeRanges.Create(nowUtc, _timeZone);
        var historyStart = new[] { ranges.MonthStartUtc, ranges.ThirtyDayStartUtc }.Min();
        var historyEnd = new[] { ranges.MonthEndUtc, ranges.TomorrowStartUtc }.Max();
        var usageEvents = await _repository.QueryEventsAsync(historyStart, historyEnd, cancellationToken);

        var history = await _repository.GetResetHistoryAsync(cancellationToken: cancellationToken);
        var pace = generalWeeklyStale
            ? WeeklyPace.Unavailable
            : UsageCalculator.CalculateWeeklyPace(quota?.General?.Weekly, nowUtc);
        var snapshot = new DashboardSnapshot(
            account,
            quota,
            quotaUpdatedAt,
            quotaStale,
            officialUsage,
            officialUsageUpdatedAt,
            officialUsageStale,
            AggregateRange(usageEvents, ranges.MonthStartUtc, ranges.MonthEndUtc, pricing.Prices),
            AggregateRange(usageEvents, ranges.TodayStartUtc, ranges.TomorrowStartUtc, pricing.Prices),
            AggregateRange(usageEvents, ranges.SevenDayStartUtc, ranges.TomorrowStartUtc, pricing.Prices),
            AggregateRange(usageEvents, ranges.ThirtyDayStartUtc, ranges.TomorrowStartUtc, pricing.Prices),
            pricing,
            history,
            pace,
            TimeZoneInfo.ConvertTime(nowUtc, _timeZone),
            string.Join("；", warnings.Where(message => !string.IsNullOrWhiteSpace(message)).Distinct()))
        {
            Freshness = new DashboardPartitionFreshness(
                generalWeeklyUpdatedAt,
                generalWeeklyStale,
                sparkUpdatedAt,
                sparkStale,
                resetUpdatedAt,
                resetStale,
                summaryUpdatedAt,
                summaryStale,
                dailyUpdatedAt,
                dailyStale)
            {
                GeneralFiveHourQuotaUpdatedAt = generalFiveHourUpdatedAt,
                IsGeneralFiveHourQuotaStale = generalFiveHourStale,
                GeneralWeeklyQuotaUpdatedAt = generalWeeklyUpdatedAt,
                IsGeneralWeeklyQuotaStale = generalWeeklyStale,
                HasCurrentDailyUsageResponse = appServer.HasDailyUsageResponse
            }
        };

        return snapshot;
    }

    private static UsageAggregation AggregateRange(
        IReadOnlyList<TokenUsageEvent> events,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyDictionary<string, TokenPrice> prices)
        => UsageCalculator.Aggregate(
            events.Where(item => item.TimestampUtc >= from && item.TimestampUtc < to),
            prices);

    private static QuotaBucketSnapshot? MergeGeneralQuota(
        QuotaBucketSnapshot? current,
        QuotaBucketSnapshot? stored,
        bool fiveHourComplete,
        bool weeklyComplete,
        DateTimeOffset nowUtc)
    {
        var storedFiveHour = stored?.FiveHour?.ResetsAt > nowUtc ? stored.FiveHour : null;
        var fiveHour = fiveHourComplete ? current?.FiveHour : storedFiveHour ?? current?.FiveHour;
        var weekly = weeklyComplete ? current?.Weekly : stored?.Weekly ?? current?.Weekly;
        if (fiveHour is null && weekly is null)
        {
            return null;
        }

        var metadata = fiveHourComplete || weeklyComplete ? current ?? stored : stored ?? current;
        return new QuotaBucketSnapshot(
            metadata?.LimitId ?? "codex",
            metadata?.LimitName,
            metadata?.PlanType,
            fiveHour,
            weekly);
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second)
        => first.HasValue && second.HasValue
            ? (first.Value > second.Value ? first : second)
            : first ?? second;

    public Task<bool> GetNotificationsEnabledAsync(CancellationToken cancellationToken = default)
        => GetBoolSettingAsync("reset_expiry_notifications", true, cancellationToken);

    public Task SetNotificationsEnabledAsync(bool value, CancellationToken cancellationToken = default)
        => SetBoolSettingAsync("reset_expiry_notifications", value, cancellationToken);

    public Task<bool> GetDarkModeEnabledAsync(CancellationToken cancellationToken = default)
        => GetBoolSettingAsync("dark_mode_enabled", false, cancellationToken);

    public Task SetDarkModeEnabledAsync(bool value, CancellationToken cancellationToken = default)
        => SetBoolSettingAsync("dark_mode_enabled", value, cancellationToken);

    public Task<bool> GetEnglishEnabledAsync(CancellationToken cancellationToken = default)
        => GetBoolSettingAsync("english_language_enabled", false, cancellationToken);

    public Task SetEnglishEnabledAsync(bool value, CancellationToken cancellationToken = default)
        => SetBoolSettingAsync("english_language_enabled", value, cancellationToken);

    public Task<bool> GetAutoUpdateCheckEnabledAsync(CancellationToken cancellationToken = default)
        => GetBoolSettingAsync("auto_update_check_enabled", true, cancellationToken);

    public Task SetAutoUpdateCheckEnabledAsync(bool value, CancellationToken cancellationToken = default)
        => SetBoolSettingAsync("auto_update_check_enabled", value, cancellationToken);

    public async Task<UpdateCheckCache> GetUpdateCheckCacheAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetStringSettingAsync("update_check_cache", cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return UpdateCheckCache.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<UpdateCheckCache>(json) ?? UpdateCheckCache.Empty;
        }
        catch (JsonException)
        {
            return UpdateCheckCache.Empty;
        }
    }

    public Task SetUpdateCheckCacheAsync(UpdateCheckCache cache, CancellationToken cancellationToken = default)
        => SetStringSettingAsync("update_check_cache", JsonSerializer.Serialize(cache), cancellationToken);

    public string DatabasePath => _repository.DatabasePath;

    private async Task<bool> GetBoolSettingAsync(
        string key,
        bool defaultValue,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            return await _repository.GetBoolSettingAsync(key, defaultValue, cancellationToken);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task SetBoolSettingAsync(string key, bool value, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            await _repository.SetBoolSettingAsync(key, value, cancellationToken);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task<string?> GetStringSettingAsync(string key, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            return await _repository.GetStringSettingAsync(key, cancellationToken);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task SetStringSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            await _repository.SetStringSettingAsync(key, value, cancellationToken);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                await _repository.InitializeAsync(cancellationToken);
                _initialized = true;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<AppServerSnapshot> ReadAppServerAsync(
        DateTimeOffset nowUtc,
        bool force,
        ConcurrentQueue<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!force && nowUtc < _nextAppServerAttemptUtc)
        {
            var localRetry = TimeZoneInfo.ConvertTime(_nextAppServerAttemptUtc, _timeZone);
            warnings.Enqueue($"Codex app-server 暂时退避，{localRetry:HH:mm} 后重试");
            return new AppServerSnapshot(null, null, null, nowUtc, null);
        }

        AppServerSnapshot snapshot;
        try
        {
            snapshot = await _appServerClient.ReadSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            snapshot = new AppServerSnapshot(null, null, null, nowUtc, $"Codex app-server：{exception.Message}");
        }

        var globalFailure = snapshot.ProcessFailure
                            || snapshot.SuccessfulMethodCount == 0
                            && snapshot.Account is null
                            && snapshot.Quota is null
                            && snapshot.Usage is null
                            && snapshot.Error is not null;
        if (!globalFailure)
        {
            _consecutiveAppServerFailures = 0;
            _nextAppServerAttemptUtc = DateTimeOffset.MinValue;
        }
        else
        {
            var index = Math.Min(_consecutiveAppServerFailures, AppServerBackoff.Length - 1);
            _nextAppServerAttemptUtc = nowUtc + AppServerBackoff[index];
            _consecutiveAppServerFailures++;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            warnings.Enqueue(snapshot.Error);
        }

        return snapshot;
    }

    private async Task<PricingSnapshot> ReadPricingAsync(ConcurrentQueue<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            return await _priceCatalogService.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            warnings.Enqueue($"价格：{exception.Message}");
            return PriceCatalogService.CreateBuiltInSnapshot("价格读取失败，使用内置价格");
        }
    }

    private static async Task<int> ReadScanResultAsync(
        Task<int> scanTask,
        ConcurrentQueue<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await scanTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            warnings.Enqueue($"本地 rollout：{exception.Message}");
            return 0;
        }
    }

    private static QuotaSnapshot? ApplyPlanRules(string? planType, QuotaSnapshot? quota)
    {
        if (quota is null)
        {
            return null;
        }

        planType ??= quota.General?.PlanType;
        var showSpark = PlanCapabilities.ShouldShowSpark(planType, quota.Spark is not null);
        return quota with { Spark = showSpark ? quota.Spark : null };
    }

    private void AddPartitionWarning(
        ConcurrentQueue<string> warnings,
        string partition,
        bool stale,
        DateTimeOffset? updatedAt)
    {
        if (!stale || !updatedAt.HasValue)
        {
            return;
        }

        warnings.Enqueue(
            $"{partition}暂不可用，显示 {TimeZoneInfo.ConvertTime(updatedAt.Value, _timeZone):M月d日 HH:mm} 的上次成功值");
    }
}
