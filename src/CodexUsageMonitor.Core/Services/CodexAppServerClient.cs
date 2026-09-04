using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.Core.Services;

public sealed class CodexAppServerClient : ICodexAppServerClient
{
    public const int MaxStdoutFrameChars = 1024 * 1024;
    public const int MaxStderrChars = 64 * 1024;
    private const int MaxResetDetailsAttempts = 3;
    private static readonly TimeSpan ResetDetailsRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DefaultSnapshotTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StderrWaitTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex SensitiveValuePattern = new(
        @"sk-(?:(?:proj|svcacct)-)?[A-Za-z0-9_-]{20,}|Authorization\s*:\s*Bearer\s+\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PrivatePathPattern = new(
        @"(?:[A-Za-z]:\\Users\\|\\\\)[^\r\n\""']{1,512}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ICodexExecutableLocator _locator;
    private readonly IAppServerProcessFactory _processFactory;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _snapshotTimeout;

    public CodexAppServerClient(ICodexExecutableLocator locator)
        : this(locator, new SystemAppServerProcessFactory(), DefaultRequestTimeout, DefaultSnapshotTimeout)
    {
    }

    public CodexAppServerClient(
        ICodexExecutableLocator locator,
        IAppServerProcessFactory processFactory,
        TimeSpan requestTimeout,
        TimeSpan snapshotTimeout)
    {
        _locator = locator;
        _processFactory = processFactory;
        _requestTimeout = requestTimeout;
        _snapshotTimeout = snapshotTimeout;
    }

    public async Task<AppServerSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAt = DateTimeOffset.UtcNow;
        var executable = _locator.Locate();
        if (executable is null)
        {
            return new AppServerSnapshot(null, null, null, capturedAt, "未找到可信且可启动的 codex.exe")
            {
                ProcessFailure = true
            };
        }

        IAppServerProcess? process = null;
        Task<string>? stderrTask = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_snapshotTimeout);
        using var stderrCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var errors = new List<string>();
        AccountSnapshot? account = null;
        QuotaSnapshot? quota = null;
        OfficialUsageSnapshot? usage = null;
        var accountComplete = false;
        var generalFiveHourQuotaComplete = false;
        var generalWeeklyQuotaComplete = false;
        var sparkQuotaComplete = false;
        var resetCountComplete = false;
        var resetDetailsComplete = false;
        var usageSummaryComplete = false;
        var hasDailyUsageResponse = false;
        var dailyUsageComplete = false;
        var successfulMethodCount = 0;
        var initialized = false;

        try
        {
            process = _processFactory.Start(executable);
            stderrTask = ReadBoundedTextAsync(process.StandardError, MaxStderrChars, stderrCancellation.Token);
            var stdout = new BoundedTextLineReader(process.StandardOutput, MaxStdoutFrameChars);

            await WriteAsync(process, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "codex-usage-monitor", title = "Codex Usage Monitor", version = ClientVersion },
                    capabilities = (object?)null
                }
            }, deadline.Token);
            await ReadResultAsync(process, stdout, 1, deadline.Token);
            await WriteAsync(process, new { method = "initialized", @params = (object?)null }, deadline.Token);
            initialized = true;

            try
            {
                await WriteAsync(process, new { id = 2, method = "account/read", @params = new { refreshToken = false } }, deadline.Token);
                account = AppServerResponseParser.ParseAccount(await ReadResultAsync(process, stdout, 2, deadline.Token));
                accountComplete = account is not null;
                successfulMethodCount++;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add($"账户：{SanitizeError(exception.Message)}");
            }

            try
            {
                await WriteAsync(process, new { id = 3, method = "account/rateLimits/read" }, deadline.Token);
                var quotaResult = AppServerResponseParser.ParseQuotaDetailed(
                    await ReadResultAsync(process, stdout, 3, deadline.Token));
                quota = quotaResult.Snapshot;
                generalFiveHourQuotaComplete = quotaResult.GeneralFiveHourComplete;
                generalWeeklyQuotaComplete = quotaResult.GeneralWeeklyComplete;
                sparkQuotaComplete = quotaResult.SparkComplete;
                resetCountComplete = quotaResult.ResetCreditCountComplete;
                resetDetailsComplete = quotaResult.ResetCreditDetailsComplete;
                successfulMethodCount++;
                for (var attempt = 1;
                     attempt < MaxResetDetailsAttempts && NeedsResetDetailsRetry(quota);
                     attempt++)
                {
                    await Task.Delay(ResetDetailsRetryDelay, deadline.Token);
                    var requestId = 30 + attempt;
                    await WriteAsync(process, new { id = requestId, method = "account/rateLimits/read" }, deadline.Token);
                    var retryResult = AppServerResponseParser.ParseQuotaDetailed(
                        await ReadResultAsync(process, stdout, requestId, deadline.Token));
                    successfulMethodCount++;
                    var retry = retryResult.Snapshot;
                    if (retryResult.ResetCreditDetailsComplete && retry is not null)
                    {
                        quota = quota! with
                        {
                            ResetCreditCount = retry.ResetCreditCount ?? quota!.ResetCreditCount,
                            ResetCreditDetailsComplete = true,
                            ResetCredits = retry.ResetCredits
                        };
                        resetCountComplete = retryResult.ResetCreditCountComplete;
                        resetDetailsComplete = true;
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add($"额度：{SanitizeError(exception.Message)}");
            }

            try
            {
                await WriteAsync(process, new { id = 4, method = "account/usage/read" }, deadline.Token);
                var usageResult = AppServerResponseParser.ParseOfficialUsageDetailed(
                    await ReadResultAsync(process, stdout, 4, deadline.Token));
                usage = usageResult.Snapshot;
                usageSummaryComplete = usageResult.SummaryComplete;
                hasDailyUsageResponse = usageResult.DailyUsageRecognized;
                dailyUsageComplete = usageResult.DailyUsageComplete;
                successfulMethodCount++;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add($"趋势：{SanitizeError(exception.Message)}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            errors.Add("整体读取超过截止时间");
        }
        catch (Exception exception)
        {
            errors.Add($"Codex app-server：{SanitizeError(exception.Message)}");
        }
        finally
        {
            if (process is not null)
            {
                TryStop(process);
                using var exitTimeout = new CancellationTokenSource(ExitWaitTimeout);
                try
                {
                    await process.WaitForExitAsync(exitTimeout.Token);
                }
                catch
                {
                    // 终止后的短等待不能拖住应用退出。
                }

                stderrCancellation.Cancel();
                if (stderrTask is not null)
                {
                    try
                    {
                        _ = await stderrTask.WaitAsync(StderrWaitTimeout);
                    }
                    catch
                    {
                        // stderr 读取超时后放弃，缓冲区始终有大小上限。
                    }
                }

                process.Dispose();
            }
        }

        return new AppServerSnapshot(
            account,
            quota,
            usage,
            capturedAt,
            errors.Count == 0 ? null : string.Join("；", errors))
        {
            Completeness = new AppServerCompleteness(
                accountComplete,
                generalFiveHourQuotaComplete,
                generalWeeklyQuotaComplete,
                sparkQuotaComplete,
                resetCountComplete,
                resetDetailsComplete,
                usageSummaryComplete,
                dailyUsageComplete),
            HasDailyUsageResponse = hasDailyUsageResponse,
            SuccessfulMethodCount = successfulMethodCount,
            ProcessFailure = !initialized
        };
    }

    private static bool NeedsResetDetailsRetry(QuotaSnapshot? quota)
        => quota?.ResetCreditCount is > 0 && !quota.ResetCreditDetailsComplete;

    private async Task<JsonElement> ReadResultAsync(
        IAppServerProcess process,
        BoundedTextLineReader reader,
        int expectedId,
        CancellationToken operationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        request.CancelAfter(_requestTimeout);
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(request.Token);
                }
                catch (InvalidDataException)
                {
                    TryStop(process);
                    throw;
                }

                if (line is null)
                {
                    throw new InvalidOperationException("app-server 已退出");
                }

                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var idValue) || idValue != expectedId)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var text) ? text.GetString() : "app-server 请求失败";
                    throw new InvalidOperationException(message ?? "app-server 请求失败");
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw new InvalidOperationException("app-server 响应缺少 result");
                }

                return result.Clone();
            }
        }
        catch (OperationCanceledException) when (!operationToken.IsCancellationRequested)
        {
            throw new TimeoutException("app-server 单项请求超时");
        }
    }

    private static async Task WriteAsync(IAppServerProcess process, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<string> ReadBoundedTextAsync(TextReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new StringBuilder(Math.Min(maxChars, 4096));
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return result.ToString();
            }

            var remaining = maxChars - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
    }

    private static void TryStop(IAppServerProcess process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // 进程已经退出时无需处理。
        }
    }

    private static string SanitizeError(string message)
    {
        var sanitized = message.Replace('\r', ' ').Replace('\n', ' ');
        sanitized = SensitiveValuePattern.Replace(sanitized, "[credential]");
        sanitized = PrivatePathPattern.Replace(sanitized, "[path]");
        return sanitized.Length <= 512 ? sanitized : sanitized[..512] + "…";
    }

    internal static string ClientVersion
        => typeof(CodexAppServerClient).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion?
               .Split('+', 2)[0]
           ?? "0.1.0";

    private sealed class BoundedTextLineReader(TextReader reader, int maxChars)
    {
        private readonly char[] _buffer = new char[4096];
        private readonly StringBuilder _line = new(Math.Min(maxChars, 4096));
        private int _position;
        private int _length;

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            _line.Clear();
            while (true)
            {
                if (_position >= _length)
                {
                    _length = await reader.ReadAsync(_buffer, cancellationToken);
                    _position = 0;
                    if (_length == 0)
                    {
                        return _line.Length == 0 ? null : _line.ToString();
                    }
                }

                var value = _buffer[_position++];
                if (value == '\n')
                {
                    if (_line.Length > 0 && _line[^1] == '\r')
                    {
                        _line.Length--;
                    }

                    return _line.ToString();
                }

                if (_line.Length >= maxChars)
                {
                    throw new InvalidDataException($"app-server stdout JSON 帧超过 {maxChars} 字符上限");
                }

                _line.Append(value);
            }
        }
    }
}
