namespace CodexUsageMonitor.Core.Services;

public static class PlanCapabilities
{
    private static readonly HashSet<string> SparkEligiblePlans = new(StringComparer.OrdinalIgnoreCase)
    {
        "pro",
        "prolite",
        "team",
        "self_serve_business_prolite",
        "self_serve_business_usage_based",
        "business",
        "ent26",
        "enterprise_cbp_automation",
        "enterprise_cbp_usage_based",
        "enterprise",
        "edu_pro"
    };

    public static bool ShouldShowSpark(string? planType, bool hasSparkBucket)
        => hasSparkBucket
           && planType is not null
           && SparkEligiblePlans.Contains(planType);

    public static string DisplayName(string? planType) => planType?.ToLowerInvariant() switch
    {
        "plus" => "Plus",
        "pro" or "prolite" => "Pro",
        "team" => "Team",
        "business" or "self_serve_business_prolite" or "self_serve_business_usage_based" => "Business",
        "enterprise" or "ent26" or "enterprise_cbp_automation" or "enterprise_cbp_usage_based" => "Enterprise",
        "edu" or "edu_plus" or "edu_pro" => "Edu",
        "free" => "Free",
        "go" => "Go",
        _ => "未知套餐"
    };
}
