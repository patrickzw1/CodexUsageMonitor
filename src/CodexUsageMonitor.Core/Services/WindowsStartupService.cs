using Microsoft.Win32;
using System.Runtime.Versioning;

namespace CodexUsageMonitor.Core.Services;

public sealed record StartupRegistrationResult(bool? IsEnabled, string? Error = null);

public interface IStartupRegistrationService
{
    StartupRegistrationResult Reconcile();
    StartupRegistrationResult SetEnabled(bool enabled);
}

public interface IRunRegistryBackend
{
    string? Read(string valueName);
    void Write(string valueName, string command);
    void Delete(string valueName);
}

public sealed class WindowsStartupRegistrationService(
    IRunRegistryBackend registry,
    Func<string?> processPath) : IStartupRegistrationService
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "CodexUsageMonitor";

    public StartupRegistrationResult Reconcile()
    {
        string? current;
        try
        {
            current = registry.Read(ValueName);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return new StartupRegistrationResult(null, DescribeFailure(exception));
        }

        if (current is null)
        {
            return new StartupRegistrationResult(false);
        }

        if (!TryCreateCommand(processPath(), out var expected, out var error))
        {
            return new StartupRegistrationResult(true, error);
        }

        if (string.Equals(current, expected, StringComparison.Ordinal))
        {
            return new StartupRegistrationResult(true);
        }

        try
        {
            registry.Write(ValueName, expected);
            return new StartupRegistrationResult(true);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return new StartupRegistrationResult(true, DescribeFailure(exception));
        }
    }

    public StartupRegistrationResult SetEnabled(bool enabled)
    {
        string? current;
        try
        {
            current = registry.Read(ValueName);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return new StartupRegistrationResult(null, DescribeFailure(exception));
        }

        try
        {
            if (enabled)
            {
                if (!TryCreateCommand(processPath(), out var command, out var error))
                {
                    return new StartupRegistrationResult(current is not null, error);
                }

                registry.Write(ValueName, command);
                return new StartupRegistrationResult(true);
            }

            registry.Delete(ValueName);
            return new StartupRegistrationResult(false);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return new StartupRegistrationResult(current is not null, DescribeFailure(exception));
        }
    }

    internal static bool TryCreateCommand(string? executablePath, out string command, out string? error)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains('"')
            || executablePath.Length < 3
            || !char.IsAsciiLetter(executablePath[0])
            || executablePath[1] != ':'
            || executablePath[2] is not ('\\' or '/'))
        {
            command = string.Empty;
            error = "无法确定安全的程序绝对路径，请从有效的 EXE 位置重新启动后再试。";
            return false;
        }

        command = $"\"{Path.GetFullPath(executablePath)}\" --hidden";
        error = null;
        return true;
    }

    private static bool IsRegistryFailure(Exception exception)
        => exception is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or PlatformNotSupportedException;

    private static string DescribeFailure(Exception exception)
        => exception is UnauthorizedAccessException or System.Security.SecurityException
            ? "Windows 拒绝修改当前用户的开机启动设置。"
            : "Windows 开机启动设置暂时不可用。";
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRunRegistryBackend : IRunRegistryBackend
{
    public string? Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsStartupRegistrationService.RunKeyPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string valueName, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(WindowsStartupRegistrationService.RunKeyPath, writable: true)
                        ?? throw new IOException("Unable to open the current-user Run key.");
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsStartupRegistrationService.RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
