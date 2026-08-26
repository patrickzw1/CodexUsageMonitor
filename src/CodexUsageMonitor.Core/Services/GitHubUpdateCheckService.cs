using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.Core.Services;

public sealed class GitHubUpdateCheckService : IUpdateCheckService
{
    internal static readonly Uri LatestApiUri = new("https://api.github.com/repos/patrickzw1/CodexUsageMonitor/releases/latest");
    internal static readonly Uri ReleasesFallbackUri = new("https://github.com/patrickzw1/CodexUsageMonitor/releases/latest");
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private const int MaxResponseBytes = 256 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IUpdateCheckSettingsStore _settings;
    private readonly Func<string> _currentVersion;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _timeout;
    private readonly object _checkSync = new();
    private Task<UpdateCheckResult>? _activeCheck;

    public GitHubUpdateCheckService(
        HttpClient httpClient,
        IUpdateCheckSettingsStore settings,
        Func<string> currentVersion,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? timeout = null)
    {
        _httpClient = httpClient;
        _settings = settings;
        _currentVersion = currentVersion;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public static GitHubUpdateCheckService CreateDefault(IUpdateCheckSettingsStore settings)
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return new GitHubUpdateCheckService(client, settings, ReadCurrentVersion);
    }

    public Task<bool> GetAutoCheckEnabledAsync(CancellationToken cancellationToken = default)
        => _settings.GetAutoUpdateCheckEnabledAsync(cancellationToken);

    public Task SetAutoCheckEnabledAsync(bool value, CancellationToken cancellationToken = default)
        => _settings.SetAutoUpdateCheckEnabledAsync(value, cancellationToken);

    public Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_checkSync)
        {
            if (_activeCheck is { IsCompleted: false })
            {
                return _activeCheck.WaitAsync(cancellationToken);
            }

            _activeCheck = CheckAndClearAsync(force, cancellationToken);
            return _activeCheck;
        }
    }

    public async Task SnoozeAsync(string version, CancellationToken cancellationToken = default)
    {
        var cache = await _settings.GetUpdateCheckCacheAsync(cancellationToken);
        await _settings.SetUpdateCheckCacheAsync(
            cache with
            {
                SnoozedVersion = version,
                SnoozedUntil = _utcNow().AddHours(24)
            },
            cancellationToken);
    }

    private async Task<UpdateCheckResult> CheckAndClearAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            return await CheckCoreAsync(force, cancellationToken);
        }
        finally
        {
            lock (_checkSync)
            {
                _activeCheck = null;
            }
        }
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(bool force, CancellationToken cancellationToken)
    {
        var now = _utcNow();
        var cache = await _settings.GetUpdateCheckCacheAsync(cancellationToken);
        if (!force
            && cache.LastCheckedAt is { } lastChecked
            && now - lastChecked < AutomaticCheckInterval)
        {
            return Evaluate(cache, force, networkRequested: false, skippedWhenCurrent: true);
        }

        if (!SemanticVersion.TryParse(_currentVersion(), out _))
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, null, null, false, "invalid-current-version");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApiUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodexUsageMonitor", ProductVersion()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (EntityTagHeaderValue.TryParse(cache.ETag, out var etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var refreshed = cache with { LastCheckedAt = now };
                await _settings.SetUpdateCheckCacheAsync(refreshed, cancellationToken);
                return Evaluate(refreshed, force, networkRequested: true, skippedWhenCurrent: false);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return await SaveFailureAsync(cache, now, $"http-{(int)response.StatusCode}", cancellationToken);
            }

            using var payload = await ReadBoundedJsonAsync(response.Content, timeout.Token);
            if (!TryParseRelease(payload, out var latestVersion, out var releaseUrl))
            {
                return await SaveFailureAsync(cache, now, "invalid-release-response", cancellationToken);
            }

            var updated = cache with
            {
                LastCheckedAt = now,
                ETag = response.Headers.ETag?.ToString(),
                LatestVersion = latestVersion,
                LatestReleaseUrl = releaseUrl.AbsoluteUri
            };
            await _settings.SetUpdateCheckCacheAsync(updated, cancellationToken);
            return Evaluate(updated, force, networkRequested: true, skippedWhenCurrent: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await SaveFailureAsync(cache, now, "timeout", cancellationToken);
        }
        catch (HttpRequestException)
        {
            return await SaveFailureAsync(cache, now, "network", cancellationToken);
        }
        catch (JsonException)
        {
            return await SaveFailureAsync(cache, now, "invalid-json", cancellationToken);
        }
        catch (InvalidDataException)
        {
            return await SaveFailureAsync(cache, now, "response-too-large", cancellationToken);
        }
    }

    private UpdateCheckResult Evaluate(
        UpdateCheckCache cache,
        bool force,
        bool networkRequested,
        bool skippedWhenCurrent)
    {
        if (!SemanticVersion.TryParse(_currentVersion(), out var current)
            || !SemanticVersion.TryParse(cache.LatestVersion, out var latest))
        {
            return new UpdateCheckResult(
                skippedWhenCurrent ? UpdateCheckOutcome.Skipped : UpdateCheckOutcome.NoUpdate,
                cache.LatestVersion,
                null,
                networkRequested);
        }

        var releaseUrl = ValidateReleaseUrl(cache.LatestReleaseUrl);
        var snoozed = !force
                      && cache.SnoozedVersion == latest.DisplayVersion
                      && cache.SnoozedUntil > _utcNow();
        if (latest.CompareTo(current) > 0 && !snoozed)
        {
            return new UpdateCheckResult(
                UpdateCheckOutcome.UpdateAvailable,
                latest.DisplayVersion,
                releaseUrl,
                networkRequested);
        }

        return new UpdateCheckResult(
            skippedWhenCurrent ? UpdateCheckOutcome.Skipped : UpdateCheckOutcome.NoUpdate,
            latest.DisplayVersion,
            releaseUrl,
            networkRequested);
    }

    private async Task<UpdateCheckResult> SaveFailureAsync(
        UpdateCheckCache cache,
        DateTimeOffset checkedAt,
        string error,
        CancellationToken cancellationToken)
    {
        await _settings.SetUpdateCheckCacheAsync(cache with { LastCheckedAt = checkedAt }, cancellationToken);
        return new UpdateCheckResult(UpdateCheckOutcome.Failed, cache.LatestVersion, null, true, error);
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("GitHub release response is too large.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("GitHub release response is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }

    private static bool TryParseRelease(
        JsonDocument document,
        out string latestVersion,
        out Uri releaseUrl)
    {
        latestVersion = string.Empty;
        releaseUrl = ReleasesFallbackUri;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tag)
            || tag.ValueKind != JsonValueKind.String
            || !SemanticVersion.TryParse(tag.GetString(), out var parsed)
            || parsed.Prerelease is not null
            || !root.TryGetProperty("html_url", out var url)
            || url.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        latestVersion = parsed.DisplayVersion;
        releaseUrl = ValidateReleaseUrl(url.GetString());
        return true;
    }

    internal static Uri ValidateReleaseUrl(string? candidate)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsolutePath.StartsWith(
                "/patrickzw1/CodexUsageMonitor/releases/",
                StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return ReleasesFallbackUri;
    }

    private string ProductVersion()
        => SemanticVersion.TryParse(_currentVersion(), out var current)
            ? $"{current.Major}.{current.Minor}.{current.Patch}"
            : "0.0.0";

    private static string ReadCurrentVersion()
        => Assembly.GetEntryAssembly()?
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? "0.0.0";
}

internal sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"\A[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant);

    private SemanticVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string DisplayVersion => $"{Major}.{Minor}.{Patch}{(Prerelease is null ? string.Empty : $"-{Prerelease}")}";

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = Pattern.Match(value);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch))
        {
            return false;
        }

        if (match.Groups[4].Success
            && match.Groups[4].Value.Split('.')
                .Any(identifier => identifier.Length > 1
                                   && identifier[0] == '0'
                                   && identifier.All(char.IsDigit)))
        {
            return false;
        }

        version = new SemanticVersion(
            major,
            minor,
            patch,
            match.Groups[4].Success ? match.Groups[4].Value : null);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var numeric = Major.CompareTo(other.Major);
        if (numeric == 0) numeric = Minor.CompareTo(other.Minor);
        if (numeric == 0) numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0) return numeric;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var comparison = CompareIdentifier(left[index], right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(left, out var leftValue);
        var rightNumeric = int.TryParse(right, out var rightValue);
        if (leftNumeric && rightNumeric) return leftValue.CompareTo(rightValue);
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.Compare(left, right, StringComparison.Ordinal);
    }
}
