using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Models;
using StatisticsAnalysisTool.Models.ItemsJsonModel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StatisticsAnalysisTool.Island;

internal static class FarmablePlotData
{
    internal static readonly object FarmableCacheLock = new();
    internal static bool _farmableCacheInitialized;
    internal static readonly Dictionary<string, double> FarmableGrowHoursByName = new(StringComparer.OrdinalIgnoreCase);

    // UniqueName → FarmablePlotInfo (populated once from ItemController on first use).
    internal static readonly Dictionary<string, FarmablePlotInfo> FarmablePlotInfoByUniqueName = new(StringComparer.OrdinalIgnoreCase);

    // UniqueName → FarmableItem raw data (for tooltip).
    internal static readonly Dictionary<string, FarmableItem> FarmableItemByUniqueName = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> CropNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CARROT", "BEAN", "WHEAT", "TURNIP", "CABBAGE", "POTATO", "CORN", "PUMPKIN"
    };

    private static readonly HashSet<string> HerbNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AGARIC", "COMFREY", "BURDOCK", "TEASEL", "FOXGLOVE", "MULLEIN", "YARROW"
    };

    // Standard mounts raised on Saddler
    private static readonly HashSet<string> SaddlerAnimalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HORSE", "OX", "GIANTSTAG"
    };

    // Small livestock raised on Pasture
    private static readonly HashSet<string> PastureAnimalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHICKEN", "GOOSE", "SHEEP", "GOAT", "COW", "PIG",
        "RABBIT_EASTER", "RABBIT_EASTER_DARK"
    };

    // Combat pets / rare mounts raised on Kennel
    private static readonly HashSet<string> KennelAnimalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DIREWOLF", "DIREBOAR", "DIREBEAR", "GREYWOLF", "COUGAR",
        "MOABIRD", "RAM", "SWAMPDRAGON", "OWL", "SPIDER_HELL",
        "MAMMOTH", "GIANTSTAG_MOOSE",
        "DIREBEAR_FW_FORTSTERLING", "DIREBOAR_FW_LYMHURST", "GREYWOLF_FW_CAERLEON",
        "MOABIRD_FW_BRIDGEWATCH", "RAM_FW_MARTLOCK", "SWAMPDRAGON_FW_THETFORD",
        "OWL_FW_BRECILIEN", "SPIDER_HELL"
    };

    internal static void EnsureFarmableGrowCache()
    {
        if (_farmableCacheInitialized)
        {
            return;
        }

        lock (FarmableCacheLock)
        {
            if (_farmableCacheInitialized)
            {
                return;
            }

            var pairs = ItemController.GetFarmableItemsWithIndex();
            if (pairs.Count == 0)
                return;

            foreach (var (farmable, indexed) in pairs)
            {
                var info = BuildFarmablePlotInfo(farmable, indexed)
                    ?? TryParseFarmablePlotInfoFromUniqueName(farmable.UniqueName);
                if (info != null)
                {
                    FarmablePlotInfoByUniqueName[farmable.UniqueName] = info;
                }

                FarmableItemByUniqueName[farmable.UniqueName] = farmable;

                var cycleSeconds = GetCollectionCycleSeconds(farmable);
                if (cycleSeconds <= 0 || indexed == null)
                {
                    continue;
                }

                var growHours = cycleSeconds / 3600d;
                var names = new[]
                {
                    indexed.LocalizedName,
                    ItemController.LocalizedName(indexed.LocalizedNames, "EN-US", string.Empty),
                    NormalizeConfiguredObjectName(indexed.LocalizedName),
                    NormalizeConfiguredObjectName(ItemController.LocalizedName(indexed.LocalizedNames, "EN-US", string.Empty))
                }
                .Where(n => !string.IsNullOrWhiteSpace(n));

                foreach (var name in names)
                {
                    FarmableGrowHoursByName[name] = growHours;
                }
            }

            _farmableCacheInitialized = true;
        }
    }

    private static FarmablePlotInfo BuildFarmablePlotInfo(FarmableItem farmable, Item item)
    {
        if (item == null)
            return null;

        var (plotType, configKey) = ClassifyFarmableByUniqueName(farmable.UniqueName);
        if (plotType == null)
            return null;

        var displayName = ItemController.LocalizedName(item.LocalizedNames, "EN-US", string.Empty);
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = item.LocalizedName;

        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        return new FarmablePlotInfo
        {
            PlotType = plotType.Value,
            ConfigKey = configKey,
            DisplayName = displayName,
            UniqueName = farmable.UniqueName
        };
    }

    internal static (PlotType? plotType, string configKey) ClassifyFarmableByUniqueName(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return (null, null);

        var upper = uniqueName.ToUpperInvariant();
        var parts = upper.Split('_');

        if (upper.EndsWith("_SEED", StringComparison.Ordinal))
        {
            var cropName = parts.Length >= 3 ? parts[^2] : string.Empty;
            if (CropNames.Contains(cropName))
                return (PlotType.Farm, "CropType");
            if (HerbNames.Contains(cropName))
                return (PlotType.HerbGarden, "CropType");
            return (null, null);
        }

        bool isBaby = upper.EndsWith("_BABY", StringComparison.Ordinal);
        bool isGrown = upper.EndsWith("_GROWN", StringComparison.Ordinal);
        if (!isBaby && !isGrown)
            return (null, null);

        // Extract animal name: strip T{n}_FARM_ prefix and _BABY/_GROWN suffix
        // e.g. T5_FARM_DIREWOLF_FW_CAERLEON_BABY → DIREWOLF_FW_CAERLEON
        var animalName = parts.Length >= 3
            ? string.Join("_", parts[2..^1])
            : string.Empty;

        if (string.IsNullOrWhiteSpace(animalName))
            return (null, null);

        if (SaddlerAnimalNames.Contains(animalName))
            return (PlotType.Saddler, "MountType");

        if (PastureAnimalNames.Contains(animalName))
            return (PlotType.Pasture, "AnimalType");

        if (KennelAnimalNames.Contains(animalName))
            return (PlotType.Kennel, "AnimalType");

        // Unknown animal — default to Pasture for _GROWN, Kennel for _BABY (legacy fallback)
        return isGrown ? (PlotType.Pasture, "AnimalType") : (PlotType.Kennel, "AnimalType");
    }

    // Classify a plot by its stored configuration display name (e.g. "Elusive Foxglove Seeds",
    // "Carrot Seeds", "Baby Chickens"). Used to migrate plots whose PlotType was resolved by an older
    // keyword classifier that mistook herb seeds (T*_FARM_*_SEED) for farm crops. Matches the crop/animal
    // token anywhere in the name so plurals and prefixes ("Baby Chickens") still resolve.
    internal static (PlotType? plotType, string configKey) ClassifyFarmableByDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return (null, null);

        var upper = displayName.ToUpperInvariant();

        if (HerbNames.Any(h => upper.Contains(h, StringComparison.Ordinal)))
            return (PlotType.HerbGarden, "CropType");
        if (CropNames.Any(c => upper.Contains(c, StringComparison.Ordinal)))
            return (PlotType.Farm, "CropType");
        if (SaddlerAnimalNames.Any(a => upper.Contains(a, StringComparison.Ordinal)))
            return (PlotType.Saddler, "MountType");
        if (PastureAnimalNames.Any(a => upper.Contains(a, StringComparison.Ordinal)))
            return (PlotType.Pasture, "AnimalType");
        if (KennelAnimalNames.Any(a => upper.Contains(a, StringComparison.Ordinal)))
            return (PlotType.Kennel, "AnimalType");

        return (null, null);
    }

    internal static FarmablePlotInfo TryParseFarmablePlotInfoFromUniqueName(string uniqueName)
    {
        var (plotType, configKey) = ClassifyFarmableByUniqueName(uniqueName);
        if (plotType == null)
            return null;

        var parts = uniqueName.Split('_');
        // T{tier}_FARM_{name}_{SEED|BABY|GROWN} → name starts at index 2, strip last part
        var nameParts = parts.Length >= 3 ? parts[2..^1] : parts;
        var rawName = string.Join(" ", nameParts);
        var displayName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(rawName.ToLowerInvariant());

        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        return new FarmablePlotInfo
        {
            PlotType = plotType.Value,
            ConfigKey = configKey,
            DisplayName = displayName,
            UniqueName = uniqueName
        };
    }

    // The island collection-timer cycle length. Confirmed from packet capture: the code-201 elapsed
    // counter is capped at activefarmcyclelengthseconds, so that field — not @growtime — is the real
    // per-cycle duration the server uses. @growtime is the longer baby->grown maturation (~2x) and is
    // wrong for the collection countdown. Falls back to @growtime only when the cycle field is absent.
    internal static double GetCollectionCycleSeconds(FarmableItem farmable)
    {
        if (TryParseSeconds(farmable?.ActiveFarmCycleLengthSeconds, out var cycleSeconds) && cycleSeconds > 0)
        {
            return cycleSeconds;
        }

        return GetGrowTimeSeconds(farmable);
    }

    internal static double GetGrowTimeSeconds(FarmableItem farmable)
    {
        if (TryParseSeconds(farmable?.GrowTime, out var rootSeconds) && rootSeconds > 0)
            return rootSeconds;

        if (TryParseSeconds(farmable?.Harvest?.GrowTime, out var harvestSeconds) && harvestSeconds > 0)
            return harvestSeconds;

        if (TryParseSeconds(farmable?.GrownItem?.GrowTime, out var grownSeconds) && grownSeconds > 0)
            return grownSeconds;

        if (TryParseSeconds(farmable?.ActiveFarmCycleLengthSeconds, out var activeCycleSeconds) && activeCycleSeconds > 0)
            return activeCycleSeconds;

        return 0;
    }

    private static bool TryParseSeconds(string value, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Replace(',', '.').Trim();
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    internal static string NormalizeConfiguredObjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("Kid ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Baby ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(Milk)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(Goat)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }
}
