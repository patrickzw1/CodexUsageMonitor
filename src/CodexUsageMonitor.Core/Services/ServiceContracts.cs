using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.Core.Services;

public interface IRolloutUsageScanner
{
    Task<int> RefreshAsync(CancellationToken cancellationToken = default);
}

public interface ICodexAppServerClient
{
    Task<AppServerSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface ICodexExecutableLocator
{
    string? Locate();
}

public interface IAppServerProcess : IDisposable
{
    TextWriter StandardInput { get; }
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    bool HasExited { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IAppServerProcessFactory
{
    IAppServerProcess Start(string executable);
}

public interface IPriceCatalogService
{
    Task<PricingSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}

public interface IDashboardService
{
    string DatabasePath { get; }

    Task<DashboardSnapshot> RefreshAsync(
        bool forceAppServer = false,
        CancellationToken cancellationToken = default);

    Task<bool> GetNotificationsEnabledAsync(CancellationToken cancellationToken = default);
    Task SetNotificationsEnabledAsync(bool value, CancellationToken cancellationToken = default);
    Task<bool> GetDarkModeEnabledAsync(CancellationToken cancellationToken = default);
    Task SetDarkModeEnabledAsync(bool value, CancellationToken cancellationToken = default);
}
