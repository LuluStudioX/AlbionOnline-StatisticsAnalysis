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
using StatisticsAnalysisTool.Views;
using System;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace StatisticsAnalysisTool.Network.Manager;

public partial class IslandController
{
    private const string IslandsFileName = "Islands.json";

    private static readonly PlotType[] FarmPlotTypes =
        [PlotType.Farm, PlotType.HerbGarden, PlotType.Pasture, PlotType.Kennel];

    private readonly MainWindowViewModel _mainWindowViewModel;
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

    // Farmable signature dedup and detected item names
    private readonly ConcurrentDictionary<long, string> _farmableSignatures = new();
    private readonly ConcurrentDictionary<long, string> _detectedFarmableNames = new();

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
    private readonly HashSet<long> _seenItemObjectIds = [];
    private readonly object _seenItemObjectIdsLock = new();
    private DateTime _laborerCollectWindowEnd = DateTime.MinValue;
    private PlotType _collectWindowPlotType = PlotType.House;

    public void ClearSession()
    {
        _snapshots.Clear();
        lock (_snapshotOrderLock)
            _snapshotsByOrder.Clear();
        _sessionBuildingCounts.Clear();
        _farmableSignatures.Clear();
        _detectedFarmableNames.Clear();
        _sessionIslandName = null;
        _sessionOwner = null;
        _sessionWorldMapDataType = null;
        _sessionSourceClusterIndex = null;
        _sessionHasPremium = false;
        lock (_seenItemObjectIdsLock) _seenItemObjectIds.Clear();
        _laborerCollectWindowEnd = DateTime.MinValue;
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
        lock (_seenItemObjectIdsLock) _seenItemObjectIds.Clear();
        _laborerCollectWindowEnd = DateTime.MinValue;
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

            var anchorPlotType = GetPlotTypeFromAnchor(e.UniqueName);
            if (anchorPlotType.HasValue && e.Position.HasValue)
            {
                var island = FindCurrentIsland();
                if (island != null)
                {
                    var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
                    var slotIndex = layout?.WorldToNearestSlot(e.Position.Value.X, e.Position.Value.Y, requireLarge: null);
                    if (slotIndex.HasValue)
                    {
                        var alreadyOwned = island.Plots.Any(p =>
                            p.PlotType == anchorPlotType.Value && p.MapSlotIndex == slotIndex.Value);
                        if (!alreadyOwned)
                        {
                            var matchedPlot = island.Plots.FirstOrDefault(p =>
                                p.PlotType == anchorPlotType.Value && !p.MapSlotIndex.HasValue);
                            if (matchedPlot != null)
                            {
                                matchedPlot.MapSlotIndex = slotIndex.Value;
                                island.UpdateModificationDate();
                                _ = SaveToFileAsync();
                                Log.Information("[IslandController] Auto-assigned slot {Slot} to {Type} plot via world pos ({X},{Y})",
                                    slotIndex.Value, anchorPlotType.Value, e.Position.Value.X, e.Position.Value.Y);
                                RefreshIslandStatusAsync(island);
                            }
                        }
                    }
                }
            }

            // Seed island cycle timer from server-reported planted time (farmable plant events only).
            // This ensures the countdown is accurate even when visiting an island that was planted
            // before the current session — without this, auto-start would stamp DateTime.UtcNow instead.
            if (IsFarmablePlant(e.UniqueName))
            {
                var island = FindCurrentIsland();
                if (island != null)
                {
                    // Code 45 param 20 = server-now timestamp, not planted-at.
                    // Timer is seeded accurately by FarmableObjectInfo (code 201) which carries elapsed time.
                    // No timer update from NewBuilding.

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
                    if (isJustPlanted)
                    {
                        var item = ItemController.GetItemByUniqueName(e.UniqueName);
                        if (item != null && item.Index > 0)
                        {
                            var plotType = IsFarmableSeed(e.UniqueName) ? PlotType.HerbGarden : PlotType.Pasture;
                            island.AddConsumed(item.Index, 1, plotType);
                            island.UpdateModificationDate();
                            _ = SaveToFileAsync();
                            PushYieldUpdateToBindings(island);
                            Log.Information("[IslandController] Recorded planted item as consumed: island={Island}, item={Item}, plotType={PlotType}",
                                island.Name, e.UniqueName, plotType);
                        }
                    }
                }
            }
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

    private static PlotType? GetPlotTypeFromAnchor(string uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName)) return null;
        var upper = uniqueName.ToUpperInvariant();
        if (upper.Contains("LABOURER")) return PlotType.House;
        if (upper.Contains("PLAYERHOUSE") || upper.Contains("PLAYER_HOUSE")) return PlotType.House;
        if (upper.Contains("FARMHOUSE")) return PlotType.Farm;
        if (upper.Contains("HERBGARDEN") || upper.Contains("_HERB_")) return PlotType.HerbGarden;
        if (upper.Contains("PASTURE")) return PlotType.Pasture;
        if (upper.Contains("KENNEL")) return PlotType.Kennel;
        if (upper.Contains("SADDLER")) return PlotType.Saddler;
        if (upper.Contains("BUTCHER")) return PlotType.Butcher;
        if (upper.Contains("SMELTER")) return PlotType.Smelter;
        if (upper.Contains("TANNER")) return PlotType.Tanner;
        if (upper.Contains("LUMBERMILL") || upper.Contains("LUMBER_MILL")) return PlotType.Lumbermill;
        if (upper.Contains("STONEMASON") || upper.Contains("STONE_MASON")) return PlotType.Stonemason;
        if (upper.Contains("COOK")) return PlotType.Cook;
        if (upper.Contains("ALCHEMYLAB") || upper.Contains("ALCHEMY_LAB") || upper.Contains("ALCHLAB")) return PlotType.AlchemyLab;
        if (upper.Contains("HUNTERLODGE") || upper.Contains("HUNTER_LODGE") || upper.Contains("HUNTER")) return PlotType.HunterLodge;
        if (upper.Contains("WARRIORGUILD") || upper.Contains("WARRIOR_GUILD") || upper.Contains("WARRIOR")) return PlotType.WarriorGuild;
        if (upper.Contains("MAGETOWER") || upper.Contains("MAGE_TOWER") || upper.Contains("MAGE")) return PlotType.MageTower;
        if (upper.Contains("WEAVER")) return PlotType.Weaver;
        if (upper.Contains("TOOLMAKER") || upper.Contains("TOOL_MAKER")) return PlotType.Toolmaker;
        if (upper.Contains("MILL")) return PlotType.Mill;
        if (upper.Contains("REPAIRSHOP") || upper.Contains("REPAIR")) return PlotType.RepairStation;
        return null;
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
        PersistLaborerStatusToConfig(snapshot);
    }

    private void TryAutoStartIslandTimerFromLaborer(LaborerSnapshot snapshot)
    {
        var prefs = _mainWindowViewModel?.IslandBindings?.Preferences;
        if (prefs == null || !prefs.AutoStartCycleOnIslandActivity) return;
        if (!snapshot.IsOnJob) return;

        // JobDispatchTime = ready-at (param 8, only in same-session dispatch).
        // On reconnect it's absent — fall back to NextReturnAt (param 6, always sent) or JobStartTime + cycle.
        DateTime? readyUtcNullable = snapshot.JobDispatchTime
            ?? snapshot.NextReturnAt
            ?? (snapshot.JobStartTime.HasValue ? snapshot.JobStartTime.Value.AddHours(IslandConstants.LaborerBaseCycleHours) : null);

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
        var wasLootReady = snapshot.IsLootReady;
        var wasOnJob = snapshot.IsOnJob;
        var prevJobStartTime = snapshot.JobStartTime;
        snapshot.UpdateFromJobInfo(e);

        if (e.IsLootReady && !wasLootReady)
        {
            // Open collect window immediately when loot becomes ready — item broadcasts
            // (NewJournalItem, NewSimpleItem) arrive while loot_ready=true, before the
            // idle transition, so the window must open here not on the idle transition.
            _laborerCollectWindowEnd = DateTime.UtcNow.AddSeconds(10);
            _collectWindowPlotType = PlotType.House;
            Log.Information("[IslandController] Laborer collect window opened on loot-ready: laborer objectId={ObjectId}", e.ObjectId);

            var island = FindCurrentIsland();
            if (island != null)
            {
                island.TotalLootCollected++;
                island.UpdateModificationDate();
                _ = SaveToFileAsync();
                RefreshIslandStatusAsync(island);
            }
        }

        if (wasLootReady && !snapshot.IsLootReady)
        {
            // Extend window briefly on idle transition to catch any late-arriving items.
            if (_laborerCollectWindowEnd > DateTime.UtcNow)
                _laborerCollectWindowEnd = DateTime.UtcNow.AddSeconds(3);
            Log.Information("[IslandController] Laborer collect idle transition: laborer objectId={ObjectId}", e.ObjectId);
        }

        if (e.JournalItemId > 0)
        {
            var journalName = ItemController.GetItemUniqueNameByIndex(e.JournalItemId);
            snapshot.TrySetTypeFromJournal(journalName);
        }

        var isNewDispatch = e.JobStartTime.HasValue && e.JournalItemId > 0
                            && prevJobStartTime != null
                            && e.JobStartTime != prevJobStartTime;

        Log.Debug("[IslandController] LaborerJobInfo: objectId={ObjId}, journalId={JournalId}, jobStart={JobStart}, prevJobStart={PrevJobStart}, lootReady={LootReady}, isNewDispatch={IsNewDispatch}",
            e.ObjectId, e.JournalItemId, e.JobStartTime, prevJobStartTime, e.IsLootReady, isNewDispatch);

        if (isNewDispatch)
        {
            var island = FindCurrentIsland();
            if (island != null)
            {
                island.AddConsumed(e.JournalItemId, 1, PlotType.House);
                _ = SaveToFileAsync();
                PushYieldUpdateToBindings(island);
                Log.Information("[IslandController] Recorded consumed journal: island={Island}, journalId={JournalId}", island.Name, e.JournalItemId);
            }
            else
            {
                Log.Warning("[IslandController] isNewDispatch=true but no current island found");
            }
        }

        if (e.IsAwayOnJob)
            TryAutoStartIslandTimerFromLaborer(snapshot);

        UpdateLastSnapshotCache();
        PushLiveStatusToBindings();
        LaborerSnapshotsChanged?.Invoke();
        PersistLaborerStatusToConfig(snapshot);

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
        // Fire only when ALL laborers are on job — last dispatch just sent.
        if (!snapshots.All(s => s.IsOnJob)) return;

        _collectionReadyWebhookSentThisSession = true;
        var islandOwner = FindCurrentIsland()?.Owner?.Trim() ?? _sessionOwner;
        _ = TrySendCollectionReadyWebhookAsync(islandOwner);
    }

    private async Task TrySendCollectionReadyWebhookAsync(string ownerName)
    {
        if (string.IsNullOrEmpty(ownerName)) return;

        var profile = GetOwnerProfile(ownerName);
        if (string.IsNullOrWhiteSpace(profile?.WebhookUrl)) return;

        var confirmed = await PromptWebhookConfirmAsync(ownerName).ConfigureAwait(false);
        if (!confirmed) return;

        var message = _mainWindowViewModel?.IslandBindings?.BuildDiscordMessage(ownerName);
        if (string.IsNullOrEmpty(message)) return;

        Log.Information("[IslandController] Sending collection-ready webhook: owner={Owner}", ownerName);
        await DiscordWebhookService.SendAsync(profile.WebhookUrl, message).ConfigureAwait(false);
    }

    private bool IsOnlyAutoPrefilled(string ownerName)
    {
        var profile = GetOwnerProfile(ownerName);
        if (profile?.CycleHistory == null) return false;

        var today = DateTime.Today;
        var todayIslandRecords = profile.CycleHistory
            .Where(c => c.Date.Date == today && c.RecordType == CycleRecordType.Islands)
            .ToList();

        if (todayIslandRecords.Count == 0) return false;

        var allAutoPrefilled = todayIslandRecords.All(c =>
            string.IsNullOrWhiteSpace(c.Notes)
            || string.Equals(c.Notes.Trim(), AutoPrefillNotesMarker, StringComparison.OrdinalIgnoreCase));

        var hasExtraEarned = profile.CycleHistory
            .Any(c => c.Date.Date == today && c.RecordType != CycleRecordType.Islands);

        return allAutoPrefilled && !hasExtraEarned;
    }

    private Task<bool> PromptWebhookConfirmAsync(string ownerName)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new WebhookConfirmDialog
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() != true) return false;

            if (dialog.Result == WebhookConfirmDialog.ConfirmResult.DontSend) return false;

            if (dialog.Result == WebhookConfirmDialog.ConfirmResult.SaveAndSend)
            {
                var notes = dialog.DailyNotes;
                var emv = dialog.EmvAmount;

                if (!string.IsNullOrWhiteSpace(notes) || emv.HasValue)
                {
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
            }

            return true;
        }).Task;
    }

    public async Task<bool> SendWebhookManualAsync(string ownerName)
    {
        if (string.IsNullOrEmpty(ownerName)) return false;
        var profile = GetOwnerProfile(ownerName);
        if (string.IsNullOrWhiteSpace(profile?.WebhookUrl)) return false;

        var message = _mainWindowViewModel?.IslandBindings?.BuildDiscordMessage();
        if (string.IsNullOrEmpty(message)) return false;

        Log.Information("[IslandController] Manual webhook send: owner={Owner}", ownerName);
        await DiscordWebhookService.SendAsync(profile.WebhookUrl, message).ConfigureAwait(false);
        return true;
    }

    private void PersistLaborerStatusToConfig(LaborerSnapshot snapshot)
    {
        var island = FindCurrentIsland();
        if (island?.Plots == null) return;

        foreach (var plot in island.Plots.Where(p => p.PlotType == PlotType.House))
        {
            var dict = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
            var matched = false;
            for (var slot = 1; slot <= 3; slot++)
            {
                if (!SlotMatchesSnapshot(dict, slot, snapshot)) continue;

                if (snapshot.JobDispatchTime.HasValue)
                    dict[LaborerConfigHelper.DispatchTimeKey(slot)] = LaborerConfigHelper.FormatUtc(snapshot.JobDispatchTime.Value);

                dict[LaborerConfigHelper.LootReadyKey(slot)] = snapshot.IsLootReady ? "true" : "false";
                plot.Configuration = LaborerConfigHelper.BuildConfiguration(dict);
                matched = true;
                break;
            }
            if (matched) break;
        }

        island.UpdateModificationDate();
        _ = SaveToFileAsync();
    }

    private static bool SlotMatchesSnapshot(Dictionary<string, string> config, int slot, LaborerSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.FullName)
            && config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var storedName)
            && !string.IsNullOrWhiteSpace(storedName)
            && string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(storedName),
                LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName),
                StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(snapshot.LaborerType)
            && config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var storedType)
            && !string.IsNullOrWhiteSpace(storedType)
            && !string.Equals(storedType, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase)
            && string.Equals(LaborerConfigHelper.NormalizeLaborerType(storedType),
                LaborerConfigHelper.NormalizeLaborerType(snapshot.LaborerType),
                StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private void PersistPlotPlantedAt(Island.Island island, DateTime plantedAt)
    {
        if (island?.Plots == null) return;
        foreach (var plot in island.Plots.Where(p => FarmPlotTypes.Contains(p.PlotType)))
            plot.PlotPlantedAt = plantedAt;
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
    }

    private void ClearPlotPlantedAt(Island.Island island)
    {
        if (island?.Plots == null) return;
        foreach (var plot in island.Plots.Where(p => FarmPlotTypes.Contains(p.PlotType)))
            plot.PlotPlantedAt = null;
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
    }

    private void ClearPlotPlantedAtByType(Island.Island island, PlotType plotType)
    {
        if (island?.Plots == null) return;
        foreach (var plot in island.Plots.Where(p => p.PlotType == plotType))
            plot.PlotPlantedAt = null;
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
    }

    private void CommitIslandPlant(Island.Island island)
    {
        island.PlantAll();
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        RefreshIslandStatusAsync(island);
        TryAutoPrefillPayout(island);
    }

    public void HandleFarmBuildingInfo(FarmBuildingInfoEvent e)
    {
        if (e.ObjectId < 0) return;
        Log.Debug("[IslandController] FarmBuildingInfo ObjectId={ObjectId} PlantedAt={PlantedAt}", e.ObjectId, e.PlantedAt);

        if (!e.PlantedAt.HasValue) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        // Param 4 is only a valid planting timestamp when crops are still growing.
        // When harvestable the game still sends param 4 but with current-time semantics.
        // Guard: if PlantedAt + minimum cycle is already past, crops are ready and
        // the timestamp is not a real planted-at — ignore to avoid resetting the timer.
        if (e.PlantedAt.Value.AddHours(IslandConstants.LaborerBaseCycleHours) <= DateTime.UtcNow)
        {
            Log.Debug("[IslandController] FarmBuildingInfo PlantedAt already past minimum cycle — skipping timer update: island={Island}, plantedAt={PlantedAt:O}",
                island.Name, e.PlantedAt.Value);
            return;
        }

        island.LastPlantedAt = e.PlantedAt.Value;
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        Log.Information("[IslandController] Updated island timer from FarmBuildingInfo: island={Island}, plantedAt={PlantedAt:O}",
            island.Name, e.PlantedAt.Value);
        PersistPlotPlantedAt(island, e.PlantedAt.Value);
        PushLiveStatusToBindings();
    }

    public void HandleHarvestFinished(HarvestFinishedObject harvest)
    {
        if (harvest == null) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        Log.Information("[IslandController] Island harvest activity: island={Island}, objectId={ObjectId}, itemId={ItemId}",
            island.Name, harvest.ObjectId, harvest.ItemId);

        var localUserObjectId = _trackingController?.EntityController?.LocalUserData?.UserObjectId;
        if (!localUserObjectId.HasValue || harvest.UserObjectId != localUserObjectId.Value)
        {
            Log.Debug("[IslandController] Harvest belongs to different player (local={LocalId}, harvest={HarvestId}) — skipping auto-start",
                localUserObjectId, harvest.UserObjectId);
            return;
        }

        var totalYield = harvest.StandardAmount + harvest.CollectorBonusAmount + harvest.PremiumBonusAmount;
        if (harvest.ItemId > 0 && totalYield > 0)
        {
            island.AddYield(harvest.ItemId, totalYield, PlotType.Farm);
            PushYieldUpdateToBindings(island);
            Log.Information("[IslandController] Recorded harvest yield: island={Island}, itemId={ItemId}, qty={Qty}",
                island.Name, harvest.ItemId, totalYield);
        }

        // Evaluate cycleRunning before clearing so the check uses live plot timers,
        // not the stale island-level LastPlantedAt fallback.
        var cycleWasRunning = island.NextCollectionReadyAt.HasValue
            && island.NextCollectionReadyAt.Value > DateTime.UtcNow;

        ClearPlotPlantedAt(island);
        RefreshIslandStatusAsync(island);
        Log.Information("[IslandController] Cleared plot timers after harvest: island={Island}", island.Name);

        var prefs = _mainWindowViewModel?.IslandBindings?.Preferences;
        if (prefs == null || !prefs.AutoStartCycleOnIslandActivity) return;

        if (cycleWasRunning) return;

        if (IsIslandInRoyalCity(island))
        {
            Log.Debug("[IslandController] Skipped auto-start for island in royal city: {Island}", island.Name);
            return;
        }

        CommitIslandPlant(island);
        Log.Information("[IslandController] Auto-started island cycle from harvest activity: island={Island}", island.Name);
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
        PushYieldUpdateToBindings(island);
    }

    private void HandleFarmableHarvestInternal(FarmableHarvestResponse response, PlotType plotType)
    {
        Log.Debug("[IslandController] FarmableHarvestResponse received: plotType={PlotType}, itemCount={Count}", plotType, response?.Items?.Count ?? -1);

        if (response?.Items == null || response.Items.Count == 0) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        foreach (var (uniqueName, quantity) in response.Items)
        {
            var item = ItemController.GetItemByUniqueName(uniqueName);
            if (item == null || item.Index <= 0) continue;

            island.AddYield(item.Index, quantity, plotType);
            island.UpdateModificationDate();

            Log.Information("[IslandController] Recorded farmable harvest: island={Island}, item={Item}, qty={Qty}, plotType={PlotType}",
                island.Name, uniqueName, quantity, plotType);
        }

        ClearPlotPlantedAtByType(island, plotType);
        RefreshIslandStatusAsync(island);
        Log.Information("[IslandController] Cleared plot timers after farmable harvest: island={Island}, plotType={PlotType}", island.Name, plotType);

        _ = SaveToFileAsync();
        PushYieldUpdateToBindings(island);
    }

    public void HandleActionOnBuildingFinished(ActionOnBuildingFinishedEvent e)
    {
        if (e == null) return;

        var island = FindCurrentIsland();

        // Log all action types on island so unknown values (e.g. replant) can be captured and identified.
        if (island != null)
            Log.Information("[IslandController] ActionOnBuildingFinished on island: island={Island}, type={ActionType} ({ActionTypeInt})",
                island.Name, e.ActionType, (int) e.ActionType);

        if (island == null) return;

        var localUserObjectId = _trackingController?.EntityController?.LocalUserData?.UserObjectId;
        if (!e.UserObjectId.HasValue || !localUserObjectId.HasValue || e.UserObjectId.Value != localUserObjectId.Value)
            return;

        if (e.ActionType == ActionOnBuildingType.Repair) return;
        if (e.ActionType == ActionOnBuildingType.BuyAndCrafting) return;

        // Clear per-plot timers on collect/harvest so plots show "awaiting replant".
        // Cycle restart is NOT triggered here: laborer dispatch is tracked via TryAutoStartIslandTimerFromLaborer
        // (back-calculates accurate dispatch time); crop replant is detected via HandleFarmableObjectInfo /
        // HandleFarmBuildingInfo. Triggering CommitIslandPlant here would stamp a wrong "now" timestamp
        // mid-collection before the user has actually dispatched laborers or replanted crops.
        ClearPlotPlantedAt(island);
        RefreshIslandStatusAsync(island);
        Log.Information("[IslandController] Cleared plot timers after building action: island={Island}, actionType={ActionType}",
            island.Name, e.ActionType);
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

        if (!IsNewFarmableSignature(e.ObjectId, e.Signature)) return;

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
            _detectedFarmableNames[e.ObjectId] = e.FarmableUniqueName;
            Log.Debug("[IslandController] Farmable item detected: island={Island}, objectId={ObjectId}, uniqueName={UniqueName}",
                island.Name, e.ObjectId, e.FarmableUniqueName);
            TryAutoApplyFarmableConfig(island, e.FarmableUniqueName);
        }

        Log.Information("[IslandController] Farmable state changed: island={Island}, objectId={ObjectId}, activityAt={ActivityAt:O}",
            island.Name, e.ObjectId, activityTimestampUtc);

        // Param 4 (remaining 100µs) + param 5 (server ticks) → derive PlantedAt and update per-plot timers.
        if (e.PlantedAt.HasValue && e.PlantedAt.Value.AddHours(IslandConstants.LaborerBaseCycleHours) > DateTime.UtcNow)
        {
            island.LastPlantedAt = e.PlantedAt.Value;
            island.UpdateModificationDate();
            _ = SaveToFileAsync();
            PersistPlotPlantedAt(island, e.PlantedAt.Value);
            RefreshIslandStatusAsync(island);
            Log.Information("[IslandController] Updated island timer from FarmableObjectInfo: island={Island}, objectId={ObjectId}, plantedAt={PlantedAt:O}",
                island.Name, e.ObjectId, e.PlantedAt.Value);
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
        PushYieldUpdateToBindings(island);
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
            PushYieldUpdateToBindings(island);
        }
        _ = SaveToFileAsync();
    }

    public void HandleLaborerYieldItem(DiscoveredItem item)
    {
        if (item == null || item.ItemIndex <= 0 || item.Quantity <= 0) return;

        bool isNewObject;
        lock (_seenItemObjectIdsLock)
            isNewObject = _seenItemObjectIds.Add(item.ObjectId);

        // Always register the ObjectId, even outside the collect window.
        // Zone-in broadcasts all existing chest contents — pre-populating the seen set
        // ensures those ObjectIds are blocked when collect windows open later.
        if (!isNewObject) return;

        // Only record yield during an active collect window.
        if (DateTime.UtcNow > _laborerCollectWindowEnd) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        var plotType = _collectWindowPlotType;
        island.AddYield(item.ItemIndex, item.Quantity, plotType);
        island.TotalLootCollected += item.Quantity;
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        PushYieldUpdateToBindings(island);

        Log.Information("[IslandController] Recorded {PlotType} yield: island={Island}, itemId={ItemId}, qty={Qty}, objectId={ObjectId}",
            plotType, island.Name, item.ItemIndex, item.Quantity, item.ObjectId);
    }

    private void PushYieldUpdateToBindings(Island.Island island)
    {
        var bindings = _mainWindowViewModel?.IslandBindings;
        if (bindings == null) return;

        var entry = bindings.Islands.FirstOrDefault(e => e.IslandId == island.Id);
        if (entry == null) return;

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            entry.YieldItems = new ObservableCollection<IslandYieldEntry>(island.YieldHistory);
            entry.ConsumedItems = new ObservableCollection<IslandConsumedEntry>(island.ConsumedHistory);
            bindings.RefreshOwnerYield();
        });
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
        if (!string.IsNullOrWhiteSpace(existing)) return;

        dict[info.ConfigKey] = info.DisplayName;
        slotPlot.Configuration = LaborerConfigHelper.BuildConfiguration(dict);
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        RefreshIslandStatusAsync(island);
        Log.Information("[IslandController] Position-matched farmable config: island={Island}, plotType={PlotType}, slot={Slot}, key={Key}, value={Value}",
            island.Name, info.PlotType, slotIndex.Value, info.ConfigKey, info.DisplayName);
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

        var entry = _mainWindowViewModel?.IslandBindings?.Islands?
            .FirstOrDefault(e => e.IslandId == match.Id);
        if (entry == null) return;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_mainWindowViewModel?.IslandBindings != null)
                _mainWindowViewModel.IslandBindings.SelectedIsland = entry;
        });

        Log.Information("[IslandController] Auto-selected island '{Name}' on cluster entry.", match.Name);
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

    private void TryAutoAssignHousePlotMapSlot(Island.Island island, LaborerSnapshot snapshot)
    {
        if (!snapshot.WorldPosition.HasValue) return;
        var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
        if (layout == null) return;

        var (wx, wy) = snapshot.WorldPosition.Value;
        var slotIndex = layout.WorldToNearestSlot(wx, wy, requireLarge: null);
        if (!slotIndex.HasValue) return;

        // Skip if a house plot already claims this slot.
        if (island.Plots.Any(p => p.PlotType == PlotType.House && p.MapSlotIndex == slotIndex.Value))
            return;

        var unassigned = island.Plots.FirstOrDefault(p =>
            p.PlotType == PlotType.House && !p.MapSlotIndex.HasValue);
        if (unassigned == null) return;

        unassigned.MapSlotIndex = slotIndex.Value;
        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        Log.Information("[IslandController] Auto-assigned house map slot {Slot} from laborer world pos ({X},{Y})",
            slotIndex.Value, wx, wy);
        RefreshIslandStatusAsync(island);
    }

    private IslandPlot FindOrAssignHousePlotBySlot(Island.Island island, int slotIndex)
    {
        var existing = island.Plots.FirstOrDefault(p =>
            p.PlotType == PlotType.House && p.MapSlotIndex == slotIndex);
        if (existing != null) return existing;

        // Claim first unassigned house plot.
        var unassigned = island.Plots.FirstOrDefault(p =>
            p.PlotType == PlotType.House && !p.MapSlotIndex.HasValue);
        if (unassigned == null) return null;

        // Don't assign if another plot already owns this map slot (race guard).
        if (island.Plots.Any(p => p.PlotType == PlotType.House && p.MapSlotIndex == slotIndex))
            return island.Plots.First(p => p.PlotType == PlotType.House && p.MapSlotIndex == slotIndex);

        unassigned.MapSlotIndex = slotIndex;
        return unassigned;
    }

    private bool TryEnsureHousePlotConfiguration(Island.Island island, LaborerSnapshot snapshot)
    {
        if (island?.Plots == null || snapshot.BuildingTier <= 0 || string.IsNullOrWhiteSpace(snapshot.LaborerType))
            return false;

        // Name match first: config-stored names are reliable ground truth and survive slot resets.
        // World position match second: assigns MapSlotIndex once name confirms the right plot.
        if (TryMatchHousePlotByLaborerName(island, snapshot))
            return true;

        if (TryMatchHousePlotByWorldPosition(island, snapshot))
            return true;

        return TryEnrichHousePlotByTypeMatch(island, snapshot);
    }

    private bool TryMatchHousePlotByWorldPosition(Island.Island island, LaborerSnapshot snapshot)
    {
        if (!snapshot.WorldPosition.HasValue)
            return false;

        // HousePlotGuid (param 9) is shared across ALL houses on the same island, so it cannot
        // uniquely identify a house. World position is the only per-house discriminator available.
        var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
        if (layout == null)
            return false;

        var slotIndex = layout.WorldToNearestSlot(snapshot.WorldPosition.Value.X, snapshot.WorldPosition.Value.Y, requireLarge: null);
        if (!slotIndex.HasValue)
            return false;

        var slotPlot = FindOrAssignHousePlotBySlot(island, slotIndex.Value);
        if (slotPlot != null && HousePlotHasEmptySlot(slotPlot.Configuration))
        {
            if (!string.IsNullOrWhiteSpace(snapshot.FullName) && IsLaborerNameInAnyOtherHousePlot(island, slotPlot, snapshot.FullName))
                return true;

            if (TryAutofillHousePlot(slotPlot, snapshot))
            {
                if (slotPlot.MapSlotIndex != slotIndex.Value)
                    slotPlot.MapSlotIndex = slotIndex.Value;
                PurgeDuplicateLaborerName(island, slotPlot, snapshot.FullName);
                island.UpdateModificationDate();
                _ = SaveToFileAsync();
                RefreshIslandStatusAsync(island);
                Log.Information("[IslandController] Position-matched house on live detection: island={Island}, laborer={Laborer}, type={Type}, tier=T{Tier}, slot={Slot}",
                    island.Name, snapshot.FullName, snapshot.LaborerType, snapshot.BuildingTier, slotIndex.Value);
                return true;
            }
        }
        else if (slotPlot != null && !HousePlotHasEmptySlot(slotPlot.Configuration))
        {
            // Card fully configured for this slot. Check if the detected laborer still matches;
            // if not, the user has swapped a laborer — overwrite the stale slot.
            if (!HousePlotMatchesLaborer(slotPlot, snapshot) && !HousePlotMatchesLaborerByName(slotPlot, snapshot))
            {
                if (TryOverwriteHousePlotSlotForSwap(slotPlot, snapshot))
                {
                    PurgeDuplicateLaborerName(island, slotPlot, snapshot.FullName);
                    island.UpdateModificationDate();
                    _ = SaveToFileAsync();
                    RefreshIslandStatusAsync(island);
                    Log.Information("[IslandController] Laborer swap detected at position-matched house: island={Island}, laborer={Laborer}, type={Type}, tier=T{Tier}, slot={Slot}",
                        island.Name, snapshot.FullName, snapshot.LaborerType, snapshot.BuildingTier, slotIndex.Value);
                }
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.FullName))
            {
                if (PurgeDuplicateLaborerName(island, slotPlot, snapshot.FullName))
                {
                    island.UpdateModificationDate();
                    _ = SaveToFileAsync();
                }
            }
            return true;
        }

        return false;
    }

    private bool TryMatchHousePlotByLaborerName(Island.Island island, LaborerSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.FullName))
            return false;

        var namePlot = island.Plots.FirstOrDefault(p =>
            p.PlotType == PlotType.House && HousePlotMatchesLaborerByName(p, snapshot));
        if (namePlot == null) return false;

        var changed = PurgeDuplicateLaborerName(island, namePlot, snapshot.FullName);

        // Also assign MapSlotIndex from world position when it's missing (e.g. after a slot reset).
        var slotAssigned = false;
        if (!namePlot.MapSlotIndex.HasValue && snapshot.WorldPosition.HasValue)
        {
            var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
            var slotIndex = layout?.WorldToNearestSlot(snapshot.WorldPosition.Value.X, snapshot.WorldPosition.Value.Y, requireLarge: null);
            if (slotIndex.HasValue && !island.Plots.Any(p => p.MapSlotIndex == slotIndex.Value))
            {
                namePlot.MapSlotIndex = slotIndex.Value;
                changed = true;
                slotAssigned = true;
                Log.Information("[IslandController] Name-matched house re-anchored to slot {Slot} for laborer {Name}", slotIndex.Value, snapshot.FullName);
            }
        }

        if (changed)
        {
            island.UpdateModificationDate();
            _ = SaveToFileAsync();
            // Full binding rebuild needed when slot was re-assigned so cards re-sort and labels update.
            if (slotAssigned)
                RefreshBindingsAsync();
            else
                RefreshIslandStatusAsync(island);
        }

        return true;
    }

    private bool TryEnrichHousePlotByTypeMatch(Island.Island island, LaborerSnapshot snapshot)
    {
        try
        {
            // Secondary: type+name match against already-configured cards (useful on re-visit when position
            // resolves to a card that is already fully filled, so we only need to update tier/name if changed).
            foreach (var plot in island.Plots.Where(p => p.PlotType == PlotType.House))
            {
                var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);

                for (var slot = 1; slot <= 3; slot++)
                {
                    if (!config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var laborerValue)
                        || string.IsNullOrWhiteSpace(laborerValue)
                        || string.Equals(laborerValue, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var configuredType = LaborerConfigHelper.NormalizeLaborerType(laborerValue);
                    var detectedType = LaborerConfigHelper.NormalizeLaborerType(snapshot.LaborerType);
                    if (!string.Equals(configuredType, detectedType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var digits = new string((config.TryGetValue(LaborerConfigHelper.JournalTierKey(slot), out var tierVal) ? tierVal : string.Empty).Where(char.IsDigit).ToArray());
                    var tierChanged = !int.TryParse(digits, out var configuredTier) || configuredTier != snapshot.BuildingTier;
                    var nameKey = LaborerConfigHelper.LaborerNameKey(slot);
                    var storedName = config.TryGetValue(nameKey, out var sn) ? sn : string.Empty;
                    var nameChanged = !string.IsNullOrWhiteSpace(snapshot.FullName)
                        && !string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(storedName),
                            LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName),
                            StringComparison.OrdinalIgnoreCase);

                    // Don't overwrite with a name that already exists in a different house card —
                    // that would duplicate the laborer across two cards.
                    if (nameChanged && IsLaborerNameInAnyOtherHousePlot(island, plot, snapshot.FullName))
                        nameChanged = false;

                    if (tierChanged || nameChanged)
                    {
                        if (tierChanged)
                            config[LaborerConfigHelper.JournalTierKey(slot)] = $"Tier {snapshot.BuildingTier}";
                        if (nameChanged)
                            config[nameKey] = snapshot.FullName;
                        plot.Configuration = LaborerConfigHelper.BuildConfiguration(config);
                        PurgeDuplicateLaborerName(island, plot, snapshot.FullName);
                        island.UpdateModificationDate();
                        _ = SaveToFileAsync();
                        RefreshIslandStatusAsync(island);
                        Log.Information("[IslandController] Enriched house plot config from type-match: island={Island}, laborer={Laborer}, slot={Slot}",
                            island.Name, snapshot.FullName, slot);
                    }
                    else if (!string.IsNullOrWhiteSpace(snapshot.FullName))
                    {
                        // No write needed, but purge stale duplicates if this card is the authority for the name.
                        if (PurgeDuplicateLaborerName(island, plot, snapshot.FullName))
                        {
                            island.UpdateModificationDate();
                            _ = SaveToFileAsync();
                        }
                    }
                    return true; // type (and tier) matched — this snapshot belongs to this plot
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to auto-adjust house tier for island {Island}", island?.Name);
        }

        return false;
    }

    private static bool HousePlotMatchesLaborerByName(IslandPlot plot, LaborerSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.FullName)) return false;
        var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
        var normalizedName = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);
        for (var slot = 1; slot <= 3; slot++)
        {
            if (config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var storedName)
                && !string.IsNullOrWhiteSpace(storedName)
                && string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(storedName), normalizedName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsLaborerNameInAnyOtherHousePlot(Island.Island island, IslandPlot excludePlot, string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        var normalized = LaborerConfigHelper.NormalizeLaborerFullName(fullName);
        foreach (var plot in island.Plots.Where(p => p.PlotType == PlotType.House && p.Id != excludePlot.Id))
        {
            var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
            for (var slot = 1; slot <= 3; slot++)
            {
                if (config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var storedName)
                    && !string.IsNullOrWhiteSpace(storedName)
                    && string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(storedName), normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // Removes fullName from all house plots OTHER than authorityPlot.
    // Returns true if any config was changed.
    private static bool PurgeDuplicateLaborerName(Island.Island island, IslandPlot authorityPlot, string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        var normalized = LaborerConfigHelper.NormalizeLaborerFullName(fullName);
        var changed = false;
        foreach (var plot in island.Plots.Where(p => p.PlotType == PlotType.House && p.Id != authorityPlot.Id))
        {
            var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
            var plotChanged = false;
            for (var slot = 1; slot <= 3; slot++)
            {
                if (!config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var storedName)
                    || string.IsNullOrWhiteSpace(storedName))
                    continue;
                if (!string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(storedName), normalized, StringComparison.OrdinalIgnoreCase))
                    continue;
                config[LaborerConfigHelper.LaborerNameKey(slot)] = string.Empty;
                plotChanged = true;
            }
            if (plotChanged)
            {
                plot.Configuration = LaborerConfigHelper.BuildConfiguration(config);
                changed = true;
                Log.Information("[IslandController] Purged duplicate laborer name '{Name}' from house plot {PlotId}", fullName, plot.Id);
            }
        }
        return changed;
    }

    private static bool HousePlotMatchesLaborer(IslandPlot plot, LaborerSnapshot snapshot)
    {
        var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
        for (var slot = 1; slot <= 3; slot++)
        {
            if (!config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var laborerValue)
                || !config.TryGetValue(LaborerConfigHelper.JournalTierKey(slot), out var tierValue))
                continue;

            var configuredType = LaborerConfigHelper.NormalizeLaborerType(laborerValue);
            var detectedType = LaborerConfigHelper.NormalizeLaborerType(snapshot.LaborerType);
            if (!string.Equals(configuredType, detectedType, StringComparison.OrdinalIgnoreCase))
                continue;

            var digits = new string(tierValue.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var configuredTier) && configuredTier == snapshot.BuildingTier)
                return true;
        }
        return false;
    }

    private static bool HousePlotHasEmptySlot(string configuration)
    {
        var config = LaborerConfigHelper.ParseConfiguration(configuration);
        for (var slot = 1; slot <= 3; slot++)
        {
            if (!config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var laborerValue)
                || string.IsNullOrWhiteSpace(laborerValue)
                || string.Equals(laborerValue, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryAutofillHousePlot(IslandPlot plot, LaborerSnapshot snapshot)
    {
        var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
        for (var slot = 1; slot <= 3; slot++)
        {
            if (config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var laborerValue)
                && !string.IsNullOrWhiteSpace(laborerValue)
                && !string.Equals(laborerValue, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                continue;

            var displayType = LaborerConfigHelper.ToDisplayLaborerType(snapshot.LaborerType);
            config[LaborerConfigHelper.LaborerKey(slot)] = displayType;
            config[LaborerConfigHelper.JournalKey(slot)] = LaborerConfigHelper.GetJournalName(snapshot.LaborerType, displayType);
            config[LaborerConfigHelper.LaborerNameKey(slot)] = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);
            config[LaborerConfigHelper.JournalTierKey(slot)] = $"Tier {snapshot.BuildingTier}";
            plot.Configuration = LaborerConfigHelper.BuildConfiguration(config);
            return true;
        }
        return false;
    }

    // Overwrites the first slot whose laborer name or type doesn't match the incoming snapshot.
    // Called when a position-matched house has no empty slots but the detected laborer is unknown —
    // indicating the user swapped a laborer. Stale dispatch/loot data is cleared for the replaced slot.
    private static bool TryOverwriteHousePlotSlotForSwap(IslandPlot plot, LaborerSnapshot snapshot)
    {
        var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
        var detectedType = LaborerConfigHelper.NormalizeLaborerType(snapshot.LaborerType);
        var detectedName = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);

        for (var slot = 1; slot <= 3; slot++)
        {
            var storedType = config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var tv)
                ? LaborerConfigHelper.NormalizeLaborerType(tv) : string.Empty;
            var storedName = config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var nv)
                ? LaborerConfigHelper.NormalizeLaborerFullName(nv) : string.Empty;

            var typeMatches = !string.IsNullOrEmpty(storedType) && string.Equals(storedType, detectedType, StringComparison.OrdinalIgnoreCase);
            var nameMatches = !string.IsNullOrEmpty(storedName) && !string.IsNullOrEmpty(detectedName)
                && string.Equals(storedName, detectedName, StringComparison.OrdinalIgnoreCase);

            if (typeMatches || nameMatches) continue;

            var displayType = LaborerConfigHelper.ToDisplayLaborerType(snapshot.LaborerType);
            config[LaborerConfigHelper.LaborerKey(slot)] = displayType;
            config[LaborerConfigHelper.JournalKey(slot)] = LaborerConfigHelper.GetJournalName(snapshot.LaborerType, displayType);
            config[LaborerConfigHelper.LaborerNameKey(slot)] = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);
            config[LaborerConfigHelper.JournalTierKey(slot)] = $"Tier {snapshot.BuildingTier}";
            config.Remove(LaborerConfigHelper.DispatchTimeKey(slot));
            config.Remove(LaborerConfigHelper.LootReadyKey(slot));
            plot.Configuration = LaborerConfigHelper.BuildConfiguration(config);
            return true;
        }
        return false;
    }

    public IslandSessionSuggestion BuildSessionSuggestion()
    {
        if (string.IsNullOrWhiteSpace(_sessionIslandName) && _sessionBuildingCounts.IsEmpty)
            return _lastIslandSuggestion;

        var plotCounts = new Dictionary<PlotType, int>();
        foreach (var (uniqueName, count) in _sessionBuildingCounts)
        {
            if (TryParseIslandPlotType(uniqueName, out var plotType))
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

    private static bool TryParseIslandPlotType(string uniqueName, out PlotType plotType)
    {
        var upper = uniqueName.ToUpperInvariant();
        if (upper.Contains("_FARM_") || upper.Contains("_CROPS_"))       { plotType = PlotType.Farm; return true; }
        if (upper.Contains("_HERB_") || upper.Contains("_HERBGARDEN_"))   { plotType = PlotType.HerbGarden; return true; }
        if (upper.Contains("_PASTURE_") || upper.Contains("_ANIMAL_"))    { plotType = PlotType.Pasture; return true; }
        if (upper.Contains("_KENNEL_") || upper.Contains("_BABY_"))       { plotType = PlotType.Kennel; return true; }
        if (upper.Contains("_HOUSE_") || upper.Contains("_LABOURER_"))    { plotType = PlotType.House; return true; }
        if (upper.Contains("_MILL_"))                                      { plotType = PlotType.Mill; return true; }
        if (upper.Contains("_SMELTER_"))                                   { plotType = PlotType.Smelter; return true; }
        if (upper.Contains("_TANNER_"))                                    { plotType = PlotType.Tanner; return true; }
        if (upper.Contains("_LUMBERMILL_") || upper.Contains("_SAWMILL_")){ plotType = PlotType.Lumbermill; return true; }
        if (upper.Contains("_STONEMASON_"))                                { plotType = PlotType.Stonemason; return true; }
        if (upper.Contains("_BUTCHER_"))                                   { plotType = PlotType.Butcher; return true; }
        if (upper.Contains("_COOK_"))                                      { plotType = PlotType.Cook; return true; }
        if (upper.Contains("_ALCHEMYLAB_") || upper.Contains("_ALCHEMY_")){ plotType = PlotType.AlchemyLab; return true; }
        if (upper.Contains("_HUNTERLODGE_") || upper.Contains("_HUNTER_")){ plotType = PlotType.HunterLodge; return true; }
        if (upper.Contains("_WARRIORGUILD_") || upper.Contains("_WARRIOR_")){ plotType = PlotType.WarriorGuild; return true; }
        if (upper.Contains("_SADDLER_") || upper.Contains("_MOUNT_"))     { plotType = PlotType.Saddler; return true; }
        if (upper.Contains("_MAGETOWER_") || upper.Contains("_MAGE_"))    { plotType = PlotType.MageTower; return true; }
        if (upper.Contains("_WEAVER_"))                                    { plotType = PlotType.Weaver; return true; }
        if (upper.Contains("_TOOLMAKER_"))                                 { plotType = PlotType.Toolmaker; return true; }
        if (upper.Contains("_REPAIR_") || upper.Contains("_REPAIRSTATION_")){ plotType = PlotType.RepairStation; return true; }
        plotType = default;
        return false;
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
        var path = AppDataPaths.UserDataFile(IslandsFileName);
        var loaded = await FileController.LoadAsync<List<IslandDto>>(path);

        List<Island.Island> islands;
        if (loaded == null || loaded.Count == 0)
        {
            islands = [];
        }
        else
        {
            islands = loaded.Select(IslandMapping.FromDto).ToList();
        }

        lock (_islandsLock)
        {
            _islands.Clear();
            _islands.AddRange(islands);
        }

        await LoadOwnerProfilesAsync();

        RefreshBindingsAsync();
        Log.Information("[IslandController] Loaded {Count} islands from file.", islands.Count);
    }

    public async Task SaveToFileAsync()
    {
        List<IslandDto> dtos;
        lock (_islandsLock)
            dtos = _islands.Select(IslandMapping.ToDto).ToList();

        DirectoryController.CreateDirectoryWhenNotExists(AppDataPaths.UserDataDirectory);
        await FileController.SaveAsync(dtos, AppDataPaths.UserDataFile(IslandsFileName));
        Log.Debug("[IslandController] Saved {Count} islands.", dtos.Count);
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
                var anyChanged = false;
                foreach (var p in sessionIsland.Plots)
                    if (p.UpdateLaborerStatuses(snapshots, sessionIsland.LastPlantedAt)) anyChanged = true;
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
                var anyChanged = false;
                foreach (var p in isl.Plots)
                    if (p.UpdateLaborerStatuses(islSnapshots, isl.LastPlantedAt)) anyChanged = true;
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

// ---------- Session suggestion ----------

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

// ---------- DTO + mapping ----------

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
    string Notes,
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
    List<IslandConsumedEntryDto> ConsumedHistory = null
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

public static class IslandMapping
{
    public static IslandDto ToDto(Island.Island isl) => new(
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
            : null
    );

    public static Island.Island FromDto(IslandDto dto)
    {
        Enum.TryParse<IslandType>(dto.IslandType, out var islandType);
        var isl = new Island.Island(dto.Name, dto.Owner, dto.Tier, dto.Biome, dto.HasPremium, dto.City, islandType);

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

    public static IslandEntry ToEntry(Island.Island isl, int sortOrder)
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
            YieldItems = new ObservableCollection<IslandYieldEntry>(isl.YieldHistory),
            ConsumedItems = new ObservableCollection<IslandConsumedEntry>(isl.ConsumedHistory)
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
            IsHouse = plot.PlotType == Island.PlotType.House,
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

    public static Island.Island NewIslandFromEntry(IslandEntry e)
    {
        var city = CityFactionToName(e.CityFaction, e.CityName);
        var island = new Island.Island(e.Name, e.OwnerName, e.Tier, e.Biome, e.HasPremium, city);
        island.TrackingEnabled = e.TrackingEnabled;
        return island;
    }

    public static void ApplyEntryToIsland(IslandEntry e, Island.Island isl)
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
