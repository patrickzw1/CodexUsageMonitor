using System.Globalization;

namespace CodexUsageMonitor.Core.Services;

public static class TokenDisplayFormatter
{
    public static string Format(long value)
    {
        if (value >= 100_000_000) return $"{value / 100_000_000d:0.##}亿";
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.0}M";
        if (value >= 1_000) return $"{value / 1_000d:0.0}K";
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
