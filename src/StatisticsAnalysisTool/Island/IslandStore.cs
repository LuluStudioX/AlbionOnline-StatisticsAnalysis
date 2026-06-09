using Serilog;
using StatisticsAnalysisTool.Common;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Island;

// Persistence for the island list: file load/save plus the one-time data heals (invalid house slots,
// legacy plot-type migration) applied on load. Holds no live state — the controller owns the in-memory
// list and orchestrates when to load/save; this type only does the disk + mapping work.
public static class IslandStore
{
    private const string IslandsFileName = "Islands.json";

    // Returns the loaded islands and whether any were migrated (caller persists once when true).
    public static async Task<(List<Island> Islands, bool Migrated)> LoadAsync()
    {
        var path = AppDataPaths.UserDataFile(IslandsFileName);
        var loaded = await FileController.LoadAsync<List<IslandDto>>(path);

        if (loaded == null || loaded.Count == 0)
            return ([], false);

        var islands = loaded.Select(IslandMapping.FromDto).ToList();
        var anyMigrated = false;
        foreach (var island in islands)
        {
            SanitizeHouseSlotAssignments(island);
            if (MigratePlotTypesFromConfiguration(island))
                anyMigrated = true;
        }

        return (islands, anyMigrated);
    }

    // Caller snapshots the list under its lock and passes it in, so enumeration here is race-free.
    public static async Task SaveAsync(IReadOnlyList<Island> islands)
    {
        var dtos = islands.Select(IslandMapping.ToDto).ToList();

        DirectoryController.CreateDirectoryWhenNotExists(AppDataPaths.UserDataDirectory);
        await FileController.SaveAsync(dtos, AppDataPaths.UserDataFile(IslandsFileName));
        Log.Debug("[IslandStore] Saved {Count} islands.", dtos.Count);
    }

    // Auto-heal slot assignments persisted by an earlier bug where houses could resolve onto the
    // small S1/S2 slots. A house is large-footprint, so a house plot pointing at a small slot is
    // invalid — null it so it re-resolves to a large slot on the next visit (no manual reset needed).
    private static void SanitizeHouseSlotAssignments(Island island)
    {
        if (island?.Plots == null) return;
        var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
        if (layout == null) return;

        foreach (var plot in island.Plots.Where(p => p.PlotType == PlotType.House && p.MapSlotIndex.HasValue))
        {
            var slot = layout.GetSlot(plot.MapSlotIndex.Value);
            if (slot is { IsLarge: false })
            {
                Log.Information("[IslandStore] Cleared invalid house slot {Slot} (small slot) on '{Name}' — will re-resolve on next visit",
                    plot.MapSlotIndex.Value, island.Name);
                plot.MapSlotIndex = null;
            }
        }
    }

    // One-time migration: older builds resolved plot type with a keyword classifier that bucketed every
    // T*_FARM_*_SEED as Farm — so herb gardens (foxglove/agaric/etc.) were stored as Farm. Re-classify each
    // farmable plot by its configured crop/animal name so its type, slot assignment and yield bucketing
    // agree. Returns true if any plot type changed (caller persists once).
    private static bool MigratePlotTypesFromConfiguration(Island island)
    {
        if (island?.Plots == null) return false;

        var changed = false;
        foreach (var plot in island.Plots)
        {
            // Only the configurable farmable plot types can be mis-typed by the old classifier.
            if (plot.PlotType is not (PlotType.Farm or PlotType.HerbGarden or PlotType.Pasture or PlotType.Kennel or PlotType.Saddler))
                continue;

            var configuredName = plot.PlotType.GetConfiguredTypeName(plot.Configuration);
            if (string.IsNullOrWhiteSpace(configuredName))
                continue;

            var (resolved, _) = FarmablePlotData.ClassifyFarmableByDisplayName(configuredName);
            if (!resolved.HasValue || resolved.Value == plot.PlotType)
                continue;

            Log.Information("[IslandStore] Migrated plot type on '{Island}': {Old} -> {New} (config '{Config}')",
                island.Name, plot.PlotType, resolved.Value, configuredName);
            plot.PlotType = resolved.Value;
            changed = true;
        }

        if (changed)
            island.UpdateModificationDate();

        return changed;
    }
}
