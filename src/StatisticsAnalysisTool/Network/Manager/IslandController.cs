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

public partial class IslandController
{
    private static readonly PlotType[] FarmPlotTypes =
        [PlotType.Farm, PlotType.HerbGarden, PlotType.Pasture, PlotType.Kennel];

    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IslandYieldTracker _yieldTracker;
    private readonly IslandWebhookService _webhookService = new();
    private TrackingController _trackingController;
    private readonly List<Island.Island> _islands = [];
    private readonly object _islandsLock = new();

    private readonly ConcurrentDictionary<long, LaborerSnapshot> _snapshots = new();
    private readonly List<LaborerSnapshot> _snapshotsByOrder = new();
    private readonly object _snapshotOrderLock = new();
    private long _detectionCounter;
    private bool _collectionReadyWebhookSentThisSession;
    private System.Windows.Threading.DispatcherTimer _countdownTimer;
    private System.Windows.Threading.DispatcherTimer _transitionTimer;
    private volatile System.Threading.Timer _pushDebounceTimer;
    private const int PushDebounceMs = 200;

    // Farmable state-change dedup keyed by ObjectId.
    private readonly ConcurrentDictionary<long, string> _farmableSignatures = new();

    // 5-min snapshot cache so UI stays populated briefly after loot collection
    private readonly object _lastSnapshotLock = new();
    private string _lastSnapshotIslandName = string.Empty;
    private DateTime _lastSnapshotUtc = DateTime.MinValue;
    private List<LaborerSnapshot> _lastSnapshotList = new();

    public event Action LaborerSnapshotsChanged;

    // the "Add Island" dialog can prefill city even after the player has left the island.
    private IslandSessionSuggestion _lastIslandSuggestion;

    public IslandController(MainWindowViewModel mainWindowViewModel)
    {
        _mainWindowViewModel = mainWindowViewModel;
        _yieldTracker = new IslandYieldTracker(mainWindowViewModel, SaveToFileAsync);
    }

    public void SetTrackingController(TrackingController trackingController)
    {
        _trackingController = trackingController;
    }

    public void StartCountdownTimer()
    {
        if (_countdownTimer != null) return;
        _countdownTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _countdownTimer.Tick += (_, _) =>
        {
            ExecutePushAllIslandsStatus();
            LaborerSnapshotsChanged?.Invoke();
        };
        _countdownTimer.Start();
        ScheduleNextPlotTransition();
    }

    public void StopCountdownTimer()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
        _transitionTimer?.Stop();
        _transitionTimer = null;
        _pushDebounceTimer?.Dispose();
        _pushDebounceTimer = null;
        _yieldTracker.StopFlushTimer();
    }

    private void ScheduleNextPlotTransition()
    {
        _transitionTimer?.Stop();
        _transitionTimer = null;

        DateTime? earliest = null;
        lock (_islandsLock)
        {
            foreach (var island in _islands)
            {
                if (island.Plots == null) continue;
                foreach (var plot in island.Plots)
                {
                    var planted = plot.PlotPlantedAt;
                    if (!planted.HasValue) continue;
                    var hours = plot.PlotType.GetBaseCollectionHours(plot.Configuration);
                    if (hours <= 0) continue;
                    var ready = planted.Value.ToUniversalTime().AddHours(hours);
                    if (ready <= DateTime.UtcNow) continue;
                    if (!earliest.HasValue || ready < earliest.Value)
                        earliest = ready;
                }
            }
        }

        if (!earliest.HasValue) return;
        var delay = earliest.Value - DateTime.UtcNow;
        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);

        _transitionTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = delay
        };
        _transitionTimer.Tick += (_, _) =>
        {
            _transitionTimer?.Stop();
            _transitionTimer = null;
            ExecutePushAllIslandsStatus();
            LaborerSnapshotsChanged?.Invoke();
            ScheduleNextPlotTransition();
        };
        _transitionTimer.Start();
    }

    #region Session (per cluster visit)

    private readonly ConcurrentDictionary<string, int> _sessionBuildingCounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _sessionHasPremium;
    private string _sessionIslandName;
    private string _sessionOwner;
    private string _sessionWorldMapDataType;
    private string _sessionSourceClusterIndex;
    private readonly object _consumedTilesLock = new();
    // Tiles ("islandId|uniqueName|x|y") already booked as a consumed seed. Keyed by stable position, not
    // object id: re-entering an island re-broadcasts every existing plant with a NEW object id, so an
    // object-id dedup that reset on entry re-counted every plant on every visit. Position is stable across
    // re-entries, and this set is deliberately NOT cleared on island change/entry so an already-handled
    // island never re-books its existing plantings as freshly consumed. A collect evicts the tile's booking
    // (EvictConsumedTileBooking) so a same-run replant on that position re-counts its new seed.
    private readonly HashSet<string> _consumedPlantedTiles = [];
    // Last seen quantity per laborer-loot inventory object (NewLaborerItem, code 32). Yield is the
    // positive growth between broadcasts; the first sighting is the baseline. Reset on island change.
    private readonly ConcurrentDictionary<long, int> _lastItemQty = new();
    // Last seen quantity per laborer-journal stack (NewJournalItem, code 35). Empty journals rise =
    // collected; full journals fall = consumed. Reset on island change.
    private readonly ConcurrentDictionary<long, int> _lastJournalQty = new();
    // Timestamp (UTC ticks) of the last laborer collect REQUEST (op 257). Collected yield (code 32 / empty
    // journal rise) is only counted within LaborerCollectYieldWindow of it: verified against captures, real
    // collect growth lands 1-3s after the 257, while storage repaints/streaming/object-id reuse (incl. the
    // 999 cap sentinel) fire outside any collect and would otherwise inflate yield (~73% of raw deltas).
    private long _lastLaborerCollectTicks;
    private static readonly TimeSpan LaborerCollectYieldWindow = TimeSpan.FromSeconds(5);
    // Real collect growth lands in a tight band AROUND the 257 request — verified against captures, ~75%
    // of it arrives up to ~1s BEFORE the 257 is logged, not after. A forward-only window dropped that
    // growth (~30% under-count). So growth seen outside the forward window is buffered briefly and
    // committed retroactively when a 257 arrives within this look-back; growth with no nearby collect
    // (storage repaints / zone-in streaming) ages out of the buffer uncounted.
    private static readonly TimeSpan LaborerCollectLookback = TimeSpan.FromSeconds(3);
    private readonly record struct PendingYield(long Ticks, int ItemIndex, int Quantity);
    private readonly object _pendingYieldLock = new();
    private readonly List<PendingYield> _pendingYield = [];
    // Farmable plant ObjectId -> world position (from NewBuilding 45). Lets a collect request (op 73/74/76/77)
    // and FarmableObjectInfo (201) resolve the specific plot card via the layout's nearest slot, so timers are
    // set/cleared per plot instead of across every plot of the type (which caused the collect clear-storm).
    private readonly ConcurrentDictionary<long, (float X, float Y)> _farmablePositions = new();
    // Per-plot, per-tile planted time keyed by world-position (stable across the object-id churn). Drives the
    // per-slot dots on each card; runtime only (reset per island session). null value = collected/empty slot.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime?>> _plotTilePlanted = new();

    public void ClearSession()
    {
        // Commit any yield collected on the island we're leaving before the session state is reset.
        _yieldTracker.FlushNow();
        _snapshots.Clear();
        lock (_snapshotOrderLock)
            _snapshotsByOrder.Clear();
        _sessionBuildingCounts.Clear();
        _farmableSignatures.Clear();
        _sessionIslandName = null;
        _sessionOwner = null;
        _sessionWorldMapDataType = null;
        _sessionSourceClusterIndex = null;
        _sessionHasPremium = false;
        // Yield baselines reset per island session; the consumed-plant tile set deliberately persists so a
        // re-joined, already-handled island does not re-book its existing plantings as freshly consumed.
        _lastItemQty.Clear();
        _lastJournalQty.Clear();
        System.Threading.Volatile.Write(ref _lastLaborerCollectTicks, 0);
        lock (_pendingYieldLock) _pendingYield.Clear();
        _farmablePositions.Clear();
        _plotTilePlanted.Clear();
        _collectionReadyWebhookSentThisSession = false;
        Interlocked.Exchange(ref _detectionCounter, 0);
        lock (_lastSnapshotLock)
        {
            _lastSnapshotList.Clear();
            _lastSnapshotIslandName = string.Empty;
            _lastSnapshotUtc = DateTime.MinValue;
        }
        ExecutePushAllIslandsStatus();
    }

    public void HandleIslandClusterEntry(ClusterInfo cluster)
    {
        if (cluster.MapType != MapType.Island) return;
        _sessionIslandName = cluster.InstanceName;
        _sessionWorldMapDataType = cluster.WorldMapDataType;
        _sessionSourceClusterIndex = cluster.SourceClusterIndex;
        _sessionOwner = SettingsController.CurrentSettings.MainTrackingCharacterName
            ?? _trackingController?.EntityController?.LocalUserData?.Username;
        // Only yield baselines reset on entry; _consumedPlantedTiles persists so re-joining an
        // already-handled island does not re-count its existing plantings as consumed.
        _lastItemQty.Clear();
        _lastJournalQty.Clear();
        Log.Information("[IslandController] Entered island cluster: name={Name} wmd={Wmd} src={Src} owner={Owner}",
            _sessionIslandName, _sessionWorldMapDataType, _sessionSourceClusterIndex, _sessionOwner);

        var island = FindCurrentIsland();
        if (island != null)
        {
            island.LastVisited = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(_sessionSourceClusterIndex)
                && !string.Equals(island.SourceClusterIndex, _sessionSourceClusterIndex, StringComparison.OrdinalIgnoreCase))
            {
                island.SourceClusterIndex = _sessionSourceClusterIndex;
            }
            if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType)
                && !string.Equals(island.WorldMapDataType, _sessionWorldMapDataType, StringComparison.OrdinalIgnoreCase))
            {
                island.WorldMapDataType = _sessionWorldMapDataType;
            }
            island.UpdateModificationDate();
            _ = SaveToFileAsync();
        }
    }

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
                    // Large-footprint plots (houses, workshops) must never resolve to the small S1/S2 slots.
                    var requireLarge = anchorPlotType is not (PlotType.Farm or PlotType.HerbGarden or PlotType.Pasture);
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
                                _ = SaveToFileAsync();
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

        // Dedup by stable tile (island + plant + world position), NOT object id: each island re-entry
        // re-broadcasts existing plants with fresh object ids, so an object-id key re-counted every plant
        // on every visit. Position is constant across re-entries and the set persists for the app run, so
        // an already-handled island never re-books its plants. (Computed unconditionally so the tile is
        // marked known even for zone-in re-broadcasts of pre-existing plants.)
        var isNewPlanting = false;
        if (e.Position.HasValue)
        {
            var tileKey = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{island.Id}|{e.UniqueName}|{e.Position.Value.X:0.##}|{e.Position.Value.Y:0.##}");
            lock (_consumedTilesLock) isNewPlanting = _consumedPlantedTiles.Add(tileKey);
        }

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
            _ = SaveToFileAsync();
            RefreshIslandStatusAsync(island);
        }

        if (!isNewPlanting) return;

        var item = ItemController.GetItemByUniqueName(e.UniqueName);
        if (item == null || item.Index <= 0) return;

        // Bucket consumed by the same classifier used everywhere else, so a crop seed (carrot/pumpkin)
        // counts under Farm and a herb seed under HerbGarden — not the old "_SEED => HerbGarden" rule.
        var plotType = PlotTypeExtensions.TryResolveFarmablePlotInfo(e.UniqueName)?.PlotType
            ?? (IsFarmableSeed(e.UniqueName) ? PlotType.HerbGarden : PlotType.Pasture);
        island.AddConsumed(item.Index, 1, plotType);
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        _yieldTracker.PushUpdate(island);
        Log.Information("[IslandController] Recorded planted item as consumed: island={Island}, item={Item}, plotType={PlotType}",
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

    public void HandleLaborerObjectInfo(LaborerObjectInfoEvent e)
    {
        if (e.ObjectId < 0) return;
        if (!_snapshots.TryGetValue(e.ObjectId, out var snapshot))
        {
            // LaborerObjectInfo can arrive before NewBuilding on re-entry (new ObjectId after respawn).
            // Create a stub snapshot so names and job state are captured immediately.
            snapshot = _snapshots.GetOrAdd(e.ObjectId, id =>
            {
                var s = new LaborerSnapshot(id) { DetectionOrder = Interlocked.Increment(ref _detectionCounter) };
                lock (_snapshotOrderLock) _snapshotsByOrder.Add(s);
                return s;
            });
        }
        var wasOnJob = snapshot.IsOnJob;
        snapshot.UpdateFromLaborerObjectInfo(e);

        PushLiveStatusToBindings();

        var currentIsland = FindCurrentIsland();

        // Ensure tier/name updates from reconnect visits are reflected in config.
        // NewBuilding fires only on first detection in a session; LaborerObjectInfo fires every visit.
        if (currentIsland != null && snapshot.BuildingTier > 0 && !string.IsNullOrWhiteSpace(snapshot.LaborerType))
            TryEnsureHousePlotConfiguration(currentIsland, snapshot);

        if (e.IsOnJob && !wasOnJob && currentIsland != null)
        {
            currentIsland.TotalLaborersSent++;
            currentIsland.UpdateModificationDate();
            _ = SaveToFileAsync();
            RefreshIslandStatusAsync(currentIsland);
        }

        TryAutoStartIslandTimerFromLaborer(snapshot);
        LaborerSnapshotsChanged?.Invoke();
    }

    private void TryAutoStartIslandTimerFromLaborer(LaborerSnapshot snapshot)
    {
        var prefs = _mainWindowViewModel?.IslandBindings?.Preferences;
        if (prefs == null || !prefs.AutoStartCycleOnIslandActivity) return;
        if (!snapshot.IsOnJob) return;

        // Ready-at = param 8 (same-session dispatch); on reconnect param 8 is absent, so ReadyAtUtc
        // falls back to JobStartTime + base cycle. Param 6/7 are food timestamps and never used here.
        DateTime? readyUtcNullable = snapshot.ReadyAtUtc;

        if (!readyUtcNullable.HasValue) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        var hasConfiguredHousePlots = island.Plots?.Any(p => p.PlotType == PlotType.House) == true;
        var houseMatchedOrAutofilled = TryEnsureHousePlotConfiguration(island, snapshot);
        if (hasConfiguredHousePlots && !houseMatchedOrAutofilled)
        {
            Log.Debug("[IslandController] Auto-start skipped: no matching house config for laborer {Laborer}", snapshot.FullName);
            return;
        }

        var readyUtc = readyUtcNullable.Value.ToUniversalTime();
        var cycleStartUtc = readyUtc.AddHours(-IslandConstants.LaborerBaseCycleHours);

        var shouldUpdate = !island.LastPlantedAt.HasValue
            || island.LastPlantedAt.Value.AddHours(IslandConstants.LaborerBaseCycleHours) <= DateTime.UtcNow;
        if (!shouldUpdate) return;

        if (IsIslandInRoyalCity(island))
        {
            Log.Debug("[IslandController] Skipped auto-start for island in royal city (laborer): {Island}", island.Name);
            return;
        }

        island.LastPlantedAt = cycleStartUtc;
        island.LastHandledAt = DateTime.UtcNow;
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        RefreshIslandStatusAsync(island);
        TryAutoPrefillPayout(island);
        Log.Information("[IslandController] Auto-started island timer from laborer cycle: island={Island}, laborer={Laborer}, ready={ReadyUtc:O}, cycleStart={CycleStartUtc:O}",
            island.Name, snapshot.FullName, readyUtc, cycleStartUtc);
    }

    public void HandleLaborerObjectJobInfo(LaborerObjectJobInfoEvent e)
    {
        if (e.ObjectId < 0) return;
        if (!_snapshots.TryGetValue(e.ObjectId, out var snapshot)) return;
        var wasOnJob = snapshot.IsOnJob;
        var prevJobStartTime = snapshot.JobStartTime;
        snapshot.UpdateFromJobInfo(e);

        // Yield is recorded from NewLaborerItem (code 32) quantity growth — see HandleLaborerItemDetail.

        if (e.JournalItemId > 0)
        {
            var journalName = ItemController.GetItemUniqueNameByIndex(e.JournalItemId);
            snapshot.TrySetTypeFromJournal(journalName);
        }

        // Dispatch detection — two paths:
        // 1. Transition observed this session: was home (HasBeenSeenAsHome), now away on job.
        // 2. Re-dispatch across visits: job start time changed since last observation.
        var isNewDispatch = e.JobStartTime.HasValue && e.JournalItemId > 0
                            && (
                                (snapshot.HasBeenSeenAsHome && !wasOnJob && snapshot.IsOnJob)
                                || (prevJobStartTime != null && e.JobStartTime != prevJobStartTime)
                            );

        Log.Debug("[IslandController] LaborerJobInfo: objectId={ObjId}, journalId={JournalId}, jobStart={JobStart}, prevJobStart={PrevJobStart}, awayOnJob={AwayOnJob}, isNewDispatch={IsNewDispatch}",
            e.ObjectId, e.JournalItemId, e.JobStartTime, prevJobStartTime, e.IsAwayOnJob, isNewDispatch);

        // Consumed/collected journals are tracked from the actual NewJournalItem (code 35) stack deltas
        // in HandleLaborerJournalDetail — NOT booked here per dispatch (that under-counted to one each).

        if (e.IsAwayOnJob)
            TryAutoStartIslandTimerFromLaborer(snapshot);

        UpdateLastSnapshotCache();
        PushLiveStatusToBindings();
        LaborerSnapshotsChanged?.Invoke();

        if (isNewDispatch)
            TryTriggerCollectionReadyWebhook();
    }

    private void TryTriggerCollectionReadyWebhook()
    {
        if (_collectionReadyWebhookSentThisSession) return;
        var prefs = _mainWindowViewModel?.IslandBindings?.Preferences;
        if (prefs?.AutoNotifyOwnerWhenAllDone != true) return;

        var snapshots = _snapshots.Values.ToList();
        if (snapshots.Count == 0) return;
        // The island we're on must itself be fully re-dispatched (all laborers away on a fresh job).
        if (!snapshots.All(s => s.IsOnJob)) return;

        var islandOwner = FindCurrentIsland()?.Owner?.Trim() ?? _sessionOwner;
        if (string.IsNullOrWhiteSpace(islandOwner)) return;

        // Fire only when EVERY island of this owner is done this cycle — i.e. none still NeedsVisit
        // (ready/overdue, or never planted). Royal-city islands have no laborer cycle and are excluded.
        List<Island.Island> ownerIslands;
        lock (_islandsLock)
            ownerIslands = _islands
                .Where(i => string.Equals(i.Owner?.Trim(), islandOwner, StringComparison.OrdinalIgnoreCase)
                            && !IsIslandInRoyalCity(i))
                .ToList();

        if (ownerIslands.Count == 0) return;
        var pending = ownerIslands.Where(i => i.NeedsVisit).ToList();
        if (pending.Count > 0)
        {
            Log.Debug("[IslandController] Owner webhook held: {Owner} still has {Count} island(s) to collect: {Names}",
                islandOwner, pending.Count, string.Join(", ", pending.Select(i => i.Name)));
            return;
        }

        _collectionReadyWebhookSentThisSession = true;
        Log.Information("[IslandController] All {Count} islands done for owner {Owner} — triggering collection-ready webhook.",
            ownerIslands.Count, islandOwner);
        _ = TrySendCollectionReadyWebhookAsync(islandOwner);
    }

    private async Task TrySendCollectionReadyWebhookAsync(string ownerName)
    {
        if (string.IsNullOrEmpty(ownerName)) return;

        var profile = GetOwnerProfile(ownerName);
        if (string.IsNullOrWhiteSpace(profile?.WebhookUrl)) return;

        var outcome = await _webhookService.PromptAsync().ConfigureAwait(false);
        if (!outcome.Send) return;

        if (outcome.SaveNote)
            ApplyWebhookNote(ownerName, outcome.Notes, outcome.Emv);

        var message = _mainWindowViewModel?.IslandBindings?.BuildDiscordMessage(ownerName);
        if (string.IsNullOrEmpty(message)) return;

        Log.Information("[IslandController] Sending collection-ready webhook: owner={Owner}", ownerName);
        await _webhookService.SendAsync(profile.WebhookUrl, message).ConfigureAwait(false);
    }

    // Persist the daily notes / EMV captured by the "Save and send" path onto the owner's cycle history.
    private void ApplyWebhookNote(string ownerName, string notes, decimal? emv)
    {
        if (string.IsNullOrWhiteSpace(notes) && !emv.HasValue) return;

        lock (_ownerProfilesLock)
        {
            var profile = GetOwnerProfile(ownerName);
            if (profile != null)
            {
                var today = DateTime.Today;
                var record = profile.CycleHistory?
                    .FirstOrDefault(c => c.Date.Date == today && c.RecordType == CycleRecordType.Islands);

                if (record != null && !string.IsNullOrWhiteSpace(notes))
                {
                    record.Notes = string.IsNullOrWhiteSpace(record.Notes) || string.Equals(record.Notes.Trim(), AutoPrefillNotesMarker, StringComparison.OrdinalIgnoreCase)
                        ? notes
                        : $"{record.Notes}; {notes}";
                }

                if (emv.HasValue)
                {
                    profile.CycleHistory.Add(new OwnerCycleRecord
                    {
                        Date = today,
                        RecordType = CycleRecordType.Other,
                        EarnedAmount = emv.Value,
                        Notes = "EMV"
                    });
                }
            }
        }
        _ = SaveOwnerProfilesAsync();
        _mainWindowViewModel?.IslandBindings?.RefreshOwnerOverview();
    }

    public async Task<bool> SendWebhookManualAsync(string ownerName)
    {
        if (string.IsNullOrEmpty(ownerName)) return false;
        var profile = GetOwnerProfile(ownerName);
        if (string.IsNullOrWhiteSpace(profile?.WebhookUrl)) return false;

        var message = _mainWindowViewModel?.IslandBindings?.BuildDiscordMessage();
        if (string.IsNullOrEmpty(message)) return false;

        Log.Information("[IslandController] Manual webhook send: owner={Owner}", ownerName);
        return await _webhookService.SendAsync(profile.WebhookUrl, message).ConfigureAwait(false);
    }

    // Resolve the specific farm/herb/pasture plot card a farmable ObjectId belongs to, via its cached world
    // position and the island layout's nearest small slot. Returns null when the position is unknown or no
    // matching plot owns that slot — callers then fall back to the per-type behaviour (no regression).
    private IslandPlot ResolveFarmablePlotByObjectId(Island.Island island, long objectId)
    {
        if (island?.Plots == null || objectId < 0) return null;
        if (!_farmablePositions.TryGetValue(objectId, out var pos)) return null;

        var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
        var slot = layout?.WorldToNearestSlot(pos.X, pos.Y, requireLarge: false);
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

        // Collecting frees the tile for a replant, so drop its consumed-seed booking: a same-run replant on
        // this position then re-counts its new seed as consumed. (The booking is otherwise kept for the whole
        // app run so re-entering an already-handled island never re-counts its existing plants.)
        EvictConsumedTileBooking(island, plotObjectId);

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

    // Removes the consumed-seed booking for the tile at a farmable object's world position (any crop), so a
    // replant on the same tile within this app run re-counts its seed. No-op when the position is unknown.
    private void EvictConsumedTileBooking(Island.Island island, long objectId)
    {
        if (island == null) return;
        if (!_farmablePositions.TryGetValue(objectId, out var pos)) return;

        var prefix = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{island.Id}|");
        var suffix = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"|{pos.X:0.##}|{pos.Y:0.##}");
        lock (_consumedTilesLock)
        {
            var stale = _consumedPlantedTiles
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.EndsWith(suffix, StringComparison.Ordinal))
                .ToList();
            foreach (var k in stale)
                _consumedPlantedTiles.Remove(k);
        }
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

    public IReadOnlyList<LaborerSnapshot> GetCurrentSnapshots()
    {
        List<LaborerSnapshot> current;
        lock (_snapshotOrderLock)
            current = _snapshotsByOrder.Count > 0 ? new List<LaborerSnapshot>(_snapshotsByOrder) : null;

        if (current != null) return current;

        var island = FindCurrentIsland();
        if (island == null) return Array.Empty<LaborerSnapshot>();

        lock (_lastSnapshotLock)
        {
            if (!string.IsNullOrWhiteSpace(_lastSnapshotIslandName)
                && string.Equals(_lastSnapshotIslandName, island.Name?.Trim(), StringComparison.OrdinalIgnoreCase)
                && (DateTime.UtcNow - _lastSnapshotUtc) <= TimeSpan.FromMinutes(5)
                && _lastSnapshotList.Count > 0)
            {
                return new List<LaborerSnapshot>(_lastSnapshotList);
            }
        }

        return Array.Empty<LaborerSnapshot>();
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

    public void ClearIslandYield(Guid islandId)
    {
        Island.Island island;
        lock (_islandsLock)
            island = _islands.FirstOrDefault(i => i.Id == islandId);
        if (island == null) return;

        island.ClearYield();
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        _yieldTracker.PushUpdate(island);
    }

    public void ClearAllYield(IEnumerable<Guid> islandIds)
    {
        List<Island.Island> targets;
        lock (_islandsLock)
            targets = _islands.Where(i => islandIds.Contains(i.Id)).ToList();
        if (targets.Count == 0) return;

        foreach (var island in targets)
        {
            island.ClearYield();
            island.UpdateModificationDate();
            _yieldTracker.PushUpdate(island);
        }
        _ = SaveToFileAsync();
    }

    // NewLaborerItem (code 32) broadcasts a laborer-loot inventory object's CURRENT quantity, re-sent
    // as that stack grows while collecting. Yield = the positive growth (delta) of each object's
    // quantity since first seen this island visit. The first sighting is the pre-collection baseline
    // (no yield), so pre-existing inventory and merely viewing a laborer never count — only the
    // increase from an actual collect does. (The bare NewSimpleItem "collected" marker / code 27 used
    // previously is never delivered by the live event pipeline; this delta reproduces it exactly.)
    private static readonly string[] LaborerResourceTokens =
    {
        "_PLANKS", "_METALBAR", "_LEATHER", "_CLOTH", "_STONEBLOCK",
        "_WOOD", "_ORE", "_HIDE", "_FIBER", "_ROCK"
    };

    // True only for the resource families a laborer produces (raw + refined). Farm/herb/pasture products
    // also land in island storage and broadcast code 32, but they are tracked precisely by the harvest-
    // response path (HerbGarden/Pasture). Without this filter every farm item was double-recorded under
    // PlotType.House (e.g. T8_YARROW counted twice). Journals go through HandleLaborerJournalDetail.
    private static bool IsLaborerLootResource(string uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName)) return false;
        var u = uniqueName.ToUpperInvariant();
        if (u.Contains("FARM") || u.Contains("SEED")) return false;
        foreach (var token in LaborerResourceTokens)
            if (u.Contains(token)) return true;
        return false;
    }

    // Open the collect window: a laborer collect REQUEST (op 257) just fired, so the storage-stack growth
    // that follows over the next few seconds is real collected loot. Called from LaborerCollectRequestHandler.
    public void NotifyLaborerCollect(long laborerObjectId)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        System.Threading.Volatile.Write(ref _lastLaborerCollectTicks, nowTicks);
        FlushPendingYield(nowTicks);
        Log.Debug("[IslandController] Laborer collect request: objectId={ObjectId} — yield window opened", laborerObjectId);
    }

    // Growth seen outside the forward window is held here until a 257 confirms it (look-back). Trims stale
    // entries on each add so an idle period (no collect) can't let the buffer grow unbounded.
    private void BufferPendingYield(int itemIndex, int quantity)
    {
        if (quantity <= 0) return;
        var nowTicks = DateTime.UtcNow.Ticks;
        var cutoff = nowTicks - LaborerCollectLookback.Ticks;
        lock (_pendingYieldLock)
        {
            _pendingYield.RemoveAll(p => p.Ticks < cutoff);
            _pendingYield.Add(new PendingYield(nowTicks, itemIndex, quantity));
        }
    }

    // A 257 just fired — commit buffered growth from the look-back window (real loot that streamed in just
    // before the request) and drop the rest (uncorrelated repaints/streaming).
    private void FlushPendingYield(long collectTicks)
    {
        var cutoff = collectTicks - LaborerCollectLookback.Ticks;
        List<PendingYield> toCommit;
        lock (_pendingYieldLock)
        {
            toCommit = _pendingYield.FindAll(p => p.Ticks >= cutoff);
            _pendingYield.Clear();
        }

        foreach (var pending in toCommit)
            RecordCollectedYield(pending.ItemIndex, pending.Quantity);
    }

    // Book collected laborer yield (resource or empty journal) against the current island.
    private void RecordCollectedYield(int itemIndex, int quantity)
    {
        if (quantity <= 0) return;
        var island = FindCurrentIsland();
        if (island == null) return;

        island.AddYield(itemIndex, quantity, PlotType.House);
        island.TotalLootCollected += quantity;
        island.UpdateModificationDate();
        // Collecting fires this many times per second as each stack grows. Debounce the file save and
        // UI push so we don't flood the disk and dispatcher (which was starving the yield/card refresh).
        _yieldTracker.Schedule(island);

        Log.Information("[IslandController] Recorded collected laborer yield: island={Island}, itemId={ItemId}, qty={Qty}",
            island.Name, itemIndex, quantity);
    }

    // True while within LaborerCollectYieldWindow of the last collect request. Storage stacks (code 32/35)
    // are repainted/streamed/object-id-reused constantly; only growth inside this window is a real collect.
    private bool InLaborerCollectWindow()
    {
        var last = System.Threading.Volatile.Read(ref _lastLaborerCollectTicks);
        if (last == 0) return false;
        return DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) <= LaborerCollectYieldWindow;
    }

    public void HandleLaborerItemDetail(DiscoveredItem item)
    {
        if (item == null || item.ObjectId < 0 || item.ItemIndex <= 0 || item.Quantity <= 0) return;

        // Only laborer-produced resources count here — farm products are handled by the harvest path.
        if (!IsLaborerLootResource(ItemController.GetItemUniqueNameByIndex(item.ItemIndex))) return;

        // Yield = positive growth of a PERSISTENT island-storage stack only. Opening a laborer spawns
        // short-lived preview objects (new high object ids, destroyed by a code-27 on collect); those
        // appear exactly once, so a baseline-only rule never counts them — which is what keeps merely
        // viewing a laborer from inflating yield. Real storage stacks carry an entry-load baseline and
        // grow as loot is deposited, so only their growth is counted.
        var hadPrev = _lastItemQty.TryGetValue(item.ObjectId, out var prevQty);
        _lastItemQty[item.ObjectId] = item.Quantity;
        if (!hadPrev) return; // first sighting — baseline only (covers preview objects and pre-existing stock)

        var delta = item.Quantity - prevQty;
        if (delta <= 0) return; // no growth (or a stack rollover) — nothing collected

        // Count growth correlated with a real collect request. Inside the forward window book it now;
        // otherwise hold it in the look-back buffer — a 257 arriving within LaborerCollectLookback will
        // commit it (most collect growth lands just BEFORE the request). Uncorrelated growth (storage
        // repaint / zone-in stream / object-id reuse) ages out of the buffer uncounted.
        if (InLaborerCollectWindow())
            RecordCollectedYield(item.ItemIndex, delta);
        else
            BufferPendingYield(item.ItemIndex, delta);
    }

    // NewJournalItem (code 35) broadcasts a laborer-journal stack's CURRENT quantity. EMPTY journals
    // (…_JOURNAL_…_EMPTY) rise as laborers hand them back = collected; FULL journals (…_FULL) fall as
    // they are fed back in as fame fuel = consumed. Same baseline rule as resources (see above).
    public void HandleLaborerJournalDetail(DiscoveredItem item)
    {
        if (item == null || item.ObjectId < 0 || item.ItemIndex <= 0 || item.Quantity < 0) return;

        var name = ItemController.GetItemUniqueNameByIndex(item.ItemIndex);
        if (string.IsNullOrEmpty(name) || name.IndexOf("JOURNAL", StringComparison.OrdinalIgnoreCase) < 0) return;
        var isEmpty = name.EndsWith("_EMPTY", StringComparison.OrdinalIgnoreCase);
        var isFull = name.EndsWith("_FULL", StringComparison.OrdinalIgnoreCase);
        if (!isEmpty && !isFull) return;

        var hadPrev = _lastJournalQty.TryGetValue(item.ObjectId, out var prevQty);
        _lastJournalQty[item.ObjectId] = item.Quantity;

        var island = FindCurrentIsland();
        if (island == null) return;

        if (isEmpty)
        {
            // Baseline-only growth, same as resources: only a persistent stack's increase counts, so
            // preview/temp journal objects (seen once) never inflate the collected total.
            if (!hadPrev) return;
            var gained = item.Quantity - prevQty;
            if (gained <= 0) return;

            // Empty journals rise when laborers hand them back on collect. Same bidirectional correlation
            // as resources: book inside the forward window, otherwise hold for a 257 look-back commit so
            // growth arriving just before the request isn't dropped.
            if (InLaborerCollectWindow())
                RecordCollectedYield(item.ItemIndex, gained);
            else
                BufferPendingYield(item.ItemIndex, gained);

            return;
        }

        // full journal — consumed as it is spent
        if (!hadPrev) return; // need a baseline before a drop can be measured
        var spent = prevQty - item.Quantity;
        if (spent <= 0) return;

        island.AddConsumed(item.ItemIndex, spent, PlotType.House);
        island.UpdateModificationDate();
        _yieldTracker.Schedule(island);
        Log.Information("[IslandController] Recorded consumed journal: island={Island}, itemId={ItemId}, qty={Qty}, objectId={ObjectId}",
            island.Name, item.ItemIndex, spent, item.ObjectId);
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

    private void UpdateLastSnapshotCache()
    {
        var island = FindCurrentIsland();
        if (island == null) return;

        lock (_lastSnapshotLock)
        {
            _lastSnapshotIslandName = island.Name?.Trim() ?? string.Empty;
            _lastSnapshotUtc = DateTime.UtcNow;
            lock (_snapshotOrderLock)
                _lastSnapshotList = new List<LaborerSnapshot>(_snapshotsByOrder);
        }
    }

    public void AutoSelectCurrentIsland()
    {
        var name = ClusterController.CurrentCluster?.InstanceName?.Trim();

        Island.Island match;
        lock (_islandsLock)
            match = FindCurrentIslandNoLock(name);

        if (match == null) return;

        // Backfill SourceClusterIndex and WorldMapDataType so future visits resolve via step 2.
        if (TryBackfillClusterIdentifiers(match))
        {
            match.UpdateModificationDate();
            _ = SaveToFileAsync();
            Log.Information("[IslandController] Backfilled cluster identifiers on '{Name}' after auto-select.", match.Name);
        }

        var islandId = match.Id;
        var islandName = match.Name;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var bindings = _mainWindowViewModel?.IslandBindings;
            if (bindings == null) return;

            // Resolve the entry ON the UI thread: a binding rebuild can replace the entry instance
            // between cluster entry and this tick. Selecting a captured (now-orphaned) instance leaves
            // the list highlighting the wrong row — or none — so look it up against the live collection.
            var entry = bindings.Islands?.FirstOrDefault(e => e.IslandId == islandId);
            if (entry == null) return;

            bindings.SelectedIsland = entry;
            Log.Information("[IslandController] Auto-selected island '{Name}' on cluster entry.", islandName);
        });
    }

    public void OnIslandManuallySelected(Guid islandId)
    {
        Island.Island island;
        lock (_islandsLock)
        {
            island = _islands.FirstOrDefault(i => i.Id == islandId);
        }

        if (island == null) return;

        // Only backfill session identifiers when the selected island matches the current session island name.
        // Prevents stamping the wrong island's GUID onto an unrelated island the user clicks while visiting elsewhere.
        if (string.IsNullOrWhiteSpace(_sessionIslandName)
            || !string.Equals(island.Name, _sessionIslandName, StringComparison.OrdinalIgnoreCase))
            return;

        if (!TryBackfillClusterIdentifiers(island)) return;

        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        Log.Information("[IslandController] Backfilled cluster identifiers on '{Name}' after manual selection.", island.Name);
    }

    private Island.Island FindCurrentIsland()
    {
        var name = ClusterController.CurrentCluster?.InstanceName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        lock (_islandsLock)
        {
            return FindCurrentIslandNoLock(name);
        }
    }

    private bool TryBackfillClusterIdentifiers(Island.Island island)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(_sessionSourceClusterIndex)
            && !string.Equals(island.SourceClusterIndex, _sessionSourceClusterIndex, StringComparison.OrdinalIgnoreCase))
        {
            island.SourceClusterIndex = _sessionSourceClusterIndex;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType)
            && !string.Equals(island.WorldMapDataType, _sessionWorldMapDataType, StringComparison.OrdinalIgnoreCase))
        {
            island.WorldMapDataType = _sessionWorldMapDataType;
            changed = true;
        }
        return changed;
    }

    // Must be called with _islandsLock already held.
    private Island.Island FindCurrentIslandNoLock(string name = null)
    {
        name ??= ClusterController.CurrentCluster?.InstanceName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        // 1. Exact name match — only when unambiguous (multiple same-named islands fall through to cluster index).
        var nameMatches = _islands
            .Where(i => !string.IsNullOrWhiteSpace(i.Name)
                     && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nameMatches.Count == 1) return nameMatches[0];

        // 2. GUID match — scoped to same-named islands to prevent cross-island GUID pollution.
        if (!string.IsNullOrWhiteSpace(_sessionSourceClusterIndex))
        {
            var pool = _islands.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var srcMatches = pool
                .Where(i => string.Equals(i.SourceClusterIndex, _sessionSourceClusterIndex, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (srcMatches.Count == 1) return srcMatches[0];
        }

        // 3. WMD match scoped to same-named islands.
        if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType))
        {
            var wmdMatches = _islands
                .Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(i.WorldMapDataType, _sessionWorldMapDataType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (wmdMatches.Count == 1) return wmdMatches[0];
        }

        // 4. City from WMD biome — among same-named Player islands only.
        if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType))
        {
            var sessionCity = ParseCityFromWorldMapDataType(_sessionWorldMapDataType);
            if (!string.IsNullOrWhiteSpace(sessionCity))
            {
                var cityMatches = _islands
                    .Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(i.City, sessionCity, StringComparison.OrdinalIgnoreCase)
                             && i.IslandType == IslandType.Player)
                    .ToList();
                if (cityMatches.Count == 1) return cityMatches[0];
            }
        }

        // 5. Partial name + city — handles app name "OrangeZones Lymhurst" vs game instance "OrangeZones".
        if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType))
        {
            var sessionCity5 = ParseCityFromWorldMapDataType(_sessionWorldMapDataType);
            if (!string.IsNullOrWhiteSpace(sessionCity5))
            {
                var partialMatches = _islands
                    .Where(i => !string.IsNullOrWhiteSpace(i.Name)
                             && i.IslandType == IslandType.Player
                             && string.Equals(i.City, sessionCity5, StringComparison.OrdinalIgnoreCase)
                             && (i.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                 || name.StartsWith(i.Name, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (partialMatches.Count == 1) return partialMatches[0];
            }
        }

        // 6. SourceClusterIndex alone — last resort when name is completely mismatched but island
        //    was previously identified and had SourceClusterIndex backfilled.
        if (!string.IsNullOrWhiteSpace(_sessionSourceClusterIndex))
        {
            var srcOnlyMatches = _islands
                .Where(i => string.Equals(i.SourceClusterIndex, _sessionSourceClusterIndex, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (srcOnlyMatches.Count == 1) return srcOnlyMatches[0];
        }

        return null;
    }


    private static bool IsIslandInRoyalCity(Island.Island island)
    {
        if (island == null) return false;
        var city = island.City ?? string.Empty;
        var biome = island.Biome ?? string.Empty;
        return city.IndexOf("royal", StringComparison.OrdinalIgnoreCase) >= 0
               || biome.IndexOf("royal", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public IslandSessionSuggestion BuildSessionSuggestion()
    {
        if (string.IsNullOrWhiteSpace(_sessionIslandName) && _sessionBuildingCounts.IsEmpty)
            return _lastIslandSuggestion;

        var plotCounts = new Dictionary<PlotType, int>();
        foreach (var (uniqueName, count) in _sessionBuildingCounts)
        {
            if (TryResolveIslandPlotType(uniqueName, out var plotType))
                plotCounts[plotType] = plotCounts.TryGetValue(plotType, out var existing) ? existing + count : count;
        }

        var suggestion = new IslandSessionSuggestion(
            _sessionIslandName ?? string.Empty,
            _sessionOwner ?? string.Empty,
            _sessionWorldMapDataType ?? string.Empty,
            _sessionHasPremium,
            plotCounts,
            ParseCityFromWorldMapDataType(_sessionWorldMapDataType),
            ParseTierFromWorldMapDataType(_sessionWorldMapDataType),
            ParseIslandTypeFromWorldMapDataType(_sessionWorldMapDataType),
            _sessionSourceClusterIndex ?? string.Empty
        );

        if (!string.IsNullOrWhiteSpace(suggestion.City))
            _lastIslandSuggestion = suggestion;

        return suggestion;
    }

    private static bool IsIslandBuildingUniqueName(string uniqueName)
    {
        var upper = uniqueName.ToUpperInvariant();
        return upper.StartsWith("ISLAND_") || upper.StartsWith("HOUSE_");
    }

    #endregion

    #region Domain island list

    public IReadOnlyList<Island.Island> Islands
    {
        get { lock (_islandsLock) return _islands.ToList(); }
    }

    public Guid? AddIsland(Island.Island island)
    {
        ArgumentNullException.ThrowIfNull(island);
        lock (_islandsLock)
        {
            var isDuplicate = _islands.Any(i =>
                string.Equals(i.Name, island.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.City, island.City, StringComparison.OrdinalIgnoreCase));
            if (isDuplicate)
            {
                Log.Warning("[IslandController] Duplicate island rejected: name={Name} city={City}", island.Name, island.City);
                return null;
            }
            _islands.Add(island);
        }

        RefreshBindingsAsync();
        _ = SaveToFileAsync();
        return island.Id;
    }

    public void SelectIslandById(Guid id)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var bindings = _mainWindowViewModel?.IslandBindings;
            if (bindings == null) return;
            var entry = bindings.Islands?.FirstOrDefault(e => e.IslandId == id);
            if (entry != null)
                bindings.SelectedIsland = entry;
        });
    }

    public bool IslandExists(string name, string city)
    {
        lock (_islandsLock)
            return _islands.Any(i =>
                string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.City, city, StringComparison.OrdinalIgnoreCase));
    }

    public void UpdateIsland(Island.Island island)
    {
        ArgumentNullException.ThrowIfNull(island);
        lock (_islandsLock)
        {
            var idx = _islands.FindIndex(x => x.Id == island.Id);
            if (idx >= 0)
                _islands[idx] = island;
        }

        RefreshBindingsAsync();
        _ = SaveToFileAsync();
    }

    public void RemoveIsland(Guid id)
    {
        lock (_islandsLock)
            _islands.RemoveAll(x => x.Id == id);

        RefreshBindingsAsync();
        _ = SaveToFileAsync();
    }

    public Island.Island GetById(Guid id)
    {
        lock (_islandsLock)
            return _islands.FirstOrDefault(x => x.Id == id);
    }

    #endregion

    #region Persistence

    public async Task LoadFromFileAsync()
    {
        var (islands, migrated) = await IslandStore.LoadAsync();

        lock (_islandsLock)
        {
            _islands.Clear();
            _islands.AddRange(islands);
        }

        if (migrated)
            await SaveToFileAsync();

        await LoadOwnerProfilesAsync();

        RefreshBindingsAsync();
        Log.Information("[IslandController] Loaded {Count} islands from file.", islands.Count);
    }

    public async Task SaveToFileAsync()
    {
        List<Island.Island> snapshot;
        lock (_islandsLock)
            snapshot = _islands.ToList();

        await IslandStore.SaveAsync(snapshot);
    }

    #endregion

    #region Session suggestion application

    public async Task ApplyOrSuggestSessionAsync(IslandSessionSuggestion suggestion)
    {
        if (suggestion == null) return;

        Log.Information("[IslandController] ApplyOrSuggest: name='{Name}' owner='{Owner}' plots={PlotCount}",
            suggestion.IslandName, suggestion.Owner, suggestion.DetectedPlotCounts.Count);

        var matchedIsland = FindIslandForSuggestion(suggestion);

        if (matchedIsland != null)
        {
            var metaChanged = false;
            if (string.IsNullOrWhiteSpace(matchedIsland.City) && !string.IsNullOrWhiteSpace(suggestion.City))
            {
                matchedIsland.City = suggestion.City;
                if (string.IsNullOrWhiteSpace(matchedIsland.Biome))
                    matchedIsland.Biome = IslandMapping.CityToDefaultBiome(suggestion.City);
                metaChanged = true;
            }
            if (string.IsNullOrWhiteSpace(matchedIsland.Owner) && !string.IsNullOrWhiteSpace(suggestion.Owner))
            { matchedIsland.Owner = suggestion.Owner; metaChanged = true; }
            if (matchedIsland.Tier <= 0 && suggestion.Tier > 0)
            { matchedIsland.Tier = suggestion.Tier; metaChanged = true; }
            if (matchedIsland.IslandType == IslandType.Other && suggestion.IslandType != IslandType.Other)
            { matchedIsland.IslandType = suggestion.IslandType; metaChanged = true; }
            if (string.IsNullOrWhiteSpace(matchedIsland.SourceClusterIndex) && !string.IsNullOrWhiteSpace(suggestion.SourceClusterIndex))
            { matchedIsland.SourceClusterIndex = suggestion.SourceClusterIndex; metaChanged = true; }

            var plotsChanged = false;
            if ((matchedIsland.Plots == null || matchedIsland.Plots.Count == 0)
                && suggestion.DetectedPlotCounts.Count > 0)
            {
                foreach (var (plotType, count) in suggestion.DetectedPlotCounts)
                    matchedIsland.AddPlot(new Island.IslandPlot(plotType, count));

                matchedIsland.HasPremium = matchedIsland.HasPremium || suggestion.HasPremium;
                plotsChanged = true;
                Log.Information("[IslandController] Auto-applied {Count} plot types to island '{Name}'.",
                    suggestion.DetectedPlotCounts.Count, matchedIsland.Name);
            }
            else if (suggestion.DetectedPlotCounts.Count > 0)
            {
                Log.Information("[IslandController] Island '{Name}' already has plots, skipping auto-apply: existing={PlotCount} detected={DetectedCount}",
                    matchedIsland.Name, matchedIsland.Plots?.Count ?? 0, suggestion.DetectedPlotCounts.Count);
            }

            if (metaChanged || plotsChanged)
            {
                matchedIsland.UpdateModificationDate();
                await SaveToFileAsync();
                RefreshBindingsAsync();
            }
        }
        else
        {
            Log.Information("[IslandController] No island matched for name='{Name}' owner='{Owner}'. Islands in list: {IslandList}",
                suggestion.IslandName, suggestion.Owner,
                string.Join(", ", Islands.Select(i => $"'{i.Name}'(owner='{i.Owner}')")));
        }

        await Task.CompletedTask;
    }

    private Island.Island FindIslandForSuggestion(IslandSessionSuggestion suggestion)
    {
        lock (_islandsLock)
        {
            // 1. SourceClusterIndex exact match — most reliable (unique GUID per island instance).
            if (!string.IsNullOrWhiteSpace(suggestion.SourceClusterIndex))
            {
                var bySrc = _islands.FirstOrDefault(i =>
                    string.Equals(i.SourceClusterIndex, suggestion.SourceClusterIndex, StringComparison.OrdinalIgnoreCase));
                if (bySrc != null) return bySrc;
            }

            // 2. Name + city — unambiguous when name is unique OR city disambiguates same-name islands.
            if (!string.IsNullOrWhiteSpace(suggestion.IslandName))
            {
                if (!string.IsNullOrWhiteSpace(suggestion.City))
                {
                    var byNameCity = _islands.FirstOrDefault(i =>
                        string.Equals(i.Name, suggestion.IslandName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(i.City, suggestion.City, StringComparison.OrdinalIgnoreCase));
                    if (byNameCity != null) return byNameCity;
                }

                var byNameOnly = _islands.Where(i =>
                    string.Equals(i.Name, suggestion.IslandName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byNameOnly.Count == 1) return byNameOnly[0];
            }

            // 3. Single player island for owner.
            if (!string.IsNullOrWhiteSpace(suggestion.Owner))
            {
                var byOwner = _islands.Where(i =>
                    string.Equals(i.Owner, suggestion.Owner, StringComparison.OrdinalIgnoreCase)
                    && i.IslandType == IslandType.Player).ToList();
                if (byOwner.Count == 1) return byOwner[0];
            }

            return null;
        }
    }

    public static string ParseCityFromWorldMapDataType(string worldMapDataType)
    {
        if (string.IsNullOrWhiteSpace(worldMapDataType)) return string.Empty;
        var upper = worldMapDataType.ToUpperInvariant();
        // Full city name matches (guild islands, named clusters)
        if (upper.Contains("BRIDGEWATCH"))  return "Bridgewatch";
        if (upper.Contains("LYMHURST"))     return "Lymhurst";
        if (upper.Contains("MARTLOCK"))     return "Martlock";
        if (upper.Contains("FORTSTERLING") || upper.Contains("FORT_STERLING") || upper.Contains("STERLING")) return "Fort Sterling";
        if (upper.Contains("THETFORD"))     return "Thetford";
        if (upper.Contains("CAERLEON"))     return "Caerleon";
        if (upper.Contains("BRECILIEN") || upper.Contains("_MI_") || upper.Contains("MISTS")) return "Brecilien";
        // Biome code matches — short codes (ISL_ST_AUTO) and full words (ISLAND-PLAYER-STEPPE-0001f)
        if (upper.Contains("_ST_") || upper.Contains("STEPPE")) return "Bridgewatch";
        if (upper.Contains("_FR_") || upper.Contains("FOREST")) return "Lymhurst";
        if (upper.Contains("_SW_") || upper.Contains("SWAMP")) return "Thetford";
        if (upper.Contains("_MN_") || upper.Contains("MOUNTAIN")) return "Fort Sterling";
        if (upper.Contains("_HL_DEAD") || upper.Contains("DEAD")) return "Caerleon";
        if (upper.Contains("_HL_") || upper.Contains("HIGHLAND")) return "Martlock";
        return string.Empty;
    }

    public static int ParseTierFromWorldMapDataType(string worldMapDataType)
    {
        if (string.IsNullOrWhiteSpace(worldMapDataType)) return 6;
        var upper = worldMapDataType.ToUpperInvariant();
        for (var t = 6; t >= 1; t--)
        {
            if (upper.Contains($"_T{t}_") || upper.Contains($"_T{t}NON") || upper.EndsWith($"_T{t}"))
                return t;
        }
        return 6;
    }

    public static IslandType ParseIslandTypeFromWorldMapDataType(string worldMapDataType)
    {
        if (string.IsNullOrWhiteSpace(worldMapDataType)) return IslandType.Player;
        var upper = worldMapDataType.ToUpperInvariant();
        if (upper.Contains("GUILD")) return IslandType.Guild;
        return IslandType.Player;
    }

    #endregion

    #region UI projection

    private void PushLiveStatusToBindings()
    {
        if (_pushDebounceTimer != null)
        {
            _pushDebounceTimer.Change(PushDebounceMs, Timeout.Infinite);
            return;
        }
        var t = new System.Threading.Timer(_ => ExecutePushSessionIslandStatus(), null, PushDebounceMs, Timeout.Infinite);
        if (Interlocked.CompareExchange(ref _pushDebounceTimer, t, null) != null)
            t.Dispose();
    }

    // Called on packet debounce — processes live snapshots for session island only.
    private void ExecutePushSessionIslandStatus()
    {
        var snapshots = GetCurrentSnapshots();

        List<Island.Island> islandsCopy;
        Guid? sessionIslandId;
        lock (_islandsLock)
        {
            var sessionIsland = FindCurrentIslandNoLock();
            sessionIslandId = sessionIsland?.Id;

            if (sessionIsland?.Plots != null)
            {
                var assignments = IslandLaborerResolver.Resolve(
                    sessionIsland.Plots.Where(p => p.PlotType == PlotType.House).ToList(), snapshots);
                HealHouseMapSlots(sessionIsland, assignments);
                var anyChanged = false;
                foreach (var p in sessionIsland.Plots)
                {
                    assignments.TryGetValue(p.Id, out var slotMap);
                    if (p.UpdateLaborerStatuses(snapshots, sessionIsland.LastPlantedAt, slotMap)) anyChanged = true;
                }
                if (anyChanged)
                {
                    sessionIsland.UpdateModificationDate();
                    _ = SaveToFileAsync();
                }
            }

            islandsCopy = new List<Island.Island>(_islands);
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _mainWindowViewModel?.IslandBindings?.UpdateLiveStatus(snapshots, islandsCopy, sessionIslandId);
            ScheduleNextPlotTransition();
        });
        LaborerSnapshotsChanged?.Invoke();
    }

    // Called on 60s countdown tick and transition timer — refreshes all islands (no live snapshots for non-session).
    private void ExecutePushAllIslandsStatus()
    {
        var snapshots = GetCurrentSnapshots();

        List<Island.Island> islandsCopy;
        Guid? sessionIslandId;
        lock (_islandsLock)
        {
            var sessionIsland = FindCurrentIslandNoLock();
            sessionIslandId = sessionIsland?.Id;

            foreach (var isl in _islands)
            {
                if (isl.Plots == null) continue;
                var islSnapshots = isl.Id == sessionIslandId ? snapshots : Array.Empty<LaborerSnapshot>();
                IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, LaborerSnapshot>> assignments = null;
                if (islSnapshots.Count > 0)
                {
                    assignments = IslandLaborerResolver.Resolve(
                        isl.Plots.Where(p => p.PlotType == PlotType.House).ToList(), islSnapshots);
                    HealHouseMapSlots(isl, assignments);
                }
                var anyChanged = false;
                foreach (var p in isl.Plots)
                {
                    IReadOnlyDictionary<int, LaborerSnapshot> slotMap = null;
                    assignments?.TryGetValue(p.Id, out slotMap);
                    if (p.UpdateLaborerStatuses(islSnapshots, isl.LastPlantedAt, slotMap)) anyChanged = true;
                }
                if (anyChanged)
                {
                    isl.UpdateModificationDate();
                    _ = SaveToFileAsync();
                }
            }

            islandsCopy = new List<Island.Island>(_islands);
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var isl in islandsCopy)
                isl.RefreshTimerDisplay();
            _mainWindowViewModel?.IslandBindings?.UpdateLiveStatus(snapshots, islandsCopy, sessionIslandId);
            ScheduleNextPlotTransition();
        });
    }

    private void RefreshBindingsAsync()
    {
        List<IslandEntry> entries;
        List<Island.Island> islandsCopy;
        var snapshots = GetCurrentSnapshots();
        Guid? sessionIslandId;
        lock (_islandsLock)
        {
            entries = _islands.Select((isl, i) => IslandMapping.ToEntry(isl, i)).ToList();
            islandsCopy = new List<Island.Island>(_islands);
            sessionIslandId = FindCurrentIslandNoLock()?.Id;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var bindings = _mainWindowViewModel?.IslandBindings;
            if (bindings == null) return;
            bindings.LoadFrom(entries);
            bindings.UpdateLiveStatus(snapshots, islandsCopy, sessionIslandId);
        });
    }

    // Refreshes only one island's live status in the bindings — no collection rebuild.
    // Use this when only one island's data changed (timer, plot config, laborer state).
    private void RefreshIslandStatusAsync(Island.Island island)
    {
        var snapshots = GetCurrentSnapshots();
        var islandSnapshot = island;
        Guid? sessionIslandId;
        lock (_islandsLock)
            sessionIslandId = FindCurrentIslandNoLock()?.Id;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _mainWindowViewModel?.IslandBindings?.UpdateLiveStatus(snapshots, [islandSnapshot], sessionIslandId);
        });
        LaborerSnapshotsChanged?.Invoke();
    }

    #endregion
}
