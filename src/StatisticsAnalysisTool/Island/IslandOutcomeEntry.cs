using System;

namespace StatisticsAnalysisTool.Island;

public class IslandOutcomeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IslandName { get; set; } = string.Empty;
    public int StartAmount { get; set; }
    public int EndAmount { get; set; }
    public int ConsumedAmount => StartAmount - EndAmount;
    public int CollectedQuantity { get; set; }
    public string CollectedItemName { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
