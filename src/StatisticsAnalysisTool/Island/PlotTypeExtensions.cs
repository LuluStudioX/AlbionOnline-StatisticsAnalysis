using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Models;
using StatisticsAnalysisTool.Models.ItemsJsonModel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StatisticsAnalysisTool.Island;

public static class PlotTypeExtensions
{
    public static string GetDisplayName(this PlotType plotType)
    {
        return plotType switch
        {
            PlotType.House => "House",
            PlotType.Farm => "Farm",
            PlotType.Pasture => "Pasture",
            PlotType.HerbGarden => "Herb Garden",
            PlotType.Mill => "Mill",
            PlotType.Smelter => "Smelter",
            PlotType.Tanner => "Tanner",
            PlotType.Lumbermill => "Lumbermill",
            PlotType.Stonemason => "Stonemason",
            PlotType.Butcher => "Butcher",
            PlotType.Cook => "Cook",
            PlotType.AlchemyLab => "Alchemy Lab",
            PlotType.HunterLodge => "Hunter's Lodge",
            PlotType.WarriorGuild => "Warrior's Forge",
            PlotType.Kennel => "Kennel",
            PlotType.Saddler => "Saddler",
            PlotType.MageTower => "Mage's Tower",
            PlotType.Weaver => "Weaver",
            PlotType.Toolmaker => "Toolmaker",
            PlotType.RepairStation => "Repair Station",
            _ => plotType.ToString()
        };
    }

    public static double GetBaseCollectionHours(this PlotType plotType, string configuration = "")
    {
        var configuredObjectName = GetConfiguredObjectName(plotType, configuration);
        if (!string.IsNullOrWhiteSpace(configuredObjectName) && TryResolveGrowHoursFromFarmableData(configuredObjectName, out var resolvedHours))
        {
            return resolvedHours;
        }

        return plotType switch
        {
            PlotType.Farm => IslandConstants.LaborerBaseCycleHours,
            PlotType.HerbGarden => IslandConstants.LaborerBaseCycleHours,
            PlotType.House => IslandConstants.LaborerBaseCycleHours,
            PlotType.Pasture => IsExtendedCyclePastureAnimal(configuredObjectName)
                ? IslandConstants.LaborerExtendedCycleHours : IslandConstants.LaborerBaseCycleHours,
            PlotType.Kennel => IslandConstants.LaborerBaseCycleHours,
            PlotType.Saddler => IslandConstants.LaborerBaseCycleHours,
            _ => 0.0
        };
    }

    // Animal display names whose pasture breeding runs the extended (52h) cycle rather than the base cycle.
    private static readonly string[] ExtendedCyclePastureAnimals = { "Horse", "Foal", "Ox" };

    // Exact whole-word match on the PARSED AnimalType, not a Contains on the raw config blob — so an
    // unrelated value that merely contains "Ox" (e.g. "Oxtongue") can no longer trip the 52h cycle.
    private static bool IsExtendedCyclePastureAnimal(string configuredObjectName)
    {
        if (string.IsNullOrWhiteSpace(configuredObjectName)) return false;
        var tokens = configuredObjectName.Split(new[] { ' ', '\t', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(tok => ExtendedCyclePastureAnimals.Contains(tok, StringComparer.OrdinalIgnoreCase));
    }

    public static string GetPremiumEffectSummary(this PlotType plotType, string configuration = "")
    {
        var configuredObjectName = GetConfiguredObjectName(plotType, configuration);

        return plotType switch
        {
            PlotType.Farm => string.IsNullOrWhiteSpace(configuredObjectName)
                ? "Premium: ~2x crop yield and stronger focus-based seed sustainability."
                : $"{configuredObjectName}: Premium ~2x crop yield and stronger focus-based seed sustainability.",
            PlotType.HerbGarden => string.IsNullOrWhiteSpace(configuredObjectName)
                ? "Premium: better herb profitability via focus-based seed sustainability."
                : $"{configuredObjectName}: Premium improves herb profitability via focus-based seed sustainability.",
            PlotType.Pasture => string.IsNullOrWhiteSpace(configuredObjectName)
                ? "Premium: breeding economics are object-dependent; verify specific animal behavior in-game."
                : $"{configuredObjectName}: Premium breeding economics are object-dependent; verify exact behavior in-game.",
            PlotType.House => "Premium: no direct laborer cycle-time reduction on island house collection.",
            _ => string.Empty,
        };
    }

    public static bool HasCollectionTimer(this PlotType plotType)
        => plotType is PlotType.Farm or PlotType.HerbGarden or PlotType.House or PlotType.Pasture or PlotType.Kennel or PlotType.Saddler;

    public static bool HasFarmableConfig(this PlotType plotType)
        => plotType is PlotType.Farm or PlotType.HerbGarden or PlotType.Pasture or PlotType.Kennel or PlotType.Saddler;

    public static string GetConfiguredTypeName(this PlotType plotType, string configuration)
        => GetConfiguredObjectName(plotType, configuration);

    public static string GetFarmableConfigKey(this PlotType plotType) => plotType switch
    {
        PlotType.Farm => "CropType",
        PlotType.HerbGarden => "CropType",
        PlotType.Pasture => "AnimalType",
        PlotType.Kennel => "AnimalType",
        PlotType.Saddler => "MountType",
        _ => string.Empty
    };

    public static IReadOnlyList<FarmablePlotInfo> GetFarmableOptions(this PlotType plotType)
    {
        FarmablePlotData.EnsureFarmableGrowCache();
        return FarmablePlotData.FarmablePlotInfoByUniqueName
            .Where(kv => kv.Value.PlotType == plotType)
            .OrderBy(kv => kv.Value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Value)
            .DistinctBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static FarmablePlotInfo TryResolveFarmablePlotInfo(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
        {
            return null;
        }

        FarmablePlotData.EnsureFarmableGrowCache();

        if (FarmablePlotData.FarmablePlotInfoByUniqueName.TryGetValue(uniqueName, out var cached))
        {
            return cached;
        }

        return FarmablePlotData.TryParseFarmablePlotInfoFromUniqueName(uniqueName);
    }

    public static FarmablePlotInfo TryResolveFarmablePlotInfoByDisplayName(PlotType plotType, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        FarmablePlotData.EnsureFarmableGrowCache();

        return FarmablePlotData.FarmablePlotInfoByUniqueName
            .Values
            .FirstOrDefault(f => f.PlotType == plotType
                && string.Equals(f.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetCropTooltip(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return null;

        FarmablePlotData.EnsureFarmableGrowCache();

        if (!FarmablePlotData.FarmableItemByUniqueName.TryGetValue(uniqueName, out var item))
            return null;

        var parts = new List<string>();

        var growSeconds = FarmablePlotData.GetGrowTimeSeconds(item);
        if (growSeconds > 0)
        {
            var hours = growSeconds / 3600d;
            parts.Add($"Grow time: {hours:0.#}h");
        }

        if (double.TryParse(item.Harvest?.Seed?.Chance?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seedChance) && seedChance > 0)
        {
            parts.Add($"Seed return: {seedChance * 100:0.#}%");
        }

        var offspringChanceStr = item.GrownItem?.OffSpring?.Chance ?? item.Harvest?.Seed?.Chance;
        if (item.Harvest?.Seed == null
            && double.TryParse(offspringChanceStr?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var offspringChance)
            && offspringChance > 0)
        {
            parts.Add($"Offspring: {offspringChance * 100:0.#}%");
        }

        return parts.Count > 0 ? string.Join("  |  ", parts) : null;
    }

    private static string GetConfiguredObjectName(PlotType plotType, string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return string.Empty;
        }

        var key = plotType switch
        {
            PlotType.Farm => "CropType",
            PlotType.HerbGarden => "CropType",
            PlotType.Pasture => "AnimalType",
            PlotType.Kennel => "AnimalType",
            PlotType.Saddler => "MountType",
            _ => string.Empty,
        };

        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var lines = configuration.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var idx = line.IndexOf(':');
            if (idx < 0)
            {
                continue;
            }

            var candidateKey = line[..idx].Trim();
            if (!candidateKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(idx + 1)..].Trim();
        }

        return string.Empty;
    }

    private static bool TryResolveGrowHoursFromFarmableData(string configuredObjectName, out double hours)
    {
        hours = 0;
        FarmablePlotData.EnsureFarmableGrowCache();

        var keysToTry = new[]
        {
            configuredObjectName,
            FarmablePlotData.NormalizeConfiguredObjectName(configuredObjectName),
            configuredObjectName.Replace("Baby ", string.Empty, StringComparison.OrdinalIgnoreCase),
            configuredObjectName.Replace(" (Milk)", string.Empty, StringComparison.OrdinalIgnoreCase),
            configuredObjectName.Replace(" (Goat)", string.Empty, StringComparison.OrdinalIgnoreCase)
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keysToTry)
        {
            if (FarmablePlotData.FarmableGrowHoursByName.TryGetValue(key, out var value) && value > 0)
            {
                hours = value;
                return true;
            }
        }

        return false;
    }

    public static System.Collections.Generic.Dictionary<string, int> ParseConfiguredObjectCounts(PlotType plotType, string configuration, int defaultQuantity = 1)
    {
        var result = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return result;
        }

        var mapKey = plotType switch
        {
            PlotType.Farm => "CropTypeMap",
            PlotType.HerbGarden => "CropTypeMap",
            PlotType.Pasture => "AnimalTypeMap",
            PlotType.Kennel => "AnimalTypeMap",
            PlotType.Saddler => "MountTypeMap",
            _ => string.Empty,
        };

        var lines = configuration.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var key = line.Substring(0, idx).Trim();
            var value = line.Substring(idx + 1).Trim();

            if (string.Equals(key, mapKey, System.StringComparison.OrdinalIgnoreCase) || value.Contains('=') || value.Contains(',') || value.Contains(';'))
            {
                var parts = value.Split(new[] { ';', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var p = part.Trim();
                    if (string.IsNullOrEmpty(p)) continue;
                    if (p.Contains('='))
                    {
                        var kv = p.Split(new[] { '=' }, 2);
                        var name = kv[0].Trim();
                        if (int.TryParse(kv[1].Trim(), out var count) && count > 0)
                        {
                            result[name] = (result.ContainsKey(name) ? result[name] + count : count);
                        }
                    }
                    else
                    {
                        result[p] = (result.ContainsKey(p) ? result[p] + 1 : 1);
                    }
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }
        }

        var singleKey = plotType switch
        {
            PlotType.Farm => "CropType",
            PlotType.HerbGarden => "CropType",
            PlotType.Pasture => "AnimalType",
            PlotType.Kennel => "AnimalType",
            PlotType.Saddler => "MountType",
            _ => string.Empty,
        };

        if (!string.IsNullOrEmpty(singleKey))
        {
            foreach (var line in lines)
            {
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var key = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();
                if (string.Equals(key, singleKey, System.StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    result[value] = defaultQuantity;
                    return result;
                }
            }
        }

        return result;
    }
}
