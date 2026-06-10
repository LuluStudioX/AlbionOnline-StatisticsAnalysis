using Serilog;
using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.GameFileData;
using StatisticsAnalysisTool.Common.UserSettings;
using StatisticsAnalysisTool.Enumerations;
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

namespace StatisticsAnalysisTool.Island;

// NewBuilding handling: island/laborer/farmable building detection, plot-type resolution and plant placement for IslandController.
public partial class IslandController
{
    public void HandleNewBuilding(NewBuildingEvent e)
    {
        if (e.ObjectId < 0) return;

        if (e.IsLaborerBuilding)
        {
            var isNew = false;
            var snapshot = _snapshots.GetOrAdd(e.ObjectId, id =>
            {
                isNew = true;
                return new LaborerSnapshot(id) { DetectionOrder = Interlocked.Increment(ref _detectionCounter) };
            });
            if (isNew)
            {
                lock (_snapshotOrderLock)
                    _snapshotsByOrder.Add(snapshot);
            }
            snapshot.UpdateFromNewBuilding(e);
            // A tier upgrade respawns the laborer building under a new ObjectId. Drop the stale
            // pre-upgrade snapshot (same name) so it can't shadow the live one in status matching.
            if (isNew && !string.IsNullOrWhiteSpace(snapshot.FullName))
                EvictStaleDuplicateSnapshots(snapshot);
            if (e.HasPremium) _sessionHasPremium = true;
            UpdateLastSnapshotCache();
            if (e.Position.HasValue)
                Log.Information("[IslandController] Laborer building pos: objectId={ObjectId} pos=({X},{Y}) housePlotGuid={HousePlotGuid}",
                    e.ObjectId, e.Position.Value.X, e.Position.Value.Y, e.HousePlotGuid);

            var island = FindCurrentIsland();
            if (island != null)
            {
                TryEnsureHousePlotConfiguration(island, snapshot);
                TryAutoAssignHousePlotMapSlot(island, snapshot);
                TryDetectMixedRegionPlacement(island, snapshot);
            }

            PushLiveStatusToBindings();
        }
        else if (!string.IsNullOrEmpty(e.UniqueName))
        {
            if (IsIslandBuildingUniqueName(e.UniqueName))
            {
                _sessionBuildingCounts.AddOrUpdate(e.UniqueName, 1, (_, c) => c + 1);
                if (e.HasPremium) _sessionHasPremium = true;
                Log.Information("[IslandController] Detected island building: {UniqueName}", e.UniqueName);
            }
            else
            {
                Log.Information("[IslandController] Skipped non-island building: {UniqueName}", e.UniqueName);
            }

            if (TryResolveIslandPlotType(e.UniqueName, out var anchorPlotType) && e.Position.HasValue)
            {
                var island = FindCurrentIsland();
                if (island != null)
                {
                    var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
                    // Large-footprint plots (houses, workshops) must never resolve to the small S1/S2 slots, so
                    // they require a large slot. Farm/herb/pasture plots can occupy ANY of the 18 slots
                    // (including the S1/S2 small slots), so they match the nearest of all — never restrict them
                    // to small-only, which collapsed every such plot onto slots 17/18.
                    bool? requireLarge = anchorPlotType is PlotType.Farm or PlotType.HerbGarden or PlotType.Pasture
                        ? null
                        : true;
                    var slotIndex = layout?.WorldToNearestSlot(e.Position.Value.X, e.Position.Value.Y, requireLarge: requireLarge);
                    if (slotIndex.HasValue)
                    {
                        var alreadyOwned = island.Plots.Any(p =>
                            p.PlotType == anchorPlotType && p.MapSlotIndex == slotIndex.Value);
                        if (!alreadyOwned)
                        {
                            var matchedPlot = island.Plots.FirstOrDefault(p =>
                                p.PlotType == anchorPlotType && !p.MapSlotIndex.HasValue);
                            if (matchedPlot != null)
                            {
                                matchedPlot.MapSlotIndex = slotIndex.Value;
                                island.UpdateModificationDate();
                                RequestSaveToFile();
                                Log.Information("[IslandController] Auto-assigned slot {Slot} to {Type} plot via world pos ({X},{Y})",
                                    slotIndex.Value, anchorPlotType, e.Position.Value.X, e.Position.Value.Y);
                                RefreshIslandStatusAsync(island);
                            }
                        }
                    }
                }
            }

            // Farmable plant placement (position cache, per-tile timer seed, consumed-seed booking).
            TryHandleFarmablePlantPlacement(e);
        }
    }

    // Caches a freshly placed farmable plant's position, seeds its per-tile timer and books the seed as
    // consumed (once per stable tile). Split out of HandleNewBuilding for readability.
    private void TryHandleFarmablePlantPlacement(NewBuildingEvent e)
    {
        if (!IsFarmablePlant(e.UniqueName)) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        // Cache plant position so collect requests / FarmableObjectInfo can resolve this plot card.
        // Code 45 param 20 is a server-now timestamp, not planted-at, so no timer is seeded here; the
        // timer is seeded accurately by FarmableObjectInfo (code 201), which carries elapsed time.
        if (e.Position.HasValue)
            _farmablePositions[e.ObjectId] = (e.Position.Value.X, e.Position.Value.Y);

        // Position-based crop/animal type assignment — resolves the correct plot when
        // multiple plots of the same type exist (e.g. 2 herb gardens on one island).
        if (e.Position.HasValue)
        {
            var info = PlotTypeExtensions.TryResolveFarmablePlotInfo(e.UniqueName);
            if (info != null && !string.IsNullOrWhiteSpace(info.ConfigKey))
                TryAutoApplyFarmableConfigByPosition(island, info, e.Position.Value.X, e.Position.Value.Y);
        }

        // PlantedAt from key 8 matches packet timestamp when player places item (<1s delta).
        // Zone-in broadcasts carry old PlantedAt (hours ago) — 30s guard filters them out.
        var isJustPlanted = e.PlantedAt.HasValue
            && (DateTime.UtcNow - e.PlantedAt.Value).TotalSeconds <= 30;

        if (!isJustPlanted) return;

        // Stamp this plot's timer at the observed plant time. Freshly-planted plots only ever send the
        // array-form FarmableObjectInfo (no scalar elapsed to derive PlantedAt from), so the plant action
        // is the reliable PlantedAt source for them; pre-existing plots are covered by scalar 201.
        // Idempotent, so re-broadcasts within the 30s window are harmless.
        var plantedPlot = ResolveFarmablePlotByObjectId(island, e.ObjectId);
        if (plantedPlot != null && e.PlantedAt.HasValue
            && UpdatePlotTile(plantedPlot, e.ObjectId, e.PlantedAt.Value))
        {
            island.UpdateModificationDate();
            RequestSaveToFile();
            RefreshIslandStatusAsync(island);
        }

        // Seed consumption is the replant of a tile harvested earlier this session. The just-planted check
        // above passes on EVERY zone-in re-broadcast (param-8 = server-now there too), so it cannot tell a
        // real replant from a pre-existing plant streaming in on entry — both look "just planted". Gate on a
        // prior collect of this exact tile instead: only a position freed by HandleFarmableCollect counts when
        // it is replanted, so the zone-in burst of pre-existing plants is never booked as consumed.
        if (!e.Position.HasValue) return;
        var replantKey = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{island.Id}|{e.Position.Value.X:0.##}|{e.Position.Value.Y:0.##}");
        bool isReplantAfterCollect;
        lock (_consumedTilesLock) isReplantAfterCollect = _collectedTilesAwaitingReplant.Remove(replantKey);
        if (!isReplantAfterCollect) return;

        var item = ItemController.GetItemByUniqueName(e.UniqueName);
        if (item == null || item.Index <= 0) return;

        // Bucket consumed by the same classifier used everywhere else, so a crop seed (carrot/pumpkin)
        // counts under Farm and a herb seed under HerbGarden — not the old "_SEED => HerbGarden" rule.
        var plotType = PlotTypeExtensions.TryResolveFarmablePlotInfo(e.UniqueName)?.PlotType
            ?? (IsFarmableSeed(e.UniqueName) ? PlotType.HerbGarden : PlotType.Pasture);
        island.AddConsumed(item.Index, 1, plotType);
        island.UpdateModificationDate();
        RequestSaveToFile();
        _yieldTracker.PushUpdate(island);
        Log.Information("[IslandController] Recorded replanted seed as consumed: island={Island}, item={Item}, plotType={PlotType}",
            island.Name, e.UniqueName, plotType);
    }

    // Removes any snapshot that shares this laborer's name but has a different ObjectId — the
    // residue of a respawn (tier upgrade / rebuild). Laborer names are unique per island, so a
    // same-name/different-id snapshot is always the same laborer's stale instance.
    private void EvictStaleDuplicateSnapshots(LaborerSnapshot keep)
    {
        var name = LaborerConfigHelper.NormalizeLaborerFullName(keep.FullName);
        if (string.IsNullOrWhiteSpace(name)) return;

        var stale = _snapshots.Values
            .Where(s => s.ObjectId != keep.ObjectId
                && string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(s.FullName), name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var s in stale)
        {
            _snapshots.TryRemove(s.ObjectId, out _);
            lock (_snapshotOrderLock)
                _snapshotsByOrder.Remove(s);
            Log.Information("[IslandController] Evicted stale duplicate laborer snapshot: name={Name}, oldObjectId={Old}, newObjectId={New}",
                keep.FullName, s.ObjectId, keep.ObjectId);
        }
    }

    private static bool IsFarmablePlant(string uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName)) return false;
        var upper = uniqueName.ToUpperInvariant();
        return upper.Contains("_FARM_") || upper.Contains("_HERB_") || upper.Contains("_HERBGARDEN_")
            || upper.Contains("_ANIMAL_") || upper.Contains("_BABY_");
    }

    private static bool IsFarmableSeed(string uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName)) return false;
        var upper = uniqueName.ToUpperInvariant();
        return upper.Contains("_SEED") || upper.Contains("_HERB_") || upper.Contains("_HERBGARDEN_");
    }

    // Single source of truth for uniqueName -> PlotType. Handles BOTH name shapes seen on the wire:
    // bare anchor names (PLAYERHOUSE, FARMHOUSE, HUNTERLODGE) and section-delimited names
    // (T7_LABOURER_HUNTER, ISLAND_..._FARM_...). House is matched FIRST so a laborer building like
    // "T7_LABOURER_HUNTER" resolves to House, not HunterLodge. Keyword sets are the union of the two
    // legacy mappers; greedy bare tokens (FARM, MOUNT) are deliberately kept delimited to avoid
    // false matches (FARMING_MERCHANT, MOUNTAIN_..._BANK).
    private static bool TryResolveIslandPlotType(string uniqueName, out PlotType plotType)
    {
        plotType = default;
        if (string.IsNullOrEmpty(uniqueName)) return false;
        var u = uniqueName.ToUpperInvariant();

        if (u.Contains("LABOURER") || u.Contains("PLAYERHOUSE") || u.Contains("PLAYER_HOUSE") || u.Contains("_HOUSE_")) { plotType = PlotType.House; return true; }

        // Farmable plant names (T*_FARM_*_SEED / *_BABY / *_GROWN) carry the crop/animal in the name, so a
        // herb seed like T6_FARM_FOXGLOVE_SEED must classify by FOXGLOVE (HerbGarden) — not by the literal
        // "_FARM_" token below, which would wrongly bucket every herb/animal seed as a farm crop. Delegate to
        // the single name→type table so plot typing, slot assignment and yield bucketing agree.
        var (farmableType, _) = FarmablePlotData.ClassifyFarmableByUniqueName(uniqueName);
        if (farmableType.HasValue) { plotType = farmableType.Value; return true; }

        if (u.Contains("FARMHOUSE") || u.Contains("_FARM_") || u.Contains("_CROPS_")) { plotType = PlotType.Farm; return true; }
        if (u.Contains("HERBGARDEN") || u.Contains("_HERB_") || u.Contains("_HERBGARDEN_")) { plotType = PlotType.HerbGarden; return true; }
        if (u.Contains("PASTURE") || u.Contains("_ANIMAL_")) { plotType = PlotType.Pasture; return true; }
        if (u.Contains("KENNEL") || u.Contains("_BABY_")) { plotType = PlotType.Kennel; return true; }
        if (u.Contains("SADDLER") || u.Contains("_MOUNT_")) { plotType = PlotType.Saddler; return true; }
        if (u.Contains("BUTCHER")) { plotType = PlotType.Butcher; return true; }
        if (u.Contains("SMELTER")) { plotType = PlotType.Smelter; return true; }
        if (u.Contains("TANNER")) { plotType = PlotType.Tanner; return true; }
        if (u.Contains("LUMBERMILL") || u.Contains("LUMBER_MILL") || u.Contains("_SAWMILL_")) { plotType = PlotType.Lumbermill; return true; }
        if (u.Contains("STONEMASON") || u.Contains("STONE_MASON")) { plotType = PlotType.Stonemason; return true; }
        if (u.Contains("COOK")) { plotType = PlotType.Cook; return true; }
        if (u.Contains("ALCHEMYLAB") || u.Contains("ALCHEMY_LAB") || u.Contains("ALCHLAB") || u.Contains("_ALCHEMY_")) { plotType = PlotType.AlchemyLab; return true; }
        if (u.Contains("HUNTERLODGE") || u.Contains("HUNTER_LODGE") || u.Contains("HUNTER")) { plotType = PlotType.HunterLodge; return true; }
        if (u.Contains("WARRIORGUILD") || u.Contains("WARRIOR_GUILD") || u.Contains("WARRIOR")) { plotType = PlotType.WarriorGuild; return true; }
        if (u.Contains("MAGETOWER") || u.Contains("MAGE_TOWER") || u.Contains("MAGE")) { plotType = PlotType.MageTower; return true; }
        if (u.Contains("WEAVER")) { plotType = PlotType.Weaver; return true; }
        if (u.Contains("TOOLMAKER") || u.Contains("TOOL_MAKER")) { plotType = PlotType.Toolmaker; return true; }
        if (u.Contains("MILL")) { plotType = PlotType.Mill; return true; }
        if (u.Contains("REPAIRSHOP") || u.Contains("REPAIR")) { plotType = PlotType.RepairStation; return true; }
        return false;
    }
}
