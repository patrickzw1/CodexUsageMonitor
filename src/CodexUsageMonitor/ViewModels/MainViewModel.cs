using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo ChineseCulture = CultureInfo.GetCultureInfo("zh-CN");
    private readonly IDashboardService _dashboardService;
    private readonly IStartupRegistrationService _startupService;
    private readonly IUpdateCheckService? _updateCheckService;
    private readonly Action<Uri> _openUri;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _checkForUpdatesCommand;
    private readonly AsyncRelayCommand _updateActionCommand;
    private readonly AsyncRelayCommand _snoozeUpdateCommand;
    private readonly HashSet<string> _sentExpiryNotifications = new(StringComparer.Ordinal);
    private readonly object _lifecycleSync = new();
    private readonly object _refreshSync = new();
    private readonly object _updateSync = new();
    private readonly object _startupSettingsSync = new();
    private CancellationTokenSource? _initializationCancellation;
    private Task? _initializationTask;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _historyRangeCancellation;
    private readonly CancellationTokenSource _updateCancellation = new();
    private Task? _activeRefresh;
    private Task? _activeHistoryRangeQuery;
    private Task? _activeUpdateCheck;
    private bool _activeRefreshForcesAppServer;
    private bool _refreshLoopAcceptingRequests;
    private bool _forceRefreshPending;
    private Task _notificationSettingsWriteTask = Task.CompletedTask;
    private Task _themeSettingsWriteTask = Task.CompletedTask;
    private Task _languageSettingsWriteTask = Task.CompletedTask;
    private Task _updateSettingsWriteTask = Task.CompletedTask;
    private Task _startupSettingsWriteTask = Task.CompletedTask;
    private long _notificationSettingsVersion;
    private long _themeSettingsVersion;
    private long _languageSettingsVersion;
    private long _updateSettingsVersion;
    private long _startupSettingsVersion;
    private bool _notificationSettingsReadPending;
    private bool _themeSettingsReadPending;
    private bool _languageSettingsReadPending;
    private bool _updateSettingsReadPending;
    private bool _startupReconcilePending;
    private bool _shutdownStarted;
    private bool _isLoading;
    private bool _isUpdateChecking;
    private string _currentPage = "overview";
    private string _selectedModelRange = "7d";
    private UsageAggregation? _localTodayUsage;
    private UsageAggregation? _localSevenDayUsage;
    private UsageAggregation? _localThirtyDayUsage;
    private string _planDisplay = "—";
    private string _syncText = "等待同步";
    private MediaBrush _syncStatusBrush = MediaBrushes.DarkGoldenrod;
    private string _warning = string.Empty;
    private double _weeklyRemaining;
    private string _weeklyRemainingText = "不可用";
    private string _weeklyResetText = "服务端未返回";
    private bool _generalFiveHourVisible;
    private string _generalFiveRemaining = "—";
    private double _generalFiveRemainingPercent;
    private string _generalFiveReset = "—";
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
    private string _officialMonthTokens = "—";
    private string _officialMonthStatus = "等待官方用量";
    private bool _notificationsEnabled = true;
    private bool _isDarkMode;
    private bool _isEnglish;
    private bool _autoUpdateCheckEnabled = true;
    private bool _isUpdateAvailable;
    private bool _isUpdateFlyoutOpen;
    private string? _latestVersion;
    private Uri? _latestReleaseUrl;
    private string _updateCheckStatus = "每 24 小时最多自动检查一次";
    private UpdateUiStatus _updateUiStatus = UpdateUiStatus.Ready;
    private bool _startupEnabled;
    private bool _confirmedStartupEnabled;
    private string _startupStatus = "默认关闭，登录 Windows 时不会自动运行";
    private string _resetDetailsStatus = "尚未同步";
    private bool _hasResetCreditRows;
    private string _resetCreditEmptyText = "尚未同步重置卡明细";
    private string _officialRangeTokens = "—";
    private string _officialRangeStatus = "等待官方用量";
    private string _localModelRangeTokens = "0";
    private string _modelRangeCost = "—";
    private string _modelRangeCacheHit = "—";
    private string _modelRangeCacheSavings = "—";
    private string _modelRangeCostWithoutCaching = "—";
    private string _modelRangeLabel = "最近 7 天";
    private string _modelRangeTokensLabel = "官方 Tokens · 所选时段";
    private bool _hasModelRows;
    private bool _hasResetExpiry;
    private string _resetNotificationStatus = "等待获取有效期";
    private string _modelPricingStatus = "等待价格同步";
    private string _historyMode = "month";
    private bool _historyRangeInitialized;
    private int _selectedHistoryYear;
    private int _selectedHistoryMonth;
    private DateTime? _historyCustomStartDate;
    private DateTime? _historyCustomEndDate;
    private DateTime? _historyMaximumDate;
    private string _historyValidationMessage = string.Empty;
    private string _historyRangeSummary = "等待刷新";
    private string _historyOfficialTokens = "—";
    private string _historyOfficialStatus = "等待官方用量";
    private string _historyOfficialReturnedDays = "0";
    private string _historyOfficialAverageTokens = "—";
    private string _historyOfficialPeakDate = "—";
    private string _historyOfficialPeakTokens = "—";
    private bool _hasHistoryOfficialDays;
    private bool _isHistoryRangeLoading;
    private string _historyLocalCost = "—";
    private string _historyLocalTokens = "—";
    private string _historyLocalCacheHit = "—";
    private string _historyLocalCostWithoutCaching = "—";
    private string _historyLocalCacheSavings = "—";
    private string _historyLocalPricingStatus = "等待本机样本";
    private long _historyRangeVersion;
    private DateOnly? _historyAppliedStart;
    private DateOnly? _historyAppliedEnd;
    private UsageAggregation? _historyLocalUsage;
    private DashboardSnapshot? _historySnapshot;
    private HistoryTrendData? _historyTrend;
    private bool _isHistoryTrendVisible;
    private bool _historySelectionPending;
    private DateTimeOffset? _lastRefreshCompletedAt;
    private DashboardSnapshot? _lastSnapshot;

    public MainViewModel(
        IDashboardService dashboardService,
        IStartupRegistrationService startupService,
        IUpdateCheckService? updateCheckService = null,
        Action<Uri>? openUri = null,
        string? appVersion = null)
    {
        _dashboardService = dashboardService;
        _startupService = startupService;
        _updateCheckService = updateCheckService;
        _openUri = openUri ?? (uri => StartShell(uri.AbsoluteUri));
        AppVersionText = FormatAppVersion(appVersion ?? ReadAppVersion());
        _refreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading);
        _checkForUpdatesCommand = new AsyncRelayCommand(
            CheckForUpdatesAsync,
            () => _updateCheckService is not null && !IsUpdateChecking);
        _updateActionCommand = new AsyncRelayCommand(
            HandleUpdateActionAsync,
            () => IsUpdateAvailable && !IsUpdateChecking);
        _snoozeUpdateCommand = new AsyncRelayCommand(SnoozeUpdateAsync);
        RefreshCommand = _refreshCommand;
        ShowOverviewCommand = new RelayCommand(() => CurrentPage = "overview");
        ShowModelsCommand = new RelayCommand(() => CurrentPage = "models");
        ShowResetDetailsCommand = new RelayCommand(() => CurrentPage = "reset");
        ShowHistoryCommand = new RelayCommand(() => CurrentPage = "history");
        SelectTodayCommand = new RelayCommand(() => SelectModelRange("today"));
        SelectSevenDayCommand = new RelayCommand(() => SelectModelRange("7d"));
        SelectThirtyDayCommand = new RelayCommand(() => SelectModelRange("30d"));
        SelectHistoryMonthModeCommand = new RelayCommand(() => SetHistoryMode("month"));
        SelectHistoryCustomModeCommand = new RelayCommand(() => SetHistoryMode("custom"));
        PreviousHistoryMonthCommand = new RelayCommand(() => MoveHistoryMonth(-1));
        NextHistoryMonthCommand = new RelayCommand(() => MoveHistoryMonth(1));
        ApplyHistoryRangeCommand = new RelayCommand(ApplyHistorySelection);
        ShowHistoryListCommand = new RelayCommand(() => SetHistoryTrendVisible(false));
        ShowHistoryTrendCommand = new RelayCommand(() => SetHistoryTrendVisible(true));
        OpenUsagePageCommand = new RelayCommand(OpenUsagePage);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        SetLightThemeCommand = new RelayCommand(() => SetDarkMode(false, persist: true));
        SetDarkThemeCommand = new RelayCommand(() => SetDarkMode(true, persist: true));
        SetChineseLanguageCommand = new RelayCommand(() => SetEnglish(false, persist: true));
        SetEnglishLanguageCommand = new RelayCommand(() => SetEnglish(true, persist: true));
        ToggleUpdateFlyoutCommand = new RelayCommand(ToggleUpdateFlyout);
        CloseUpdateFlyoutCommand = new RelayCommand(() => IsUpdateFlyoutOpen = false);
        SnoozeUpdateCommand = _snoozeUpdateCommand;
        ViewUpdateCommand = new RelayCommand(ViewUpdate);
        UpdateActionCommand = _updateActionCommand;
        CheckForUpdatesCommand = _checkForUpdatesCommand;
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? QuitRequested;
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;
    public event EventHandler<ResetExpiryNotificationEventArgs>? ResetExpiryNotificationRequested;

    public ICommand RefreshCommand { get; }
    public ICommand ShowOverviewCommand { get; }
    public ICommand ShowModelsCommand { get; }
    public ICommand ShowResetDetailsCommand { get; }
    public ICommand ShowHistoryCommand { get; }
    public ICommand SelectTodayCommand { get; }
    public ICommand SelectSevenDayCommand { get; }
    public ICommand SelectThirtyDayCommand { get; }
    public ICommand SelectHistoryMonthModeCommand { get; }
    public ICommand SelectHistoryCustomModeCommand { get; }
    public ICommand PreviousHistoryMonthCommand { get; }
    public ICommand NextHistoryMonthCommand { get; }
    public ICommand ApplyHistoryRangeCommand { get; }
    public ICommand ShowHistoryListCommand { get; }
    public ICommand ShowHistoryTrendCommand { get; }
    public ICommand OpenUsagePageCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand SetLightThemeCommand { get; }
    public ICommand SetDarkThemeCommand { get; }
    public ICommand SetChineseLanguageCommand { get; }
    public ICommand SetEnglishLanguageCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand ToggleUpdateFlyoutCommand { get; }
    public ICommand CloseUpdateFlyoutCommand { get; }
    public ICommand SnoozeUpdateCommand { get; }
    public ICommand ViewUpdateCommand { get; }
    public ICommand UpdateActionCommand { get; }
    public ICommand QuitCommand { get; }

    public ObservableCollection<ModelUsageRowViewModel> Models { get; } = [];
    public ObservableCollection<ResetCreditRowViewModel> ResetCredits { get; } = [];
    public ObservableCollection<ResetHistoryRowViewModel> ResetHistory { get; } = [];
    public ObservableCollection<DailyUsageRowViewModel> DailyUsage { get; } = [];
    public HistoryTrendData? HistoryTrend { get => _historyTrend; private set => SetProperty(ref _historyTrend, value); }
    public bool IsHistoryTrendVisible { get => _isHistoryTrendVisible; set => SetHistoryTrendVisible(value); }
    public bool IsHistoryListVisible { get => !_isHistoryTrendVisible; set => SetHistoryTrendVisible(!value); }

    private void SetHistoryTrendVisible(bool value)
    {
        if (SetProperty(ref _isHistoryTrendVisible, value, nameof(IsHistoryTrendVisible)))
            OnPropertyChanged(nameof(IsHistoryListVisible));
    }
    public ObservableCollection<int> HistoryYears { get; } = [];
    public IReadOnlyList<int> HistoryMonths { get; } = Enumerable.Range(1, 12).ToArray();

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                _refreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsUpdateChecking
    {
        get => _isUpdateChecking;
        private set
        {
            if (SetProperty(ref _isUpdateChecking, value))
            {
                _checkForUpdatesCommand.RaiseCanExecuteChanged();
                _updateActionCommand.RaiseCanExecuteChanged();
            }
        }
    }

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
    public bool GeneralFiveHourVisible { get => _generalFiveHourVisible; private set => SetProperty(ref _generalFiveHourVisible, value); }
    public string GeneralFiveRemaining { get => _generalFiveRemaining; private set => SetProperty(ref _generalFiveRemaining, value); }
    public double GeneralFiveRemainingPercent { get => _generalFiveRemainingPercent; private set => SetProperty(ref _generalFiveRemainingPercent, value); }
    public string GeneralFiveReset { get => _generalFiveReset; private set => SetProperty(ref _generalFiveReset, value); }
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
    public string ResetCreditCountText => ResetCreditCount.HasValue
        ? IsEnglish ? $"{ResetCreditCount} available" : $"{ResetCreditCount} 次可用"
        : IsEnglish ? "Count unavailable" : "次数不可用";
    public string ResetCreditCountValueText => ResetCreditCount?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string ResetSummary { get => _resetSummary; private set => SetProperty(ref _resetSummary, value); }
    public string ResetNearestExpiry { get => _resetNearestExpiry; private set => SetProperty(ref _resetNearestExpiry, value); }
    public double ResetTimelinePercent { get => _resetTimelinePercent; private set => SetProperty(ref _resetTimelinePercent, value); }
    public string OfficialMonthTokens { get => _officialMonthTokens; private set => SetProperty(ref _officialMonthTokens, value); }
    public string OfficialMonthStatus { get => _officialMonthStatus; private set => SetProperty(ref _officialMonthStatus, value); }
    public string ResetDetailsStatus { get => _resetDetailsStatus; private set => SetProperty(ref _resetDetailsStatus, value); }
    public bool HasResetCreditRows { get => _hasResetCreditRows; private set => SetProperty(ref _hasResetCreditRows, value); }
    public string ResetCreditEmptyText { get => _resetCreditEmptyText; private set => SetProperty(ref _resetCreditEmptyText, value); }
    public string OfficialRangeTokens { get => _officialRangeTokens; private set => SetProperty(ref _officialRangeTokens, value); }
    public string OfficialRangeStatus { get => _officialRangeStatus; private set => SetProperty(ref _officialRangeStatus, value); }
    public string LocalModelRangeTokens { get => _localModelRangeTokens; private set => SetProperty(ref _localModelRangeTokens, value); }
    public string ModelRangeCost { get => _modelRangeCost; private set => SetProperty(ref _modelRangeCost, value); }
    public string ModelRangeCacheHit { get => _modelRangeCacheHit; private set => SetProperty(ref _modelRangeCacheHit, value); }
    public string ModelRangeCacheSavings { get => _modelRangeCacheSavings; private set => SetProperty(ref _modelRangeCacheSavings, value); }
    public string ModelRangeCostWithoutCaching { get => _modelRangeCostWithoutCaching; private set => SetProperty(ref _modelRangeCostWithoutCaching, value); }
    public string ModelRangeLabel { get => _modelRangeLabel; private set => SetProperty(ref _modelRangeLabel, value); }
    public string ModelRangeTokensLabel { get => _modelRangeTokensLabel; private set => SetProperty(ref _modelRangeTokensLabel, value); }
    public bool HasModelRows { get => _hasModelRows; private set => SetProperty(ref _hasModelRows, value); }
    public bool HasResetExpiry { get => _hasResetExpiry; private set => SetProperty(ref _hasResetExpiry, value); }
    public string ResetNotificationStatus { get => _resetNotificationStatus; private set => SetProperty(ref _resetNotificationStatus, value); }
    public string ModelPricingStatus { get => _modelPricingStatus; private set => SetProperty(ref _modelPricingStatus, value); }
    public bool IsHistoryMonthMode => _historyMode == "month";
    public bool IsHistoryCustomMode => _historyMode == "custom";
    public int SelectedHistoryYear
    {
        get => _selectedHistoryYear;
        set
        {
            if (SetProperty(ref _selectedHistoryYear, value))
            {
                HistorySelectionChanged();
            }
        }
    }
    public int SelectedHistoryMonth
    {
        get => _selectedHistoryMonth;
        set
        {
            if (SetProperty(ref _selectedHistoryMonth, value))
            {
                HistorySelectionChanged();
            }
        }
    }
    public DateTime? HistoryCustomStartDate
    {
        get => _historyCustomStartDate;
        set
        {
            if (SetProperty(ref _historyCustomStartDate, value?.Date))
            {
                HistorySelectionChanged();
            }
        }
    }
    public DateTime? HistoryCustomEndDate
    {
        get => _historyCustomEndDate;
        set
        {
            if (SetProperty(ref _historyCustomEndDate, value?.Date))
            {
                HistorySelectionChanged();
            }
        }
    }
    public DateTime? HistoryMaximumDate { get => _historyMaximumDate; private set => SetProperty(ref _historyMaximumDate, value); }
    public string HistoryValidationMessage { get => _historyValidationMessage; private set { SetProperty(ref _historyValidationMessage, value); OnPropertyChanged(nameof(HasHistoryValidationError)); } }
    public bool HasHistoryValidationError => !string.IsNullOrWhiteSpace(HistoryValidationMessage);
    public string HistoryRangeSummary { get => _historyRangeSummary; private set => SetProperty(ref _historyRangeSummary, value); }
    public string HistoryOfficialTokens { get => _historyOfficialTokens; private set => SetProperty(ref _historyOfficialTokens, value); }
    public string HistoryOfficialStatus { get => _historyOfficialStatus; private set => SetProperty(ref _historyOfficialStatus, value); }
    public string HistoryOfficialReturnedDays { get => _historyOfficialReturnedDays; private set => SetProperty(ref _historyOfficialReturnedDays, value); }
    public string HistoryOfficialAverageTokens { get => _historyOfficialAverageTokens; private set => SetProperty(ref _historyOfficialAverageTokens, value); }
    public string HistoryOfficialPeakDate { get => _historyOfficialPeakDate; private set => SetProperty(ref _historyOfficialPeakDate, value); }
    public string HistoryOfficialPeakTokens { get => _historyOfficialPeakTokens; private set => SetProperty(ref _historyOfficialPeakTokens, value); }
    public bool HasHistoryOfficialDays { get => _hasHistoryOfficialDays; private set => SetProperty(ref _hasHistoryOfficialDays, value); }
    public bool IsHistoryRangeLoading { get => _isHistoryRangeLoading; private set => SetProperty(ref _isHistoryRangeLoading, value); }
    public string HistoryLocalCost { get => _historyLocalCost; private set => SetProperty(ref _historyLocalCost, value); }
    public string HistoryLocalTokens { get => _historyLocalTokens; private set => SetProperty(ref _historyLocalTokens, value); }
    public string HistoryLocalCacheHit { get => _historyLocalCacheHit; private set => SetProperty(ref _historyLocalCacheHit, value); }
    public string HistoryLocalCostWithoutCaching { get => _historyLocalCostWithoutCaching; private set => SetProperty(ref _historyLocalCostWithoutCaching, value); }
    public string HistoryLocalCacheSavings { get => _historyLocalCacheSavings; private set => SetProperty(ref _historyLocalCacheSavings, value); }
    public string HistoryLocalPricingStatus { get => _historyLocalPricingStatus; private set => SetProperty(ref _historyLocalPricingStatus, value); }
    public bool CanSelectNextHistoryMonth
    {
        get
        {
            if (!_historyRangeInitialized || _lastSnapshot is null || _selectedHistoryYear <= 0 || _selectedHistoryMonth is < 1 or > 12)
            {
                return false;
            }

            var currentMonth = new DateOnly(_lastSnapshot.RefreshedAt.Year, _lastSnapshot.RefreshedAt.Month, 1);
            return new DateOnly(_selectedHistoryYear, _selectedHistoryMonth, 1).AddMonths(1) <= currentMonth;
        }
    }
    public bool IsDarkMode => _isDarkMode;
    public bool IsLightMode => !_isDarkMode;
    public bool IsEnglish => _isEnglish;
    public bool IsChinese => !_isEnglish;
    public string StartupStatus { get => _startupStatus; private set => SetProperty(ref _startupStatus, value); }
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set
        {
            if (!SetProperty(ref _isUpdateAvailable, value))
            {
                return;
            }

            if (!value)
            {
                IsUpdateFlyoutOpen = false;
            }

            _updateActionCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsUpdateFlyoutOpen { get => _isUpdateFlyoutOpen; set => SetProperty(ref _isUpdateFlyoutOpen, value); }
    public string LatestVersionText => string.IsNullOrWhiteSpace(_latestVersion) ? string.Empty : $"v{_latestVersion}";
    public string AppVersionText { get; }
    public string UpdateCheckStatus { get => _updateCheckStatus; private set => SetProperty(ref _updateCheckStatus, value); }
    public string UpdateFlyoutMessage => IsEnglish
        ? "A new version is available on GitHub. Exit the old app, extract the ZIP to a new empty folder, then run it; the app will not download or modify installation files."
        : "GitHub 已发布新版本。请先退出旧程序，将下载的 ZIP 解压到新的空目录后再运行；应用不会下载或修改安装文件。";
    public string UpdateActionText => IsEnglish ? "Open download page" : "打开下载页";
    public string UpdateSecondaryActionText => IsEnglish ? "Remind me later" : "稍后提醒";

    public bool AutoUpdateCheckEnabled
    {
        get => _autoUpdateCheckEnabled;
        set
        {
            lock (_lifecycleSync)
            {
                if (_shutdownStarted || _updateCheckService is null)
                {
                    return;
                }

                var changed = SetProperty(ref _autoUpdateCheckEnabled, value);
                if (!changed && !_updateSettingsReadPending)
                {
                    return;
                }

                _updateSettingsVersion++;
                _updateUiStatus = value ? UpdateUiStatus.Ready : UpdateUiStatus.Disabled;
                UpdateLocalizedUpdateStatus();
                _updateSettingsWriteTask = PersistUpdateSettingAsync(
                    _updateSettingsWriteTask,
                    value,
                    _updateSettingsVersion);
            }
        }
    }

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

                    StartupStatus = IsEnglish ? "Updating startup setting…" : "正在更新开机启动设置…";
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
            await InitializeLanguageSettingAsync(cancellationToken);
            await InitializeUpdateCheckSettingAsync(cancellationToken);
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

                Warning = IsEnglish
                    ? $"Failed to initialize local history: {exception.Message}"
                    : $"本地历史初始化失败：{exception.Message}";
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (_updateCheckService is not null && AutoUpdateCheckEnabled)
        {
            _ = StartUpdateCheckAsync(force: false, showStatus: false);
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
        Task? historyRangeQuery;
        Task startupSettingsWriteTask;
        Task notificationSettingsWriteTask;
        Task themeSettingsWriteTask;
        Task languageSettingsWriteTask;
        Task updateSettingsWriteTask;
        Task? initializationTask;
        Task? updateCheck;
        lock (_lifecycleSync)
        {
            _shutdownStarted = true;
            _initializationCancellation?.Cancel();
            _updateCancellation.Cancel();
            _historyRangeCancellation?.Cancel();
            historyRangeQuery = _activeHistoryRangeQuery;
            initializationTask = _initializationTask;
            lock (_refreshSync)
            {
                _refreshLoopAcceptingRequests = false;
                _forceRefreshPending = false;
                _refreshCancellation?.Cancel();
                refresh = _activeRefresh;
            }

            lock (_updateSync)
            {
                updateCheck = _activeUpdateCheck;
            }

            lock (_startupSettingsSync)
            {
                startupSettingsWriteTask = _startupSettingsWriteTask;
            }

            notificationSettingsWriteTask = _notificationSettingsWriteTask;
            themeSettingsWriteTask = _themeSettingsWriteTask;
            languageSettingsWriteTask = _languageSettingsWriteTask;
            updateSettingsWriteTask = _updateSettingsWriteTask;
        }

        var tasks = new List<Task>
        {
            notificationSettingsWriteTask,
            themeSettingsWriteTask,
            languageSettingsWriteTask,
            updateSettingsWriteTask,
            startupSettingsWriteTask
        };
        if (refresh is not null)
        {
            tasks.Add(refresh);
        }
        if (historyRangeQuery is not null)
        {
            tasks.Add(historyRangeQuery);
        }
        if (initializationTask is not null)
        {
            tasks.Add(initializationTask);
        }
        if (updateCheck is not null)
        {
            tasks.Add(updateCheck);
        }
        await AsyncShutdown.WaitAsync(tasks, timeout);
    }

    public void CancelPendingOperations()
    {
        lock (_refreshSync)
        {
            _refreshCancellation?.Cancel();
        }

        _historyRangeCancellation?.Cancel();
        _updateCancellation.Cancel();
    }

    private Task CheckForUpdatesAsync()
        => StartUpdateCheckAsync(force: true, showStatus: true);

    private Task StartUpdateCheckAsync(bool force, bool showStatus)
    {
        lock (_lifecycleSync)
        {
            if (_shutdownStarted || _updateCheckService is null)
            {
                return Task.CompletedTask;
            }

            lock (_updateSync)
            {
                if (_activeUpdateCheck is { IsCompleted: false })
                {
                    return _activeUpdateCheck;
                }

                _activeUpdateCheck = RunUpdateCheckAsync(force, showStatus);
                return _activeUpdateCheck;
            }
        }
    }

    private async Task RunUpdateCheckAsync(bool force, bool showStatus)
    {
        IsUpdateChecking = true;
        IsUpdateAvailable = false;
        if (showStatus)
        {
            _updateUiStatus = UpdateUiStatus.Checking;
            UpdateLocalizedUpdateStatus();
        }

        try
        {
            var result = await _updateCheckService!.CheckAsync(force, _updateCancellation.Token);
            lock (_lifecycleSync)
            {
                if (_shutdownStarted || _updateCancellation.IsCancellationRequested)
                {
                    return;
                }

                if (result.IsUpdateAvailable && result.ReleaseUrl is not null)
                {
                    _latestVersion = result.LatestVersion;
                    _latestReleaseUrl = result.ReleaseUrl;
                    OnPropertyChanged(nameof(LatestVersionText));
                    IsUpdateAvailable = true;
                    _updateUiStatus = UpdateUiStatus.Available;
                    UpdateLocalizedUpdateStatus();
                }
                else if (result.Outcome is UpdateCheckOutcome.NoUpdate or UpdateCheckOutcome.Skipped)
                {
                    IsUpdateAvailable = false;
                    if (showStatus || result.NetworkRequested)
                    {
                        _updateUiStatus = UpdateUiStatus.Latest;
                        UpdateLocalizedUpdateStatus();
                    }
                }
                else if (showStatus)
                {
                    IsUpdateAvailable = false;
                    _updateUiStatus = UpdateUiStatus.Failed;
                    UpdateLocalizedUpdateStatus();
                }
            }
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
            // Application shutdown owns this cancellation.
        }
        catch
        {
            if (showStatus)
            {
                _updateUiStatus = UpdateUiStatus.Failed;
                UpdateLocalizedUpdateStatus();
            }
        }
        finally
        {
            IsUpdateChecking = false;
            lock (_updateSync)
            {
                _activeUpdateCheck = null;
            }
        }
    }

    private Task HandleUpdateActionAsync()
    {
        ViewUpdate();
        return Task.CompletedTask;
    }

    private void ToggleUpdateFlyout()
    {
        if (IsUpdateAvailable)
        {
            IsUpdateFlyoutOpen = !IsUpdateFlyoutOpen;
        }
    }

    private async Task SnoozeUpdateAsync()
    {
        if (_updateCheckService is null || string.IsNullOrWhiteSpace(_latestVersion))
        {
            return;
        }

        try
        {
            await _updateCheckService.SnoozeAsync(_latestVersion, _updateCancellation.Token);
            if (_shutdownStarted)
            {
                return;
            }

            IsUpdateAvailable = false;
            _updateUiStatus = UpdateUiStatus.Snoozed;
            UpdateLocalizedUpdateStatus();
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
            // Application shutdown owns this cancellation.
        }
        catch
        {
            _updateUiStatus = UpdateUiStatus.Failed;
            UpdateLocalizedUpdateStatus();
        }
    }

    private void ViewUpdate()
    {
        if (!IsUpdateAvailable || _latestReleaseUrl is null)
        {
            return;
        }

        _openUri(_latestReleaseUrl);
        IsUpdateFlyoutOpen = false;
    }

    private void UpdateLocalizedUpdateStatus()
    {
        UpdateCheckStatus = _updateUiStatus switch
        {
            UpdateUiStatus.Disabled => IsEnglish ? "Automatic update checks are off" : "自动检查更新已关闭",
            UpdateUiStatus.Checking => IsEnglish ? "Checking GitHub for updates…" : "正在检查 GitHub 更新…",
            UpdateUiStatus.Latest => IsEnglish ? "You are using the latest version" : "当前已是最新版本",
            UpdateUiStatus.Available => IsEnglish
                ? $"Version v{_latestVersion} is available"
                : $"发现新版本 v{_latestVersion}",
            UpdateUiStatus.Failed => IsEnglish ? "Check failed; try again later" : "检查失败，请稍后重试",
            UpdateUiStatus.Snoozed => IsEnglish ? "This version is snoozed for 24 hours" : "已延后 24 小时提醒",
            _ => IsEnglish ? "Checks automatically at most once every 24 hours" : "每 24 小时最多自动检查一次"
        };
        OnPropertyChanged(nameof(UpdateFlyoutMessage));
        OnPropertyChanged(nameof(UpdateActionText));
        OnPropertyChanged(nameof(UpdateSecondaryActionText));
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
            SyncText = IsEnglish ? $"Up to date · {lastRefresh:HH:mm}" : $"已是最新 · {lastRefresh:HH:mm}";
            return;
        }

        IsLoading = true;
        SyncText = IsEnglish ? "Syncing…" : "正在同步…";
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
            SyncText = IsEnglish ? "Refresh canceled" : "刷新已取消";
            SyncStatusBrush = MediaBrushes.DarkGoldenrod;
        }
        catch (Exception exception)
        {
            Warning = exception.Message;
            SyncText = IsEnglish ? "Sync failed" : "同步失败";
            SyncStatusBrush = MediaBrushes.Firebrick;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(DashboardSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        PlanDisplay = PlanCapabilities.DisplayName(snapshot.Account?.PlanType);
        SyncText = IsEnglish ? $"Updated {snapshot.RefreshedAt:HH:mm}" : $"更新于 {snapshot.RefreshedAt:HH:mm}";
        Warning = FormatWarning(snapshot.Warning);
        SyncStatusBrush = string.IsNullOrWhiteSpace(snapshot.Warning) ? MediaBrushes.SeaGreen : MediaBrushes.DarkGoldenrod;

        var weekly = snapshot.Quota?.General?.Weekly;
        WeeklyRemaining = weekly?.RemainingPercent ?? 0;
        WeeklyRemainingText = weekly?.RemainingPercent is { } weeklyRemaining
            ? $"{weeklyRemaining}%"
            : IsEnglish ? "Unavailable" : "不可用";
        WeeklyResetText = weekly?.ResetsAt is { } weeklyReset
            ? $"{FormatDateTime(weeklyReset)}{(IsEnglish ? " reset" : " 重置")}{StaleSuffix(snapshot.Freshness.IsGeneralQuotaStale, snapshot.Freshness.GeneralQuotaUpdatedAt)}"
            : IsEnglish ? "Weekly reset time unavailable" : "每周重置时间未返回";
        var generalFiveHour = snapshot.Quota?.General?.FiveHour;
        GeneralFiveHourVisible = IsUsableWindow(generalFiveHour, 300);
        GeneralFiveRemaining = generalFiveHour?.RemainingPercent is { } generalFive
            ? IsEnglish ? $"{generalFive}% left" : $"剩余 {generalFive}%"
            : IsEnglish ? "Unavailable" : "不可用";
        GeneralFiveRemainingPercent = generalFiveHour?.RemainingPercent ?? 0;
        GeneralFiveReset = FormatCompactReset(generalFiveHour)
                           + StaleSuffix(
                               snapshot.Freshness.IsGeneralFiveHourQuotaStale,
                               snapshot.Freshness.GeneralFiveHourQuotaUpdatedAt);
        PaceUsedPercent = Math.Clamp(weekly?.UsedPercent ?? 0, 0, 100);
        PaceElapsedPercent = Math.Clamp(snapshot.WeeklyPace.ElapsedPercent ?? 0, 0, 100);
        PaceWillExhaust = snapshot.WeeklyPace.WillExhaust ?? false;
        PaceText = snapshot.WeeklyPace.ProjectedUsedPercent is null or <= 0
            ? IsEnglish ? "Not enough data to forecast" : "暂无足够数据预测"
            : IsEnglish
                ? $"Projected to use {snapshot.WeeklyPace.ProjectedUsedPercent:0}% this week · {(snapshot.WeeklyPace.WillExhaust == true ? "may run out early" : "will not run out") }"
                : $"预计本周消耗 {snapshot.WeeklyPace.ProjectedUsedPercent:0}% 额度 · {(snapshot.WeeklyPace.WillExhaust == true ? "可能提前耗尽" : "不会耗尽")}";
        PaceDetailText = weekly?.UsedPercent is { } used && snapshot.WeeklyPace.ElapsedPercent is { } elapsed
            ? IsEnglish
                ? $"Used {used}% ≈ {used * 7d / 100:0.0} balanced days · 14.3% daily baseline"
                : $"已用 {used}% ≈ {used * 7d / 100:0.0} 天均衡额度 · 每天基准 14.3%"
            : IsEnglish ? "Baseline: split evenly across 7 days, about 14.3% daily" : "基准：7 天均分，每天约 14.3%";
        PaceWindowText = weekly?.ResetsAt is { } reset
                         && weekly.WindowDurationMinutes is > 0
                         && snapshot.WeeklyPace.ElapsedPercent is { } windowElapsed
            ? IsEnglish
                ? $"Gray: used · dashed marker: {windowElapsed:0.0}% · starts {reset.AddMinutes(-weekly.WindowDurationMinutes.Value).ToLocalTime():M/d HH:mm}"
                : $"灰色已用 · 虚线进度 {windowElapsed:0.0}% · {reset.AddMinutes(-weekly.WindowDurationMinutes.Value).ToLocalTime():M/d HH:mm} 起算"
            : IsEnglish ? "Each segment represents 24 hours of the quota window" : "每格代表额度窗口中的 24 小时";

        var spark = snapshot.Quota?.Spark;
        SparkFiveHourVisible = IsUsableWindow(spark?.FiveHour, 300);
        SparkWeeklyVisible = IsUsableWindow(spark?.Weekly, 10_080);
        SparkVisible = SparkFiveHourVisible || SparkWeeklyVisible;
        SparkFiveRemaining = spark?.FiveHour?.RemainingPercent is { } five
            ? IsEnglish ? $"{five}% left" : $"剩余 {five}%"
            : IsEnglish ? "Unavailable" : "不可用";
        SparkFiveRemainingPercent = spark?.FiveHour?.RemainingPercent ?? 0;
        SparkFiveReset = FormatReset(spark?.FiveHour) + StaleSuffix(snapshot.Freshness.IsSparkQuotaStale, snapshot.Freshness.SparkQuotaUpdatedAt);
        SparkWeeklyRemaining = spark?.Weekly?.RemainingPercent is { } sparkWeek
            ? IsEnglish ? $"{sparkWeek}% left" : $"剩余 {sparkWeek}%"
            : IsEnglish ? "Unavailable" : "不可用";
        SparkWeeklyRemainingPercent = spark?.Weekly?.RemainingPercent ?? 0;
        SparkWeeklyReset = FormatReset(spark?.Weekly) + StaleSuffix(snapshot.Freshness.IsSparkQuotaStale, snapshot.Freshness.SparkQuotaUpdatedAt);

        ApplyResetCredits(snapshot);
        _localTodayUsage = snapshot.LocalTodayUsage;
        _localSevenDayUsage = snapshot.LocalSevenDayUsage;
        _localThirtyDayUsage = snapshot.LocalThirtyDayUsage;
        ApplyOfficialUsage(snapshot);
        ApplySelectedModelUsage();
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
            ? IsEnglish ? $"Nearest expiry in {DaysRemaining(expiry)} days" : $"最近 {DaysRemaining(expiry)} 天后到期"
            : quota?.ResetCreditDetailsComplete == false
                ? IsEnglish ? "Only the available count was returned" : "仅返回可用次数"
                : IsEnglish ? "No expiry information" : "暂无到期信息";
        ResetNearestExpiry = nearest?.ExpiresAt is { } nearestExpiry
            ? IsEnglish
                ? $"Nearest expiry: {nearestExpiry.ToLocalTime().ToString("MMM d", EnglishCulture)} (in {DaysRemaining(nearestExpiry)} days)"
                : $"最近到期：{nearestExpiry.ToLocalTime():M月d日}（{DaysRemaining(nearestExpiry)} 天后）"
            : IsEnglish ? "No expiry information" : "暂无到期信息";
        ResetTimelinePercent = nearest?.ExpiresAt is { } timelineExpiry
            ? Math.Clamp((DateTimeOffset.Now - nearest.GrantedAt.ToLocalTime()).TotalSeconds
                         / Math.Max((timelineExpiry.ToLocalTime() - nearest.GrantedAt.ToLocalTime()).TotalSeconds, 1)
                         * 100, 0, 100)
            : 0;
        HasResetExpiry = nearest?.ExpiresAt.HasValue == true;
        ResetNotificationStatus = HasResetExpiry
            ? IsEnglish ? "Windows notifications at 7, 3, and 1 day, and on expiry day" : "7 天、3 天、1 天和到期当天发送 Windows 通知"
            : IsEnglish ? "Expiry was not returned; reminders are unavailable" : "服务端未返回有效期，提醒暂不可用";
        ResetDetailsStatus = quota is null
            ? IsEnglish ? "Not returned by the service" : "服务端未返回"
            : quota.ResetCreditDetailsComplete
                ? IsEnglish ? "Details fully synchronized" : "详情已完整同步"
                : IsEnglish ? "Only the total count was returned" : "服务端仅返回总次数";
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
                            IsEnglish
                                ? $"{newIds.Length} reset credits expire in {alert.DaysRemaining} days ({alert.ExpiresAt.ToLocalTime().ToString("MMM d", EnglishCulture)})."
                                : $"有 {newIds.Length} 张重置卡将在 {alert.DaysRemaining} 天后到期（{alert.ExpiresAt.ToLocalTime():M月d日}）。"));
                }
            }
        }

        ResetCredits.Clear();
        if (quota is not null)
        {
            foreach (var credit in quota.ResetCredits.OrderBy(item => item.ExpiresAt ?? DateTimeOffset.MaxValue))
            {
                ResetCredits.Add(new ResetCreditRowViewModel(
                    IsEnglish ? "1 use" : "1 次",
                    credit.ExpiresAt is { } expires
                        ? IsEnglish ? $"Expires {expires.ToLocalTime().ToString("MMM d, yyyy", EnglishCulture)}" : $"{expires.ToLocalTime():yyyy年M月d日}到期"
                        : IsEnglish ? "No expiry time" : "无到期时间",
                    credit.ExpiresAt is { } expiryValue
                        ? IsEnglish ? $"{DaysRemaining(expiryValue)} days left" : $"剩余 {DaysRemaining(expiryValue)} 天"
                        : IsEnglish ? "Validity unavailable" : "有效期未返回",
                    IsEnglish ? $"Granted {credit.GrantedAt.ToLocalTime().ToString("MMM d, yyyy", EnglishCulture)}" : $"获得于 {credit.GrantedAt.ToLocalTime():yyyy年M月d日}",
                    credit.Title ?? (IsEnglish ? "Codex reset reward" : "Codex 重置奖励")));
            }
        }

        HasResetCreditRows = ResetCredits.Count > 0;
        ResetCreditEmptyText = quota is null
            ? IsEnglish ? "The service did not return reset credit data." : "服务端未返回重置卡数据。"
            : quota.ResetCreditDetailsComplete
                ? IsEnglish ? "No individual credit details are currently available." : "当前没有可用的单卡明细。"
                : ResetCreditCount.HasValue
                    ? IsEnglish
                        ? $"The service currently returns only {ResetCreditCount} available uses; grant and expiry times are unavailable."
                        : $"服务端当前只返回 {ResetCreditCount} 次可用，单卡授予时间和有效期暂未返回。"
                    : IsEnglish ? "Reset credit count and details are unavailable." : "重置卡次数和单卡详情均不可用。";

        ResetHistory.Clear();
        foreach (var item in snapshot.ResetHistory)
        {
            var (icon, description) = item.Kind switch
            {
                ResetHistoryKind.Granted => ("\uE109", IsEnglish ? $"Granted {item.Count}" : $"获得 {item.Count} 次"),
                ResetHistoryKind.Expired => ("\uE711", IsEnglish ? $"Expired {item.Count}" : $"已过期 {item.Count} 次"),
                _ => ("\uE108", IsEnglish ? $"Used {item.Count}" : $"使用 {item.Count} 次")
            };
            ResetHistory.Add(new ResetHistoryRowViewModel(
                icon,
                item.OccurredAt.ToLocalTime().ToString(IsEnglish ? "MMM d" : "M月d日", IsEnglish ? EnglishCulture : ChineseCulture),
                description,
                item.IsInferred ? IsEnglish ? "Inferred" : "推测" : string.Empty,
                item.IsInferred));
        }
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
            "today" => _localTodayUsage,
            "30d" => _localThirtyDayUsage,
            _ => _localSevenDayUsage
        };
        ModelRangeLabel = _selectedModelRange switch
        {
            "today" => IsEnglish ? "Today" : "今天",
            "30d" => IsEnglish ? "Last 30 days" : "最近 30 天",
            _ => IsEnglish ? "Last 7 days" : "最近 7 天"
        };
        var today = DateOnly.FromDateTime(_lastSnapshot?.RefreshedAt.DateTime ?? DateTime.Today);
        var from = _selectedModelRange switch
        {
            "today" => today,
            "30d" => today.AddDays(-29),
            _ => today.AddDays(-6)
        };
        var officialDays = GetOfficialDays(from, today.AddDays(1));
        ModelRangeTokensLabel = IsEnglish ? "Official tokens · Selected period" : "官方 Tokens · 所选时段";
        if (_selectedModelRange == "today" && officialDays.Length == 0)
        {
            if (_lastSnapshot is null)
            {
                ModelRangeTokensLabel = IsEnglish ? "Today's tokens" : "今日 Tokens";
                OfficialRangeTokens = "—";
                OfficialRangeStatus = IsEnglish ? "Waiting for official usage" : "等待官方用量";
            }
            else if (usage is { TotalTokens: > 0 })
            {
                var refreshedAt = _lastSnapshot.RefreshedAt;
                ModelRangeTokensLabel = IsEnglish ? "Today's tokens · temporary local sample" : "今日 Tokens · 本地临时统计";
                OfficialRangeTokens = $"≈ {FormatDisplayTokens(usage.TotalTokens)}";
                OfficialRangeStatus = _lastSnapshot.Freshness.HasCurrentDailyUsageResponse
                    ? IsEnglish
                        ? $"Official data synced, but today's bucket is not available yet; ≈ is a temporary local rollout sample · local view refreshed {refreshedAt:HH:mm}"
                        : $"官方已同步，但今天尚未生成；≈ 为本机 rollout 临时样本 · 本地视图刷新于 {refreshedAt:HH:mm}"
                    : IsEnglish
                        ? $"Official daily usage request failed; ≈ is a temporary local rollout sample · local view refreshed {refreshedAt:HH:mm}"
                        : $"官方每日用量请求失败；≈ 为本机 rollout 临时样本 · 本地视图刷新于 {refreshedAt:HH:mm}";
            }
            else
            {
                ModelRangeTokensLabel = IsEnglish ? "Today's tokens" : "今日 Tokens";
                OfficialRangeTokens = "—";
                OfficialRangeStatus = _lastSnapshot.Freshness.HasCurrentDailyUsageResponse
                    ? IsEnglish
                        ? "Today's official data is not available yet, and there are no local samples"
                        : "今日官方数据尚未生成，且暂无本地样本"
                    : IsEnglish
                        ? "Official daily usage request failed; today's official data is not available yet, and there are no local samples"
                        : "官方每日用量请求失败；今日官方数据尚未生成，且暂无本地样本";
            }
        }
        else
        {
            OfficialRangeTokens = FormatOfficialTotal(officialDays);
            OfficialRangeStatus = FormatOfficialUsageStatus(officialDays);
        }
        Models.Clear();
        if (usage is null)
        {
            LocalModelRangeTokens = "0";
            ModelRangeCost = "—";
            ModelRangeCacheHit = "—";
            ModelRangeCacheSavings = "—";
            ModelRangeCostWithoutCaching = "—";
            ModelPricingStatus = IsEnglish ? "Waiting for local samples" : "等待本机样本";
            HasModelRows = false;
            return;
        }

        LocalModelRangeTokens = FormatDisplayTokens(usage.TotalTokens);
        ModelRangeCost = usage.EstimatedCostUsd.HasValue ? $"≈ ${usage.EstimatedCostUsd:0.00}" : IsEnglish ? "No price" : "暂无定价";
        ModelRangeCacheHit = $"{usage.CacheHitPercent:0}%";
        ModelRangeCacheSavings = usage.EstimatedCacheSavingsUsd.HasValue
            ? $"≈ ${usage.EstimatedCacheSavingsUsd:0.00}"
            : IsEnglish ? "No estimate" : "暂无估算";
        ModelRangeCostWithoutCaching = usage.EstimatedCostWithoutCachingUsd.HasValue
            ? $"≈ ${usage.EstimatedCostWithoutCachingUsd:0.00}"
            : IsEnglish ? "No price" : "暂无定价";
        ModelPricingStatus = FormatPricingStatus(usage, _lastSnapshot?.Pricing);
        var colors = new[] { "#3B76E8", "#11A9C2", "#8667E8", "#E6A63B", "#E86C91", "#7BC2DA" };
        var modelRows = usage.Models.Take(5).ToList();
        if (usage.Models.Count > 5)
        {
            var remainder = usage.Models.Skip(5).ToArray();
            var priced = remainder.Where(item => item.EstimatedCostUsd.HasValue).ToArray();
            modelRows.Add(new ModelUsageSummary(
                IsEnglish ? "Other" : "其他",
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
                FormatDisplayTokens(model.TotalTokens),
                IsEnglish ? $"{model.CacheHitPercent:0}% cached" : $"{model.CacheHitPercent:0}% 缓存",
                model.EstimatedCostUsd.HasValue ? $"${model.EstimatedCostUsd:0.00}" : "—",
                share,
                $"{share:0}%",
                colors[index % colors.Length]));
        }

        HasModelRows = Models.Count > 0;
    }

    private void ApplyOfficialUsage(DashboardSnapshot snapshot)
    {
        var today = DateOnly.FromDateTime(snapshot.RefreshedAt.DateTime);
        var month = GetOfficialDays(new DateOnly(today.Year, today.Month, 1), today.AddDays(1));
        OfficialMonthTokens = FormatOfficialTotal(month);
        OfficialMonthStatus = FormatOfficialUsageStatus(month);
        ApplyHistorySnapshot(snapshot, today);
    }

    private void ApplyHistorySnapshot(DashboardSnapshot snapshot, DateOnly today)
    {
        var sameSnapshot = ReferenceEquals(_historySnapshot, snapshot);
        HistoryMaximumDate = today.ToDateTime(TimeOnly.MinValue);
        UpdateHistoryYears(snapshot, today);
        if (!_historyRangeInitialized)
        {
            SelectedHistoryYear = today.Year;
            SelectedHistoryMonth = today.Month;
            HistoryCustomStartDate = new DateOnly(today.Year, today.Month, 1).ToDateTime(TimeOnly.MinValue);
            HistoryCustomEndDate = today.ToDateTime(TimeOnly.MinValue);
            _historyRangeInitialized = true;
        }

        OnPropertyChanged(nameof(CanSelectNextHistoryMonth));
        if (!TryGetHistoryRange(out var start, out var end, out var validation))
        {
            HistoryValidationMessage = validation;
            ClearHistoryResults(validation);
            _historySnapshot = snapshot;
            return;
        }

        HistoryValidationMessage = string.Empty;
        if (_historySelectionPending)
        {
            HistoryRangeSummary = FormatHistoryRangeSummary(start, end);
            ClearHistoryResults(IsEnglish ? "Selection changed · apply to view results" : "选择已更改 · 应用后查看结果");
            _historySnapshot = snapshot;
            return;
        }
        var sameRange = _historyAppliedStart == start && _historyAppliedEnd == end;
        ApplyHistoryOfficialRange(start, end);
        _historySnapshot = snapshot;
        if (sameSnapshot && sameRange && _historyLocalUsage is not null)
        {
            ApplyHistoryLocalUsage(_historyLocalUsage);
            return;
        }

        var currentMonthStart = new DateOnly(today.Year, today.Month, 1);
        if (start == currentMonthStart && end == today)
        {
            CancelHistoryRangeQuery();
            _historyLocalUsage = snapshot.LocalMonthUsage;
            _historyAppliedStart = start;
            _historyAppliedEnd = end;
            ApplyHistoryLocalUsage(snapshot.LocalMonthUsage);
            return;
        }

        BeginHistoryRangeQuery(start, end);
    }

    private void UpdateHistoryYears(DashboardSnapshot snapshot, DateOnly today)
    {
        var firstYear = snapshot.OfficialUsage?.DailyUsage.Count > 0
            ? Math.Min(today.Year, snapshot.OfficialUsage.DailyUsage.Min(day => day.Date.Year))
            : today.Year;
        if (HistoryYears.Count > 0
            && HistoryYears[0] == today.Year
            && HistoryYears[^1] == firstYear)
        {
            return;
        }

        HistoryYears.Clear();
        for (var year = today.Year; year >= firstYear; year--)
        {
            HistoryYears.Add(year);
        }
    }

    private void SetHistoryMode(string mode)
    {
        if (_historyMode == mode)
        {
            return;
        }

        _historyMode = mode;
        OnPropertyChanged(nameof(IsHistoryMonthMode));
        OnPropertyChanged(nameof(IsHistoryCustomMode));
        HistorySelectionChanged();
    }

    private void MoveHistoryMonth(int offset)
    {
        if (!_historyRangeInitialized || _selectedHistoryYear <= 0 || _selectedHistoryMonth is < 1 or > 12)
        {
            return;
        }

        var target = new DateOnly(_selectedHistoryYear, _selectedHistoryMonth, 1).AddMonths(offset);
        if (_lastSnapshot is null)
        {
            return;
        }

        var currentMonth = new DateOnly(_lastSnapshot.RefreshedAt.Year, _lastSnapshot.RefreshedAt.Month, 1);
        if (target > currentMonth)
        {
            return;
        }

        if (!HistoryYears.Contains(target.Year))
        {
            HistoryYears.Add(target.Year);
        }

        SetProperty(ref _selectedHistoryYear, target.Year, nameof(SelectedHistoryYear));
        SetProperty(ref _selectedHistoryMonth, target.Month, nameof(SelectedHistoryMonth));
        OnPropertyChanged(nameof(CanSelectNextHistoryMonth));
        ApplyHistorySelection();
    }

    private void HistorySelectionChanged()
    {
        if (!_historyRangeInitialized)
        {
            return;
        }

        _historySelectionPending = true;
        CancelHistoryRangeQuery();
        OnPropertyChanged(nameof(CanSelectNextHistoryMonth));
        if (!TryGetHistoryRange(out var start, out var end, out var validation))
        {
            HistoryValidationMessage = validation;
            ClearHistoryResults(validation);
            return;
        }

        HistoryValidationMessage = string.Empty;
        HistoryRangeSummary = FormatHistoryRangeSummary(start, end);
        ClearHistoryResults(IsEnglish ? "Selection changed · apply to view results" : "选择已更改 · 应用后查看结果");
    }

    private void ApplyHistorySelection()
    {
        if (!TryGetHistoryRange(out var start, out var end, out var validation))
        {
            HistoryValidationMessage = validation;
            ClearHistoryResults(validation);
            return;
        }

        HistoryValidationMessage = string.Empty;
        _historySelectionPending = false;
        ApplyHistoryOfficialRange(start, end);
        BeginHistoryRangeQuery(start, end);
    }

    private bool TryGetHistoryRange(out DateOnly start, out DateOnly end, out string validation)
    {
        start = default;
        end = default;
        if (_lastSnapshot is null)
        {
            validation = IsEnglish ? "Waiting for the first refresh" : "等待首次刷新";
            return false;
        }

        var today = DateOnly.FromDateTime(_lastSnapshot.RefreshedAt.DateTime);
        if (IsHistoryMonthMode)
        {
            if (_selectedHistoryYear <= 0 || _selectedHistoryMonth is < 1 or > 12)
            {
                validation = IsEnglish ? "Choose a year and month" : "请选择年份和月份";
                return false;
            }

            start = new DateOnly(_selectedHistoryYear, _selectedHistoryMonth, 1);
            if (start > today)
            {
                validation = IsEnglish ? "The selected month is in the future" : "所选月份晚于当前日期";
                return false;
            }

            end = start.Year == today.Year && start.Month == today.Month
                ? today
                : start.AddMonths(1).AddDays(-1);
            validation = string.Empty;
            return true;
        }

        if (!_historyCustomStartDate.HasValue || !_historyCustomEndDate.HasValue)
        {
            validation = IsEnglish ? "Choose both a start and end date" : "请选择开始日期和结束日期";
            return false;
        }

        start = DateOnly.FromDateTime(_historyCustomStartDate.Value);
        end = DateOnly.FromDateTime(_historyCustomEndDate.Value);
        if (start > end)
        {
            validation = IsEnglish ? "The start date must not be later than the end date" : "开始日期不得晚于结束日期";
            return false;
        }

        if (end > today)
        {
            validation = IsEnglish ? "The end date must not be later than the latest snapshot date" : "结束日期不得晚于最新快照日期";
            return false;
        }

        validation = string.Empty;
        return true;
    }

    private void ApplyHistoryOfficialRange(DateOnly start, DateOnly end)
    {
        var days = GetOfficialDays(start, end.AddDays(1));
        var total = UsageCalculator.SaturatingSum(days.Select(day => day.Tokens));
        HistoryRangeSummary = FormatHistoryRangeSummary(start, end);
        HistoryOfficialTokens = days.Length == 0 ? "—" : FormatDisplayTokens(total);
        HistoryOfficialReturnedDays = days.Length.ToString(CultureInfo.InvariantCulture);
        HistoryOfficialAverageTokens = days.Length == 0
            ? "—"
            : FormatDisplayTokens((long)Math.Round((decimal)total / days.Length, 0, MidpointRounding.AwayFromZero));
        var peakDay = days
            .OrderByDescending(day => day.Tokens)
            .ThenByDescending(day => day.Date)
            .FirstOrDefault();
        HistoryOfficialPeakDate = peakDay is null
            ? "—"
            : peakDay.Date.ToString(IsEnglish ? "MMM d, yyyy" : "yyyy年M月d日", IsEnglish ? EnglishCulture : ChineseCulture);
        HistoryOfficialPeakTokens = peakDay is null ? "—" : FormatDisplayTokens(peakDay.Tokens);
        HistoryOfficialStatus = FormatOfficialUsageStatus(days);
        HasHistoryOfficialDays = days.Length > 0;
        DailyUsage.Clear();
        var peak = Math.Max(days.Select(day => day.Tokens).DefaultIfEmpty(0).Max(), 1);
        foreach (var day in days)
        {
            DailyUsage.Add(new DailyUsageRowViewModel(
                day.Date.ToString(IsEnglish ? "MMM d, yyyy dddd" : "yyyy年M月d日 dddd", IsEnglish ? EnglishCulture : ChineseCulture),
                FormatDisplayTokens(day.Tokens),
                (double)day.Tokens / peak * 100));
        }

        _historyAppliedStart = start;
        _historyAppliedEnd = end;
        HistoryTrend = new HistoryTrendData(start, end, days);
    }

    private string FormatHistoryRangeSummary(DateOnly start, DateOnly end)
    {
        if (IsHistoryMonthMode)
        {
            return start.ToString(IsEnglish ? "MMMM yyyy" : "yyyy年M月", IsEnglish ? EnglishCulture : ChineseCulture);
        }

        return IsEnglish
            ? $"{start.ToString("MMM d, yyyy", EnglishCulture)} – {end.ToString("MMM d, yyyy", EnglishCulture)} (inclusive)"
            : $"{start:yyyy年M月d日} – {end:yyyy年M月d日}（含首尾日期）";
    }

    private void BeginHistoryRangeQuery(DateOnly start, DateOnly end)
    {
        CancelHistoryRangeQuery();
        var version = ++_historyRangeVersion;
        var cancellation = new CancellationTokenSource();
        _historyRangeCancellation = cancellation;
        IsHistoryRangeLoading = true;
        _historyLocalUsage = null;
        HistoryLocalCost = "—";
        HistoryLocalTokens = "—";
        HistoryLocalCacheHit = "—";
        HistoryLocalCostWithoutCaching = "—";
        HistoryLocalCacheSavings = "—";
        HistoryLocalPricingStatus = IsEnglish ? "Querying the matching local calendar range…" : "正在查询相同本地日历区间…";
        _activeHistoryRangeQuery = RunHistoryRangeQueryAsync(start, end, version, cancellation.Token);
    }

    private async Task RunHistoryRangeQueryAsync(
        DateOnly start,
        DateOnly end,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            var usage = await _dashboardService.QueryLocalUsageAsync(start, end, cancellationToken);
            lock (_lifecycleSync)
            {
                if (_shutdownStarted || cancellationToken.IsCancellationRequested || version != _historyRangeVersion)
                {
                    return;
                }

                _historyLocalUsage = usage;
                _historyAppliedStart = start;
                _historyAppliedEnd = end;
                ApplyHistoryLocalUsage(usage);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer range or shutdown owns this cancellation.
        }
        catch (Exception)
        {
            if (version == _historyRangeVersion)
            {
                HistoryLocalCost = "—";
                HistoryLocalTokens = "—";
                HistoryLocalCacheHit = "—";
                HistoryLocalCostWithoutCaching = "—";
                HistoryLocalCacheSavings = "—";
                HistoryLocalPricingStatus = IsEnglish ? "Local range query failed" : "本机区间查询失败";
            }
        }
        finally
        {
            if (version == _historyRangeVersion)
            {
                IsHistoryRangeLoading = false;
            }
        }
    }

    private void ApplyHistoryLocalUsage(UsageAggregation usage)
    {
        if (usage.TotalTokens <= 0)
        {
            HistoryLocalCost = "—";
            HistoryLocalTokens = "—";
            HistoryLocalCacheHit = "—";
            HistoryLocalCostWithoutCaching = "—";
            HistoryLocalCacheSavings = "—";
            HistoryLocalPricingStatus = IsEnglish
                ? "No local rollout samples in this period"
                : "此区间暂无本机 rollout 样本";
            return;
        }

        HistoryLocalCost = usage.EstimatedCostUsd.HasValue ? $"≈ ${usage.EstimatedCostUsd:0.00}" : "—";
        HistoryLocalTokens = FormatDisplayTokens(usage.TotalTokens);
        var inputTokens = UsageCalculator.SaturatingSum(
            [usage.Composition.UncachedInputTokens, usage.Composition.CachedInputTokens]);
        HistoryLocalCacheHit = inputTokens > 0 ? $"{usage.CacheHitPercent:0}%" : "—";
        HistoryLocalCostWithoutCaching = usage.EstimatedCostWithoutCachingUsd.HasValue
            ? $"≈ ${usage.EstimatedCostWithoutCachingUsd:0.00}"
            : "—";
        HistoryLocalCacheSavings = usage.EstimatedCacheSavingsUsd.HasValue
            ? $"≈ ${usage.EstimatedCacheSavingsUsd:0.00}"
            : "—";
        HistoryLocalPricingStatus = FormatPricingStatus(usage, _lastSnapshot?.Pricing);
    }

    private void ClearHistoryResults(string status)
    {
        HistoryTrend = null;
        DailyUsage.Clear();
        HasHistoryOfficialDays = false;
        HistoryOfficialTokens = "—";
        HistoryOfficialReturnedDays = "0";
        HistoryOfficialAverageTokens = "—";
        HistoryOfficialPeakDate = "—";
        HistoryOfficialPeakTokens = "—";
        HistoryOfficialStatus = status;
        _historyLocalUsage = null;
        HistoryLocalCost = "—";
        HistoryLocalTokens = "—";
        HistoryLocalCacheHit = "—";
        HistoryLocalCostWithoutCaching = "—";
        HistoryLocalCacheSavings = "—";
        HistoryLocalPricingStatus = status;
    }

    private void CancelHistoryRangeQuery()
    {
        _historyRangeVersion++;
        _historyRangeCancellation?.Cancel();
        _historyRangeCancellation = null;
        IsHistoryRangeLoading = false;
    }

    private DailyUsagePoint[] GetOfficialDays(DateOnly from, DateOnly to)
        => (_lastSnapshot?.OfficialUsage?.DailyUsage ?? [])
            .Where(item => item.Date >= from && item.Date < to)
            .GroupBy(item => item.Date)
            .Select(group => group.Last())
            .OrderByDescending(item => item.Date)
            .ToArray();

    private string FormatOfficialTotal(IReadOnlyList<DailyUsagePoint> days)
        => days.Count == 0 ? "—" : FormatDisplayTokens(UsageCalculator.SaturatingSum(days.Select(item => item.Tokens)));

    private string FormatOfficialUsageStatus(IReadOnlyList<DailyUsagePoint> days)
    {
        if (days.Count == 0)
        {
            return IsEnglish ? "No official daily data in this period" : "此时段暂无官方日数据";
        }

        var snapshot = _lastSnapshot!;
        var updatedAt = snapshot.Freshness.DailyUsageUpdatedAt ?? snapshot.OfficialUsageUpdatedAt;
        var isStale = snapshot.Freshness.DailyUsageUpdatedAt.HasValue
            ? snapshot.Freshness.IsDailyUsageStale
            : snapshot.IsOfficialUsageStale;
        var coverage = IsEnglish
            ? $"Sum of {days.Count} returned official days · through {days[0].Date.ToString("MMM d", EnglishCulture)}"
            : $"仅合计官方已返回的 {days.Count} 天 · 截至 {days[0].Date:M月d日}";
        var cachedDays = days.Count(day => day.IsCached);
        if (days.Any(day => day.CapturedAt.HasValue))
        {
            updatedAt = days.Max(day => day.CapturedAt);
            isStale = cachedDays > 0;
        }
        if (cachedDays > 0 && cachedDays < days.Count)
        {
            coverage += IsEnglish ? $" · {cachedDays} cached days" : $" · 含 {cachedDays} 天历史缓存";
            var oldestCached = days.Where(day => day.IsCached).Min(day => day.CapturedAt);
            return coverage + (IsEnglish
                ? $" · mixed freshness; latest fetch {updatedAt?.ToLocalTime():M/d HH:mm}; oldest cache {oldestCached?.ToLocalTime():M/d HH:mm}"
                : $" · 混合新鲜度；最近获取 {updatedAt?.ToLocalTime():M/d HH:mm}；最早缓存 {oldestCached?.ToLocalTime():M/d HH:mm}");
        }
        return updatedAt.HasValue
            ? coverage + (IsEnglish
                ? $" · {(isStale ? "cached" : "fetched")} {updatedAt.Value.ToLocalTime():M/d HH:mm}"
                : $" · {(isStale ? "官方缓存" : "获取于")} {updatedAt.Value.ToLocalTime():M/d HH:mm}")
            : coverage;
    }

    private bool IsUsableWindow(RateLimitWindowSnapshot? window, long expectedMinutes)
        => window?.WindowDurationMinutes == expectedMinutes
           && window.UsedPercent.HasValue
           && window.ResetsAt > DateTimeOffset.UtcNow;

    private string FormatReset(RateLimitWindowSnapshot? window)
        => window?.ResetsAt is { } reset
            ? IsEnglish ? $"Resets {reset.ToLocalTime().ToString("MMM d HH:mm", EnglishCulture)}" : $"{reset.ToLocalTime():M月d日 HH:mm} 重置"
            : IsEnglish ? "Reset time unavailable" : "重置时间未返回";

    private string FormatCompactReset(RateLimitWindowSnapshot? window)
        => window?.ResetsAt is { } reset
            ? IsEnglish ? $"Resets {reset.ToLocalTime():M/d HH:mm}" : $"{reset.ToLocalTime():M/d HH:mm} 重置"
            : IsEnglish ? "Reset unavailable" : "重置时间未返回";

    private string FormatDateTime(DateTimeOffset value)
        => value.ToLocalTime().ToString(IsEnglish ? "MMM d HH:mm" : "M月d日 HH:mm", IsEnglish ? EnglishCulture : ChineseCulture);

    private string FormatWarning(string? warning)
    {
        if (!IsEnglish || string.IsNullOrWhiteSpace(warning))
        {
            return warning ?? string.Empty;
        }

        return string.Join("; ", warning.Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(LocalizeWarningPart));
    }

    private static string LocalizeWarningPart(string warning)
    {
        if (warning.Equals("额度不可用", StringComparison.Ordinal))
        {
            return "Quota unavailable";
        }

        if (warning.Equals("整体读取超过截止时间", StringComparison.Ordinal))
        {
            return "Codex app-server read exceeded its deadline";
        }

        var localized = warning
            .Replace("通用 5 小时额度", "General 5-hour quota", StringComparison.Ordinal)
            .Replace("周额度", "Weekly quota", StringComparison.Ordinal)
            .Replace("Spark 额度", "Spark quota", StringComparison.Ordinal)
            .Replace("重置卡", "Reset credits", StringComparison.Ordinal)
            .Replace("官方每日用量", "Official daily usage", StringComparison.Ordinal)
            .Replace("官方汇总", "Official summary", StringComparison.Ordinal)
            .Replace("暂不可用，显示 ", " unavailable; showing last successful value from ", StringComparison.Ordinal)
            .Replace(" 的上次成功值", string.Empty, StringComparison.Ordinal)
            .Replace("Codex app-server 暂时退避，", "Codex app-server is temporarily backed off; retry after ", StringComparison.Ordinal)
            .Replace(" 后重试", string.Empty, StringComparison.Ordinal)
            .Replace("账户：", "Account: ", StringComparison.Ordinal)
            .Replace("额度：", "Quota: ", StringComparison.Ordinal)
            .Replace("趋势：", "Usage trends: ", StringComparison.Ordinal)
            .Replace("Codex app-server：", "Codex app-server: ", StringComparison.Ordinal)
            .Replace("价格：", "Pricing: ", StringComparison.Ordinal)
            .Replace("本地 rollout：", "Local rollout: ", StringComparison.Ordinal);
        return Regex.Replace(localized, @"(?<month>\d{1,2})月(?<day>\d{1,2})日", "${month}/${day}");
    }

    private string StaleSuffix(bool stale, DateTimeOffset? updatedAt)
        => stale && updatedAt.HasValue
            ? IsEnglish
                ? $" (last successful {updatedAt.Value.ToLocalTime():M/d HH:mm})"
                : $"（上次成功 {updatedAt.Value.ToLocalTime():M/d HH:mm}）"
            : string.Empty;

    private static int DaysRemaining(DateTimeOffset expiry)
        => Math.Max(0, (int)Math.Ceiling((expiry.ToLocalTime() - DateTimeOffset.Now).TotalDays));

    private string FormatPricingStatus(UsageAggregation usage, PricingSnapshot? pricing)
    {
        var pricedTokens = usage.Models
            .Where(item => item.EstimatedCostUsd.HasValue);
        var coveredTokens = UsageCalculator.SaturatingSum(pricedTokens.Select(item => item.TotalTokens));
        var coverage = usage.TotalTokens <= 0 ? 0 : (double)coveredTokens / usage.TotalTokens * 100;
        var source = pricing is null
            ? IsEnglish ? "Public API prices" : "公开 API 单价"
            : pricing.IsLive
                ? IsEnglish ? "Official prices fetched at startup" : "本次启动获取的官方价格"
                : IsEnglish ? "Cached or built-in price snapshot" : pricing.StatusMessage ?? "价格快照";
        return IsEnglish ? $"{source} · Prices cover {coverage:0}% of local tokens" : $"{source} · 定价覆盖 {coverage:0}% 本地样本";
    }

    public static string FormatTokens(long value)
        => TokenDisplayFormatter.Format(value);

    private string FormatDisplayTokens(long value)
    {
        if (!IsEnglish)
        {
            return FormatTokens(value);
        }

        return value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
            >= 1_000 => $"{value / 1_000d:0.##}K",
            _ => value.ToString(CultureInfo.InvariantCulture)
        };
    }

    private async Task PersistNotificationsAsync(bool value)
    {
        try
        {
            await _dashboardService.SetNotificationsEnabledAsync(value);
        }
        catch (Exception exception)
        {
            Warning = IsEnglish
                ? $"Failed to save notification setting: {exception.Message}"
                : $"通知设置保存失败：{exception.Message}";
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

        OnPropertyChanged(nameof(IsLightMode));
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(value));
        return true;
    }

    private void SetEnglish(bool value, bool persist)
    {
        lock (_lifecycleSync)
        {
            if (persist && _shutdownStarted)
            {
                return;
            }

            var changed = ApplyEnglish(value);
            if (!changed && !(persist && _languageSettingsReadPending))
            {
                return;
            }

            if (persist)
            {
                _languageSettingsVersion++;
                _languageSettingsWriteTask = PersistLanguageAsync(_languageSettingsWriteTask, value);
            }
        }
    }

    private bool ApplyEnglish(bool value)
    {
        if (!SetProperty(ref _isEnglish, value, nameof(IsEnglish)))
        {
            return false;
        }

        OnPropertyChanged(nameof(IsChinese));
        OnPropertyChanged(nameof(ResetCreditCountText));
        UpdateLocalizedUpdateStatus();
        LanguageChanged?.Invoke(this, new LanguageChangedEventArgs(value));
        StartupStatus = StartupEnabled
            ? IsEnglish ? "Enabled · runs in the background after sign-in" : "已启用 · 登录后在后台运行"
            : IsEnglish ? "Off by default; will not start with Windows" : "默认关闭，登录 Windows 时不会自动运行";
        if (_lastSnapshot is not null)
        {
            Apply(_lastSnapshot);
        }
        else
        {
            SyncText = IsEnglish ? "Waiting to sync" : "等待同步";
            WeeklyRemainingText = IsEnglish ? "Unavailable" : "不可用";
            WeeklyResetText = IsEnglish ? "Not returned by the service" : "服务端未返回";
            PaceText = IsEnglish ? "Waiting for quota data" : "等待额度数据";
            PaceDetailText = IsEnglish ? "Baseline: split evenly across 7 days, about 14.3% daily" : "基准：7 天均分，每天约 14.3%";
            PaceWindowText = IsEnglish ? "Each segment represents 24 hours of the quota window" : "每格代表额度窗口中的 24 小时";
            ResetSummary = IsEnglish ? "Details unavailable" : "详情未返回";
            ResetNearestExpiry = IsEnglish ? "No expiry information" : "暂无到期信息";
            ResetDetailsStatus = IsEnglish ? "Not synchronized yet" : "尚未同步";
            ResetCreditEmptyText = IsEnglish ? "Reset credit details have not synchronized yet" : "尚未同步重置卡明细";
            ModelRangeLabel = _selectedModelRange switch
            {
                "today" => IsEnglish ? "Today" : "今天",
                "30d" => IsEnglish ? "Last 30 days" : "最近 30 天",
                _ => IsEnglish ? "Last 7 days" : "最近 7 天"
            };
            ModelRangeTokensLabel = _selectedModelRange == "today"
                ? IsEnglish ? "Today's tokens" : "今日 Tokens"
                : IsEnglish ? "Official tokens · Selected period" : "官方 Tokens · 所选时段";
            OfficialRangeStatus = IsEnglish ? "Waiting for official usage" : "等待官方用量";
            ResetNotificationStatus = IsEnglish ? "Waiting for validity data" : "等待获取有效期";
            ModelPricingStatus = IsEnglish ? "Waiting for prices" : "等待价格同步";
            HistoryRangeSummary = IsEnglish ? "Waiting for refresh" : "等待刷新";
            HistoryOfficialStatus = IsEnglish ? "Waiting for official usage" : "等待官方用量";
            HistoryLocalPricingStatus = IsEnglish ? "Waiting for local samples" : "等待本机样本";
        }

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

    private async Task InitializeLanguageSettingAsync(CancellationToken cancellationToken)
    {
        long version;
        lock (_lifecycleSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdownStarted)
            {
                return;
            }

            _languageSettingsReadPending = true;
            version = _languageSettingsVersion;
        }

        bool value;
        try
        {
            value = await _dashboardService.GetEnglishEnabledAsync(cancellationToken);
        }
        catch
        {
            lock (_lifecycleSync)
            {
                _languageSettingsReadPending = false;
            }

            throw;
        }

        lock (_lifecycleSync)
        {
            _languageSettingsReadPending = false;
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdownStarted || version != _languageSettingsVersion)
            {
                return;
            }

            ApplyEnglish(value);
        }
    }

    private async Task InitializeUpdateCheckSettingAsync(CancellationToken cancellationToken)
    {
        if (_updateCheckService is null)
        {
            return;
        }

        long version;
        lock (_lifecycleSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdownStarted)
            {
                return;
            }

            _updateSettingsReadPending = true;
            version = _updateSettingsVersion;
        }

        bool value;
        try
        {
            value = await _updateCheckService.GetAutoCheckEnabledAsync(cancellationToken);
        }
        catch
        {
            lock (_lifecycleSync)
            {
                _updateSettingsReadPending = false;
            }

            throw;
        }

        lock (_lifecycleSync)
        {
            _updateSettingsReadPending = false;
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdownStarted || version != _updateSettingsVersion)
            {
                return;
            }

            SetProperty(ref _autoUpdateCheckEnabled, value, nameof(AutoUpdateCheckEnabled));
            _updateUiStatus = value ? UpdateUiStatus.Ready : UpdateUiStatus.Disabled;
            UpdateLocalizedUpdateStatus();
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
            Warning = IsEnglish
                ? $"Failed to save theme setting: {exception.Message}"
                : $"主题设置保存失败：{exception.Message}";
            SyncStatusBrush = MediaBrushes.DarkGoldenrod;
        }
    }

    private async Task PersistLanguageAsync(Task previousWrite, bool value)
    {
        try
        {
            await previousWrite;
            await _dashboardService.SetEnglishEnabledAsync(value);
        }
        catch (Exception exception)
        {
            Warning = IsEnglish
                ? $"Failed to save language setting: {exception.Message}"
                : $"语言设置保存失败：{exception.Message}";
            SyncStatusBrush = MediaBrushes.DarkGoldenrod;
        }
    }

    private async Task PersistUpdateSettingAsync(Task previousWrite, bool value, long version)
    {
        try
        {
            await previousWrite;
            await _updateCheckService!.SetAutoCheckEnabledAsync(value, _updateCancellation.Token);
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
            // Application shutdown owns this cancellation.
        }
        catch
        {
            lock (_lifecycleSync)
            {
                if (!_shutdownStarted && version == _updateSettingsVersion)
                {
                    _updateUiStatus = UpdateUiStatus.Failed;
                    UpdateLocalizedUpdateStatus();
                }
            }
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

        ApplyStartupResult(result, IsEnglish ? "read" : "读取");
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

        ApplyStartupResult(result, IsEnglish ? "save" : "保存");
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
            true when result.Error is null => IsEnglish ? "Enabled · runs in the background after sign-in" : "已启用 · 登录后在后台运行",
            false when result.Error is null => IsEnglish ? "Off by default; will not start with Windows" : "默认关闭，登录 Windows 时不会自动运行",
            _ => IsEnglish
                ? $"Startup status unavailable · keeping the last confirmed {(actual ? "on" : "off")} state"
                : $"开机启动状态不可用 · 保持上次确认的{(actual ? "开启" : "关闭")}状态"
        };
        if (result.Error is not null)
        {
            Warning = IsEnglish
                ? $"Failed to {operation} startup setting: {result.Error}"
                : $"开机自启动设置{operation}失败：{result.Error}";
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

    private static string ReadAppVersion()
        => typeof(MainViewModel).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)
           ?? "0.0.0";

    private static string FormatAppVersion(string version)
    {
        var normalized = version.Trim();
        if (normalized.StartsWith('v'))
        {
            normalized = normalized[1..];
        }

        var metadata = normalized.IndexOf('+');
        if (metadata >= 0)
        {
            normalized = normalized[..metadata];
        }

        return $"v{(string.IsNullOrWhiteSpace(normalized) ? "0.0.0" : normalized)}";
    }
}

internal enum UpdateUiStatus
{
    Ready,
    Disabled,
    Checking,
    Latest,
    Available,
    Failed,
    Snoozed
}

public sealed class ResetExpiryNotificationEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class ThemeChangedEventArgs(bool isDarkMode) : EventArgs
{
    public bool IsDarkMode { get; } = isDarkMode;
}

public sealed class LanguageChangedEventArgs(bool isEnglish) : EventArgs
{
    public bool IsEnglish { get; } = isEnglish;
}
