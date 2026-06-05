using System;

namespace StatisticsAnalysisTool.Island;

public record IslandYieldHistoryPoint(DateTime Date, double CollectedValue, double ConsumedValue)
{
    public double NetProfit => CollectedValue - ConsumedValue;
    public double ROI => ConsumedValue > 0 ? (CollectedValue / ConsumedValue - 1) * 100 : 0;
}
