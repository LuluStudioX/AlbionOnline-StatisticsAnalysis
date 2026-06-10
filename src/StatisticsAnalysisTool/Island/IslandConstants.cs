namespace StatisticsAnalysisTool.Island;

internal static class IslandConstants
{
    public const double LaborerBaseCycleHours = 22.0;
    public const double LaborerExtendedCycleHours = 52.0;

    // How long collected loot stays claimable after a laborer returns before it expires (Albion island
    // laborer reward window). Loot Ready persists until this lapses, then the laborer reverts to idle/home.
    public const double LaborerLootExpiryHours = 168.0; // 7 days
    public const int IslandMinTier = 1;
    public const int IslandMaxTier = 6;
}
