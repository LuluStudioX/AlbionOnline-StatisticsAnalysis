using Serilog;
using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.GameFileData;
using StatisticsAnalysisTool.Common.UserSettings;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Models.BindingModel;
using StatisticsAnalysisTool.Models.NetworkModel;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Operations.Responses;
using StatisticsAnalysisTool.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace StatisticsAnalysisTool.Network.Manager;

// Farmable plot handling: collect/feed/harvest, per-tile timers and farmable config auto-apply for IslandController.
public partial class IslandController
{
    // Resolve the specific farm/herb/pasture plot card a farmable ObjectId belongs to, via its cached world
    // position and the island layout's nearest small slot. Returns null when the position is unknown or no
    // matching plot owns that slot — callers then fall back to the per-type behaviour (no regression).
    private IslandPlot ResolveFarmablePlotByObjectId(Island.Island island, long objectId)
    {
        if (island?.Plots == null || objectId < 0) return null;
        if (!_farmablePositions.TryGetValue(objectId, out var pos)) return null;

        var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
        // Farm/herb/pasture plots can sit on any slot (incl. the small S1/S2), so match the nearest of all —
        // restricting to small-only mis-resolved every farmable to slots 17/18.
        var slot = layout?.WorldToNearestSlot(pos.X, pos.Y, requireLarge: null);
        if (!slot.HasValue) return null;

        return island.Plots.FirstOrDefault(p => FarmPlotTypes.Contains(p.PlotType) && p.MapSlotIndex == slot.Value);
    }

    // Per-plot collect: a collect REQUEST (op 73/74/76/77) carries the collected plant's ObjectId. Clear only
    // that tile (awaiting replant) instead of every plot of the type — kills the collect clear-storm.
    public void HandleFarmableCollect(long plotObjectId)
    {
        if (plotObjectId < 0) return;
        var island = FindCurrentIsland();
        if (island == null) return;

        var plot = ResolveFarmablePlotByObjectId(island, plotObjectId);
        if (plot == null)
        {
            Log.Debug("[IslandController] Collect for unresolved farmable objId={ObjectId} — no per-plot timer cleared", plotObjectId);
            return;
        }

        // Collecting frees the tile for a replant, so mark its position as awaiting one: the next plant
        // (code 45) on this position is then a real replant and counts its seed as consumed. Zone-in
        // re-broadcasts of pre-existing plants never pass through here, so they stay uncounted.
        MarkTileAwaitingReplant(island, plotObjectId);

        if (!UpdatePlotTile(plot, plotObjectId, null)) return;
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        RefreshIslandStatusAsync(island);
        Log.Information("[IslandController] Cleared plot tile on collect: island={Island}, plot={Plot}, objId={ObjectId}",
            island.Name, plot.DisplayLabel, plotObjectId);
    }

    // A pasture/breeding cycle is started by FEEDING the animal (op 77), not by planting — the grown animal
    // persists across cycles, so there is no per-cycle seed-plant (code 45) to seed the timer the way a farm
    // crop does. Feeding is that per-cycle trigger: stamp the fed plot tile's PlotPlantedAt = now so the same
    // GetBaseCollectionHours countdown + status dots used by farms drive the pasture. Routed from the op-77
    // request, which carries the fed plot's ObjectId (same id space as the collect requests / NewBuilding 45).
    public void HandlePastureFeed(long plotObjectId)
    {
        if (plotObjectId < 0) return;
        var island = FindCurrentIsland();
        if (island == null) return;

        var plot = ResolveFarmablePlotByObjectId(island, plotObjectId);
        if (plot == null)
        {
            Log.Debug("[IslandController] Feed for unresolved pasture objId={ObjectId} — no cycle timer started", plotObjectId);
            return;
        }

        if (!UpdatePlotTile(plot, plotObjectId, DateTime.UtcNow)) return;
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        RefreshIslandStatusAsync(island);
        Log.Information("[IslandController] Started pasture cycle on feed: island={Island}, plot={Plot}, objId={ObjectId}",
            island.Name, plot.DisplayLabel, plotObjectId);
    }

    // Marks the tile at a farmable object's world position as awaiting a replant, so the next plant (code 45)
    // on that position counts its seed as consumed. Keyed by stable position (the object id churns per visit).
    // No-op when the position is unknown.
    private void MarkTileAwaitingReplant(Island.Island island, long objectId)
    {
        if (island == null) return;
        if (!_farmablePositions.TryGetValue(objectId, out var pos)) return;

        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{island.Id}|{pos.X:0.##}|{pos.Y:0.##}");
        lock (_consumedTilesLock) _collectedTilesAwaitingReplant.Add(key);
    }

    // Record/clear one tile's planted time for its plot and refresh the plot's per-slot dots + aggregate timer.
    // Tiles are keyed by world position so the per-visit object-id churn never double-counts a slot. Returns
    // false when nothing changed (so callers skip a redundant save). Falls back to the plot-level timer when
    // the plant has no cached position.
    private bool UpdatePlotTile(IslandPlot plot, long objectId, DateTime? plantedAt)
    {
        if (plot == null) return false;

        if (!_farmablePositions.TryGetValue(objectId, out var pos))
        {
            if (plot.PlotPlantedAt == plantedAt) return false;
            plot.PlotPlantedAt = plantedAt;
            return true;
        }

        var posKey = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{pos.X:0.##}|{pos.Y:0.##}");
        var tiles = _plotTilePlanted.GetOrAdd(plot.Id, _ => new ConcurrentDictionary<string, DateTime?>());
        if (tiles.TryGetValue(posKey, out var existing) && existing == plantedAt) return false;
        tiles[posKey] = plantedAt;

        // One dot per occupied tile (stable order), capped to the plot's slot count.
        var ordered = tiles.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .Take(plot.SlotsPerPlot)
            .ToList();
        plot.SetTilePlantedAts(ordered);

        // Aggregate card timer = the earliest still-growing tile (soonest collection); null if none growing.
        var growing = tiles.Values.Where(v => v.HasValue).Select(v => v.Value).ToList();
        plot.PlotPlantedAt = growing.Count > 0 ? growing.Min() : (DateTime?) null;
        return true;
    }

    // Fallback (no per-plot resolution): stamp every farm-type plot. Returns whether anything changed so the
    // caller persists/refreshes only on a real change. Does not save itself.
    private bool PersistPlotPlantedAt(Island.Island island, DateTime plantedAt)
    {
        if (island?.Plots == null) return false;
        var changed = false;
        foreach (var plot in island.Plots.Where(p => FarmPlotTypes.Contains(p.PlotType)))
        {
            if (plot.PlotPlantedAt == plantedAt) continue;
            plot.PlotPlantedAt = plantedAt;
            changed = true;
        }
        return changed;
    }

    private void CommitIslandPlant(Island.Island island)
    {
        island.PlantAll();
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        RefreshIslandStatusAsync(island);
        TryAutoPrefillPayout(island);
    }

    public void HandleFarmableHarvestResponse(FarmableHarvestResponse response)
    {
        HandleFarmableHarvestInternal(response, PlotType.HerbGarden);
    }

    public void HandlePastureHarvestResponse(FarmableHarvestResponse response)
    {
        HandleFarmableHarvestInternal(response, PlotType.Pasture);
    }

    public void HandlePastureFeedConsumed(FarmableHarvestResponse response)
    {
        if (response?.Items == null || response.Items.Count == 0) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        foreach (var (uniqueName, quantity) in response.Items)
        {
            var item = ItemController.GetItemByUniqueName(uniqueName);
            if (item == null || item.Index <= 0) continue;

            island.AddConsumed(item.Index, quantity, PlotType.Pasture);
            island.UpdateModificationDate();

            Log.Information("[IslandController] Recorded pasture feed consumed: island={Island}, item={Item}, qty={Qty}",
                island.Name, uniqueName, quantity);
        }

        _ = SaveToFileAsync();
        _yieldTracker.PushUpdate(island);
    }

    private void HandleFarmableHarvestInternal(FarmableHarvestResponse response, PlotType plotType)
    {
        Log.Debug("[IslandController] FarmableHarvestResponse received: plotType={PlotType}, itemCount={Count}", plotType, response?.Items?.Count ?? -1);

        if (response?.Items == null || response.Items.Count == 0) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        // Op 73 delivers every crop, herb and fibre harvest under one code, so the route-level plotType
        // (HerbGarden) mislabels farm crops (carrot/corn/cabbage). The response carries the farmable
        // signal — its *_SEED entry — so resolve the real plot type from that and fall back to the route
        // default when nothing in the response resolves (e.g. output-only responses).
        var effectivePlotType = plotType;
        foreach (var (uniqueName, _) in response.Items)
        {
            var info = PlotTypeExtensions.TryResolveFarmablePlotInfo(uniqueName);
            if (info != null)
            {
                effectivePlotType = info.PlotType;
                break;
            }

            // Output-only responses carry no *_SEED entry, so the strict resolver above returns nothing and
            // the crop falls back to the route default — booking the SAME crop under two SourcePlots across
            // different harvests (the split). Classify the product name itself (token match, language-neutral
            // on the unique name) so a crop always resolves to one deterministic plot type.
            var (byName, _) = FarmablePlotData.ClassifyFarmableByDisplayName(uniqueName);
            if (byName != null)
            {
                effectivePlotType = byName.Value;
                break;
            }
        }

        foreach (var (uniqueName, quantity) in response.Items)
        {
            var item = ItemController.GetItemByUniqueName(uniqueName);
            if (item == null || item.Index <= 0) continue;

            island.AddYield(item.Index, quantity, effectivePlotType);
            island.UpdateModificationDate();

            Log.Information("[IslandController] Recorded farmable harvest: island={Island}, item={Item}, qty={Qty}, plotType={PlotType}",
                island.Name, uniqueName, quantity, effectivePlotType);
        }

        // Timer clearing is per-plot via the collect REQUEST (HandleFarmableCollect) — not here. The response
        // fires once per item and carries no plot id, so clearing by type here re-wiped freshly-replanted
        // plots on every harvest (the collect clear-storm). Yield recording only.
        _ = SaveToFileAsync();
        _yieldTracker.PushUpdate(island);
    }

    private bool IsNewFarmableSignature(long objectId, string signature)
    {
        _farmableSignatures.TryGetValue(objectId, out var previous);
        _farmableSignatures[objectId] = signature;
        return !string.Equals(previous, signature, StringComparison.Ordinal);
    }

    public void HandleFarmableObjectInfo(FarmableObjectInfoEvent e)
    {
        if (e == null || e.ObjectId < 0) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        // Dedup by DERIVED state (planted-minute + crop), not the raw param signature. The raw signature
        // embeds the live server tick, so it changed on every broadcast — reprocessing and re-saving on
        // every 201 (12k+ Islands.json saves per session). PlantedAt is stable while a plant grows, so this
        // collapses the re-broadcast storm to one process per real plant/replant.
        var stateKey = (e.PlantedAt.HasValue ? e.PlantedAt.Value.Ticks / TimeSpan.TicksPerMinute : -1L)
                       + "|" + e.FarmableUniqueName;
        if (!IsNewFarmableSignature(e.ObjectId, stateKey)) return;

        var activityTimestampUtcResolved = e.TryResolveActivityTimestampUtc();
        var activityTimestampUtc = activityTimestampUtcResolved ?? DateTime.MinValue;

        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
        {
            var paramDump = string.Join(", ", e.Parameters
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            Log.Debug("[IslandController] Farmable params: island={Island}, objectId={ObjectId}, params=[{Params}]",
                island.Name, e.ObjectId, paramDump);
        }

        if (!string.IsNullOrWhiteSpace(e.FarmableUniqueName))
        {
            Log.Debug("[IslandController] Farmable item detected: island={Island}, objectId={ObjectId}, uniqueName={UniqueName}",
                island.Name, e.ObjectId, e.FarmableUniqueName);
            TryAutoApplyFarmableConfig(island, e.FarmableUniqueName);
        }

        Log.Information("[IslandController] Farmable state changed: island={Island}, objectId={ObjectId}, activityAt={ActivityAt:O}",
            island.Name, e.ObjectId, activityTimestampUtc);

        // Param 4 (remaining 100µs) + param 5 (server ticks) → derive PlantedAt and update the timer.
        if (e.PlantedAt.HasValue && e.PlantedAt.Value.AddHours(IslandConstants.LaborerBaseCycleHours) > DateTime.UtcNow)
        {
            // Set only the tile this object belongs to (per-slot); fall back to per-type when unresolved.
            // Persist/refresh only on a real change so a minute-boundary re-process doesn't re-save.
            var plot = ResolveFarmablePlotByObjectId(island, e.ObjectId);
            var changed = plot != null
                ? UpdatePlotTile(plot, e.ObjectId, e.PlantedAt.Value)
                : PersistPlotPlantedAt(island, e.PlantedAt.Value);
            if (changed)
            {
                island.LastPlantedAt = e.PlantedAt.Value;
                island.UpdateModificationDate();
                _ = SaveToFileAsync();
                RefreshIslandStatusAsync(island);
                Log.Information("[IslandController] Updated plot timer from FarmableObjectInfo: island={Island}, objectId={ObjectId}, plot={Plot}, plantedAt={PlantedAt:O}",
                    island.Name, e.ObjectId, plot?.DisplayLabel ?? "(per-type)", e.PlantedAt.Value);
            }
        }

        var prefs = _mainWindowViewModel?.IslandBindings?.Preferences;
        if (activityTimestampUtcResolved.HasValue && prefs?.AutoStartCycleOnIslandActivity == true)
        {
            var activityAge = DateTime.UtcNow - activityTimestampUtc;
            if (activityAge >= TimeSpan.Zero && activityAge <= TimeSpan.FromMinutes(3))
            {
                var cycleRunning = island.NextCollectionReadyAt.HasValue
                    && island.NextCollectionReadyAt.Value > DateTime.UtcNow;

                // Also treat a recently-set LastPlantedAt (within the past 26h) as a running
                // cycle — prevents re-stamping when the island was planted before this visit
                // but LastPlantedAt wasn't yet stored (new island added after planting).
                var recentlyPlanted = island.LastPlantedAt.HasValue
                    && (DateTime.UtcNow - island.LastPlantedAt.Value.ToUniversalTime()).TotalHours <= 26;

                if (!cycleRunning && !recentlyPlanted && !IsIslandInRoyalCity(island))
                {
                    CommitIslandPlant(island);
                    Log.Information("[IslandController] Auto-started island cycle from farmable activity: island={Island}, objectId={ObjectId}, activityAge={ActivityAge:N1}s",
                        island.Name, e.ObjectId, activityAge.TotalSeconds);
                }
            }
        }

    }

    private void TryAutoApplyFarmableConfig(Island.Island island, string farmableUniqueName)
    {
        var info = PlotTypeExtensions.TryResolveFarmablePlotInfo(farmableUniqueName);
        if (info == null || string.IsNullOrWhiteSpace(info.ConfigKey)) return;

        var matchingPlots = island.Plots?.Where(p => p.PlotType == info.PlotType).ToList();
        if (matchingPlots == null || matchingPlots.Count == 0) return;

        var unconfigured = matchingPlots
            .Where(p => string.IsNullOrWhiteSpace(LaborerConfigHelper.ParseConfiguration(p.Configuration)
                .GetValueOrDefault(info.ConfigKey)))
            .ToList();

        if (unconfigured.Count == 0) return;

        var target = unconfigured[0];
        var existing = LaborerConfigHelper.ParseConfiguration(target.Configuration);
        existing[info.ConfigKey] = info.DisplayName;
        target.Configuration = LaborerConfigHelper.BuildConfiguration(existing);
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        RefreshIslandStatusAsync(island);
        Log.Information("[IslandController] Auto-applied farmable config: island={Island}, plotType={PlotType}, key={Key}, value={Value}",
            island.Name, info.PlotType, info.ConfigKey, info.DisplayName);
    }

    private void TryAutoApplyFarmableConfigByPosition(Island.Island island, FarmablePlotInfo info, float wx, float wy)
    {
        var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
        if (layout == null) return;

        var slotIndex = layout.WorldToNearestSlot(wx, wy, requireLarge: null);
        if (!slotIndex.HasValue) return;

        // Find the plot of matching type at this slot.
        var slotPlot = island.Plots?.FirstOrDefault(p =>
            p.PlotType == info.PlotType && p.MapSlotIndex == slotIndex.Value);

        // Fallback: slot not yet assigned — use position-based first-unconfigured (single-plot case).
        slotPlot ??= island.Plots?.Where(p =>
                p.PlotType == info.PlotType && !p.MapSlotIndex.HasValue)
            .FirstOrDefault();

        if (slotPlot == null) return;

        var dict = LaborerConfigHelper.ParseConfiguration(slotPlot.Configuration);
        var existing = dict.GetValueOrDefault(info.ConfigKey, string.Empty);
        // Replace when the freshly-detected plant differs (e.g. chickens swapped for calves). Code 45
        // re-broadcasts every plant on each island visit, so this also self-heals stale stored types.
        // Skip only when it already matches, so re-broadcasts don't churn saves.
        if (string.Equals(existing, info.DisplayName, StringComparison.OrdinalIgnoreCase)) return;

        dict[info.ConfigKey] = info.DisplayName;
        slotPlot.Configuration = LaborerConfigHelper.BuildConfiguration(dict);
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        RefreshIslandStatusAsync(island);
        Log.Information("[IslandController] Position-matched farmable config: island={Island}, plotType={PlotType}, slot={Slot}, key={Key}, {Old} -> {New}",
            island.Name, info.PlotType, slotIndex.Value, info.ConfigKey, string.IsNullOrEmpty(existing) ? "(empty)" : existing, info.DisplayName);
    }

    public void ResetSlotAssignments(Guid islandId)
    {
        Island.Island island;
        lock (_islandsLock)
            island = _islands.FirstOrDefault(i => i.Id == islandId);
        if (island == null) return;

        foreach (var plot in island.Plots)
            plot.MapSlotIndex = null;

        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        Log.Information("[IslandController] Slot assignments reset for island {Name} — will re-assign on next visit", island.Name);
        RefreshBindingsAsync();
    }
}
