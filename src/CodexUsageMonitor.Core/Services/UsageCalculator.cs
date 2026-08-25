using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.Core.Services;

public static class UsageCalculator
{
    public const int MaxModelNameChars = 256;

    public static UsageAggregation Aggregate(
        IEnumerable<TokenUsageEvent> events,
        IReadOnlyDictionary<string, TokenPrice> prices)
    {
        var rows = events
            .GroupBy(item => NormalizeModel(item.Model), StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateModelSummary(group.Key, group, prices))
            .OrderByDescending(item => item.TotalTokens)
            .ToArray();

        var input = SaturatingSum(rows.Select(item => item.InputTokens));
        var cached = Math.Min(SaturatingSum(rows.Select(item => item.CachedInputTokens)), input);
        var output = SaturatingSum(rows.Select(item => item.OutputTokens));
        var reasoning = Math.Min(SaturatingSum(rows.Select(item => item.ReasoningTokens)), output);
        var composition = new TokenComposition(
            Math.Max(input - cached, 0),
            cached,
            Math.Max(output - reasoning, 0),
            reasoning);

        var pricedRows = rows.Where(item => item.EstimatedCostUsd.HasValue).ToArray();
        decimal? cost = pricedRows.Length == 0 ? null : pricedRows.Sum(item => item.EstimatedCostUsd!.Value);
        var savings = CalculateSavings(rows, prices);

        return new UsageAggregation(
            rows,
            composition,
            SaturatingSum(rows.Select(item => item.TotalTokens)),
            cost,
            savings,
            input <= 0 ? 0 : (double)cached / input * 100);
    }

    public static WeeklyPace CalculateWeeklyPace(RateLimitWindowSnapshot? weekly, DateTimeOffset now)
    {
        if (weekly?.ResetsAt is null
            || weekly.WindowDurationMinutes is null
            || weekly.WindowDurationMinutes <= 0
            || weekly.WindowDurationMinutes > 60L * 24 * 31
            || weekly.UsedPercent is null)
        {
            return WeeklyPace.Unavailable;
        }

        DateTimeOffset start;
        try
        {
            start = weekly.ResetsAt.Value.AddMinutes(-weekly.WindowDurationMinutes.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return WeeklyPace.Unavailable;
        }
        var duration = weekly.ResetsAt.Value - start;
        var elapsed = now - start;
        if (elapsed <= TimeSpan.Zero || duration <= TimeSpan.Zero)
        {
            return WeeklyPace.Unavailable;
        }

        var elapsedPercent = Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds * 100, 0.1, 100);
        var projected = Math.Clamp(weekly.UsedPercent.Value / (elapsedPercent / 100), 0, 999);
        return new WeeklyPace(projected, projected >= 100, elapsedPercent);
    }

    private static ModelUsageSummary CreateModelSummary(
        string model,
        IEnumerable<TokenUsageEvent> events,
        IReadOnlyDictionary<string, TokenPrice> prices)
    {
        var items = events.ToArray();
        var input = SaturatingSum(items.Select(item => item.InputTokens));
        var cached = Math.Min(SaturatingSum(items.Select(item => item.CachedInputTokens)), input);
        var output = SaturatingSum(items.Select(item => item.OutputTokens));
        var reasoning = Math.Min(SaturatingSum(items.Select(item => item.ReasoningTokens)), output);
        var total = SaturatingSum(items.Select(item => item.TotalTokens));

        decimal? cost = null;
        if (TryGetPrice(model, prices, out var price))
        {
            var uncached = Math.Max(input - cached, 0);
            cost = (uncached * price.InputPerMillion
                    + cached * price.CachedInputPerMillion
                    + output * price.OutputPerMillion) / 1_000_000m;
        }

        return new ModelUsageSummary(model, input, cached, output, reasoning, total, cost);
    }

    private static decimal? CalculateSavings(
        IEnumerable<ModelUsageSummary> rows,
        IReadOnlyDictionary<string, TokenPrice> prices)
    {
        decimal total = 0;
        var found = false;
        foreach (var row in rows)
        {
            if (!TryGetPrice(row.Model, prices, out var price))
            {
                continue;
            }

            found = true;
            total += row.CachedInputTokens * (price.InputPerMillion - price.CachedInputPerMillion) / 1_000_000m;
        }

        return found ? Math.Max(total, 0) : null;
    }

    private static bool TryGetPrice(
        string model,
        IReadOnlyDictionary<string, TokenPrice> prices,
        out TokenPrice price)
    {
        if (prices.TryGetValue(model, out price!))
        {
            return true;
        }

        var normalized = NormalizeModel(model);
        return prices.TryGetValue(normalized, out price!);
    }

    public static string NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "unknown";
        }

        var trimmed = model.Trim();
        return (trimmed.Length <= MaxModelNameChars ? trimmed : trimmed[..MaxModelNameChars]).ToLowerInvariant();
    }

    public static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (value <= 0)
            {
                continue;
            }

            if (long.MaxValue - total < value)
            {
                return long.MaxValue;
            }

            total += value;
        }

        return total;
    }
}
