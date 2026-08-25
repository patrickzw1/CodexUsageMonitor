using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Input;
using CodexUsageMonitor.Core.Models;
using CodexUsageMonitor.Core.Services;

namespace CodexUsageMonitor.ViewModels;

using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(2);
    private readonly IDashboardService _dashboardService;
    private readonly IStartupRegistrationService _startupService;
    private readonly HashSet<string> _sentExpiryNotifications = new(StringComparer.Ordinal);
    private readonly object _lifecycleSync = new();
    private readonly object _refreshSync = new();
    private readonly object _startupSettingsSync = new();
    private CancellationTokenSource? _initializationCancellation;
    private Task? _initializationTask;
    private CancellationTokenSource? _refreshCancellation;
    private Task? _activeRefresh;
    private bool _activeRefreshForcesAppServer;
    private bool _refreshLoopAcceptingRequests;
    private bool _forceRefreshPending;
    private Task _notificationSettingsWriteTask = Task.CompletedTask;
    private Task _themeSettingsWriteTask = Task.CompletedTask;
    private Task _startupSettingsWriteTask = Task.CompletedTask;
    private long _notificationSettingsVersion;
    private long _themeSettingsVersion;
    private long _startupSettingsVersion;
    private bool _notificationSettingsReadPending;
    private bool _themeSettingsReadPending;
    private bool _startupReconcilePending;
    private bool _shutdownStarted;
    private bool _isLoading;
    private string _currentPage = "overview";
    private string _selectedModelRange = "7d";
    private UsageAggregation? _todayUsage;
    private UsageAggregation? _sevenDayUsage;
    private UsageAggregation? _thirtyDayUsage;
    private string _planDisplay = "—";
    private string _syncText = "等待同步";
    private MediaBrush _syncStatusBrush = MediaBrushes.DarkGoldenrod;
    private string _warning = string.Empty;
    private double _weeklyRemaining;
    private string _weeklyRemainingText = "不可用";
    private string _weeklyResetText = "服务端未返回";
    private double _paceUsedPercent;
    private double _paceElapsedPercent;
    private bool _paceWillExhaust;
    private string _paceText = "等待额度数据";
    private string _paceDetailText = "基准：7 天均分，每天约 14.3%";
    private string _paceWindowText = "每格代表额度窗口中的 24 小时";
    private bool _sparkVisible;
    private bool _sparkFiveHourVisible;
    private bool _sparkWeeklyVisible;
    private string _sparkFiveRemaining = "—";
    private double _sparkFiveRemainingPercent;
    private string _sparkFiveReset = "—";
    private string _sparkWeeklyRemaining = "—";
    private double _sparkWeeklyRemainingPercent;
    private string _sparkWeeklyReset = "—";
    private int? _resetCreditCount;
    private string _resetSummary = "详情未返回";
    private string _resetNearestExpiry = "暂无到期信息";
    private double _resetTimelinePercent;
    private string _monthCost = "—";
    private string _cacheHit = "—";
    private string _cacheSavings = "—";
    private string _pricingStatus = "等待价格同步";
    private string _monthTokens = "0";
    private double _uncachedPercent;
    private double _cachedPercent;
    private double _outputPercent;
    private double _reasoningPercent;
    private string _uncachedLegend = "0";
    private string _cachedLegend = "0";
    private string _outputLegend = "0";
    private string _reasoningLegend = "0";
    private bool _notificationsEnabled = true;
    private bool _isDarkMode;
    private bool _startupEnabled;
    private bool _confirmedStartupEnabled;
    private string _startupStatus = "默认关闭，登录 Windows 时不会自动运行";
    private string _resetDetailsStatus = "尚未同步";
    private bool _hasResetCreditRows;
    private string _resetCreditEmptyText = "尚未同步重置卡明细";
    private string _modelRangeTokens = "0";
    private string _modelRangeCost = "—";
    private string _modelRangeCacheHit = "—";
    private string _modelRangeLabel = "最近 7 天";
    private bool _hasModelRows;
    private bool _hasResetExpiry;
    private string _resetNotificationStatus = "等待获取有效期";
    private string _modelPricingStatus = "等待价格同步";
    private DateTimeOffset? _lastRefreshCompletedAt;

    public MainViewModel(
        IDashboardService dashboardService,
        IStartupRegistrationService startupService)
    {
        _dashboardService = dashboardService;
        _startupService = startupService;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading);
        ShowOverviewCommand = new RelayCommand(() => CurrentPage = "overview");
        ShowModelsCommand = new RelayCommand(() => CurrentPage = "models");
        ShowResetDetailsCommand = new RelayCommand(() => CurrentPage = "reset");
        ShowHistoryCommand = new RelayCommand(() => CurrentPage = "history");
        SelectTodayCommand = new RelayCommand(() => SelectModelRange("today"));
        SelectSevenDayCommand = new RelayCommand(() => SelectModelRange("7d"));
        SelectThirtyDayCommand = new RelayCommand(() => SelectModelRange("30d"));
        OpenUsagePageCommand = new RelayCommand(OpenUsagePage);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        ToggleThemeCommand = new RelayCommand(() => SetDarkMode(!IsDarkMode, persist: true));
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? QuitRequested;
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
    public event EventHandler<ResetExpiryNotificationEventArgs>? ResetExpiryNotificationRequested;

    public ICommand RefreshCommand { get; }
    public ICommand ShowOverviewCommand { get; }
    public ICommand ShowModelsCommand { get; }
    public ICommand ShowResetDetailsCommand { get; }
    public ICommand ShowHistoryCommand { get; }
    public ICommand SelectTodayCommand { get; }
    public ICommand SelectSevenDayCommand { get; }
    public ICommand SelectThirtyDayCommand { get; }
    public ICommand OpenUsagePageCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand QuitCommand { get; }

    public ObservableCollection<ModelUsageRowViewModel> Models { get; } = [];
    public ObservableCollection<ResetCreditRowViewModel> ResetCredits { get; } = [];
    public ObservableCollection<ResetHistoryRowViewModel> ResetHistory { get; } = [];
    public ObservableCollection<DailyUsageRowViewModel> DailyUsage { get; } = [];

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    public string CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(IsOverviewVisible));
                OnPropertyChanged(nameof(IsModelsVisible));
                OnPropertyChanged(nameof(IsResetDetailsVisible));
                OnPropertyChanged(nameof(IsHistoryVisible));
                OnPropertyChanged(nameof(IsMainShellVisible));
            }
        }
    }

    public bool IsOverviewVisible => CurrentPage == "overview";
    public bool IsModelsVisible => CurrentPage == "models";
    public bool IsResetDetailsVisible => CurrentPage == "reset";
    public bool IsHistoryVisible => CurrentPage == "history";
    public bool IsMainShellVisible => !IsResetDetailsVisible;
    public bool IsTodaySelected => _selectedModelRange == "today";
    public bool IsSevenDaySelected => _selectedModelRange == "7d";
    public bool IsThirtyDaySelected => _selectedModelRange == "30d";
    public string PlanDisplay { get => _planDisplay; private set => SetProperty(ref _planDisplay, value); }
    public string SyncText { get => _syncText; private set => SetProperty(ref _syncText, value); }
    public MediaBrush SyncStatusBrush { get => _syncStatusBrush; private set => SetProperty(ref _syncStatusBrush, value); }
    public string Warning { get => _warning; private set { SetProperty(ref _warning, value); OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);
    public double WeeklyRemaining { get => _weeklyRemaining; private set => SetProperty(ref _weeklyRemaining, value); }
    public string WeeklyRemainingText { get => _weeklyRemainingText; private set => SetProperty(ref _weeklyRemainingText, value); }
    public string WeeklyResetText { get => _weeklyResetText; private set => SetProperty(ref _weeklyResetText, value); }
    public double PaceUsedPercent { get => _paceUsedPercent; private set => SetProperty(ref _paceUsedPercent, value); }
    public double PaceElapsedPercent { get => _paceElapsedPercent; private set => SetProperty(ref _paceElapsedPercent, value); }
    public bool PaceWillExhaust { get => _paceWillExhaust; private set => SetProperty(ref _paceWillExhaust, value); }
    public string PaceText { get => _paceText; private set => SetProperty(ref _paceText, value); }
    public string PaceDetailText { get => _paceDetailText; private set => SetProperty(ref _paceDetailText, value); }
    public string PaceWindowText { get => _paceWindowText; private set => SetProperty(ref _paceWindowText, value); }
    public bool SparkVisible { get => _sparkVisible; private set => SetProperty(ref _sparkVisible, value); }
    public bool SparkFiveHourVisible { get => _sparkFiveHourVisible; private set => SetProperty(ref _sparkFiveHourVisible, value); }
    public bool SparkWeeklyVisible { get => _sparkWeeklyVisible; private set => SetProperty(ref _sparkWeeklyVisible, value); }
    public string SparkFiveRemaining { get => _sparkFiveRemaining; private set => SetProperty(ref _sparkFiveRemaining, value); }
    public double SparkFiveRemainingPercent { get => _sparkFiveRemainingPercent; private set => SetProperty(ref _sparkFiveRemainingPercent, value); }
    public string SparkFiveReset { get => _sparkFiveReset; private set => SetProperty(ref _sparkFiveReset, value); }
    public string SparkWeeklyRemaining { get => _sparkWeeklyRemaining; private set => SetProperty(ref _sparkWeeklyRemaining, value); }
    public double SparkWeeklyRemainingPercent { get => _sparkWeeklyRemainingPercent; private set => SetProperty(ref _sparkWeeklyRemainingPercent, value); }
    public string SparkWeeklyReset { get => _sparkWeeklyReset; private set => SetProperty(ref _sparkWeeklyReset, value); }
    public int? ResetCreditCount { get => _resetCreditCount; private set { SetProperty(ref _resetCreditCount, value); OnPropertyChanged(nameof(ResetCreditCountText)); OnPropertyChanged(nameof(ResetCreditCountValueText)); } }
    public string ResetCreditCountText => ResetCreditCount.HasValue ? $"{ResetCreditCount} 次可用" : "次数不可用";
    public string ResetCreditCountValueText => ResetCreditCount?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string ResetSummary { get => _resetSummary; private set => SetProperty(ref _resetSummary, value); }
    public string ResetNearestExpiry { get => _resetNearestExpiry; private set => SetProperty(ref _resetNearestExpiry, value); }
    public double ResetTimelinePercent { get => _resetTimelinePercent; private set => SetProperty(ref _resetTimelinePercent, value); }
    public string MonthCost { get => _monthCost; private set => SetProperty(ref _monthCost, value); }
    public string CacheHit { get => _cacheHit; private set => SetProperty(ref _cacheHit, value); }
    public string CacheSavings { get => _cacheSavings; private set => SetProperty(ref _cacheSavings, value); }
    public string PricingStatus { get => _pricingStatus; private set => SetProperty(ref _pricingStatus, value); }
    public string MonthTokens { get => _monthTokens; private set => SetProperty(ref _monthTokens, value); }
    public double UncachedPercent { get => _uncachedPercent; private set => SetProperty(ref _uncachedPercent, value); }
    public double CachedPercent { get => _cachedPercent; private set => SetProperty(ref _cachedPercent, value); }
    public double OutputPercent { get => _outputPercent; private set => SetProperty(ref _outputPercent, value); }
    public double ReasoningPercent { get => _reasoningPercent; private set => SetProperty(ref _reasoningPercent, value); }
    public string UncachedLegend { get => _uncachedLegend; private set => SetProperty(ref _uncachedLegend, value); }
    public string CachedLegend { get => _cachedLegend; private set => SetProperty(ref _cachedLegend, value); }
    public string OutputLegend { get => _outputLegend; private set => SetProperty(ref _outputLegend, value); }
    public string ReasoningLegend { get => _reasoningLegend; private set => SetProperty(ref _reasoningLegend, value); }
    public string ResetDetailsStatus { get => _resetDetailsStatus; private set => SetProperty(ref _resetDetailsStatus, value); }
    public bool HasResetCreditRows { get => _hasResetCreditRows; private set => SetProperty(ref _hasResetCreditRows, value); }
    public string ResetCreditEmptyText { get => _resetCreditEmptyText; private set => SetProperty(ref _resetCreditEmptyText, value); }
    public string ModelRangeTokens { get => _modelRangeTokens; private set => SetProperty(ref _modelRangeTokens, value); }
    public string ModelRangeCost { get => _modelRangeCost; private set => SetProperty(ref _modelRangeCost, value); }
    public string ModelRangeCacheHit { get => _modelRangeCacheHit; private set => SetProperty(ref _modelRangeCacheHit, value); }
    public string ModelRangeLabel { get => _modelRangeLabel; private set => SetProperty(ref _modelRangeLabel, value); }
    public bool HasModelRows { get => _hasModelRows; private set => SetProperty(ref _hasModelRows, value); }
    public bool HasResetExpiry { get => _hasResetExpiry; private set => SetProperty(ref _hasResetExpiry, value); }
    public string ResetNotificationStatus { get => _resetNotificationStatus; private set => SetProperty(ref _resetNotificationStatus, value); }
    public string ModelPricingStatus { get => _modelPricingStatus; private set => SetProperty(ref _modelPricingStatus, value); }
    public bool IsDarkMode => _isDarkMode;
    public string ThemeIcon => IsDarkMode ? "☀" : "☾";
    public string ThemeToolTip => IsDarkMode ? "切换为浅色模式" : "切换为暗黑模式";
    public string StartupStatus { get => _startupStatus; private set => SetProperty(ref _startupStatus, value); }

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            lock (_lifecycleSync)
            {
                if (_shutdownStarted)
                {
                    return;
                }

                var changed = SetProperty(ref _startupEnabled, value);
                lock (_startupSettingsSync)
                {
                    if (!changed && !_startupReconcilePending)
                    {
                        return;
                    }

                    StartupStatus = "正在更新开机启动设置…";
                    var version = ++_startupSettingsVersion;
                    _startupSettingsWriteTask = PersistStartupAsync(
                        _startupSettingsWriteTask,
                        value,
                        version);
                }
            }
        }
    }

    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            lock (_lifecycleSync)
            {
                if (_shutdownStarted)
                {
                    return;
                }

                var changed = SetProperty(ref _notificationsEnabled, value);
                if (!changed && !_notificationSettingsReadPending)
                {
                    return;
                }

                _notificationSettingsVersion++;
                _notificationSettingsWriteTask = PersistNotificationsAsync(value);
            }
        }
    }

    public Task InitializeAsync()
    {
        lock (_lifecycleSync)
        {
            if (_shutdownStarted)
            {
                return Task.CompletedTask;
            }

            _initializationCancellation ??= new CancellationTokenSource();
            return _initializationTask ??= InitializeCoreAsync(_initializationCancellation.Token);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            Task startupReconcileTask;
            lock (_lifecycleSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_startupSettingsSync)
                {
                    _startupReconcilePending = true;
                    var version = ++_startupSettingsVersion;
                    _startupSettingsWriteTask = ReconcileStartupAsync(_startupSettingsWriteTask, version);
                    startupReconcileTask = _startupSettingsWriteTask;
                }
            }

            await startupReconcileTask.WaitAsync(cancellationToken);
            await InitializeNotificationSettingAsync(cancellationToken);
            await InitializeThemeSettingAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            lock (_lifecycleSync)
            {
                if (_shutdownStarted)
                {
                    return;
                }

                Warning = $"本地历史初始化失败：{exception.Message}";
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await StartRefreshAsync(forceAppServer: true, enforceCooldown: false);
    }

    public Task RefreshAsync()
        => StartRefreshAsync(forceAppServer: true, enforceCooldown: true);

    public Task RefreshAutomaticallyAsync()
        => StartRefreshAsync(forceAppServer: false, enforceCooldown: false);

    public async Task StopAsync(TimeSpan timeout)
    {
        Task? refresh;
        Task startupSettingsWriteTask;
        Task notificationSettingsWriteTask;
        Task themeSettingsWriteTask;
        Task? initializationTask;
        lock (_lifecycleSync)
        {
            _shutdownStarted = true;
            _initializationCancellation?.Cancel();
            initializationTask = _initializationTask;
            lock (_refreshSync)
            {
                _refreshLoopAcceptingRequests = false;
                _forceRefreshPending = false;
                _refreshCancellation?.Cancel();
                refresh = _activeRefresh;
            }

            lock (_startupSettingsSync)
            {
                startupSettingsWriteTask = _startupSettingsWriteTask;
            }

            notificationSettingsWriteTask = _notificationSettingsWriteTask;
            themeSettingsWriteTask = _themeSettingsWriteTask;
        }

        var tasks = new List<Task>
        {
            notificationSettingsWriteTask,
            themeSettingsWriteTask,
            startupSettingsWriteTask
        };
        if (refresh is not null)
        {
            tasks.Add(refresh);
        }
        if (initializationTask is not null)
        {
            tasks.Add(initializationTask);
        }

        await AsyncShutdown.WaitAsync(tasks, timeout);
    }

    public void CancelPendingOperations()
    {
        lock (_refreshSync)
        {
            _refreshCancellation?.Cancel();
        }
    }

    private Task StartRefreshAsync(bool forceAppServer, bool enforceCooldown)
    {
        lock (_lifecycleSync)
        {
            if (_shutdownStarted)
            {
                return Task.CompletedTask;
            }

            lock (_refreshSync)
            {
                if (_activeRefresh is { IsCompleted: false } && _refreshLoopAcceptingRequests)
                {
                    if (forceAppServer && !_activeRefreshForcesAppServer)
                    {
                        _forceRefreshPending = true;
                    }

                    return _activeRefresh;
                }

                _refreshCancellation?.Dispose();
                _refreshCancellation = new CancellationTokenSource();
                _activeRefreshForcesAppServer = forceAppServer;
                _refreshLoopAcceptingRequests = true;
                _forceRefreshPending = false;
                _activeRefresh = RunRefreshLoopAsync(forceAppServer, enforceCooldown, _refreshCancellation.Token);
                return _activeRefresh;
            }
        }
    }

    private async Task RunRefreshLoopAsync(
        bool forceAppServer,
        bool enforceCooldown,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await RefreshCoreAsync(forceAppServer, enforceCooldown, cancellationToken);

            lock (_refreshSync)
            {
                if (cancellationToken.IsCancellationRequested
                    || forceAppServer
                    || !_forceRefreshPending)
                {
                    _refreshLoopAcceptingRequests = false;
                    _forceRefreshPending = false;
                    return;
                }

                _forceRefreshPending = false;
                _activeRefreshForcesAppServer = true;
                forceAppServer = true;
                enforceCooldown = false;
            }
        }
    }

    private async Task RefreshCoreAsync(bool forceAppServer, bool enforceCooldown, CancellationToken cancellationToken)
    {
        if (enforceCooldown && _lastRefreshCompletedAt is { } lastRefresh
            && DateTimeOffset.Now - lastRefresh < RefreshCooldown)
        {
            SyncText = $"已是最新 · {lastRefresh:HH:mm}";
            return;
        }

        IsLoading = true;
        SyncText = "正在同步…";
        SyncStatusBrush = MediaBrushes.DarkGoldenrod;
        try
        {
            var snapshot = await _dashboardService.RefreshAsync(forceAppServer, cancellationToken);
            lock (_lifecycleSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_shutdownStarted)
                {
                    return;
                }

                Apply(snapshot);
                _lastRefreshCompletedAt = DateTimeOffset.Now;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SyncText = "刷新已取消";
            SyncStatusBrush = MediaBrushes.DarkGoldenrod;
        }
        catch (Exception exception)
        {
            Warning = exception.Message;
            SyncText = "同步失败";
            SyncStatusBrush = MediaBrushes.Firebrick;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(DashboardSnapshot snapshot)
    {
        PlanDisplay = PlanCapabilities.DisplayName(snapshot.Account?.PlanType);
        SyncText = $"更新于 {snapshot.RefreshedAt:HH:mm}";
        Warning = snapshot.Warning ?? string.Empty;
        SyncStatusBrush = string.IsNullOrWhiteSpace(snapshot.Warning) ? MediaBrushes.SeaGreen : MediaBrushes.DarkGoldenrod;

        var weekly = snapshot.Quota?.General?.Weekly;
        WeeklyRemaining = weekly?.RemainingPercent ?? 0;
        WeeklyRemainingText = weekly?.RemainingPercent is { } weeklyRemaining ? $"{weeklyRemaining}%" : "不可用";
        WeeklyResetText = weekly?.ResetsAt is { } weeklyReset
            ? $"{weeklyReset.ToLocalTime():M月d日 HH:mm} 重置{StaleSuffix(snapshot.Freshness.IsGeneralQuotaStale, snapshot.Freshness.GeneralQuotaUpdatedAt)}"
            : "每周重置时间未返回";
        PaceUsedPercent = Math.Clamp(weekly?.UsedPercent ?? 0, 0, 100);
        PaceElapsedPercent = Math.Clamp(snapshot.WeeklyPace.ElapsedPercent ?? 0, 0, 100);
        PaceWillExhaust = snapshot.WeeklyPace.WillExhaust ?? false;
        PaceText = snapshot.WeeklyPace.ProjectedUsedPercent is null or <= 0
            ? "暂无足够数据预测"
            : $"预计本周消耗 {snapshot.WeeklyPace.ProjectedUsedPercent:0}% 额度 · {(snapshot.WeeklyPace.WillExhaust == true ? "可能提前耗尽" : "不会耗尽")}";
        PaceDetailText = weekly?.UsedPercent is { } used && snapshot.WeeklyPace.ElapsedPercent is { } elapsed
            ? $"已用 {used}% ≈ {used * 7d / 100:0.0} 天均衡额度 · 每天基准 14.3%"
            : "基准：7 天均分，每天约 14.3%";
        PaceWindowText = weekly?.ResetsAt is { } reset
                         && weekly.WindowDurationMinutes is > 0
                         && snapshot.WeeklyPace.ElapsedPercent is { } windowElapsed
            ? $"灰色已用 · 虚线进度 {windowElapsed:0.0}% · {reset.AddMinutes(-weekly.WindowDurationMinutes.Value).ToLocalTime():M/d HH:mm} 起算"
            : "每格代表额度窗口中的 24 小时";

        var spark = snapshot.Quota?.Spark;
        SparkVisible = spark is not null;
        SparkFiveHourVisible = spark?.FiveHour is not null;
        SparkWeeklyVisible = spark?.Weekly is not null;
        SparkFiveRemaining = spark?.FiveHour?.RemainingPercent is { } five ? $"剩余 {five}%" : "不可用";
        SparkFiveRemainingPercent = spark?.FiveHour?.RemainingPercent ?? 0;
        SparkFiveReset = FormatReset(spark?.FiveHour) + StaleSuffix(snapshot.Freshness.IsSparkQuotaStale, snapshot.Freshness.SparkQuotaUpdatedAt);
        SparkWeeklyRemaining = spark?.Weekly?.RemainingPercent is { } sparkWeek ? $"剩余 {sparkWeek}%" : "不可用";
        SparkWeeklyRemainingPercent = spark?.Weekly?.RemainingPercent ?? 0;
        SparkWeeklyReset = FormatReset(spark?.Weekly) + StaleSuffix(snapshot.Freshness.IsSparkQuotaStale, snapshot.Freshness.SparkQuotaUpdatedAt);

        ApplyResetCredits(snapshot);
        ApplyUsage(snapshot.Usage, snapshot.Pricing);
        _todayUsage = snapshot.TodayUsage;
        _sevenDayUsage = snapshot.SevenDayUsage;
        _thirtyDayUsage = snapshot.ThirtyDayUsage;
        ApplySelectedModelUsage();
        ApplyDailyUsage(snapshot.OfficialUsage);
    }

    private void ApplyResetCredits(DashboardSnapshot snapshot)
    {
        var quota = snapshot.Quota;
        ResetCreditCount = quota?.ResetCreditCount;
        var nearest = quota?.ResetCredits
            .Where(item => item.Status.Equals("available", StringComparison.OrdinalIgnoreCase) && item.ExpiresAt.HasValue)
            .OrderBy(item => item.ExpiresAt)
            .FirstOrDefault();
        ResetSummary = nearest?.ExpiresAt is { } expiry
            ? $"最近 {DaysRemaining(expiry)} 天后到期"
            : quota?.ResetCreditDetailsComplete == false ? "仅返回可用次数" : "暂无到期信息";
        ResetNearestExpiry = nearest?.ExpiresAt is { } nearestExpiry
            ? $"最近到期：{nearestExpiry.ToLocalTime():M月d日}（{DaysRemaining(nearestExpiry)} 天后）"
            : "暂无到期信息";
        ResetTimelinePercent = nearest?.ExpiresAt is { } timelineExpiry
            ? Math.Clamp((DateTimeOffset.Now - nearest.GrantedAt.ToLocalTime()).TotalSeconds
                         / Math.Max((timelineExpiry.ToLocalTime() - nearest.GrantedAt.ToLocalTime()).TotalSeconds, 1)
                         * 100, 0, 100)
            : 0;
        HasResetExpiry = nearest?.ExpiresAt.HasValue == true;
        ResetNotificationStatus = HasResetExpiry
            ? "7 天、3 天、1 天和到期当天发送 Windows 通知"
            : "服务端未返回有效期，提醒暂不可用";
        ResetDetailsStatus = quota is null
            ? "服务端未返回"
            : quota.ResetCreditDetailsComplete ? "详情已完整同步" : "服务端仅返回总次数";
        ResetDetailsStatus += StaleSuffix(
            snapshot.Freshness.IsResetCreditsStale,
            snapshot.Freshness.ResetCreditsUpdatedAt);

        if (NotificationsEnabled && quota is not null && !snapshot.Freshness.IsResetCreditsStale)
        {
            foreach (var alert in ResetNotificationPlanner.CreateAlerts(quota.ResetCredits, DateTimeOffset.Now))
            {
                var newIds = alert.CreditIds
                    .Where(id => _sentExpiryNotifications.Add($"{id}:{alert.DaysRemaining}"))
                    .ToArray();
                if (newIds.Length > 0)
                {
                    ResetExpiryNotificationRequested?.Invoke(
                        this,
                        new ResetExpiryNotificationEventArgs(
                            $"有 {newIds.Length} 张重置卡将在 {alert.DaysRemaining} 天后到期（{alert.ExpiresAt.ToLocalTime():M月d日}）。"));
                }
            }
        }

        ResetCredits.Clear();
        if (quota is not null)
        {
            foreach (var credit in quota.ResetCredits.OrderBy(item => item.ExpiresAt ?? DateTimeOffset.MaxValue))
            {
                ResetCredits.Add(new ResetCreditRowViewModel(
                    "1 次",
                    credit.ExpiresAt is { } expires ? $"{expires.ToLocalTime():yyyy年M月d日}到期" : "无到期时间",
                    credit.ExpiresAt is { } expiryValue ? $"剩余 {DaysRemaining(expiryValue)} 天" : "有效期未返回",
                    $"获得于 {credit.GrantedAt.ToLocalTime():yyyy年M月d日}",
                    credit.Title ?? "Codex 重置奖励"));
            }
        }

        HasResetCreditRows = ResetCredits.Count > 0;
        ResetCreditEmptyText = quota is null
            ? "服务端未返回重置卡数据。"
            : quota.ResetCreditDetailsComplete
                ? "当前没有可用的单卡明细。"
                : ResetCreditCount.HasValue
                    ? $"服务端当前只返回 {ResetCreditCount} 次可用，单卡授予时间和有效期暂未返回。"
                    : "重置卡次数和单卡详情均不可用。";

        ResetHistory.Clear();
        foreach (var item in snapshot.ResetHistory)
        {
            var (icon, description) = item.Kind switch
            {
                ResetHistoryKind.Granted => ("\uE109", $"获得 {item.Count} 次"),
                ResetHistoryKind.Expired => ("\uE711", $"已过期 {item.Count} 次"),
                _ => ("\uE108", $"使用 {item.Count} 次")
            };
            ResetHistory.Add(new ResetHistoryRowViewModel(
                icon,
                item.OccurredAt.ToLocalTime().ToString("M月d日"),
                description,
                item.IsInferred ? "推测" : string.Empty,
                item.IsInferred));
        }
    }

    private void ApplyUsage(UsageAggregation usage, PricingSnapshot pricing)
    {
        MonthCost = usage.EstimatedCostUsd.HasValue ? $"≈ ${usage.EstimatedCostUsd:0.00}" : "暂无定价";
        CacheHit = $"{usage.CacheHitPercent:0}%";
        CacheSavings = usage.EstimatedCacheSavingsUsd.HasValue ? $"节省约 ${usage.EstimatedCacheSavingsUsd:0.00}" : "暂无估算";
        PricingStatus = FormatPricingStatus(usage, pricing);
        MonthTokens = FormatTokens(usage.TotalTokens);

        var composition = usage.Composition;
        UncachedPercent = composition.UncachedInputPercent;
        CachedPercent = composition.CachedInputPercent;
        OutputPercent = composition.VisibleOutputPercent;
        ReasoningPercent = composition.ReasoningPercent;
        UncachedLegend = $"{composition.UncachedInputPercent:0.0}%  ({FormatTokens(composition.UncachedInputTokens)})";
        CachedLegend = $"{composition.CachedInputPercent:0.0}%  ({FormatTokens(composition.CachedInputTokens)})";
        OutputLegend = $"{composition.VisibleOutputPercent:0.0}%  ({FormatTokens(composition.VisibleOutputTokens)})";
        ReasoningLegend = $"{composition.ReasoningPercent:0.0}%  ({FormatTokens(composition.ReasoningTokens)})";
    }

    private void SelectModelRange(string range)
    {
        if (_selectedModelRange == range)
        {
            return;
        }

        _selectedModelRange = range;
        OnPropertyChanged(nameof(IsTodaySelected));
        OnPropertyChanged(nameof(IsSevenDaySelected));
        OnPropertyChanged(nameof(IsThirtyDaySelected));
        ApplySelectedModelUsage();
    }

    private void ApplySelectedModelUsage()
    {
        var usage = _selectedModelRange switch
        {
            "today" => _todayUsage,
            "30d" => _thirtyDayUsage,
            _ => _sevenDayUsage
        };
        ModelRangeLabel = _selectedModelRange switch
        {
            "today" => "今天",
            "30d" => "最近 30 天",
            _ => "最近 7 天"
        };
        Models.Clear();
        if (usage is null)
        {
            HasModelRows = false;
            return;
        }

        ModelRangeTokens = FormatTokens(usage.TotalTokens);
        ModelRangeCost = usage.EstimatedCostUsd.HasValue ? $"≈ ${usage.EstimatedCostUsd:0.00}" : "暂无定价";
        ModelRangeCacheHit = $"{usage.CacheHitPercent:0}%";
        ModelPricingStatus = FormatPricingStatus(usage, null);
        var colors = new[] { "#3B76E8", "#11A9C2", "#8667E8", "#E6A63B", "#E86C91", "#7BC2DA" };
        var modelRows = usage.Models.Take(5).ToList();
        if (usage.Models.Count > 5)
        {
            var remainder = usage.Models.Skip(5).ToArray();
            var priced = remainder.Where(item => item.EstimatedCostUsd.HasValue).ToArray();
            modelRows.Add(new ModelUsageSummary(
                "其他",
                UsageCalculator.SaturatingSum(remainder.Select(item => item.InputTokens)),
                UsageCalculator.SaturatingSum(remainder.Select(item => item.CachedInputTokens)),
                UsageCalculator.SaturatingSum(remainder.Select(item => item.OutputTokens)),
                UsageCalculator.SaturatingSum(remainder.Select(item => item.ReasoningTokens)),
                UsageCalculator.SaturatingSum(remainder.Select(item => item.TotalTokens)),
                priced.Length == remainder.Length ? priced.Sum(item => item.EstimatedCostUsd!.Value) : null));
        }

        for (var index = 0; index < modelRows.Count; index++)
        {
            var model = modelRows[index];
            var share = usage.TotalTokens <= 0 ? 0 : (double)model.TotalTokens / usage.TotalTokens * 100;
            Models.Add(new ModelUsageRowViewModel(
                model.Model,
                FormatTokens(model.TotalTokens),
                $"{model.CacheHitPercent:0}% 缓存",
                model.EstimatedCostUsd.HasValue ? $"${model.EstimatedCostUsd:0.00}" : "—",
                share,
                $"{share:0}%",
                colors[index % colors.Length]));
        }

        HasModelRows = Models.Count > 0;
    }

    private void ApplyDailyUsage(OfficialUsageSnapshot? officialUsage)
    {
        DailyUsage.Clear();
        if (officialUsage is null || officialUsage.DailyUsage.Count == 0)
        {
            return;
        }

        var month = officialUsage.DailyUsage
            .Where(item => item.Date.Year == DateTime.Today.Year && item.Date.Month == DateTime.Today.Month)
            .OrderByDescending(item => item.Date)
            .ToArray();
        var peak = Math.Max(month.Select(item => item.Tokens).DefaultIfEmpty(0).Max(), 1);
        foreach (var day in month)
        {
            DailyUsage.Add(new DailyUsageRowViewModel(
                day.Date.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN")),
                FormatTokens(day.Tokens),
                (double)day.Tokens / peak * 100));
        }
    }

    private static string FormatReset(RateLimitWindowSnapshot? window)
        => window?.ResetsAt is { } reset ? $"{reset.ToLocalTime():M月d日 HH:mm} 重置" : "重置时间未返回";

    private static string StaleSuffix(bool stale, DateTimeOffset? updatedAt)
        => stale && updatedAt.HasValue ? $"（上次成功 {updatedAt.Value.ToLocalTime():M/d HH:mm}）" : string.Empty;

    private static int DaysRemaining(DateTimeOffset expiry)
        => Math.Max(0, (int)Math.Ceiling((expiry.ToLocalTime() - DateTimeOffset.Now).TotalDays));

    private static string FormatPricingStatus(UsageAggregation usage, PricingSnapshot? pricing)
    {
        var pricedTokens = usage.Models
            .Where(item => item.EstimatedCostUsd.HasValue);
        var coveredTokens = UsageCalculator.SaturatingSum(pricedTokens.Select(item => item.TotalTokens));
        var coverage = usage.TotalTokens <= 0 ? 0 : (double)coveredTokens / usage.TotalTokens * 100;
        var source = pricing is null
            ? "公开 API 单价"
            : pricing.IsLive ? "本次启动获取的官方价格" : pricing.StatusMessage ?? "价格快照";
        return $"{source} · 覆盖 {coverage:0}% Token";
    }

    public static string FormatTokens(long value)
        => TokenDisplayFormatter.Format(value);

    private async Task PersistNotificationsAsync(bool value)
    {
        try
        {
            await _dashboardService.SetNotificationsEnabledAsync(value);
        }
        catch (Exception exception)
        {
            Warning = $"通知设置保存失败：{exception.Message}";
            SyncStatusBrush = MediaBrushes.DarkGoldenrod;
        }
    }

    private void SetDarkMode(bool value, bool persist)
    {
        lock (_lifecycleSync)
        {
            if (persist && _shutdownStarted)
            {
                return;
            }

            var changed = ApplyDarkMode(value);
            if (!changed && !(persist && _themeSettingsReadPending))
            {
                return;
            }

            if (persist)
            {
                _themeSettingsVersion++;
                _themeSettingsWriteTask = PersistThemeAsync(value);
            }
        }
    }

    private bool ApplyDarkMode(bool value)
    {
        if (!SetProperty(ref _isDarkMode, value, nameof(IsDarkMode)))
        {
            return false;
        }

        OnPropertyChanged(nameof(ThemeIcon));
        OnPropertyChanged(nameof(ThemeToolTip));
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(value));
        return true;
    }

    private async Task InitializeNotificationSettingAsync(CancellationToken cancellationToken)
    {
        long version;
        lock (_lifecycleSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdownStarted)
            {
                return;
            }

            _notificationSettingsReadPending = true;
            version = _notificationSettingsVersion;
        }

        bool value;
        try
        {
            value = await _dashboardService.GetNotificationsEnabledAsync(cancellationToken);
        }
        catch
        {
            lock (_lifecycleSync)
            {
                _notificationSettingsReadPending = false;
            }

            throw;
        }

        lock (_lifecycleSync)
        {
            _notificationSettingsReadPending = false;
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdownStarted || version != _notificationSettingsVersion)
            {
                return;
            }

            SetProperty(ref _notificationsEnabled, value, nameof(NotificationsEnabled));
        }
    }

    private async Task InitializeThemeSettingAsync(CancellationToken cancellationToken)
    {
        long version;
        lock (_lifecycleSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdownStarted)
            {
                return;
            }

            _themeSettingsReadPending = true;
            version = _themeSettingsVersion;
        }

        bool value;
        try
        {
            value = await _dashboardService.GetDarkModeEnabledAsync(cancellationToken);
        }
        catch
        {
            lock (_lifecycleSync)
            {
                _themeSettingsReadPending = false;
            }

            throw;
        }

        lock (_lifecycleSync)
        {
            _themeSettingsReadPending = false;
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdownStarted || version != _themeSettingsVersion)
            {
                return;
            }

            ApplyDarkMode(value);
        }
    }

    private async Task PersistThemeAsync(bool value)
    {
        try
        {
            await _dashboardService.SetDarkModeEnabledAsync(value);
        }
        catch (Exception exception)
        {
            Warning = $"主题设置保存失败：{exception.Message}";
            SyncStatusBrush = MediaBrushes.DarkGoldenrod;
        }
    }

    private async Task ReconcileStartupAsync(Task previousWrite, long version)
    {
        try
        {
            await previousWrite;
        }
        catch
        {
            // A previous failure must not prevent initialization from observing the actual state.
        }

        var result = await Task.Run(_startupService.Reconcile);
        lock (_startupSettingsSync)
        {
            _startupReconcilePending = false;
            if (version != _startupSettingsVersion)
            {
                return;
            }
        }

        ApplyStartupResult(result, "读取");
    }

    private async Task PersistStartupAsync(Task previousWrite, bool value, long version)
    {
        try
        {
            await previousWrite;
        }
        catch
        {
            // The latest user choice must still run even if an earlier write failed unexpectedly.
        }

        var result = await Task.Run(() => _startupService.SetEnabled(value));
        lock (_startupSettingsSync)
        {
            if (version != _startupSettingsVersion)
            {
                return;
            }
        }

        ApplyStartupResult(result, "保存");
    }

    private void ApplyStartupResult(StartupRegistrationResult result, string operation)
    {
        var actual = result.IsEnabled ?? _confirmedStartupEnabled;
        if (result.IsEnabled.HasValue)
        {
            _confirmedStartupEnabled = actual;
        }

        if (actual != _startupEnabled)
        {
            _startupEnabled = actual;
            OnPropertyChanged(nameof(StartupEnabled));
        }

        StartupStatus = result.IsEnabled switch
        {
            true when result.Error is null => "已启用 · 登录后在后台运行",
            false when result.Error is null => "默认关闭，登录 Windows 时不会自动运行",
            _ => $"开机启动状态不可用 · 保持上次确认的{(actual ? "开启" : "关闭")}状态"
        };
        if (result.Error is not null)
        {
            Warning = $"开机自启动设置{operation}失败：{result.Error}";
            SyncStatusBrush = MediaBrushes.DarkGoldenrod;
        }
    }

    private static void OpenUsagePage()
        => StartShell("https://chatgpt.com/codex/settings/usage");

    private void OpenDataFolder()
        => StartShell(Path.GetDirectoryName(_dashboardService.DatabasePath)!);

    private static void StartShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // 外部打开失败不影响监控数据。
        }
    }
}

public sealed class ResetExpiryNotificationEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class ThemeChangedEventArgs(bool isDarkMode) : EventArgs
{
    public bool IsDarkMode { get; } = isDarkMode;
}
