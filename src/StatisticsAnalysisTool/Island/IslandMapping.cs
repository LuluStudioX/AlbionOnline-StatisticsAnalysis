using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Models.BindingModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace StatisticsAnalysisTool.Island;

public static class IslandMapping
{
    public static IslandDto ToDto(Island isl) => new(
        isl.Id.ToString(),
        isl.Name,
        isl.Owner,
        isl.Tier,
        isl.City,
        isl.Biome,
        isl.HasPremium,
        isl.IslandType.ToString(),
        isl.LastPlantedAt,
        isl.CreatedDate,
        isl.LastModifiedDate,
        isl.ManagementPayOverride,
        isl.VisitDurationMinutes,
        null,
        isl.Plots?.Select(ToPlotDto).ToList() ?? [],
        isl.LayoutId,
        isl.SlotLabels?.Count > 0 ? new Dictionary<int, string>(isl.SlotLabels) : null,
        isl.LastVisited,
        isl.TotalLaborersSent,
        isl.TotalLootCollected,
        isl.WorldMapDataType,
        isl.SourceClusterIndex,
        isl.LastHandledAt,
        isl.YieldHistory.Count > 0
            ? isl.YieldHistory.Select(e => new IslandYieldEntryDto(e.ItemIndex, e.Quantity, e.CollectedAt, e.SourcePlot.ToString())).ToList()
            : null,
        isl.ConsumedHistory.Count > 0
            ? isl.ConsumedHistory.Select(e => new IslandConsumedEntryDto(e.ItemIndex, e.Quantity, e.ConsumedAt, e.SourcePlot.ToString())).ToList()
            : null,
        isl.MixedRegionAltActive
    );

    public static Island FromDto(IslandDto dto)
    {
        Enum.TryParse<IslandType>(dto.IslandType, out var islandType);
        var isl = new Island(dto.Name, dto.Owner, dto.Tier, dto.Biome, dto.HasPremium, dto.City, islandType);

        isl.LastPlantedAt = dto.LastPlantedAt;
        isl.CreatedDate = dto.CreatedDate;
        isl.LastModifiedDate = dto.LastModifiedDate;
        isl.ManagementPayOverride = dto.ManagementPayOverride;
        isl.VisitDurationMinutes = dto.VisitDurationMinutes;
        isl.LayoutId = dto.LayoutId ?? string.Empty;
        if (dto.SlotLabels is { Count: > 0 })
            isl.SlotLabels = new Dictionary<int, string>(dto.SlotLabels);
        isl.LastVisited = dto.LastVisited;
        isl.TotalLaborersSent = dto.TotalLaborersSent;
        isl.TotalLootCollected = dto.TotalLootCollected;
        isl.WorldMapDataType = dto.WorldMapDataType ?? string.Empty;
        isl.SourceClusterIndex = dto.SourceClusterIndex ?? string.Empty;
        isl.LastHandledAt = dto.LastHandledAt;
        isl.MixedRegionAltActive = dto.MixedRegionAltActive;

        var plots = dto.Plots?.Select(FromPlotDto) ?? [];
        foreach (var plot in plots)
            isl.Plots.Add(plot);

        if (dto.YieldHistory is { Count: > 0 })
        {
            foreach (var e in dto.YieldHistory)
            {
                Enum.TryParse<PlotType>(e.SourcePlot, out var src);
                isl.YieldHistory.Add(new IslandYieldEntry { ItemIndex = e.ItemIndex, Quantity = e.Quantity, CollectedAt = e.CollectedAt, SourcePlot = src });
            }
        }

        if (dto.ConsumedHistory is { Count: > 0 })
        {
            foreach (var e in dto.ConsumedHistory)
            {
                Enum.TryParse<PlotType>(e.SourcePlot, out var src);
                isl.ConsumedHistory.Add(new IslandConsumedEntry { ItemIndex = e.ItemIndex, Quantity = e.Quantity, ConsumedAt = e.ConsumedAt, SourcePlot = src });
            }
        }

        return isl;
    }

    public static IslandPlotDto ToPlotDto(IslandPlot plot) => new(
        plot.Id.ToString(),
        plot.PlotType.ToString(),
        plot.Quantity,
        plot.Configuration,
        plot.Notes,
        plot.PlotNumber,
        plot.MapSlotIndex
    );

    public static IslandPlot FromPlotDto(IslandPlotDto dto)
    {
        Enum.TryParse<PlotType>(dto.PlotType, out var plotType);
        return new IslandPlot(plotType, dto.Quantity, dto.Notes ?? string.Empty, dto.Configuration ?? string.Empty)
        {
            PlotNumber = dto.PlotNumber,
            MapSlotIndex = dto.MapSlotIndex
        };
    }

    public static IslandEntry ToEntry(Island isl, int sortOrder)
    {
        var (layout, imagePath) = IslandLayouts.ResolveForIsland(isl.IslandType, isl.City);
        var entry = new IslandEntry
        {
            IslandId = isl.Id,
            Name = isl.Name,
            Tier = isl.Tier,
            TierDisplay = $"T{isl.Tier}",
            HasPremium = isl.HasPremium,
            CityFaction = ParseCityFaction(isl.City),
            CityName = isl.City,
            Biome = isl.Biome,
            OwnerName = isl.Owner,
            CollectionStatusText = isl.CollectionStatusText,
            CollectionStatusState = isl.CollectionStatusState,
            NeedsVisit = isl.NeedsVisit,
            PlotCount = isl.Plots?.Count ?? 0,
            SortOrder = sortOrder,
            Notes = null,
            LayoutId = isl.LayoutId,
            MapImagePath = imagePath ?? string.Empty,
            LastVisited = isl.LastVisited,
            TotalLaborersSent = isl.TotalLaborersSent,
            TotalLootCollected = isl.TotalLootCollected,
            TrackingEnabled = isl.TrackingEnabled,
            VisitDurationMinutes = isl.VisitDurationMinutes,
            Plots = new ObservableCollection<IslandPlotEntry>(
                isl.Plots?.OrderBy(p =>
                {
                    if (!p.MapSlotIndex.HasValue) return int.MaxValue;
                    var slotDef = layout?.GetSlot(p.MapSlotIndex.Value);
                    // Small slots always sort by SlotIndex so paired cards stay in fixed order (S1 left, S2 right)
                    return (slotDef is { IsLarge: false }) ? slotDef.SlotIndex + 10000 : p.MapSlotIndex.Value;
                }).Select(p => ToPlotEntry(p, layout)) ?? []
            ),
            // Collapse to one row per item. The same crop can be booked under more than one SourcePlot
            // (harvest plot-type attribution varies per packet), which would otherwise render as duplicate
            // tiles in the Collected/Consumed panels. Totals are unaffected — the per-plot split is summed.
            YieldItems = new ObservableCollection<IslandYieldEntry>(
                isl.YieldHistory
                    .GroupBy(e => e.ItemIndex)
                    .Select(g => new IslandYieldEntry
                    {
                        ItemIndex = g.Key,
                        Quantity = g.Sum(e => e.Quantity),
                        SourcePlot = g.First().SourcePlot,
                        CollectedAt = g.Min(e => e.CollectedAt)
                    })),
            ConsumedItems = new ObservableCollection<IslandConsumedEntry>(
                isl.ConsumedHistory
                    .GroupBy(e => e.ItemIndex)
                    .Select(g => new IslandConsumedEntry
                    {
                        ItemIndex = g.Key,
                        Quantity = g.Sum(e => e.Quantity),
                        SourcePlot = g.First().SourcePlot,
                        ConsumedAt = g.Min(e => e.ConsumedAt)
                    }))
        };
        entry.RebuildSlotGrid(layout, isl.Plots ?? []);
        return entry;
    }

    private static IslandPlotEntry ToPlotEntry(IslandPlot plot, IslandLayoutDefinition layout = null)
    {
        var farmableTypeLine = plot.PlotType.HasFarmableConfig()
            ? plot.PlotType.GetConfiguredTypeName(plot.Configuration)
            : string.Empty;

        System.Windows.Media.Imaging.BitmapImage cropIcon = null;
        string cropTooltip = null;
        if (!string.IsNullOrWhiteSpace(farmableTypeLine))
        {
            var info = PlotTypeExtensions.TryResolveFarmablePlotInfoByDisplayName(plot.PlotType, farmableTypeLine);
            if (info != null)
            {
                cropIcon = ImageController.GetItemImage(info.UniqueName, 24, 24);
                cropTooltip = PlotTypeExtensions.GetCropTooltip(info.UniqueName);
            }
        }

        int? highlightCol = null;
        int? highlightRow = null;
        IReadOnlyList<SlotGridCell> plotSlotGrid = [];

        if (layout != null && layout.GridColumns > 0)
        {
            var stateCode = PlotStateCodeForEntry(plot);
            plotSlotGrid = layout.Slots.Select(s =>
            {
                var isHighlighted = plot.MapSlotIndex.HasValue && s.SlotIndex == plot.MapSlotIndex.Value;
                var state = isHighlighted ? stateCode : "empty";
                return new SlotGridCell(s.GridCol, s.GridRow, state,
                    IslandLayouts.FormatSlotLabel(s.SlotIndex), !s.IsLarge, isHighlighted);
            }).ToList();

            if (plot.MapSlotIndex.HasValue)
            {
                var cell = layout.GetSlotGridCell(plot.MapSlotIndex.Value);
                if (cell.HasValue)
                {
                    highlightCol = cell.Value.Col;
                    highlightRow = cell.Value.Row;
                }
            }
        }

        return new IslandPlotEntry
        {
            PlotId = plot.Id,
            PlotType = plot.BuildingTypeName,
            Quantity = plot.Quantity,
            PlotSentState = plot.PlotSentState,
            IsHouse = plot.PlotType == PlotType.House,
            FarmableTypeLine = farmableTypeLine,
            FarmableCropIcon = cropIcon,
            FarmableCropTooltip = cropTooltip,
            Laborer1IndicatorState = plot.Laborer1IndicatorState,
            Laborer1Line = plot.Laborer1Line,
            Laborer2IndicatorState = plot.Laborer2IndicatorState,
            Laborer2Line = plot.Laborer2Line,
            Laborer3IndicatorState = plot.Laborer3IndicatorState,
            Laborer3Line = plot.Laborer3Line,
            MapSlotIndex = plot.MapSlotIndex,
            MapSlotLabel = plot.MapSlotIndex.HasValue ? IslandLayouts.FormatSlotLabel(plot.MapSlotIndex.Value) : string.Empty,
            SlotDots = plot.SlotDots,
            SlotHighlightCol = highlightCol,
            SlotHighlightRow = highlightRow,
            SlotStateCode = PlotStateCodeForEntry(plot),
            PlotSlotGrid = plotSlotGrid
        };
    }

    private static string PlotStateCodeForEntry(IslandPlot plot) => plot.PlotType switch
    {
        PlotType.House => plot.AllLaborersSent ? "sent" : "house",
        PlotType.Farm => "farm",
        PlotType.HerbGarden => "herbgarden",
        PlotType.Pasture => "pasture",
        PlotType.Kennel => "kennel",
        _ => "empty"
    };

    public static Island NewIslandFromEntry(IslandEntry e)
    {
        var city = CityFactionToName(e.CityFaction, e.CityName);
        var island = new Island(e.Name, e.OwnerName, e.Tier, e.Biome, e.HasPremium, city);
        island.TrackingEnabled = e.TrackingEnabled;
        return island;
    }

    public static void ApplyEntryToIsland(IslandEntry e, Island isl)
    {
        isl.Name = e.Name;
        isl.Owner = e.OwnerName;
        isl.Tier = e.Tier;
        isl.Biome = e.Biome;
        isl.HasPremium = e.HasPremium;
        isl.TrackingEnabled = e.TrackingEnabled;
        isl.City = CityFactionToName(e.CityFaction, e.CityName);
        isl.VisitDurationMinutes = e.VisitDurationMinutes;
        isl.UpdateModificationDate();
    }

    private static string CityFactionToName(CityFaction faction, string fallback) =>
        string.IsNullOrWhiteSpace(fallback) ? faction.ToString() : fallback;

    public static CityFaction ParseCityFaction(string city) => city?.ToLowerInvariant() switch
    {
        "caerleon" => CityFaction.Caerleon,
        "bridgewatch" => CityFaction.Bridgewatch,
        "lymhurst" => CityFaction.Lymhurst,
        "fort sterling" or "fortsterling" => CityFaction.FortSterling,
        "martlock" => CityFaction.Martlock,
        "thetford" => CityFaction.Thetford,
        "brecilien" => CityFaction.Brecilien,
        _ => CityFaction.Unknown
    };

    public static string CityToDefaultBiome(string city) => city?.ToLowerInvariant() switch
    {
        "bridgewatch" => "Steppe",
        "thetford" => "Swamp",
        "lymhurst" => "Forest",
        "brecilien" => "Forest",
        "martlock" => "Highland",
        "fort sterling" or "fortsterling" => "Mountain",
        "caerleon" => "Steppe",
        _ => string.Empty
    };
}
