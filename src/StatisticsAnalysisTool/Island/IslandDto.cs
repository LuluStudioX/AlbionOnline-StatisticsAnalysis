using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StatisticsAnalysisTool.Island;

public record IslandDto(
    string Id,
    string Name,
    string Owner,
    int Tier,
    string City,
    string Biome,
    bool HasPremium,
    string IslandType,
    DateTime? LastPlantedAt,
    DateTime CreatedDate,
    DateTime LastModifiedDate,
    decimal? ManagementPayOverride,
    int? VisitDurationMinutes,
    List<IslandPlotDto> Plots,
    string LayoutId = "",
    Dictionary<int, string> SlotLabels = null,
    DateTime? LastVisited = null,
    int TotalLaborersSent = 0,
    int TotalLootCollected = 0,
    string WorldMapDataType = "",
    string SourceClusterIndex = "",
    DateTime? LastHandledAt = null,
    List<IslandYieldEntryDto> YieldHistory = null,
    List<IslandConsumedEntryDto> ConsumedHistory = null,
    bool? MixedRegionAltActive = null
);

public record IslandYieldEntryDto(
    [property: JsonPropertyName("I")] int ItemIndex,
    [property: JsonPropertyName("Q")] int Quantity,
    [property: JsonPropertyName("At")] DateTime CollectedAt,
    [property: JsonPropertyName("Src")] string SourcePlot
);

public record IslandConsumedEntryDto(
    [property: JsonPropertyName("I")] int ItemIndex,
    [property: JsonPropertyName("Q")] int Quantity,
    [property: JsonPropertyName("At")] DateTime ConsumedAt,
    [property: JsonPropertyName("Src")] string SourcePlot
);

public record IslandPlotDto(
    string Id,
    string PlotType,
    int Quantity,
    string Configuration,
    string Notes,
    int? PlotNumber,
    int? MapSlotIndex = null
);
