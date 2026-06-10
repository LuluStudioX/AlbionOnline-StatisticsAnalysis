using Serilog;
using System;
using System.Linq;
using System.Collections.Generic;
using StatisticsAnalysisTool.Localization;
using StatisticsAnalysisTool.ViewModels;

namespace StatisticsAnalysisTool.Island;

public class IslandPlot : BaseViewModel
{
    private PlotType _plotType;
    private int _quantity;
    private string _notes = string.Empty;
    private string _configuration = string.Empty;
    private int? _plotNumber;
    private int? _mapSlotIndex;
    private DateTime? _cachedPlotPlantedAt;
    private bool _plotPlantedAtCached;

    public Guid Id { get; } = Guid.NewGuid();

    public int? PlotNumber
    {
        get => _plotNumber;
        set
        {
            _plotNumber = value;
            OnPropertyChanged();
        }
    }

    public int? MapSlotIndex
    {
        get => _mapSlotIndex;
        set
        {
            _mapSlotIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public PlotType PlotType
    {
        get => _plotType;
        set
        {
            _plotType = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BuildingTypeName));
            OnPropertyChanged(nameof(CollectionCycleText));
            OnPropertyChanged(nameof(SlotsPerPlot));
            OnPropertyChanged(nameof(TotalSlots));
            OnPropertyChanged(nameof(PremiumEffectText));
            OnPropertyChanged(nameof(SlotDots));
        }
    }

    public string BuildingTypeName => _plotType.GetDisplayName();

    /// <summary>
    /// True when this plot type requires a large slot footprint.
    /// House/laborer buildings always need a large slot; Farm, HerbGarden, Pasture fit small slots.
    /// </summary>
    public bool IsLargePlotType() => _plotType is not (PlotType.Farm or PlotType.HerbGarden or PlotType.Pasture);

    // Prefer the physical-position number (MapSlotIndex) so a card's "#N" matches the map dot and the
    // actual house you collect; fall back to the creation-order PlotNumber when no slot is resolved yet.
    public string DisplayLabel => MapSlotIndex.HasValue
        ? $"#{MapSlotIndex} {BuildingTypeName}"
        : PlotNumber.HasValue ? $"#{PlotNumber} {BuildingTypeName}" : BuildingTypeName;

    public string CollectionCycleText
    {
        get
        {
            var hours = _plotType.GetBaseCollectionHours(_configuration);
            return hours > 0
                ? (hours % 1 == 0 ? $"{hours:0}h base cycle" : $"{hours:0.#}h base cycle")
                : string.Empty;
        }
    }

    public string PremiumEffectText => _plotType.GetPremiumEffectSummary(_configuration);

    public int Quantity
    {
        get => _quantity;
        set
        {
            _quantity = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalSlots));
        }
    }

    public int SlotsPerPlot
    {
        get
        {
            return PlotType switch
            {
                PlotType.House => 3,
                PlotType.Farm => 9,
                PlotType.HerbGarden => 9,
                PlotType.Pasture => 9,
                PlotType.Kennel => 4,
                _ => 0
            };
        }
    }

    public int TotalSlots => Math.Max(0, Quantity) * SlotsPerPlot;

    public string Configuration
    {
        get => _configuration;
        set
        {
            _configuration = value;
            _plotPlantedAtCached = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CollectionCycleText));
            OnPropertyChanged(nameof(PremiumEffectText));
            UpdateLaborerLines();
        }
    }

    public string Notes
    {
        get => _notes;
        set
        {
            _notes = value;
            OnPropertyChanged();
        }
    }

    public IslandPlot()
    {
        Id = Guid.NewGuid();
        _plotType = PlotType.House;
        Quantity = 0;
        Notes = string.Empty;
        Configuration = string.Empty;
    }

    public IslandPlot(PlotType plotType, int quantity) : this()
    {
        PlotType = plotType;
        Quantity = quantity;
    }

    public IslandPlot(PlotType plotType, int quantity, string notes, string configuration = "") : this(plotType, quantity)
    {
        Notes = notes;
        Configuration = configuration;
    }

    public string Laborer1Line { get; private set; } = string.Empty;
    public string Laborer2Line { get; private set; } = string.Empty;
    public string Laborer3Line { get; private set; } = string.Empty;
    public string Laborer1JournalTooltip { get; private set; } = string.Empty;
    public string Laborer2JournalTooltip { get; private set; } = string.Empty;
    public string Laborer3JournalTooltip { get; private set; } = string.Empty;

    private LaborerLiveStatus _laborer1Status;
    private LaborerLiveStatus _laborer2Status;
    private LaborerLiveStatus _laborer3Status;
    private string _laborer1TimeRemaining = string.Empty;
    private string _laborer2TimeRemaining = string.Empty;
    private string _laborer3TimeRemaining = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllLaborersSent
    {
        get
        {
            var n = ConfiguredLaborerCount;
            if (n == 0) return false;
            if (n >= 1 && _laborer1Status != LaborerLiveStatus.OnJob) return false;
            if (n >= 2 && _laborer2Status != LaborerLiveStatus.OnJob) return false;
            if (n >= 3 && _laborer3Status != LaborerLiveStatus.OnJob) return false;
            return true;
        }
    }

    private int ConfiguredLaborerCount =>
        (string.IsNullOrWhiteSpace(Laborer1Line) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Laborer2Line) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Laborer3Line) ? 0 : 1);

    [System.Text.Json.Serialization.JsonIgnore]
    public string Laborer1IndicatorState => ToCode(_laborer1Status);

    [System.Text.Json.Serialization.JsonIgnore]
    public string Laborer2IndicatorState => ToCode(_laborer2Status);

    [System.Text.Json.Serialization.JsonIgnore]
    public string Laborer3IndicatorState => ToCode(_laborer3Status);

    [System.Text.Json.Serialization.JsonIgnore]
    public string PlotSentState
    {
        get
        {
            if (_plotType == PlotType.House)
            {
                var n = ConfiguredLaborerCount;
                if (n == 0) return "none";
                var statuses = new[] { _laborer1Status, _laborer2Status, _laborer3Status }.Take(n);
                if (statuses.Any(s => s == LaborerLiveStatus.LootReady)) return "loot_ready";
                if (statuses.All(s => s == LaborerLiveStatus.OnJob)) return "on_job";
                return "none";
            }
            if (!_plotType.HasCollectionTimer()) return "none";
            if (!PlotPlantedAt.HasValue) return "none";
            return PlotCollectionReady ? "loot_ready" : "on_job";
        }
    }

    [System.Text.Json.Serialization.JsonIgnore] public string Laborer1TimeRemaining => _laborer1TimeRemaining;
    [System.Text.Json.Serialization.JsonIgnore] public string Laborer2TimeRemaining => _laborer2TimeRemaining;
    [System.Text.Json.Serialization.JsonIgnore] public string Laborer3TimeRemaining => _laborer3TimeRemaining;

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? PlotPlantedAt
    {
        get
        {
            if (_plotPlantedAtCached) return _cachedPlotPlantedAt;
            var dict = LaborerConfigHelper.ParseConfiguration(_configuration);
            _cachedPlotPlantedAt = dict.TryGetValue(LaborerConfigHelper.PlotPlantedAtKey, out var v)
                && LaborerConfigHelper.TryParseUtc(v, out var dt)
                ? dt
                : null;
            _plotPlantedAtCached = true;
            return _cachedPlotPlantedAt;
        }
        set
        {
            var dict = LaborerConfigHelper.ParseConfiguration(_configuration);
            if (value.HasValue)
                dict[LaborerConfigHelper.PlotPlantedAtKey] = LaborerConfigHelper.FormatUtc(value.Value);
            else
                dict.Remove(LaborerConfigHelper.PlotPlantedAtKey);
            _configuration = LaborerConfigHelper.BuildConfiguration(dict);
            _cachedPlotPlantedAt = value;
            _plotPlantedAtCached = true;
            OnPropertyChanged(nameof(Configuration));
            OnPropertyChanged(nameof(PlotPlantedAt));
            OnPropertyChanged(nameof(PlotCollectionCountdown));
            OnPropertyChanged(nameof(PlotCollectionReady));
            OnPropertyChanged(nameof(PlotSentState));
            OnPropertyChanged(nameof(SlotDots));
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool PlotCollectionReady => PlotPlantedAt.HasValue
        && _plotType.GetBaseCollectionHours(_configuration) > 0
        && DateTime.UtcNow >= PlotPlantedAt.Value.ToUniversalTime()
            .AddHours(_plotType.GetBaseCollectionHours(_configuration));

    [System.Text.Json.Serialization.JsonIgnore]
    public string PlotCollectionCountdown
    {
        get
        {
            var plantedAt = PlotPlantedAt;
            if (!plantedAt.HasValue) return string.Empty;
            var hours = _plotType.GetBaseCollectionHours(_configuration);
            if (hours <= 0) return string.Empty;
            var remaining = plantedAt.Value.ToUniversalTime().AddHours(hours) - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return LocalizationController.Translation("ISLAND_MANAGEMENT_STATUS_READY");
            var h = (int)remaining.TotalHours;
            return h > 0 ? $"{h}h {remaining.Minutes}m" : $"{remaining.Minutes}m";
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> SlotDots
    {
        get
        {
            var n = SlotsPerPlot;
            if (n <= 0) return [];
            if (PlotType == PlotType.House)
            {
                var count = ConfiguredLaborerCount;
                var dots = new List<string>(3);
                if (count >= 1) dots.Add(ToCode(_laborer1Status));
                if (count >= 2) dots.Add(ToCode(_laborer2Status));
                if (count >= 3) dots.Add(ToCode(_laborer3Status));
                return dots;
            }
            if (_plotType.HasCollectionTimer())
            {
                // Per-tile dots when live tile data is present (each slot reflects its own plant); otherwise
                // fall back to one aggregate state for all slots (off-island / before the first live update).
                if (_tilePlantedAts is { Count: > 0 })
                {
                    var dots = new List<string>(n);
                    for (var i = 0; i < n; i++)
                        dots.Add(i < _tilePlantedAts.Count ? TileState(_tilePlantedAts[i]) : "home");
                    return dots;
                }

                string state;
                if (!PlotPlantedAt.HasValue)
                    state = "home";
                else if (PlotCollectionReady)
                    state = "loot_ready";
                else
                    state = "on_job";
                return Enumerable.Repeat(state, n).ToList();
            }
            return Enumerable.Repeat("none", n).ToList();
        }
    }

    // Live per-slot planted times (one entry per occupied tile of this plot); null entry = empty slot.
    // Runtime only (not persisted); drives the per-slot SlotDots while on the island.
    private IReadOnlyList<DateTime?> _tilePlantedAts;

    /// <summary>
    /// Push the live per-tile planted times for this plot (set by IslandController from FarmableObjectInfo /
    /// plant / collect events). Each entry maps to one slot dot. Pass null/empty to revert to aggregate dots.
    /// </summary>
    public void SetTilePlantedAts(IReadOnlyList<DateTime?> tilePlantedAts)
    {
        _tilePlantedAts = tilePlantedAts;
        OnPropertyChanged(nameof(SlotDots));
        OnPropertyChanged(nameof(PlotSentState));
    }

    private string TileState(DateTime? plantedAt)
    {
        if (!plantedAt.HasValue) return "home";
        var hours = _plotType.GetBaseCollectionHours(_configuration);
        if (hours > 0 && DateTime.UtcNow >= plantedAt.Value.ToUniversalTime().AddHours(hours))
            return "loot_ready";
        return "on_job";
    }

    private static string ToCode(LaborerLiveStatus s) => s switch
    {
        LaborerLiveStatus.LootReady => "loot_ready",
        LaborerLiveStatus.OnJob => "on_job",
        LaborerLiveStatus.Home => "home",
        _ => "none"
    };

    /// <param name="slotAssignments">
    /// Pre-resolved slot -> live snapshot map for this plot, produced once per island by
    /// <see cref="IslandLaborerResolver"/> so a laborer can never light more than one card.
    /// Null/empty when the island has no live snapshots (offline path uses stored config state).
    /// </param>
    public bool UpdateLaborerStatuses(IReadOnlyList<LaborerSnapshot> snapshots, DateTime? islandLastCycleStartAt = null,
        IReadOnlyDictionary<int, LaborerSnapshot> slotAssignments = null)
    {
        var dict = LaborerConfigHelper.ParseConfiguration(_configuration);
        var configChanged = false;

        var prev1 = _laborer1Status;
        var prev2 = _laborer2Status;
        var prev3 = _laborer3Status;
        var prevT1 = _laborer1TimeRemaining;
        var prevT2 = _laborer2TimeRemaining;
        var prevT3 = _laborer3TimeRemaining;

        _laborer1Status = ResolveSlotStatus(1, snapshots, slotAssignments, dict, out var t1, ref configChanged, islandLastCycleStartAt);
        _laborer2Status = ResolveSlotStatus(2, snapshots, slotAssignments, dict, out var t2, ref configChanged, islandLastCycleStartAt);
        _laborer3Status = ResolveSlotStatus(3, snapshots, slotAssignments, dict, out var t3, ref configChanged, islandLastCycleStartAt);
        _laborer1TimeRemaining = t1;
        _laborer2TimeRemaining = t2;
        _laborer3TimeRemaining = t3;

        if (configChanged)
        {
            _configuration = LaborerConfigHelper.BuildConfiguration(dict);
            OnPropertyChanged(nameof(Configuration));
        }

        var statusChanged = prev1 != _laborer1Status || prev2 != _laborer2Status || prev3 != _laborer3Status
            || prevT1 != _laborer1TimeRemaining || prevT2 != _laborer2TimeRemaining || prevT3 != _laborer3TimeRemaining;
        if (statusChanged || configChanged)
            NotifyStatusChanged();

        return configChanged;
    }

    private void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(AllLaborersSent));
        OnPropertyChanged(nameof(PlotSentState));
        OnPropertyChanged(nameof(Laborer1IndicatorState));
        OnPropertyChanged(nameof(Laborer2IndicatorState));
        OnPropertyChanged(nameof(Laborer3IndicatorState));
        OnPropertyChanged(nameof(SlotDots));
        OnPropertyChanged(nameof(Laborer1TimeRemaining));
        OnPropertyChanged(nameof(Laborer2TimeRemaining));
        OnPropertyChanged(nameof(Laborer3TimeRemaining));
        OnPropertyChanged(nameof(PlotCollectionCountdown));
        OnPropertyChanged(nameof(PlotCollectionReady));
    }

    private LaborerLiveStatus ResolveSlotStatus(
        int slot,
        IReadOnlyList<LaborerSnapshot> snapshots,
        IReadOnlyDictionary<int, LaborerSnapshot> slotAssignments,
        Dictionary<string, string> dict,
        out string timeRemaining,
        ref bool configChanged,
        DateTime? islandLastCycleStartAt)
    {
        timeRemaining = string.Empty;
        if (PlotType != PlotType.House) return LaborerLiveStatus.None;

        // No live snapshots for this island — fall back to stored dispatch/loot-ready state.
        if (snapshots == null || snapshots.Count == 0)
            return MatchStatusOffline(slot, dict, islandLastCycleStartAt, out timeRemaining);

        // Live: render only the snapshot the island-wide resolver assigned to this slot.
        if (slotAssignments != null && slotAssignments.TryGetValue(slot, out var match) && match != null)
            return RenderLiveStatus(slot, match, dict, out timeRemaining, ref configChanged);

        // Snapshots present but this slot wasn't matched (resolver miss / not yet broadcast): keep the
        // persisted state instead of flashing Home, so a known Loot Ready survives the live handoff.
        return MatchStatusOffline(slot, dict, islandLastCycleStartAt, out timeRemaining);
    }

    private LaborerLiveStatus MatchStatusOffline(
        int slot,
        Dictionary<string, string> dict,
        DateTime? islandLastCycleStartAt,
        out string timeRemaining)
    {
        timeRemaining = string.Empty;

        if (PlotType != PlotType.House) return LaborerLiveStatus.None;

        // Derive from the persisted anchor (ReadyAtUtc) — single source of truth, same as the live path.
        DateTime? readyAtUtc = null;
        if (dict.TryGetValue(LaborerConfigHelper.DispatchTimeKey(slot), out var dtStr)
            && LaborerConfigHelper.TryParseUtc(dtStr, out var parsedDispatch))
        {
            // Stored DispatchTime is the laborer's ready-at time (JobDispatchTime), already the end of
            // the cycle — use it directly. Adding the cycle again double-counts (~44h instead of ~22h).
            readyAtUtc = parsedDispatch.ToUniversalTime();
        }

        if (readyAtUtc.HasValue)
        {
            var remaining = readyAtUtc.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                timeRemaining = FormatRemaining(remaining);
                return LaborerLiveStatus.OnJob;
            }
            // Loot stays collectable for a fixed window after the return; only after it lapses uncollected
            // (e.g. a long break with no collection) does the laborer revert to idle/home.
            if (DateTime.UtcNow < readyAtUtc.Value.AddHours(IslandConstants.LaborerLootExpiryHours))
                return LaborerLiveStatus.LootReady;
            return LaborerLiveStatus.Home;
        }

        // Legacy fallback: configs saved before the anchor change only stored a derived loot-ready bool.
        if (dict.TryGetValue(LaborerConfigHelper.LootReadyKey(slot), out var lrVal)
            && string.Equals(lrVal, "true", StringComparison.OrdinalIgnoreCase))
            return LaborerLiveStatus.LootReady;

        return LaborerLiveStatus.None;
    }

    private LaborerLiveStatus RenderLiveStatus(
        int slot,
        LaborerSnapshot match,
        Dictionary<string, string> dict,
        out string timeRemaining,
        ref bool configChanged)
    {
        timeRemaining = string.Empty;

        if (dict.TryGetValue(LaborerConfigHelper.JournalTierKey(slot), out var storedTierText))
        {
            var storedDigits = new string(storedTierText.Where(char.IsDigit).ToArray());
            if (int.TryParse(storedDigits, out var storedTier) && storedTier != match.BuildingTier)
            {
                dict[LaborerConfigHelper.JournalTierKey(slot)] = $"Tier {match.BuildingTier}";
                configChanged = true;
            }
        }

        // Persist ONLY the anchor (ReadyAtUtc = param 8, else JobStartTime + cycle). On-job, loot-ready and
        // time-left are all derived from it — live here and offline in MatchStatusOffline — so there is no
        // separate derived flag to store or keep in sync. The anchor is present iff the laborer has an active
        // job (on-job OR loot-ready) and is cleared when home so offline state can never go stale.
        var readyAt = match.ReadyAtUtc;
        if (!readyAt.HasValue)
        {
            // Fresh/incomplete snapshot — the job packets (56 param 8 / 57) haven't arrived yet on this
            // visit. Don't flash Home or wipe the stored anchor: keep rendering the persisted state (Loot
            // Ready / On Job until the loot expires) so the zone-in "yellow → grey" flicker can't happen.
            // A real new job overwrites the anchor below once its ReadyAtUtc arrives.
            return MatchStatusOffline(slot, dict, null, out timeRemaining);
        }

        if (match.IsOnJob)
        {
            var remaining = readyAt.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                timeRemaining = FormatRemaining(remaining);
        }

        var newDispatch = LaborerConfigHelper.FormatUtc(readyAt.Value);
        if (!dict.TryGetValue(LaborerConfigHelper.DispatchTimeKey(slot), out var existing) || existing != newDispatch)
        {
            dict[LaborerConfigHelper.DispatchTimeKey(slot)] = newDispatch;
            configChanged = true;
        }

        // Migrate away the old derived loot-ready bool — it is now derived from the anchor.
        if (dict.Remove(LaborerConfigHelper.LootReadyKey(slot)))
            configChanged = true;

        if (match.IsLootReady) return LaborerLiveStatus.LootReady;
        if (match.IsOnJob) return LaborerLiveStatus.OnJob;
        return LaborerLiveStatus.Home;
    }

    private static string FormatRemaining(TimeSpan ts)
    {
        var h = (int)ts.TotalHours;
        return h > 0 ? $"{h}h {ts.Minutes}m" : $"{ts.Minutes}m";
    }

    private void UpdateLaborerLines()
    {
        try
        {
            var dict = LaborerConfigHelper.ParseConfiguration(Configuration);
            for (var slot = 1; slot <= 3; slot++)
            {
                var rawType = dict.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var v) ? v : string.Empty;
                var tierTextRaw = dict.TryGetValue(LaborerConfigHelper.JournalTierKey(slot), out var t) ? t : string.Empty;
                var digits = new string((tierTextRaw ?? string.Empty).Where(char.IsDigit).ToArray());
                var tierDisplay = !string.IsNullOrWhiteSpace(digits) ? $"T{digits}" : string.Empty;

                string display;
                string journalTooltip = string.Empty;
                if (!string.IsNullOrWhiteSpace(rawType) && !string.Equals(rawType, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                {
                    var laborerName = dict.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var nv) && !string.IsNullOrWhiteSpace(nv) ? nv : string.Empty;

                    var typePart = IslandLaborerProfessions.GetProfession(rawType);
                    display = string.IsNullOrWhiteSpace(tierDisplay)
                        ? typePart
                        : $"{tierDisplay} {typePart}";
                    if (!string.IsNullOrWhiteSpace(laborerName))
                        display += $" - {laborerName}";

                    var journalName = dict.TryGetValue(LaborerConfigHelper.JournalKey(slot), out var jv) && !string.IsNullOrWhiteSpace(jv)
                        ? jv
                        : LaborerConfigHelper.GetJournalName(rawType);

                    if (!string.IsNullOrWhiteSpace(journalName))
                    {
                        journalTooltip = $"Uses {journalName}";
                    }
                }
                else
                {
                    display = string.Empty;
                }

                switch (slot)
                {
                    case 1:
                        Laborer1Line = display;
                        Laborer1JournalTooltip = journalTooltip;
                        OnPropertyChanged(nameof(Laborer1Line));
                        OnPropertyChanged(nameof(Laborer1JournalTooltip));
                        break;
                    case 2:
                        Laborer2Line = display;
                        Laborer2JournalTooltip = journalTooltip;
                        OnPropertyChanged(nameof(Laborer2Line));
                        OnPropertyChanged(nameof(Laborer2JournalTooltip));
                        break;
                    case 3:
                        Laborer3Line = display;
                        Laborer3JournalTooltip = journalTooltip;
                        OnPropertyChanged(nameof(Laborer3Line));
                        OnPropertyChanged(nameof(Laborer3JournalTooltip));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[IslandPlot] UpdateLaborerLines failed for plot {PlotType}", _plotType);
            Laborer1Line = Laborer2Line = Laborer3Line = string.Empty;
            Laborer1JournalTooltip = Laborer2JournalTooltip = Laborer3JournalTooltip = string.Empty;
            OnPropertyChanged(nameof(Laborer1Line));
            OnPropertyChanged(nameof(Laborer2Line));
            OnPropertyChanged(nameof(Laborer3Line));
            OnPropertyChanged(nameof(Laborer1JournalTooltip));
            OnPropertyChanged(nameof(Laborer2JournalTooltip));
            OnPropertyChanged(nameof(Laborer3JournalTooltip));
        }
    }
}

public enum LaborerLiveStatus
{
    None,
    Home,
    LootReady,
    OnJob
}
