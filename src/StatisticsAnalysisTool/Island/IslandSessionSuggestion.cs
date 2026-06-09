using StatisticsAnalysisTool.Enumerations;
using System.Collections.Generic;

namespace StatisticsAnalysisTool.Island;

public record IslandSessionSuggestion(
    string IslandName,
    string Owner,
    string WorldMapDataType,
    bool HasPremium,
    Dictionary<PlotType, int> DetectedPlotCounts,
    string City = "",
    int Tier = 1,
    IslandType IslandType = IslandType.Player,
    string SourceClusterIndex = ""
);
