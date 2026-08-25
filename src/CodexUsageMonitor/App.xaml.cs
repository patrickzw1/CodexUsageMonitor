using System.Drawing;
using System.Windows.Threading;
using CodexUsageMonitor.Core.Services;
using CodexUsageMonitor.ViewModels;
using Forms = System.Windows.Forms;

namespace CodexUsageMonitor;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(15);
    private const string SingletonName = @"Local\CodexUsageMonitor.Singleton.v1";
    private const string ShowRequestName = @"Local\CodexUsageMonitor.Show.v1";
    private Forms.NotifyIcon? _notifyIcon;
    private Icon? _applicationIcon;
    private MainWindow? _window;
    private TrayMenuWindow? _trayMenu;
    private DispatcherTimer? _refreshTimer;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showRequestEvent;
    private RegisteredWaitHandle? _showRequestRegistration;
    private bool _ownsSingleInstance;
    private bool _isShuttingDown;
    private MainViewModel? _viewModel;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var explicitShow = e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase)
                           || e.Args.Any(arg => arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                           || Environment.GetEnvironmentVariable("CODEX_USAGE_MONITOR_SHOW_ON_START") == "1";
        var startHidden = e.Args.Contains("--hidden", StringComparer.OrdinalIgnoreCase) && !explicitShow;
        var showOnStart = !startHidden;
        if (!AcquireSingleInstance(requestShow: showOnStart))
        {
            Shutdown();
            return;
        }

        var startupService = new WindowsStartupRegistrationService(
            new WindowsRunRegistryBackend(),
            () => Environment.ProcessPath);
        var viewModel = new MainViewModel(DashboardService.CreateDefault(), startupService);
        _viewModel = viewModel;
        viewModel.ThemeChanged += (_, args) => ThemeManager.Apply(args.IsDarkMode);
        viewModel.ResetExpiryNotificationRequested += (_, args) =>
            _notifyIcon?.ShowBalloonTip(
                7_000,
                "Codex 重置卡即将到期",
                args.Message,
                Forms.ToolTipIcon.Warning);
        var requestedPage = e.Args
            .FirstOrDefault(arg => arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];
        if (requestedPage?.Equals("reset", StringComparison.OrdinalIgnoreCase) == true)
        {
            viewModel.ShowResetDetailsCommand.Execute(null);
        }
        else if (requestedPage?.Equals("models", StringComparison.OrdinalIgnoreCase) == true)
        {
            viewModel.ShowModelsCommand.Execute(null);
        }
        else if (requestedPage?.Equals("history", StringComparison.OrdinalIgnoreCase) == true)
        {
            viewModel.ShowHistoryCommand.Execute(null);
        }

        viewModel.QuitRequested += (_, _) => ShutdownApplication();
        _window = new MainWindow(viewModel, keepOpen: explicitShow);
        _trayMenu = new TrayMenuWindow(viewModel, ShowWindow, ShutdownApplication);

        _applicationIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon ?? SystemIcons.Application,
            Text = "Codex 用量",
            Visible = true
        };
        _notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                ShowWindow();
            }
            else if (args.Button == Forms.MouseButtons.Right)
            {
                Dispatcher.BeginInvoke((Action)ShowTrayMenu, DispatcherPriority.ApplicationIdle);
            }
        };

        _refreshTimer = new DispatcherTimer { Interval = AutoRefreshInterval };
        _refreshTimer.Tick += async (_, _) => await viewModel.RefreshAutomaticallyAsync();
        _refreshTimer.Start();

        if (showOnStart)
        {
            ShowWindow();
        }

        await viewModel.InitializeAsync();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _refreshTimer?.Stop();
        _viewModel?.CancelPendingOperations();
        if (_trayMenu is not null)
        {
            _trayMenu.AllowClose = true;
            _trayMenu.Close();
        }
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _applicationIcon?.Dispose();
        _showRequestRegistration?.Unregister(null);
        _showRequestEvent?.Dispose();
        if (_ownsSingleInstance)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    private bool AcquireSingleInstance(bool requestShow)
    {
        _showRequestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);
        _singleInstanceMutex = new Mutex(true, SingletonName, out _ownsSingleInstance);
        if (!_ownsSingleInstance)
        {
            if (requestShow)
            {
                _showRequestEvent.Set();
            }

            return false;
        }

        _showRequestRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showRequestEvent,
            (_, _) => Dispatcher.BeginInvoke((Action)ShowWindow),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return true;
    }

    private void ShowWindow()
    {
        if (_isShuttingDown || _window is null)
        {
            return;
        }

        _window.ShowNearTray();
        _window.Activate();
    }

    private void ShowTrayMenu()
    {
        if (_isShuttingDown || _trayMenu is null)
        {
            return;
        }

        if (_trayMenu.IsVisible)
        {
            _trayMenu.Hide();
            return;
        }

        _trayMenu.ShowNearTray();
        _trayMenu.Activate();
    }

    private async void ShutdownApplication()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _refreshTimer?.Stop();
        _window?.Hide();
        _trayMenu?.Hide();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
        }

        if (_viewModel is not null)
        {
            await _viewModel.StopAsync(TimeSpan.FromSeconds(3));
        }

        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }

        Shutdown();
    }
}
