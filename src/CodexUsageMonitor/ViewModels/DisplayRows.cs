using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.ViewModels;

public sealed record ModelUsageRowViewModel(
    string Model,
    string Tokens,
    string Cache,
    string Cost,
    double SharePercent,
    string ShareText,
    string Color);

public sealed record ResetCreditRowViewModel(
    string Count,
    string Expires,
    string Remaining,
    string Granted,
    string Source);

public sealed record ResetHistoryRowViewModel(
    string Icon,
    string Date,
    string Description,
    string Badge,
    bool IsInferred);

public sealed record DailyUsageRowViewModel(
    string Date,
    string Tokens,
    double RelativeWidth);

public sealed record HistoryTrendData(DateOnly Start, DateOnly End, IReadOnlyList<DailyUsagePoint> Days);
