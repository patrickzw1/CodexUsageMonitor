namespace CodexUsageMonitor.Core.Models;

public sealed record UpdateCheckCache(
    DateTimeOffset? LastCheckedAt,
    string? ETag,
    string? LatestVersion,
    string? LatestReleaseUrl,
    string? SnoozedVersion,
    DateTimeOffset? SnoozedUntil)
{
    public static UpdateCheckCache Empty { get; } = new(null, null, null, null, null, null);
}

public enum UpdateCheckOutcome
{
    UpdateAvailable,
    NoUpdate,
    Skipped,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string? LatestVersion,
    Uri? ReleaseUrl,
    bool NetworkRequested,
    string? Error = null)
{
    public bool IsUpdateAvailable => Outcome == UpdateCheckOutcome.UpdateAvailable;
}
