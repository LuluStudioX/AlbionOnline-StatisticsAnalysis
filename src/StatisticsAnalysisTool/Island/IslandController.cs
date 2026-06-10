using Serilog;
using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Network.Manager;
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

public partial class IslandController
{
    private static readonly PlotType[] FarmPlotTypes =
        [PlotType.Farm, PlotType.HerbGarden, PlotType.Pasture, PlotType.Kennel];

    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IslandYieldTracker _yieldTracker;
    private readonly IslandWebhookService _webhookService = new();
    private TrackingController _trackingController;
    private readonly List<Island> _islands = [];
    private readonly object _islandsLock = new();

    private readonly ConcurrentDictionary<long, LaborerSnapshot> _snapshots = new();
    private readonly List<LaborerSnapshot> _snapshotsByOrder = new();
    private readonly object _snapshotOrderLock = new();
    private long _detectionCounter;
    // 0 = not yet sent this session, 1 = sent. Guarded with Interlocked so two push threads can't both
    // observe "not sent" and both fire the owner collection-ready webhook (G6a).
    private int _collectionReadyWebhookSentThisSession;
    private System.Windows.Threading.DispatcherTimer _countdownTimer;
    private System.Windows.Threading.DispatcherTimer _transitionTimer;
    private volatile System.Threading.Timer _pushDebounceTimer;
    private const int PushDebounceMs = 200;

    // Single debounced island writer. CRUD/push/yield paths request a save; bursts coalesce into one
    // snapshot-at-fire-time write so a slower older snapshot can never overwrite a newer one (G3).
    private volatile System.Threading.Timer _saveDebounceTimer;
    private const int SaveDebounceMs = 300;

    // Farmable state-change dedup keyed by ObjectId.
    private readonly ConcurrentDictionary<long, string> _farmableSignatures = new();

    // 5-min snapshot cache so UI stays populated briefly after loot collection
    private readonly object _lastSnapshotLock = new();
    // Keyed by the island's stable Id, not its user-editable Name — two islands the user named identically
    // must not cross-serve each other's cached laborers within the 5-min window (C3).
    private Guid _lastSnapshotIslandId = Guid.Empty;
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
        _saveDebounceTimer?.Dispose();
        _saveDebounceTimer = null;
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

    private readonly ConcurrentDictionary<string, int> _sessionBuildingCounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _sessionHasPremium;
    private string _sessionIslandName;
    private string _sessionOwner;
    private string _sessionWorldMapDataType;
    private string _sessionSourceClusterIndex;
    private readonly object _consumedTilesLock = new();
    // Tile positions ("islandId|x|y") COLLECTED this app run and awaiting a replant. A seed is only "consumed"
    // when you replant a tile you just harvested, so a plant (code 45) is booked as consumed ONLY when its
    // position is in this set. A first-ever sighting is never here, so the zone-in burst that re-broadcasts
    // every pre-existing plant — each carrying param-8 = server-now, indistinguishable from a real plant by
    // timestamp alone — is correctly ignored. HandleFarmableCollect adds the position on collect; the matching
    // replant removes it. Position is stable across the per-visit object-id churn; the set is island-scoped
    // (keyed by island id) and deliberately persists for the app run.
    private readonly HashSet<string> _collectedTilesAwaitingReplant = [];
    // Serializes the read-baseline/compute-delta/update-baseline RMW on the two yield qty maps below, so
    // two overlapping same-ObjectId broadcasts can't both measure their delta off the same stale baseline
    // and double-count the growth (G6d).
    private readonly object _yieldQtyLock = new();

    // Islands already warned that position-based slot matching is unavailable (no calibrated layout, e.g.
    // guild islands) — keeps the warning to once per island instead of every status push (G8a).
    private readonly object _positionMatchWarnedLock = new();
    private readonly HashSet<Guid> _positionMatchWarnedIslandIds = [];
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
        // Yield baselines reset per island session; the awaiting-replant tile set deliberately persists so a
        // re-joined, already-handled island does not re-book its existing plantings as freshly consumed.
        _lastItemQty.Clear();
        _lastJournalQty.Clear();
        System.Threading.Volatile.Write(ref _lastLaborerCollectTicks, 0);
        lock (_pendingYieldLock) _pendingYield.Clear();
        _farmablePositions.Clear();
        _plotTilePlanted.Clear();
        Interlocked.Exchange(ref _collectionReadyWebhookSentThisSession, 0);
        Interlocked.Exchange(ref _detectionCounter, 0);
        lock (_lastSnapshotLock)
        {
            _lastSnapshotList.Clear();
            _lastSnapshotIslandId = Guid.Empty;
            _lastSnapshotUtc = DateTime.MinValue;
        }
        ExecutePushAllIslandsStatus();
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
}
