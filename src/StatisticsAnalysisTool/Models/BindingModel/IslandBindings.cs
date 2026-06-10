using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Common.UserSettings;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.ViewModels;
using System.Windows;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Collections.Specialized;
using System.Windows.Data;

namespace StatisticsAnalysisTool.Models.BindingModel;

public partial class IslandBindings : BaseViewModel
{
    public IslandBindings()
    {
        Islands = new ObservableCollection<IslandEntry>();
        IslandsCollectionView = CollectionViewSource.GetDefaultView(Islands) as ListCollectionView;

        LiveLaborerActionRows = new ObservableCollection<LiveLaborerActionEntry>();
        LiveSummarySegments = new ObservableCollection<LiveSummarySegment>();

        LaborerHistorySeries = new ObservableCollection<ISeries>();
        LaborerHistoryXAxes = MockXAxes();

        Preferences = SettingsController.CurrentSettings.IslandManagementPreferences ?? new IslandManagementPreferences();
        _showIslandCityColors = SettingsController.CurrentSettings.ShowIslandCityColors;
        _showIslandBiome = SettingsController.CurrentSettings.ShowIslandBiome;
        _showIslandEditButtons = SettingsController.CurrentSettings.ShowIslandEditButtons;

        _groupMode = (IslandGroupMode) SettingsController.CurrentSettings.IslandGroupMode;
        _selectedGroupModeOption = AvailableGroupModes.FirstOrDefault(x => x.Value == _groupMode);
        _sortMode = (IslandSortMode) SettingsController.CurrentSettings.IslandSortMode;
        _selectedSortModeOption = AvailableSortModes.FirstOrDefault(x => x.Value == _sortMode);
    }

    public void LoadFrom(IEnumerable<IslandEntry> entries)
    {
        var previousId = _selectedIsland?.IslandId;
        _pendingPlotId = _selectedPlot?.PlotId;

        Islands.Clear();
        foreach (var entry in entries)
            Islands.Add(entry);

        ApplySorting();
        OnPropertyChanged(nameof(GlobalHandlingTimeStatusText));

        var reselected = previousId.HasValue
            ? Islands.FirstOrDefault(i => i.IslandId == previousId.Value)
            : null;
        SelectedIsland = reselected ?? (Islands.Count == 1 ? Islands[0] : null);

        if (_pendingPlotId.HasValue)
        {
            var restored = SelectedIsland?.Plots.FirstOrDefault(p => p.PlotId == _pendingPlotId.Value);
            if (restored != null) SelectedPlot = restored;
            _pendingPlotId = null;
        }
    }

    public ObservableCollection<IslandEntry> Islands { get; }
    public ListCollectionView IslandsCollectionView { get; }

    private IslandEntry _selectedIsland;
    public IslandEntry SelectedIsland
    {
        get => _selectedIsland;
        set
        {
            if (_selectedIsland != null)
            {
                _selectedIsland.IsSelected = false;
                _selectedIsland.YieldItems.CollectionChanged -= OnSelectedIslandYieldChanged;
                _selectedIsland.ConsumedItems.CollectionChanged -= OnSelectedIslandConsumedChanged;
                _selectedIsland.PropertyChanged -= OnSelectedIslandPropertyChanged;
            }
            _selectedIsland = value;
            if (_selectedIsland != null)
            {
                _selectedIsland.IsSelected = true;
                _selectedIsland.YieldItems.CollectionChanged += OnSelectedIslandYieldChanged;
                _selectedIsland.ConsumedItems.CollectionChanged += OnSelectedIslandConsumedChanged;
                // Yield/consumed updates REPLACE these collections (new instance), which silently
                // drops the CollectionChanged subscription above. Re-attach on replacement so the
                // pricing Outputs/Inputs rebuild instead of going stale.
                _selectedIsland.PropertyChanged += OnSelectedIslandPropertyChanged;
            }
            _liveRowTimestamps.Clear();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIslandSelected));
            OnPropertyChanged(nameof(SelectedIslandSummary));
            if (Preferences?.AllowMultiOwnerSelection != true)
            {
                _selectedOverviewOwner = _selectedIsland?.OwnerName?.Trim() ?? string.Empty;
                OnPropertyChanged(nameof(SelectedOverviewOwner));
            }
            RefreshOwnerProfile();
            if (_selectedIsland != null)
                GetController()?.OnIslandManuallySelected(_selectedIsland.IslandId);
            RebuildPricingRows();
        }
    }

    public bool IsIslandSelected => SelectedIsland != null;

    public ObservableCollection<IslandYieldPricingRow> SelectedYieldPricingRows { get; } = new();
    public ObservableCollection<IslandConsumedPricingRow> SelectedConsumedPricingRows { get; } = new();

    private void OnSelectedIslandYieldChanged(object sender, NotifyCollectionChangedEventArgs e) => RebuildPricingRows();
    private void OnSelectedIslandConsumedChanged(object sender, NotifyCollectionChangedEventArgs e) => RebuildPricingRows();

    private void OnSelectedIslandPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_selectedIsland == null) return;
        // The yield/consumed collections are replaced wholesale on update; re-attach CollectionChanged
        // to the new instance and rebuild the pricing rows so Outputs/Inputs stay in sync.
        if (e.PropertyName == nameof(IslandEntry.YieldItems))
        {
            _selectedIsland.YieldItems.CollectionChanged -= OnSelectedIslandYieldChanged;
            _selectedIsland.YieldItems.CollectionChanged += OnSelectedIslandYieldChanged;
            RebuildPricingRows();
        }
        else if (e.PropertyName == nameof(IslandEntry.ConsumedItems))
        {
            _selectedIsland.ConsumedItems.CollectionChanged -= OnSelectedIslandConsumedChanged;
            _selectedIsland.ConsumedItems.CollectionChanged += OnSelectedIslandConsumedChanged;
            RebuildPricingRows();
        }
    }

    private void RebuildPricingRows()
    {
        SelectedYieldPricingRows.Clear();
        SelectedConsumedPricingRows.Clear();
        if (_selectedIsland == null) return;
        var prefs = Preferences;
        foreach (var entry in _selectedIsland.YieldItems)
            SelectedYieldPricingRows.Add(new IslandYieldPricingRow(entry, prefs));
        foreach (var entry in _selectedIsland.ConsumedItems)
            SelectedConsumedPricingRows.Add(new IslandConsumedPricingRow(entry, prefs));
    }

    private Guid? _pendingPlotId;

    private IslandPlotEntry _selectedPlot;
    public IslandPlotEntry SelectedPlot
    {
        get => _selectedPlot;
        set
        {
            if (_selectedPlot != null) _selectedPlot.IsSelected = false;
            _selectedPlot = value;
            if (_selectedPlot != null) _selectedPlot.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlotSelected));
            OnPropertyChanged(nameof(SelectedPlotCollectionCycleText));
            PlotDetailPanelHeight = value != null
                ? new GridLength(SettingsController.CurrentSettings.IslandManagementPlotDetailPanelHeight)
                : new GridLength(0);
        }
    }

    public bool IsPlotSelected => SelectedPlot != null;

    private GridLength _plotDetailPanelHeight = new(0);
    public GridLength PlotDetailPanelHeight
    {
        get => _plotDetailPanelHeight;
        set
        {
            _plotDetailPanelHeight = value;
            OnPropertyChanged();
            if (value.Value > 0)
            {
                SettingsController.CurrentSettings.IslandManagementPlotDetailPanelHeight = value.Value;
                _ = SettingsController.SaveSettingsAsync();
            }
        }
    }

    public string SelectedPlotCollectionCycleText
    {
        get
        {
            var plotId = SelectedPlot?.PlotId;
            if (plotId == null || SelectedIsland == null) return string.Empty;
            var controller = GetController();
            var island = controller?.GetById(SelectedIsland.IslandId);
            var plot = island?.Plots.FirstOrDefault(p => p.Id == plotId);
            return plot?.CollectionCycleText ?? string.Empty;
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; OnPropertyChanged(); ApplyFilter(); }
    }

    private bool _showIslandCityColors = true;
    public bool ShowIslandCityColors
    {
        get => _showIslandCityColors;
        set
        {
            _showIslandCityColors = value;
            OnPropertyChanged();
            SettingsController.CurrentSettings.ShowIslandCityColors = value;
            _ = SettingsController.SaveSettingsAsync();
        }
    }

    private bool _showIslandBiome = true;
    public bool ShowIslandBiome
    {
        get => _showIslandBiome;
        set
        {
            _showIslandBiome = value;
            OnPropertyChanged();
            SettingsController.CurrentSettings.ShowIslandBiome = value;
            _ = SettingsController.SaveSettingsAsync();
        }
    }

    private bool _showIslandEditButtons = true;
    public bool ShowIslandEditButtons
    {
        get => _showIslandEditButtons;
        set
        {
            _showIslandEditButtons = value;
            OnPropertyChanged();
            SettingsController.CurrentSettings.ShowIslandEditButtons = value;
            _ = SettingsController.SaveSettingsAsync();
        }
    }

    private GridLength _gridSplitterPosition = new(SettingsController.CurrentSettings.IslandManagementGridSplitterPosition > 0
        ? SettingsController.CurrentSettings.IslandManagementGridSplitterPosition : 240);
    public GridLength GridSplitterPosition
    {
        get => _gridSplitterPosition;
        set
        {
            _gridSplitterPosition = value;
            SettingsController.CurrentSettings.IslandManagementGridSplitterPosition = _gridSplitterPosition.Value;
            _ = SettingsController.SaveSettingsAsync();
            OnPropertyChanged();
        }
    }

    private bool _isSettingsPanelOpen;
    public bool IsSettingsPanelOpen
    {
        get => _isSettingsPanelOpen;
        set { _isSettingsPanelOpen = value; OnPropertyChanged(); }
    }

    private IslandGroupMode _groupMode = IslandGroupMode.None;
    public IslandGroupMode GroupMode
    {
        get => _groupMode;
        set
        {
            _groupMode = value;
            OnPropertyChanged();
            _selectedGroupModeOption = AvailableGroupModes.FirstOrDefault(x => x.Value == value);
            OnPropertyChanged(nameof(SelectedGroupModeOption));
            ApplySorting();
            SettingsController.CurrentSettings.IslandGroupMode = (int) _groupMode;
            _ = SettingsController.SaveSettingsAsync();
        }
    }

    private IslandSortMode _sortMode = IslandSortMode.Custom;
    public IslandSortMode SortMode
    {
        get => _sortMode;
        set
        {
            _sortMode = value;
            OnPropertyChanged();
            _selectedSortModeOption = AvailableSortModes.FirstOrDefault(x => x.Value == value);
            OnPropertyChanged(nameof(SelectedSortModeOption));
            ApplySorting();
            SettingsController.CurrentSettings.IslandSortMode = (int) _sortMode;
            _ = SettingsController.SaveSettingsAsync();
        }
    }

    private IslandGroupModeOption _selectedGroupModeOption;
    public IslandGroupModeOption SelectedGroupModeOption
    {
        get => _selectedGroupModeOption ??= AvailableGroupModes[0];
        set { GroupMode = value?.Value ?? IslandGroupMode.None; }
    }

    private IslandSortModeOption _selectedSortModeOption;
    public IslandSortModeOption SelectedSortModeOption
    {
        get => _selectedSortModeOption ??= AvailableSortModes[0];
        set { SortMode = value?.Value ?? IslandSortMode.Custom; }
    }

    public bool IsDragHandleVisible => SortMode == IslandSortMode.Custom;

    public static IslandGroupModeOption[] AvailableGroupModes { get; } =
    [
        new(IslandGroupMode.None,    "No grouping"),
        new(IslandGroupMode.ByOwner, "Owner"),
        new(IslandGroupMode.ByCity,  "City"),
    ];

    public static IslandSortModeOption[] AvailableSortModes { get; } =
    [
        new(IslandSortMode.Custom,       "Custom (drag to reorder)"),
        new(IslandSortMode.Alphabetical, "Alphabetical"),
        new(IslandSortMode.ByCity,       "City"),
        new(IslandSortMode.ByTier,       "Tier"),
        new(IslandSortMode.ByOwner,      "Owner"),
    ];

    public void ApplySorting()
    {
        if (IslandsCollectionView == null) return;

        using (IslandsCollectionView.DeferRefresh())
        {
            IslandsCollectionView.SortDescriptions.Clear();
            IslandsCollectionView.GroupDescriptions.Clear();

            if (GroupMode == IslandGroupMode.ByOwner)
            {
                IslandsCollectionView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IslandEntry.OwnerName)));
            }
            else if (GroupMode == IslandGroupMode.ByCity)
            {
                IslandsCollectionView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IslandEntry.CityName)));
            }

            switch (SortMode)
            {
                case IslandSortMode.Alphabetical:
                    IslandsCollectionView.SortDescriptions.Add(new SortDescription(nameof(IslandEntry.Name), ListSortDirection.Ascending));
                    break;
                case IslandSortMode.ByCity:
                    IslandsCollectionView.SortDescriptions.Add(new SortDescription(nameof(IslandEntry.CityName), ListSortDirection.Ascending));
                    IslandsCollectionView.SortDescriptions.Add(new SortDescription(nameof(IslandEntry.Name), ListSortDirection.Ascending));
                    break;
                case IslandSortMode.ByTier:
                    IslandsCollectionView.SortDescriptions.Add(new SortDescription(nameof(IslandEntry.Tier), ListSortDirection.Descending));
                    IslandsCollectionView.SortDescriptions.Add(new SortDescription(nameof(IslandEntry.Name), ListSortDirection.Ascending));
                    break;
                case IslandSortMode.ByOwner:
                    IslandsCollectionView.SortDescriptions.Add(new SortDescription(nameof(IslandEntry.OwnerName), ListSortDirection.Ascending));
                    IslandsCollectionView.SortDescriptions.Add(new SortDescription(nameof(IslandEntry.Name), ListSortDirection.Ascending));
                    break;
                case IslandSortMode.Custom:
                    IslandsCollectionView.SortDescriptions.Add(new SortDescription(nameof(IslandEntry.SortOrder), ListSortDirection.Ascending));
                    break;
            }
        }

        OnPropertyChanged(nameof(IsDragHandleVisible));
    }

    private void ApplyFilter()
    {
        if (IslandsCollectionView == null) return;
        IslandsCollectionView.Filter = string.IsNullOrWhiteSpace(_searchText)
            ? null
            : o => o is IslandEntry e &&
                   (e.Name?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) == true
                 || e.OwnerName?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) == true
                 || e.CityName?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) == true);
    }

    public void MoveIsland(IslandEntry dragged, IslandEntry target)
    {
        if (dragged == null || target == null || dragged == target) return;
        var draggedIdx = Islands.IndexOf(dragged);
        var targetIdx = Islands.IndexOf(target);
        if (draggedIdx < 0 || targetIdx < 0) return;
        Islands.Move(draggedIdx, targetIdx);
        for (int i = 0; i < Islands.Count; i++)
            Islands[i].SortOrder = i;
        IslandsCollectionView?.Refresh();
    }

    private IslandManagementPreferences _preferences;
    public IslandManagementPreferences Preferences
    {
        get => _preferences;
        set
        {
            if (_preferences != null)
                _preferences.PropertyChanged -= OnPreferenceChanged;
            _preferences = value;
            if (_preferences != null)
                _preferences.PropertyChanged += OnPreferenceChanged;
            OnPropertyChanged();
        }
    }

    private void OnPreferenceChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Common.UserSettings.SettingsController.CurrentSettings.IslandManagementPreferences = _preferences;
        _ = Common.UserSettings.SettingsController.SaveSettingsAsync();
        OnPropertyChanged(nameof(DiscordEmbedPreview));
        if (e.PropertyName == nameof(IslandManagementPreferences.FinanceHistoryMaxVisible))
            ResetFinanceHistoryPage();
        if (e.PropertyName == nameof(IslandManagementPreferences.WeekStartDay))
            OnPropertyChanged(nameof(SelectedOwnerPayoutScheduleText));
    }

    public ObservableCollection<LiveLaborerActionEntry> LiveLaborerActionRows { get; }
    public ObservableCollection<LiveSummarySegment> LiveSummarySegments { get; }

    // Frozen first-seen timestamps for live status rows, keyed by stable row identity.
    // Prevents the displayed [HH:mm:ss] from resetting on every 60-second timer tick.
    private readonly Dictionary<string, DateTime> _liveRowTimestamps = new(StringComparer.Ordinal);

    private DateTime GetOrAddLiveRowTimestamp(string key)
    {
        if (!_liveRowTimestamps.TryGetValue(key, out var ts))
        {
            ts = DateTime.Now;
            _liveRowTimestamps[key] = ts;
        }
        return ts;
    }

    public void UpdateLiveStatus(IReadOnlyList<LaborerSnapshot> snapshots, IReadOnlyList<Island.Island> domainIslands = null, Guid? sessionIslandId = null)
    {
        Dictionary<Guid, Island.Island> domainById = null;
        if (domainIslands != null)
        {
            domainById = new Dictionary<Guid, Island.Island>(domainIslands.Count);
            foreach (var d in domainIslands) domainById[d.Id] = d;

            foreach (var islandEntry in Islands)
            {
                if (!domainById.TryGetValue(islandEntry.IslandId, out var domainIsland)) continue;
                if (domainIsland.Plots == null) continue;

                var domainPlotById = new Dictionary<Guid, IslandPlot>(domainIsland.Plots.Count);
                foreach (var dp in domainIsland.Plots) domainPlotById[dp.Id] = dp;

                islandEntry.CollectionStatusText = domainIsland.CollectionStatusText;
                islandEntry.CollectionStatusState = domainIsland.CollectionStatusState;
                islandEntry.NeedsVisit = domainIsland.NeedsVisit;

                foreach (var plotEntry in islandEntry.Plots)
                {
                    if (!domainPlotById.TryGetValue(plotEntry.PlotId, out var domainPlot)) continue;

                    // Keep the card "#N" in sync — slot assignment (e.g. after a Reset + re-visit)
                    // can land on a status-only refresh, not just a full rebuild.
                    var newSlotLabel = domainPlot.MapSlotIndex.HasValue
                        ? IslandLayouts.FormatSlotLabel(domainPlot.MapSlotIndex.Value)
                        : string.Empty;
                    if (plotEntry.MapSlotLabel != newSlotLabel)
                    {
                        plotEntry.MapSlotIndex = domainPlot.MapSlotIndex;
                        plotEntry.MapSlotLabel = newSlotLabel;
                    }

                    plotEntry.SlotDots = domainPlot.SlotDots;
                    plotEntry.Laborer1TimeRemaining = domainPlot.Laborer1TimeRemaining;
                    plotEntry.Laborer2TimeRemaining = domainPlot.Laborer2TimeRemaining;
                    plotEntry.Laborer3TimeRemaining = domainPlot.Laborer3TimeRemaining;
                    plotEntry.PlotCollectionCountdown = domainPlot.PlotCollectionCountdown;
                    plotEntry.Laborer1IndicatorState = domainPlot.Laborer1IndicatorState;
                    plotEntry.Laborer2IndicatorState = domainPlot.Laborer2IndicatorState;
                    plotEntry.Laborer3IndicatorState = domainPlot.Laborer3IndicatorState;
                    plotEntry.PlotSentState = domainPlot.PlotSentState;
                    plotEntry.Laborer1Line = domainPlot.Laborer1Line;
                    plotEntry.Laborer2Line = domainPlot.Laborer2Line;
                    plotEntry.Laborer3Line = domainPlot.Laborer3Line;

                    var farmableTypeLine = domainPlot.PlotType.HasFarmableConfig()
                        ? domainPlot.PlotType.GetConfiguredTypeName(domainPlot.Configuration)
                        : string.Empty;
                    if (plotEntry.FarmableTypeLine == farmableTypeLine) continue;
                    plotEntry.FarmableTypeLine = farmableTypeLine;
                    if (!string.IsNullOrWhiteSpace(farmableTypeLine))
                    {
                        var info = PlotTypeExtensions.TryResolveFarmablePlotInfoByDisplayName(domainPlot.PlotType, farmableTypeLine);
                        plotEntry.FarmableCropIcon = info != null ? ImageController.GetItemImage(info.UniqueName, 24, 24) : null;
                        plotEntry.FarmableCropTooltip = info != null ? PlotTypeExtensions.GetCropTooltip(info.UniqueName) : null;
                    }
                    else
                    {
                        plotEntry.FarmableCropIcon = null;
                        plotEntry.FarmableCropTooltip = null;
                    }
                }
            }
        }

        var list = snapshots;

        var selectedIslandId = SelectedIsland?.IslandId;
        var isSessionIsland = selectedIslandId.HasValue && selectedIslandId == sessionIslandId;

        List<LiveLaborerActionEntry> rows;

        if (isSessionIsland || sessionIslandId == null)
        {
            rows = list.Select(s =>
            {
                string action, detail;
                bool needsAttention = false;

                if (s.IsLootReady)
                {
                    action = "Loot Ready";
                    detail = string.IsNullOrEmpty(s.LaborerType) ? string.Empty : $"T{s.BuildingTier} {s.LaborerType}";
                    needsAttention = true;
                }
                else if (s.IsOnJob)
                {
                    action = "On Job";
                    detail = !string.IsNullOrEmpty(s.SentDetailSnapshot)
                        ? s.SentDetailSnapshot
                        : (s.JobDispatchTime.HasValue
                            ? LaborerSnapshot.FormatSentElapsed(DateTime.UtcNow, s.JobDispatchTime.Value.AddHours(-IslandConstants.LaborerBaseCycleHours))
                            : string.Empty);
                }
                else
                {
                    action = "Home";
                    detail = string.Empty;
                }

                return new LiveLaborerActionEntry
                {
                    Timestamp = GetOrAddLiveRowTimestamp($"laborer:{s.ObjectId}"),
                    Name = s.FullName,
                    Action = action,
                    Detail = detail,
                    NeedsAttention = needsAttention
                };
            }).ToList();
        }
        else
        {
            rows = new List<LiveLaborerActionEntry>();
            if (selectedIslandId.HasValue && domainById != null
                && domainById.TryGetValue(selectedIslandId.Value, out var selectedDomainIsland)
                && selectedDomainIsland.Plots != null)
            {
                foreach (var plot in selectedDomainIsland.Plots.Where(p => p.PlotType == PlotType.House))
                {
                    var dict = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
                    for (var slot = 1; slot <= plot.TotalSlots; slot++)
                    {
                        var laborerName = dict.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var nv)
                            ? LaborerConfigHelper.NormalizeLaborerFullName(nv) : string.Empty;
                        var laborerType = dict.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var tv)
                            ? IslandLaborerProfessions.GetProfession(tv) : string.Empty;
                        var displayName = !string.IsNullOrWhiteSpace(laborerName) ? laborerName
                            : !string.IsNullOrWhiteSpace(laborerType) ? laborerType : null;
                        if (displayName == null) continue;

                        string action, detail;
                        bool needsAttention = false;

                        var isLootReady = dict.TryGetValue(LaborerConfigHelper.LootReadyKey(slot), out var lrv)
                            && string.Equals(lrv, "true", StringComparison.OrdinalIgnoreCase);

                        if (isLootReady)
                        {
                            action = "Loot Ready";
                            detail = string.IsNullOrWhiteSpace(laborerType) ? string.Empty : laborerType;
                            needsAttention = true;
                        }
                        else if (dict.TryGetValue(LaborerConfigHelper.DispatchTimeKey(slot), out var dtStr)
                            && LaborerConfigHelper.TryParseUtc(dtStr, out var returnTime))
                        {
                            // DispatchTimeKey stores the return/ready time (JobDispatchTime on snapshot = when laborer returns).
                            var remaining = returnTime.ToUniversalTime() - DateTime.UtcNow;
                            if (remaining > TimeSpan.Zero)
                            {
                                action = "On Job";
                                detail = remaining.TotalHours >= 1
                                    ? $"Returns in {(int)remaining.TotalHours}h {remaining.Minutes}m"
                                    : $"Returns in {remaining.Minutes}m";
                            }
                            else
                            {
                                action = "Loot Ready";
                                detail = string.IsNullOrWhiteSpace(laborerType) ? string.Empty : laborerType;
                                needsAttention = true;
                            }
                        }
                        else
                        {
                            action = "Home";
                            detail = string.Empty;
                        }

                        rows.Add(new LiveLaborerActionEntry
                        {
                            Timestamp = GetOrAddLiveRowTimestamp($"config:{plot.Id}:{slot}"),
                            Name = displayName,
                            Action = action,
                            Detail = detail,
                            NeedsAttention = needsAttention
                        });
                    }
                }
            }
        }

        int harvestReady = 0;
        int harvestGrowing = 0;
        if (domainById != null && selectedIslandId.HasValue
            && domainById.TryGetValue(selectedIslandId.Value, out var selectedIslandDomain)
            && selectedIslandDomain.Plots != null)
        {
            foreach (var plot in selectedIslandDomain.Plots)
            {
                if (plot.PlotType == PlotType.House || !plot.PlotType.HasCollectionTimer())
                    continue;
                if (!plot.PlotPlantedAt.HasValue)
                    continue;

                var plotLabel = plot.BuildingTypeName;
                var farmableType = plot.PlotType.HasFarmableConfig()
                    ? plot.PlotType.GetConfiguredTypeName(plot.Configuration)
                    : string.Empty;

                string farmAction, farmDetail;
                bool isReady = plot.PlotCollectionReady;

                if (isReady)
                {
                    farmAction = "Ready";
                    farmDetail = string.IsNullOrWhiteSpace(farmableType) ? plotLabel : farmableType;
                    harvestReady++;
                }
                else
                {
                    farmAction = "Growing";
                    var countdown = plot.PlotCollectionCountdown;
                    farmDetail = string.IsNullOrWhiteSpace(farmableType)
                        ? countdown
                        : $"{farmableType} · {countdown}";
                    harvestGrowing++;
                }

                rows.Add(new LiveLaborerActionEntry
                {
                    Timestamp = GetOrAddLiveRowTimestamp($"farm:{plot.Id}"),
                    Name = plotLabel,
                    Action = farmAction,
                    Detail = farmDetail,
                    NeedsAttention = isReady
                });
            }
        }

        LiveLaborerActionRows.Clear();
        foreach (var r in rows) LiveLaborerActionRows.Add(r);

        int lootReady, onJob, home;
        if (isSessionIsland || sessionIslandId == null)
        {
            lootReady = list.Count(s => s.IsLootReady);
            onJob = list.Count(s => s.IsOnJob && !s.IsLootReady);
            home = list.Count(s => !s.IsOnJob && !s.IsLootReady);
        }
        else
        {
            var laborerRows = rows.Where(r => r.Action is "Loot Ready" or "On Job" or "Home").ToList();
            lootReady = laborerRows.Count(r => r.Action == "Loot Ready");
            onJob = laborerRows.Count(r => r.Action == "On Job");
            home = laborerRows.Count(r => r.Action == "Home");
        }

        LiveSummarySegments.Clear();
        if (lootReady > 0) LiveSummarySegments.Add(new LiveSummarySegment { Category = "Warning", Text = $"Loot Ready: {lootReady}" });
        if (harvestReady > 0) LiveSummarySegments.Add(new LiveSummarySegment { Category = "Warning", Text = $"Harvest Ready: {harvestReady}" });
        if (onJob > 0) LiveSummarySegments.Add(new LiveSummarySegment { Category = "Active", Text = $"On Job: {onJob}" });
        if (harvestGrowing > 0) LiveSummarySegments.Add(new LiveSummarySegment { Category = "Idle", Text = $"Growing: {harvestGrowing}" });
        if (home > 0) LiveSummarySegments.Add(new LiveSummarySegment { Category = "Idle", Text = $"Home: {home}" });

        OnPropertyChanged(nameof(GlobalHandlingTimeStatusText));
        OnPropertyChanged(nameof(GlobalIslandsDoneTodayCount));
        OnPropertyChanged(nameof(GlobalIslandsLeftTodayCount));
    }

    public ObservableCollection<ISeries> LaborerHistorySeries { get; }
    public Axis[] LaborerHistoryXAxes { get; }

    private static IslandEntry[] MockIslands() =>
    [
        new IslandEntry {
            Name = "BillionsCrafting", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.Lymhurst, CityName = "Lymhurst", Biome = "Forest",
            Tier = 6, OwnerName = "For Rental", PlotCount = 1,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 0,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "Herb Garden", Quantity = 15, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
            },
        },
        new IslandEntry {
            Name = "KySrLi", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.Lymhurst, CityName = "Lymhurst", Biome = "Forest",
            Tier = 6, OwnerName = "For Rental", PlotCount = 1,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 1,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "Farm", Quantity = 15, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
            },
        },
        new IslandEntry {
            Name = "Manolya", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.Lymhurst, CityName = "Lymhurst", Biome = "Forest",
            Tier = 6, OwnerName = "For Rental", PlotCount = 1,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 2,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "Herb Garden", Quantity = 15, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
            },
        },
        new IslandEntry {
            Name = "Manolya2", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.Lymhurst, CityName = "Lymhurst", Biome = "Forest",
            Tier = 6, OwnerName = "For Rental", PlotCount = 1,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 3,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "Pasture", Quantity = 15, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS001", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 4,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Alan Calverley", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Bartholomew Snay", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - William Pert" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Gerard Lucas", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Edric Battersby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Thomas Hornclyff" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Henry Carbott", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Henry Awkeland", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Michael Myrns" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Alfred Annatson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Eustace Darwentwater", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Wymon Horner" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Martin Wharom", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Noah Fawthorp", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Wymer Wryght" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osmond Teisdayle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Gilbert Selybarne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Godfrey Boger" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Gerard Scisson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Godfrey Mynus", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Toly Turnour" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osmer Talwrey", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Gilbert Bramton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Isaac Morthyng" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Wymon Girdlyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Philip Lonnesdayle", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Peter Maxwell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Thomas Messynger", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Osbert Fawthorp", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Alwin Drape" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Hamon Parisshe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Isaac Karre", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Albin Eslyngton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Fulke Mowbrey", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Alwin Jybbe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Richeman Emondson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Guy Robson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Elias Haliday", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Martin Clynt" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Richard Elwald", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Aylwin Webster", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Godwin Launce" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - David Hawton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Warin Lamonby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Turbert Hawdewyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Herbert Scadlok", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Hereward Jeffrayson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Wymon Slotheman" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS002", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 5,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Emma Hebdale", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Sibyl Wilkynson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Margery Whitehauce" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Hawis Wedderall", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Constance Alman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Juliana Talwrey" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Aldiva Ostayne", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Constance Norman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Cecilia Stayneton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Lettice Hunter", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Emma Torte", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Lucia Fetherston" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Avice Sawghell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Joan Grene", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Margery Mennell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Petronilla Williamson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Avelina Warde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Eunice Parkor" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Clarice Dunwiche", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Rose Vance", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Margaret Cotes" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Beatrix Agar", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Osanna Speght", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Avelina Baxster" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Amice Nicholson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Katharine Sesseton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Amice Stayneton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Avice Nawton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Petronilla Beislay", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alma Screvyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margery Sadler", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Amice Lupton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Agnes Hawkyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Ailova Fone", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Edith Calverd", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rose Farand" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Godeleva Lynsey", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Godeleva Toone", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alice Hebdale" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Gunnora Chefton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Brigit Righton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rose Kendalle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Aldiva Peally", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Rose Gilmyn", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Mabel Ovyngton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Edith Dene", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Godeleva Levoty", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Hawis Lucas" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS003", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 6,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 4 - Warrior - Joan Shawe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Clarice Cavard", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Masota Mylner" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Eunice Braght", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Avice Nycholson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Masota Hutchynson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Rochilda Cowper", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Rose Botre", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alice Perkyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lina Watterton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Margery Watson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Brigit Huton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Hawis Hayce", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Rochilda Shau", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alma Hakbarro" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Eva Fyssher", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Mabel Spark", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Gunnilda Skyrro" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alviva Cure", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Lettice Askquyth", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Isabella Fawthorp" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Godeleva Watt", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Edith Suttell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Runild Conke" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Estrild Stiknam", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Godeleva Sawnderson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Agnes Parkor" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Gunnora Biggyn", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Ragenild Mallom", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Constance Roger" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Osanna Fenton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Cecilia Runkhorne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Avelina Hartley" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Constance Marshall", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Isabella Dobson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Agnes Hunter" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Margaret Wilkynson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Rose Walton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Lettice Scayff" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Petronilla Pawlin", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Margaret Clyff", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Sibyl Peally" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Idonea Mawe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Alviva Maltby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Petronilla Notyngham" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Clarice Eryngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Avice Wharom", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Aldiva Rawling" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS004", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 7,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Isaac Dawson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Wymer Blakey", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osbert Turduff" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Nicholas Plaskett", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Richeman Lowes", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Arnold Middelton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Alexander Eslyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Martin Newby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osmer Michelson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Herbert Mawneby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Bartholomew Lee", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Roger Abney" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Reginald Boger", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Albert Shereburne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Roger Wande" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Humphrey Custance", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Albert Mynus", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Eustace Londe" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Reginald Holdesworth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Elias Herpar", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Elias Carre" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Robert Broket", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Godfrey Wiclyff", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osgood Colyer" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Robert Welburne", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Turbert Grayson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Godfrey Wilman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Philip Carleton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Godfrey Tuke", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Thomas Baynes" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Alexander Rawling", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Alfred Ostayne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Reginald Clynt" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Arthur Cowell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Arnold Brayn", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Gregory Flowre" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Herbert Baldshawe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Edric Bekwith", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Roger Calverd" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Warin Chamlan", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Osgood Riley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Alexander Sydez" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Hereward Huntclyff", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Arnold Sandwath", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Paul Hahle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Denis Symcooke", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Stephen Sadler", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Walter Grey" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS005", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 8,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Eunice Plaskot", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Ragenild Hewe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Masota Battersby" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Aldiva Nalour", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Hunter - Mabel Vertee", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Masota Esshby" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Hunter - Isabella Foster", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Margaret Hughley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alviva Cotes" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Hunter - Alviva Bikerton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Hunter - Masota Newton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Hunter - Margery Lorymer" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Matilda Costentyne", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Hawis Todde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Clarice Eldon" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Ragenild Warynell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Ragenild Tyler", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Sibyl Appylgarth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Hunter - Sibyl Sydburroo", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Idonea Lunde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Hawis Walton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margaret Taillour", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Margery Ledale", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Lina Lounesdayle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Sibyl Speght", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Gunnora Thursley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ailova Harryngton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Edith Vertee", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Alma Robson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Hunter - Ragenild Newsom" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Ailova Hudson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Brigit Nassyngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Clarice Pollard" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Clarice Bene", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Hunter - Godeleva Colynson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Margaret Scadlok" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Amice Clayton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Clarice Richardson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alice Lowes" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Hunter - Runild Henlayk", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Hunter - Lina Snawsell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Hunter - Constance Whenby" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Hunter - Masota Newton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Hunter - Alviva Bikerton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Hunter - Ida Lee" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Hunter - Juliana Thorppe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Eunice Robson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Aldiva Plompton" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS006", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 9,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lina Groves", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Lucia Pulley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Edith Faux" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Agnes Garstell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Juliana Bramley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Avelina Fraderyk" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Hawis Strynger", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Amice Knoll", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Sibyl Garth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Denise Laurence", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Brigit Mountney", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alice Westby" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Margery Beislay", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Ragenild Gachell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Isabella Langley" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Margaret Rudby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Alice Messynger", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Mabel Haliday" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Eva Buckshawe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Matilda Savage", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Emma Carpmell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Beatrix Rowley", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Rose Prynce", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Cecilia Notingham" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alice Brax", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Isabella Lupton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alice Burgh" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Emma Webster", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Beatrix Gobcroft", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Gunnora Catarton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Beatrix Lamonby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Estrild Girdlyngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alice Watterton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Ragenild Smyrthuate", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Estrild Blads", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Rose Wayde" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Aldiva Bek", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Mabel Runkhorne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alice Staneburne" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Juliana Horskepar", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Estrild Weddell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Juliana Sparlyng" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Cecilia Trewe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Ragenild Benson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Juliana Mollans" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Petronilla Morthyng", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Godeleva Horskepar", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Hawis Ashby" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS007", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 10,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Ringer Annatson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Edward Sharp", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Robert Dighton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osbert Bowe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Bartholomew Murthwayte", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Adelard Horneclyff" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Guy Dawtree", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Benedict Harryngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Albin Pilly" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Aylwin Watter", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Bartholomew Felle", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Paul Grene" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Elias Lister", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Bartholomew Dawtree", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Solomon Askquyth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Eustace Newpayle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Thomas Lawe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Adelard Stanebank" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Humphrey Nunes", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Turbert Mansum", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Robert Thorppe" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Herbert Hoode", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Alexander Cowper", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Godwin Messynger" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Wymer Thwaite", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Michael Sharparro", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Leonard Marshrudder" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osbert Stanceby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Robert Swyne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Edric Kilburne" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Alfred Hakbarro", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Denis Warynell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Christopher Snawsell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Stephen Drawswerde", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Fulke Raynard", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Edric Harland" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - William Mansum", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Hamon Toller", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Reginald Askquyth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Arnold Willson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Christopher Horneclyff", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Stephen Mawneby" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Philip Thorne", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Henry Bramley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Warin Lonnesdayle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osbert Thwaite", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Laurence Speght", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hamon Biggyn" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS008", TierDisplay = "T1", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 1, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 11,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Hawis Carrok", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Hawis Sydez", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Juliana Hansman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Amice Harnes", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Rochilda Freman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Estrild Foxgyll" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Eva Symcooke", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Gunnora Tayte", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Cecilia Bettonson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margery Bovell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lucia Freman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Margery Stiknam" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Eunice Fletcher", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Masota Davy", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rose Lund" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Osanna Dawson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Idonea Croft", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Eunice Bayne" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margaret Mallom", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Idonea Shawe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rochilda Chefton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Eunice Symondson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lina Savage", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rose Thorppe" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Petronilla Cradefort", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Godeleva Croft", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alviva Stokes" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Osanna Raynard", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Avice Watson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rochilda Dobson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Katharine Wharom", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lina Bygott", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Katharine Blyth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Idonea Wryght", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Emma Agar", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Idonea Breer" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Rochilda Mollans", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Brigit Chace", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Aldiva Hamylton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margaret Rayncok", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Beatrix Tuke", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ida Welburne" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Godeleva Appylgarth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Aldiva Nalour", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Denise Abney" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Sibyl Farand", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Petronilla Calverd", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Isabella Hansman" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS009", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 12,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Runild Groves", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Godeleva Blewmar", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Denise Edmonson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Gunnilda Fawbarne", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Ida Bailton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Amice Rogerson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Margaret Whytterwell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Cecilia Murton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Petronilla Lewty" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Denise Eldon", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Rose Plaskett", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Cecilia Stabyll" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alice Plaskot", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Gunnora Poynter", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Avelina Kent" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Eunice Smith", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Cecilia Dawson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Lucia Mynus" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Clarice Barker", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Ailova Abney", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Matilda Dawtree" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alma Atkynson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Alice Hutchynson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Sibyl Peally" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Denise Ellys", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Juliana Flemyng", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Eunice Calverley" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Eva Dalby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Gunnora Gypton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Ragenild Wymp" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alice Plompton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Margery Chase", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Runild Whenffell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Runild Knoll", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Gunnilda Melle", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Lettice Fairfax" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Emma Braght", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Hawis Levoty", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Edith Farco" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Katharine Essylwodde", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Juliana Tankerfeld", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alice Raynald" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Amice Symcooke", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Matilda Hunter", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Runild Spencer" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Denise Dykson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Osanna Bradeley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Osanna Foreman" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS010", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 13,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Richeman Ekrylton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Herbert Mynus", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Aylwin Handley" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Thomas Smith", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Nicholas Cavard", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hereward Snay" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Gilbert Knayton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Richeman Elmeslay", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Martin Chefton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - John Emondson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Christopher Glewe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Paul Thorppe" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Adam Bossall", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Ringer Carre", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Gregory Burton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Milo Newsom", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Isaac Helme", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Herbert Robson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Simon Plewman", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Arnold Foreman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Ralph Gachell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osbert Horskepar", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Godfrey Braithwate", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Solomon Smyrthuate" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Michael Armestrang", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Arthur Esyngwold", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Godwin Fetherstane" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Edward Mallom", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Reginald Wylde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Turbert Wande" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Christopher Newman", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Richard Lee", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Godwin Herpham" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Nicholas Symkynson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Alexander Chamlan", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Edric Smyrthuate" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Ralph Lawe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Alwin Hay", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Martin Tankerfeld" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Turbert Scayles", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Ringer Gylmyn", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Warin Prynce" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Richeman Eldon", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Gregory Symcooke", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Aylwin Bekwith" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Turbert Symson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Adelard Santtam", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Elias Pollard" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS011", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 14,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Sibyl Wightman", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Beatrix Roger", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Gunnilda Slee" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Runild Cuks", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lucia Smyth", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Edith Beislay" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Godeleva Fancehede", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Juliana Wilson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ailova Wilman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Alice Crosby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Clarice Lax", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Aldiva Blyth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Lettice Croft", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Estrild Dunwell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Gunnora Hoode" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Amice Brydge", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Rose Horneclyff", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Beatrix Sawghell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Alviva Cullyng", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Juliana Mason", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Eunice Braideryg" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Katharine Connyll", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lucia Stokes", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Brigit Payver" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Gunnora Grenebank", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Sibyl Welburne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ailova Ratclyff" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Joan Nawton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Denise Hutchonson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Lina Lowson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Joan Nevyle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Ragenild Mowbrey", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rose Mountney" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Lina Gilmyn", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Hawis Wightman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Sibyl Sharparro" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Agnes Welburne", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Emma Scayff", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Joan Egremond" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Runild Nicholson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Alviva Buckshawe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Edith Funtaunce" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Alma Herryson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Estrild Chadwyk", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Lina Fereman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Isabella Carre", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lina Screvyn", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Edith Trewe" },
            },
        },
        new IslandEntry {
            Name = "ZenithFarmsFS012", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 15,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lettice Nixon", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Avelina Eryngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Eunice Tomlynson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Osanna Bailton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Gunnora Walker", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Margaret Messynger" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Joan Screvyn", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Masota Hahle", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Margaret Wardeman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Cecilia Talwrey", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Cecilia Smyth", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Edith Kitchyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Isabella Halle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Alma Sawghell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Runild Symondson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Rose Hebdale", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Eunice Boyne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Avice Langton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Katharine Lupton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Idonea Lawe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Matilda Rasyng" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Beatrix Mooreton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Juliana Gurnard", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alice Luff" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Eunice Selybarne", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Rose Eddryngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alma Flanwyth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lucia Perkyn", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Edith Carlile", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Katharine Lynsey" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Rose Wodde", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Mabel Gylmyn", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Amice Hudson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lettice Skelton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Alma Thikpenny", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Isabella Dawtree" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Avelina Beislay", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Margery Wryght", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Mabel Levenyng" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Eva Lamyman", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Sibyl Symondson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Katharine Prynce" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Osanna Plompton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Lucia Symkynson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Masota Custance" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Rochilda Spark", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Gunnora Blads", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Brigit Wryght" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave003a", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 16,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Aylwin Farand", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Warin Hay", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Reginald Rayce" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Godwin Newpayle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Alfred Elmeslay", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Robert Hutchonson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Turbert Hylton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Christopher Derson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Richard Burdux" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Bartholomew Lambe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Wymer Rawling", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Aylwin Vykceman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Elias Flemyng", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Paul Grenebank", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Turbert Makerell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Theobald Gylfeld", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Godwin Hyandson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Alwin Todde" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Alwin Baldshawe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Richeman Barker", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Thomas Feron" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Laurence Strynger", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Everard Warynell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hereward Peirson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Herbert Cakeman", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Gilbert Mawe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Walter Catarton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Richeman Bowe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Hamon Mollans", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Isaac Mountney" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Milo Wande", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Gamel Gate", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hereward Whitehawce" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Edward Santtam", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Reginald Roger", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Herbert Baldshawe" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Gregory Cawodde", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Alfred Umfreyson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Geoffrey Speght" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Edric Turnour", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Ringer Dowson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Turbert Calverley" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Arthur Brigham", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Laurence Marshrudder", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hugo Frenas" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Simon Kendalle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Osgood Bartlet", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hereward Essylwodde" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave003b", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 17,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Constance Shipton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Clarice Brignall", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Lina Stiknam" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Clarice Bollyng", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Katharine Askquyth", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Juliana Jinckyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Constance Allanby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lucia Lepyngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Emma Brax" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Masota Hyll", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Juliana Clerk", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Beatrix Smyrthuate" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Lettice Thirlthorp", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lina Haxby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Margery Northyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Lettice Bewe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Runild Gillo", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Cecilia Crage" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Edith Blakey", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Avice Vertee", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Avelina Foreman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Joan Newsom", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Eva Talwrey", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Gunnora Teismond" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Mabel Rudby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Margery Huntyngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Godeleva Croludson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Gunnilda Grey", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Brigit Marshrudder", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rose Sparlyng" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Runild Wilkynson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Rose Cawodde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alma Essylwodde" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Gunnilda Dode", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Petronilla Goldesmyth", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Mabel Fletcher" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Eunice Smyrthuate", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Masota Plompton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rochilda Nunes" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Clarice Huchonson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Alviva Grayson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ailova Symcooke" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Alice Rawling", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Margaret Bustyng", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Masota Ryther" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Petronilla Grey", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lucia Morres", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Masota Annatson" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave003c", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 18,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Gunnilda Flanwyth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Alviva Vidyll", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Petronilla Swayle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Rochilda Bek", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Sibyl Lulley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Eunice Fairfax" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Beatrix Lenge", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Eva Handley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Matilda Appilgarth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Gunnora Bikerton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Avelina Huchonson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Matilda Thorppe" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Juliana Morres", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Estrild Plaskett", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Alviva Sawnderson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Osanna Wiclyff", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Margery Daylle", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Estrild Clayton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Alviva Rasebok", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Ailova Smyrthuate", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Juliana Harryngton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Katharine Wryght", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Margery Thwaite", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Godeleva Keld" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Clarice Nassyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Beatrix Savage", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Idonea Petty" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Idonea Dyneley", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Estrild Glasyn", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Aldiva Funtaunce" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Eva Mylner", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Estrild Faceby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Gunnora Huton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Ailova Doughty", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Agnes Shepard", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Ailova Screvyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Ida Grenebank", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Lina Cobbe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Denise Lulley" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Amice Vidyll", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Avice Bovell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Gunnilda Agar" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Constance Hyandson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Katharine Brownenyng", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Denise Mowbrey" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 8 - Warrior - Ailova Cowper", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 8 - Warrior - Aldiva Gilbank", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 8 - Warrior - Alice Sydburroo" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave004a", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 26,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 19,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Hamon Gillo", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Arthur Mawer", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hamon Handley" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Gilbert Donnyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Everard Walshworth", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Arnold Jeffrayson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Richeman Appilgarth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Noah Tyler", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Andrew Paycok" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Denis Almond", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Martin Carver", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Adelard Felle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Martin Home", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Alwin Talbott", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osmer Myrus" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osmer Holdesworth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Albert Conny", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Christopher Cowell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Roger Fetherstane", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Guy Gylfeld", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Eustace Todde" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Edward Turduff", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - William Whytterwell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Noah Letty" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Bartholomew Hylton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Robert Pawlin", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Nicholas Custance" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Alfred Grethede", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Denis Wayde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Michael Ridlyngton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Guy Dawtree", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Randolph Adenett", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Albin Symson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Thomas Sawghell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Simon Hill", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hamon Ekrylton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Guy Broket", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Peter Tomlynson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osmond Burton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osgood Dyneley", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Godfrey Fawthorp", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Theobald Daylle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Benedict Lupton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - John Scaffurth", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Arnold Hartley" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Aylwin Bambryge", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Richeman Wartyrer", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osmond Bayne" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Gilbert Donnyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Bartholomew Hylton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Denis Almond" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Michael Ridlyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Guy Gylfeld", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Christopher Cowell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Albert Conny", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - John Scaffurth", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osmond Burton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Roger Fetherstane", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Thomas Sawghell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Albin Symson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Richeman Appilgarth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Martin Home", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Theobald Daylle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Godfrey Fawthorp", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Randolph Adenett", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Peter Tomlynson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Hamon Handley", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Martin Carver", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osmer Myrus" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Hamon Ekrylton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Guy Dawtree", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hamon Gillo" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Arthur Mawer", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Benedict Lupton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osmer Holdesworth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Denis Wayde", Laborer2IndicatorState = "sent", Laborer2Line = "", Laborer3IndicatorState = "sent", Laborer3Line = "" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave004b", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 20,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Hawis Dawtree", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Joan Clayton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Margaret Savage" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Lina Funtaunce", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Godeleva Walker", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ailova Mennell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Alma Pilly", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Mabel Grenewelle", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Clarice Weddell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Estrild Vesey", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Idonea Gillour", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Aldiva Lightfoote" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Lucia Norton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Clarice Bekbank", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alice Harland" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Clarice Smith", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Emma Bradeley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Runild Jybbe" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Beatrix Hobson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Alma Chapman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rochilda Bittynson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Rose Feron", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Brigit Lyster", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Lucia Burne" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margery Spencer", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lina Ebden", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Cecilia Plaskot" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margery Fletcher", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Masota Sesseton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Edith Scayff" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Ragenild Girdlyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Rose Parke", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Avelina Conny" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Brigit Mountney", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lettice Speght", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Clarice Tayte" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Edith Newman", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Runild Bramley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ragenild Lawson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Cecilia Bettonson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Brigit Bradeley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rochilda Pennyngton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Sibyl Girdlyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Emma Huntyngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Masota Turnor" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Beatrix Shau", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Katharine Hyandson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Eunice Lister" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave004c", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 21,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alice Lee", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Avice Grey", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Clarice Holme" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Isabella Ellys", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Petronilla Croft", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Mabel Kitchyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Joan Colman", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Estrild Clerk", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alviva Dykson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Osanna Byng", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Margery Emondson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alice Sweerer" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Constance Askquyth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Beatrix Fairfax", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Avelina Fox" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Masota Maughen", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Edith Burnett", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Brigit Langthorn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Denise Felle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Avice Tailbus", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Rochilda Edon" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alice Moryn", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Mabel Biggyn", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Rose Wynyate" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lucia Hype", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Eva Lupton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Runild Shepard" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Clarice Trodbek", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Emma Gillyot", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Mabel Geldard" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Eunice Myles", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Eunice Twhates", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Agnes Beilby" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Edith Fone", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Eva Handley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Godeleva Grey" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Eva Dunwell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Mabel Twhates", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Amice Wardeman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Juliana Blyth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Lucia Mawe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Lucia Moryn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Gunnilda Bambryge", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Matilda Darley", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Runild Plaskot" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Emma Funtaunce", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Ailova Lawe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Lina Norton" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave005a", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 22,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Peter Allan", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Osgood Huby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Theobald Richardson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Andrew Hawkyn", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Toly Clynt", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Ringer Aynger" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Adelard Whalley", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Peter Flemyng", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Eustace Herpar" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Richard Wyngayt", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - David Cobbe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Everard Paycok" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Adelard Stokes", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Leonard Hewson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Paul Braideryg" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Benedict Harland", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Warin Eldon", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osbert Cokett" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osbert Birtbek", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Michael Bawderby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osmond Eslyngton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Aylwin Lightfoote", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Eustace Lounesdayle", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Wymon Allan" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - John Notyngham", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Godfrey Cleuston", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Andrew Lamonby" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Godwin Hebdale", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Wymon Keld", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Roger Walker" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Gregory Smyth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Aylwin Stokes", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Adam Willson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Nigel Hamshawe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Ringer Chace", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hereward Umfreyson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Ringer Halle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Solomon Whenffell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osmond Gate" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Isaac Sharparro", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Godwin Awkeland", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Leonard Doughty" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Geoffrey Tankerd", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Adelard Welburne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Michael Swayle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Martin Messynger", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Denis Strynger", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Warin Hogeson" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave005b", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 23,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Constance Prynce", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Ida White", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Emma Nixon" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Brigit Levet", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Amice Baldshawe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Hawis Suttell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Sibyl Maltby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Eva Costentyne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Idonea Barker" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Gunnora Stanebank", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Godeleva Wylde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Emma Lonnesdayle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Denise Spark", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Ragenild Olyff", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Emma Stayneton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Estrild Appylgarth", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Edith Ryg", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Idonea Hudson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margaret Harryngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Margaret Essylwodde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ailova Tindayle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Rochilda Farco", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Margery Herryson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Juliana Bettonson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Alma Sawer", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Constance Burdux", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Avice Boyne" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Estrild Teismond", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Constance Grenewelle", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alviva Cartwright" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Ida Braithwate", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Idonea Newman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Idonea Hayce" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Mabel Cowell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Ailova Hyandson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alviva Hylton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Alice Mawneby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Rochilda Webster", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Beatrix Newton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Estrild Baly", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lucia Eddryngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Lina Kettylwell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Constance Hogeson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Ida Milner", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Aldiva Dobson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margery Crage", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Juliana Raynard", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Eunice Abney" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave005c", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 24,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Juliana Felle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Petronilla Hoode", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Avice Gillo" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Matilda Chase", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Emma Glewe", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Edith Grenebank" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lettice Lawson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Rochilda Freman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Edith Eryngton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Matilda Carados", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Mabel Elwik", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Clarice Plewman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Avelina Whitehauce", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Constance Symondson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Amice Levett" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Ailova Essylwodde", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Eunice Hawton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Brigit Tailbus" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alviva Sedule", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Sibyl Jenkynson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Constance Lee" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Margaret Lowes", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Constance Hewbank", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Estrild Foxgale" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Petronilla Hamylton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Alviva Mudde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Petronilla Shipton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Rose Gulles", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Matilda Shastun", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Idonea Adenett" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Rose Sawer", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Juliana Wedderall", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Runild Foxgale" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Ida Braideryg", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Isabella Gibbon", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Juliana Mason" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lina Braideryg", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Lucia Wymp", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Aldiva Colman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lucia Benson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Amice Gibson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Lettice Sweerer" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Avelina Northyn", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Denise Bossall", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Margaret Emondson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alma Baynes", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Katharine Huntyngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Cecilia Lamonby" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave006a", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 25,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Osmer Allan", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Randolph Browne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Andrew Screvyn" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Christopher Tesshe", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Elias Rodemelle", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Roger Curtes" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Edward Hustwyk", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Martin Bayteman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Gamel Braithwate" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Andrew Frenas", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - John Paycok", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Elias Honey" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Randolph Colman", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - David Hirst", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Peter Mason" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Wymon Huntyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Alexander Clarke", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Eustace Warde" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Edric Trodbek", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Eustace Myles", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Edric Faux" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Martin Barton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Bartholomew Monkton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Edward Gillo" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Denis Gilmyn", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Albin Skelton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Martin Edon" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Eustace Tomson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Peter Hay", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Randolph Bikerton" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Toly Letty", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - William Grayson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Adam Mason" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Benedict Adenet", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Fulke Hutchynson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Edward Crage" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Philip Strynger", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Godwin Copeland", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hamon Symkynson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Gilbert Sadler", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Leonard Lister", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Osbert Appilgarth" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Benedict Funtaunce", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Adam Cullyng", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Edward Parkor" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Milo Toone", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Humphrey Bittynson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hamon Perkyn" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave006b", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 26,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Petronilla Newman", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Edith Morresby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Margaret Chadwyk" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Rochilda Grenewelle", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Agnes Raynald", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ragenild Bartlet" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Idonea Teismond", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lina North", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Sibyl Bradeley" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Denise Levett", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lina Symson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Godeleva Cokett" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Osanna Nassyngton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Brigit Thirlthorp", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Runild Gobcroft" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Rochilda Wyldon", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Godeleva Snawsell", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Margery Gulles" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Alviva Spencer", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Beatrix Karre", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Ailova Blomer" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Osanna Westby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Clarice Biggyn", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Matilda Taillour" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Emma Spark", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Eunice Foxgate", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Eva Sparlyng" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Petronilla Stabell", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Ailova Gibbon", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alice Robert" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Clarice Henlayk", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Avelina Watter", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Beatrix Dyatson" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Katharine Monkton", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Eunice Hormsby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Alma Grene" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Runild Bekwith", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Katharine Gilmyn", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Edith Dunwiche" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Eva Tayte", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Lucia Cotes", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Osanna Hirst" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Osanna Webster", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Margery Milner", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Rochilda Abney" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Isabella Christoferson", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Sibyl Spynk", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Mabel Northyn" },
            },
        },
        new IslandEntry {
            Name = "ZenithSlave006c", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.FortSterling, CityName = "Fort Sterling", Biome = "Mountains",
            Tier = 6, OwnerName = "Luca", PlotCount = 16,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 27,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Aldiva Strynger", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Lina Appylgarth", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Masota Abney" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Aldiva Graunge", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Masota Rayncok", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Rochilda Welburne" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Osanna Camsall", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Denise Nassyngton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Alice Morthyng" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alma Savage", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Beatrix Hebdale", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Godeleva Stabell" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Lucia Chamlan", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Lina Cuks", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Clarice Conyers" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Alma Karre", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Sibyl Wodde", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Eva Eldon" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Mabel Goode", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Alviva Vertee", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Ida Nalour" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Cecilia Beirby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Joan Hawton", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Joan Abney" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Masota Myles", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Rochilda Tuke", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Masota Walker" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Constance Rede", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Margery Ryddesdall", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Petronilla Stiknam" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Denise Webster", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Godeleva Keld", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Emma Carados" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Masota Bankhus", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Aldiva Wightman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Ida Horneclyff" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Juliana Eldon", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Alma Henlayk", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Lucia Arrondayle" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Aldiva Burne", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Ragenild Browne", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Katharine Brignall" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Juliana Bawderby", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Margaret Darwentwater", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Rose Lamyman" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Warrior - Godeleva Launce", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Warrior - Lucia Hobson", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Warrior - Edith Hayce" },
            },
        },
        new IslandEntry {
            Name = "95lulu", TierDisplay = "T2", HasPremium = true,
            CityFaction = CityFaction.Bridgewatch, CityName = "Bridgewatch", Biome = "Steppes",
            Tier = 2, OwnerName = "OrangeZones", PlotCount = 3,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 28,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "Herb Garden", Quantity = 1, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
                new IslandPlotEntry { PlotType = "Farm", Quantity = 1, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hide - Edward Armestrang", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hide - Gamel Allanby", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hide - Ralph Cowper" },
            },
        },
        new IslandEntry {
            Name = "IsaWitch", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.Martlock, CityName = "Martlock", Biome = "Highlands",
            Tier = 6, OwnerName = "OrangeZones", PlotCount = 1,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 29,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "Pasture", Quantity = 8, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
            },
        },
        new IslandEntry {
            Name = "OrangeZones", TierDisplay = "T2", HasPremium = true,
            CityFaction = CityFaction.Martlock, CityName = "Martlock", Biome = "Highlands",
            Tier = 2, OwnerName = "OrangeZones", PlotCount = 2,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 30,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "Kennel", Quantity = 1, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
                new IslandPlotEntry { PlotType = "Pasture", Quantity = 2, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
            },
        },
        new IslandEntry {
            Name = "OrangeZones", TierDisplay = "T6", HasPremium = true,
            CityFaction = CityFaction.Lymhurst, CityName = "Lymhurst", Biome = "Forest",
            Tier = 6, OwnerName = "OrangeZones", PlotCount = 4,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 31,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "Pasture", Quantity = 5, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
                new IslandPlotEntry { PlotType = "Farm", Quantity = 9, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
                new IslandPlotEntry { PlotType = "Herb Garden", Quantity = 1, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Wood - Alwin Myrus", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Wood - Michael Foreman", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Wood - Simon Annatson" },
            },
        },
        new IslandEntry {
            Name = "OrangeZones", TierDisplay = "T2", HasPremium = false,
            CityFaction = CityFaction.Thetford, CityName = "Thetford", Biome = "Swamp",
            Tier = 2, OwnerName = "OrangeZones", PlotCount = 2,
            CollectionStatusText = "", CollectionStatusState = "default",
            SortOrder = 32,
            Plots = new ObservableCollection<IslandPlotEntry> {
                new IslandPlotEntry { PlotType = "Farm", Quantity = 2, PlotSentState = "none", Laborer1IndicatorState = "none", Laborer1Line = "", Laborer2IndicatorState = "none", Laborer2Line = "", Laborer3IndicatorState = "none", Laborer3Line = "" },
                new IslandPlotEntry { PlotType = "House", Quantity = 1, PlotSentState = "sent", Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Hunter - Margery Funtaunce", Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Hunter - Masota Spark", Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Hunter - Constance Pert" },
            }
        }
    ];

    private static IslandPlotEntry[] MockPlotsZenithFS010() =>
    [
        new IslandPlotEntry
        {
            PlotType = "House",
            Quantity = 1,
            PlotSentState = "sent",
            Laborer1IndicatorState = "sent",      Laborer1Line = "Laborer 1: TIER 7 - Mage - Richeman Ekrylton",
            Laborer2IndicatorState = "on_job",    Laborer2Line = "Laborer 2: TIER 7 - Mage - Herbert Mynus",
            Laborer3IndicatorState = "loot_ready",Laborer3Line = "Laborer 3: TIER 7 - Mage - Aylwin Handley"
        },
        new IslandPlotEntry
        {
            PlotType = "House",
            Quantity = 1,
            PlotSentState = "sent",
            Laborer1IndicatorState = "sent", Laborer1Line = "Laborer 1: TIER 7 - Mage - Thomas Smith",
            Laborer2IndicatorState = "sent", Laborer2Line = "Laborer 2: TIER 7 - Mage - Nicholas Cavard",
            Laborer3IndicatorState = "sent", Laborer3Line = "Laborer 3: TIER 7 - Mage - Hereward Snay"
        },
        new IslandPlotEntry
        {
            PlotType = "House",
            Quantity = 1,
            PlotSentState = "none",
            Laborer1IndicatorState = "loot_ready", Laborer1Line = "Laborer 1: TIER 7 - Mage - Gilbert Knayton",
            Laborer2IndicatorState = "loot_ready", Laborer2Line = "Laborer 2: TIER 7 - Mage - Richeman Elmeslay",
            Laborer3IndicatorState = "home",        Laborer3Line = "Laborer 3: TIER 7 - Mage - Martin Chefton"
        },
        new IslandPlotEntry
        {
            PlotType = "House",
            Quantity = 1,
            PlotSentState = "none",
            Laborer1IndicatorState = "home", Laborer1Line = "Laborer 1: TIER 7 - Mage - John Emondson",
            Laborer2IndicatorState = "home", Laborer2Line = "Laborer 2: TIER 7 - Mage - Christopher Glewe",
            Laborer3IndicatorState = "home", Laborer3Line = "Laborer 3: TIER 7 - Mage - Paul Thorppe"
        }
    ];

    private static LiveLaborerActionEntry[] MockLiveActions() =>
    [
        new LiveLaborerActionEntry { Timestamp = DateTime.Now.AddMinutes(-2),  Name = "Richeman Ekrylton", Action = "Loot Ready", Detail = "T7 Mage Journal x3" },
        new LiveLaborerActionEntry { Timestamp = DateTime.Now.AddMinutes(-5),  Name = "Aylwin Handley",    Action = "Loot Ready", Detail = "T7 Mage Journal x3" },
        new LiveLaborerActionEntry { Timestamp = DateTime.Now.AddMinutes(-8),  Name = "Thomas Smith",      Action = "Sent",       Detail = "T7 Imbuer's Journal Job" },
        new LiveLaborerActionEntry { Timestamp = DateTime.Now.AddMinutes(-12), Name = "Nicholas Cavard",   Action = "Sent",       Detail = "T7 Imbuer's Journal Job" },
        new LiveLaborerActionEntry { Timestamp = DateTime.Now.AddMinutes(-20), Name = "Martin Chefton", NeedsAttention = true, Action = "Overdue", Detail = "Job expired 3h ago" }
    ];

    private static LiveSummarySegment[] MockSummary() =>
    [
        new LiveSummarySegment { Category = "Active", Text = "Sent: 4" },
        new LiveSummarySegment { Category = "Active", Text = "On Job: 6" },
        new LiveSummarySegment { Category = "Active", Text = "Loot Ready: 2" },
        new LiveSummarySegment { Category = "Idle", Text = "Home: 3" },
        new LiveSummarySegment { Category = "Warning", Text = "Overdue: 1" }
    ];

    private static ObservableCollection<ISeries> MockChartSeries()
    {
        var sentPoints = new ObservableCollection<ObservablePoint>
        {
            new(0, 2), new(1, 3), new(2, 2), new(3, 4), new(4, 3),
            new(5, 5), new(6, 4), new(7, 6), new(8, 5), new(9, 4),
            new(10, 6), new(11, 5)
        };
        var lootPoints = new ObservableCollection<ObservablePoint>
        {
            new(0, 1), new(1, 2), new(2, 1), new(3, 3), new(4, 2),
            new(5, 4), new(6, 3), new(7, 5), new(8, 4), new(9, 3),
            new(10, 5), new(11, 4)
        };

        return
        [
            new LineSeries<ObservablePoint>
            {
                Name = "Sent",
                Values = sentPoints,
                Fill = new SolidColorPaint(new SKColor(0x2D, 0x78, 0xB4, 0x40)),
                Stroke = new SolidColorPaint(new SKColor(0x2D, 0x78, 0xB4)) { StrokeThickness = 2 },
                GeometryStroke = new SolidColorPaint(new SKColor(0x2D, 0x78, 0xB4)) { StrokeThickness = 2 },
                GeometryFill = new SolidColorPaint(new SKColor(0x2D, 0x78, 0xB4)),
                GeometrySize = 5
            },
            new LineSeries<ObservablePoint>
            {
                Name = "Loot Ready",
                Values = lootPoints,
                Fill = new SolidColorPaint(new SKColor(0x3A, 0xB8, 0x3A, 0x40)),
                Stroke = new SolidColorPaint(new SKColor(0x3A, 0xB8, 0x3A)) { StrokeThickness = 2 },
                GeometryStroke = new SolidColorPaint(new SKColor(0x3A, 0xB8, 0x3A)) { StrokeThickness = 2 },
                GeometryFill = new SolidColorPaint(new SKColor(0x3A, 0xB8, 0x3A)),
                GeometrySize = 5
            }
        ];
    }

    private static Axis[] MockXAxes() =>
    [
        new Axis
        {
            Labels = ["00:00", "02:00", "04:00", "06:00", "08:00", "10:00",
                      "12:00", "14:00", "16:00", "18:00", "20:00", "22:00"]
        }
    ];

}

public class IslandEntry : BaseViewModel
{
    public Guid IslandId { get; set; }
    public string Name { get; set; }
    public string TierDisplay { get; set; }
    public int Tier { get; set; }
    public bool HasPremium { get; set; }
    public CityFaction CityFaction { get; set; }
    public string CityName { get; set; }
    public string Biome { get; set; }
    private string _collectionStatusText;
    private string _collectionStatusState;
    private bool _needsVisit;
    public string CollectionStatusText { get => _collectionStatusText; set { _collectionStatusText = value; OnPropertyChanged(); } }
    public string CollectionStatusState { get => _collectionStatusState; set { _collectionStatusState = value; OnPropertyChanged(); } }
    public bool NeedsVisit { get => _needsVisit; set { _needsVisit = value; OnPropertyChanged(); } }
    public string OwnerName { get; set; }
    public int PlotCount { get; set; }
    public ObservableCollection<IslandPlotEntry> Plots { get; set; } = new();

    // Finance
    public long RentCostPerMonth { get; set; }
    public string RentDueDateDisplay { get; set; }
    public bool IsRentOverdue { get; set; }
    // Tracking / automation
    public bool TrackingEnabled { get; set; }
    public DateTime? LastVisited { get; set; }
    public int TotalLaborersSent { get; set; }
    public int TotalLootCollected { get; set; }

    // Visit duration override (null = use global default from Preferences)
    public int? VisitDurationMinutes { get; set; }

    // Misc
    public string Notes { get; set; }
    public string DiscordWebhook { get; set; }
    public int SortOrder { get; set; }
    public string LayoutId { get; set; } = string.Empty;
    public string MapImagePath { get; set; } = string.Empty;

    private IReadOnlyList<SlotGridCell> _islandSlotGrid = [];
    public IReadOnlyList<SlotGridCell> IslandSlotGrid
    {
        get => _islandSlotGrid;
        private set { _islandSlotGrid = value; OnPropertyChanged(); }
    }

    public void RebuildSlotGrid(IslandLayoutDefinition layout, IEnumerable<IslandPlot> domainPlots)
    {
        if (layout == null || layout.GridColumns == 0)
        {
            IslandSlotGrid = [];
            return;
        }

        var plotBySlot = new Dictionary<int, IslandPlot>();
        foreach (var p in domainPlots.Where(p => p.MapSlotIndex.HasValue))
            plotBySlot.TryAdd(p.MapSlotIndex!.Value, p);

        var cells = new List<SlotGridCell>();
        foreach (var slot in layout.Slots)
        {
            var state = plotBySlot.TryGetValue(slot.SlotIndex, out var plot)
                ? PlotStateCode(plot) : "empty";
            cells.Add(new SlotGridCell(slot.GridCol, slot.GridRow, state,
                IslandLayouts.FormatSlotLabel(slot.SlotIndex), !slot.IsLarge));
        }

        IslandSlotGrid = cells;
    }

    private static string PlotStateCode(IslandPlot plot) => plot.PlotType switch
    {
        PlotType.House => plot.AllLaborersSent ? "sent" : "house",
        PlotType.Farm => "farm",
        PlotType.HerbGarden => "herbgarden",
        PlotType.Pasture => "pasture",
        PlotType.Kennel => "kennel",
        _ => "empty"
    };

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    private ObservableCollection<IslandYieldEntry> _yieldItems = [];
    public ObservableCollection<IslandYieldEntry> YieldItems
    {
        get => _yieldItems;
        set
        {
            _yieldItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalYieldValue));
            OnPropertyChanged(nameof(NetProfit));
            OnPropertyChanged(nameof(IsNetProfitNegative));
            OnPropertyChanged(nameof(ROIText));
            OnPropertyChanged(nameof(TotalYieldQuantity));
            OnPropertyChanged(nameof(UniqueYieldItems));
            BuildYieldChart();
        }
    }

    private ObservableCollection<IslandConsumedEntry> _consumedItems = [];
    public ObservableCollection<IslandConsumedEntry> ConsumedItems
    {
        get => _consumedItems;
        set
        {
            _consumedItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalConsumedValue));
            OnPropertyChanged(nameof(NetProfit));
            OnPropertyChanged(nameof(IsNetProfitNegative));
            OnPropertyChanged(nameof(ROIText));
            OnPropertyChanged(nameof(TotalConsumedQuantity));
            OnPropertyChanged(nameof(UniqueConsumedItems));
            BuildYieldChart();
        }
    }

    public double TotalYieldValue => _yieldItems.Sum(e => e.TotalAvgEstMarketValue);
    public double TotalConsumedValue => _consumedItems.Sum(e => e.TotalAvgEstMarketValue);
    public double NetProfit => TotalYieldValue - TotalConsumedValue;
    public bool IsNetProfitNegative => NetProfit < 0;
    public string ROIText => TotalConsumedValue > 0
        ? $"{(TotalYieldValue / TotalConsumedValue - 1) * 100:N0}%"
        : "N/A";
    public int TotalYieldQuantity => _yieldItems.Sum(e => e.Quantity);
    public int TotalConsumedQuantity => _consumedItems.Sum(e => e.Quantity);
    public int UniqueYieldItems => _yieldItems.Select(e => e.ItemIndex).Distinct().Count();
    public int UniqueConsumedItems => _consumedItems.Select(e => e.ItemIndex).Distinct().Count();

    private IslandYieldChartMode _selectedChartMode = IslandYieldChartMode.CollectedVsConsumed;
    public IslandYieldChartMode SelectedChartMode
    {
        get => _selectedChartMode;
        set
        {
            _selectedChartMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChartVisible));
            OnPropertyChanged(nameof(IsROIChartMode));
            BuildYieldChart();
        }
    }

    private bool _isROIModeSilver;
    public bool IsROIModeSilver
    {
        get => _isROIModeSilver;
        set
        {
            _isROIModeSilver = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsROIModePct));
            BuildYieldChart();
        }
    }

    public bool IsROIModePct
    {
        get => !_isROIModeSilver;
        set => IsROIModeSilver = !value;
    }

    public bool IsChartVisible => _selectedChartMode != IslandYieldChartMode.Summary;
    public bool IsROIChartMode => _selectedChartMode == IslandYieldChartMode.ROITrend;

    public IReadOnlyList<ChartModeOption> ChartModeOptions { get; } =
    [
        new(IslandYieldChartMode.CollectedVsConsumed, "Collected vs Consumed"),
        new(IslandYieldChartMode.ROITrend, "ROI Trend"),
        new(IslandYieldChartMode.NetProfit, "Net Profit"),
        new(IslandYieldChartMode.Summary, "Summary (no chart)"),
    ];

    public ObservableCollection<ISeries> YieldChartSeries { get; } = new();

    private Axis[] _yieldChartXAxes = [new Axis { Labels = [] }];
    public Axis[] YieldChartXAxes
    {
        get => _yieldChartXAxes;
        private set { _yieldChartXAxes = value; OnPropertyChanged(); }
    }

    public ObservableCollection<string> YieldConsumedMismatches { get; } = new();
    public bool HasYieldMismatch => YieldConsumedMismatches.Count > 0;

    public void SetYieldMismatches(IReadOnlyList<string> mismatches)
    {
        YieldConsumedMismatches.Clear();
        foreach (var m in mismatches)
            YieldConsumedMismatches.Add(m);
        OnPropertyChanged(nameof(HasYieldMismatch));
    }

    // Update the bound yield list IN PLACE (patch quantities, add new rows, drop gone ones) instead of
    // replacing the ObservableCollection. Replacing it reset the bound ItemsControl on every collect
    // tick, which flashed the whole Collected/Consumed panel blank before repopulating.
    public void UpdateYieldItems(IReadOnlyList<IslandYieldEntry> source)
    {
        // Collapse to one row per item: the same crop can be booked under more than one SourcePlot
        // (harvest plot-type attribution varies per packet), which would otherwise show as duplicate tiles.
        var merged = source
            .GroupBy(e => e.ItemIndex)
            .Select(g => new IslandYieldEntry
            {
                ItemIndex = g.Key,
                Quantity = g.Sum(e => e.Quantity),
                SourcePlot = g.First().SourcePlot,
                CollectedAt = g.Min(e => e.CollectedAt)
            })
            .ToList();

        var changed = false;
        foreach (var src in merged)
        {
            var existing = _yieldItems.FirstOrDefault(e => e.ItemIndex == src.ItemIndex);
            if (existing == null)
            {
                _yieldItems.Add(new IslandYieldEntry { ItemIndex = src.ItemIndex, Quantity = src.Quantity, SourcePlot = src.SourcePlot, CollectedAt = src.CollectedAt });
                changed = true;
            }
            else if (existing.Quantity != src.Quantity)
            {
                existing.Quantity = src.Quantity;
                changed = true;
            }
        }
        for (var i = _yieldItems.Count - 1; i >= 0; i--)
            if (merged.All(s => s.ItemIndex != _yieldItems[i].ItemIndex))
            {
                _yieldItems.RemoveAt(i);
                changed = true;
            }

        if (!changed) return;
        OnPropertyChanged(nameof(TotalYieldValue));
        OnPropertyChanged(nameof(NetProfit));
        OnPropertyChanged(nameof(IsNetProfitNegative));
        OnPropertyChanged(nameof(ROIText));
        OnPropertyChanged(nameof(TotalYieldQuantity));
        OnPropertyChanged(nameof(UniqueYieldItems));
        BuildYieldChart();
    }

    public void UpdateConsumedItems(IReadOnlyList<IslandConsumedEntry> source)
    {
        // Collapse to one row per item (see UpdateYieldItems): the same item can be booked under more than
        // one SourcePlot across consume paths, which would otherwise show as duplicate tiles.
        var merged = source
            .GroupBy(e => e.ItemIndex)
            .Select(g => new IslandConsumedEntry
            {
                ItemIndex = g.Key,
                Quantity = g.Sum(e => e.Quantity),
                SourcePlot = g.First().SourcePlot,
                ConsumedAt = g.Min(e => e.ConsumedAt)
            })
            .ToList();

        var changed = false;
        foreach (var src in merged)
        {
            var existing = _consumedItems.FirstOrDefault(e => e.ItemIndex == src.ItemIndex);
            if (existing == null)
            {
                _consumedItems.Add(new IslandConsumedEntry { ItemIndex = src.ItemIndex, Quantity = src.Quantity, SourcePlot = src.SourcePlot, ConsumedAt = src.ConsumedAt });
                changed = true;
            }
            else if (existing.Quantity != src.Quantity)
            {
                existing.Quantity = src.Quantity;
                changed = true;
            }
        }
        for (var i = _consumedItems.Count - 1; i >= 0; i--)
            if (merged.All(s => s.ItemIndex != _consumedItems[i].ItemIndex))
            {
                _consumedItems.RemoveAt(i);
                changed = true;
            }

        if (!changed) return;
        OnPropertyChanged(nameof(TotalConsumedValue));
        OnPropertyChanged(nameof(NetProfit));
        OnPropertyChanged(nameof(IsNetProfitNegative));
        OnPropertyChanged(nameof(ROIText));
        OnPropertyChanged(nameof(TotalConsumedQuantity));
        OnPropertyChanged(nameof(UniqueConsumedItems));
        BuildYieldChart();
    }

    public void BuildYieldChart()
    {
        if (_selectedChartMode == IslandYieldChartMode.Summary)
        {
            YieldChartSeries.Clear();
            return;
        }

        var yieldByDay = _yieldItems
            .GroupBy(e => e.CollectedAt.Date)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.TotalAvgEstMarketValue));
        var consumedByDay = _consumedItems
            .GroupBy(e => e.ConsumedAt.Date)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.TotalAvgEstMarketValue));

        var allDates = yieldByDay.Keys.Union(consumedByDay.Keys).OrderBy(d => d).ToList();

        if (allDates.Count == 0)
        {
            YieldChartSeries.Clear();
            YieldChartXAxes = [new Axis { Labels = [] }];
            return;
        }

        var labels = allDates.Select(d => d.ToString("MMM dd")).ToArray();
        var yieldValues = allDates.Select(d => yieldByDay.TryGetValue(d, out var v) ? v : 0).ToArray();
        var consumedValues = allDates.Select(d => consumedByDay.TryGetValue(d, out var v) ? v : 0).ToArray();
        var netValues = Enumerable.Range(0, allDates.Count).Select(i => yieldValues[i] - consumedValues[i]).ToArray();

        YieldChartSeries.Clear();

        switch (_selectedChartMode)
        {
            case IslandYieldChartMode.CollectedVsConsumed:
                YieldChartSeries.Add(new ColumnSeries<double>
                {
                    Name = "Collected",
                    Values = yieldValues,
                    Fill = new SolidColorPaint(new SKColor(0x4C, 0xAF, 0x50)),
                });
                YieldChartSeries.Add(new ColumnSeries<double>
                {
                    Name = "Consumed",
                    Values = consumedValues,
                    Fill = new SolidColorPaint(new SKColor(0xF4, 0x43, 0x36)),
                });
                break;

            case IslandYieldChartMode.ROITrend:
                if (_isROIModeSilver)
                {
                    YieldChartSeries.Add(new LineSeries<double>
                    {
                        Name = "Net Profit",
                        Values = netValues,
                        Stroke = new SolidColorPaint(new SKColor(0x21, 0x96, 0xF3)) { StrokeThickness = 2 },
                        GeometryStroke = new SolidColorPaint(new SKColor(0x21, 0x96, 0xF3)) { StrokeThickness = 2 },
                        Fill = null,
                    });
                }
                else
                {
                    var roiValues = Enumerable.Range(0, allDates.Count)
                        .Select(i => consumedValues[i] > 0 ? (yieldValues[i] / consumedValues[i] - 1) * 100 : 0)
                        .ToArray();
                    YieldChartSeries.Add(new LineSeries<double>
                    {
                        Name = "ROI %",
                        Values = roiValues,
                        Stroke = new SolidColorPaint(new SKColor(0x9C, 0x27, 0xB0)) { StrokeThickness = 2 },
                        GeometryStroke = new SolidColorPaint(new SKColor(0x9C, 0x27, 0xB0)) { StrokeThickness = 2 },
                        Fill = null,
                    });
                }
                break;

            case IslandYieldChartMode.NetProfit:
                YieldChartSeries.Add(new ColumnSeries<double>
                {
                    Name = "Net Profit",
                    Values = netValues,
                    Fill = new SolidColorPaint(new SKColor(0x21, 0x96, 0xF3)),
                });
                break;
        }

        YieldChartXAxes = [new Axis { Labels = labels }];
    }
}

public class IslandPlotEntry : BaseViewModel
{
    public Guid PlotId { get; set; }
    public string PlotType { get; set; }
    public int Quantity { get; set; }
    public bool ShowQuantity => Quantity > 1;
    private string _plotSentState = string.Empty;
    public string PlotSentState { get => _plotSentState; set { _plotSentState = value; OnPropertyChanged(); } }
    public bool IsHouse { get; set; }
    private string _farmableTypeLine = string.Empty;
    public string FarmableTypeLine
    {
        get => _farmableTypeLine;
        set { _farmableTypeLine = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCropIcon)); OnPropertyChanged(nameof(HasNoCropIcon)); }
    }

    private System.Windows.Media.Imaging.BitmapImage _farmableCropIcon;
    public System.Windows.Media.Imaging.BitmapImage FarmableCropIcon
    {
        get => _farmableCropIcon;
        set { _farmableCropIcon = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCropIcon)); OnPropertyChanged(nameof(HasNoCropIcon)); }
    }

    public bool HasCropIcon => FarmableCropIcon != null;
    public bool HasNoCropIcon => FarmableCropIcon == null;

    private string _farmableCropTooltip;
    public string FarmableCropTooltip
    {
        get => _farmableCropTooltip;
        set { _farmableCropTooltip = value; OnPropertyChanged(); }
    }
    private string _laborer1IndicatorState = string.Empty;
    public string Laborer1IndicatorState { get => _laborer1IndicatorState; set { _laborer1IndicatorState = value; OnPropertyChanged(); } }
    private string _laborer1Line = string.Empty;
    public string Laborer1Line { get => _laborer1Line; set { _laborer1Line = value; OnPropertyChanged(); } }
    private string _laborer2IndicatorState = string.Empty;
    public string Laborer2IndicatorState { get => _laborer2IndicatorState; set { _laborer2IndicatorState = value; OnPropertyChanged(); } }
    private string _laborer2Line = string.Empty;
    public string Laborer2Line { get => _laborer2Line; set { _laborer2Line = value; OnPropertyChanged(); } }
    private string _laborer3IndicatorState = string.Empty;
    public string Laborer3IndicatorState { get => _laborer3IndicatorState; set { _laborer3IndicatorState = value; OnPropertyChanged(); } }
    private string _laborer3Line = string.Empty;
    public string Laborer3Line { get => _laborer3Line; set { _laborer3Line = value; OnPropertyChanged(); } }
    private string _laborer1TimeRemaining = string.Empty, _laborer2TimeRemaining = string.Empty, _laborer3TimeRemaining = string.Empty;
    private string _plotCollectionCountdown = string.Empty;

    public string Laborer1TimeRemaining { get => _laborer1TimeRemaining; set { _laborer1TimeRemaining = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLaborer1Timer)); } }
    public string Laborer2TimeRemaining { get => _laborer2TimeRemaining; set { _laborer2TimeRemaining = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLaborer2Timer)); } }
    public string Laborer3TimeRemaining { get => _laborer3TimeRemaining; set { _laborer3TimeRemaining = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLaborer3Timer)); } }
    public bool HasLaborer1Timer => !string.IsNullOrEmpty(Laborer1TimeRemaining);
    public bool HasLaborer2Timer => !string.IsNullOrEmpty(Laborer2TimeRemaining);
    public bool HasLaborer3Timer => !string.IsNullOrEmpty(Laborer3TimeRemaining);

    public string PlotCollectionCountdown { get => _plotCollectionCountdown; set { _plotCollectionCountdown = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPlotCollectionCountdown)); } }
    public bool HasPlotCollectionCountdown => !string.IsNullOrEmpty(PlotCollectionCountdown);
    private IReadOnlyList<string> _slotDots = [];
    public IReadOnlyList<string> SlotDots
    {
        get => _slotDots;
        set { _slotDots = value; OnPropertyChanged(); }
    }

    public int? MapSlotIndex { get; set; }
    private string _mapSlotLabel = string.Empty;
    // Notifying: the plot card "#N" binds to this, and slot assignment can happen on a status-only
    // refresh (not just a full rebuild), so it must raise PropertyChanged to update the card.
    public string MapSlotLabel { get => _mapSlotLabel; set { _mapSlotLabel = value; OnPropertyChanged(); } }
    public int? SlotHighlightCol { get; set; }
    public int? SlotHighlightRow { get; set; }
    public string SlotStateCode { get; set; } = "empty";
    public IReadOnlyList<SlotGridCell> PlotSlotGrid { get; set; } = [];

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }
}

public class LiveLaborerActionEntry
{
    public DateTime Timestamp { get; set; }
    public string Name { get; set; }
    public bool NeedsAttention { get; set; }
    public string Action { get; set; }
    public string Detail { get; set; }
}

public class LiveSummarySegment
{
    public string Category { get; set; }
    public string Text { get; set; }
}

public class IslandGroupModeOption(IslandGroupMode value, string displayName)
{
    public IslandGroupMode Value { get; } = value;
    public string DisplayName { get; } = displayName;
}

public class IslandSortModeOption(IslandSortMode value, string displayName)
{
    public IslandSortMode Value { get; } = value;
    public string DisplayName { get; } = displayName;
}

public class ManagerResponsibilityItem(ManagerResponsibility flag, bool isSelected) : BaseViewModel
{
    private bool _isSelected = isSelected;

    public ManagerResponsibility Flag { get; } = flag;

    public string DisplayName => Flag switch
    {
        ManagerResponsibility.HandlesRefills => "Handles refills",
        ManagerResponsibility.NotifyLowResources => "Notify low resources",
        ManagerResponsibility.RequestsMaterials => "Requests materials",
        _ => Flag.ToString()
    };

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }
}

public class LaborerTypeCount
{
    public string Display { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class PlotTypeSummaryRow
{
    public string DisplayName { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public IReadOnlyList<LaborerTypeCount> Details { get; set; } = [];
}

public class OwnerIslandSummaryRow
{
    public string OwnerName { get; set; } = string.Empty;
    public int IslandCount { get; set; }
    public int TotalLaborers { get; set; }
    public IReadOnlyList<LaborerTypeCount> LaborersByTierType { get; set; } = [];
    public IReadOnlyList<PlotTypeSummaryRow> PlotBreakdown { get; set; } = [];
}

public sealed class ChartPeriodOption(string label, int? days)
{
    public string Label { get; } = label;
    public int? Days { get; } = days;
    public override string ToString() => Label;
}

public record ChartModeOption(IslandYieldChartMode Value, string DisplayName);

public class IslandYieldPricingRow : BaseViewModel
{
    private readonly IslandManagementPreferences _prefs;

    public IslandYieldEntry Entry { get; }

    public IslandYieldPricingRow(IslandYieldEntry entry, IslandManagementPreferences prefs)
    {
        Entry = entry;
        _prefs = prefs;
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    private string OverrideKey => Entry.Item?.UniqueName ?? string.Empty;

    private ItemPriceOverride GetOverride() =>
        !string.IsNullOrEmpty(OverrideKey) && _prefs.PriceOverrides.TryGetValue(OverrideKey, out var o) ? o : null;

    public string PriceSource
    {
        get => GetOverride()?.PriceSource ?? _prefs.GlobalPriceSource;
        set
        {
            if (string.IsNullOrEmpty(OverrideKey)) return;
            var current = GetOverride() ?? new ItemPriceOverride(null, null, null);
            _prefs.PriceOverrides[OverrideKey] = current with { PriceSource = value };
            _ = SettingsController.SaveSettingsAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectivePricePerUnit));
            OnPropertyChanged(nameof(TotalValue));
            OnPropertyChanged(nameof(PricePerUnitText));
            OnPropertyChanged(nameof(TotalValueText));
        }
    }

    public string City
    {
        get => GetOverride()?.City ?? _prefs.GlobalCity;
        set
        {
            if (string.IsNullOrEmpty(OverrideKey)) return;
            var current = GetOverride() ?? new ItemPriceOverride(null, null, null);
            _prefs.PriceOverrides[OverrideKey] = current with { City = value };
            _ = SettingsController.SaveSettingsAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectivePricePerUnit));
            OnPropertyChanged(nameof(TotalValue));
            OnPropertyChanged(nameof(PricePerUnitText));
            OnPropertyChanged(nameof(TotalValueText));
        }
    }

    public double? ManualPrice
    {
        get => GetOverride()?.ManualValue;
        set
        {
            if (string.IsNullOrEmpty(OverrideKey)) return;
            var current = GetOverride() ?? new ItemPriceOverride(null, null, null);
            _prefs.PriceOverrides[OverrideKey] = current with { ManualValue = value };
            _ = SettingsController.SaveSettingsAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ManualPriceText));
            OnPropertyChanged(nameof(EffectivePricePerUnit));
            OnPropertyChanged(nameof(TotalValue));
            OnPropertyChanged(nameof(PricePerUnitText));
            OnPropertyChanged(nameof(TotalValueText));
        }
    }

    public string ManualPriceText
    {
        get => ManualPrice.HasValue ? ManualPrice.Value.ToString("N0") : string.Empty;
        set
        {
            if (double.TryParse(value?.Replace(",", ""), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
                ManualPrice = d;
            else
                ManualPrice = null;
        }
    }

    public double EffectivePricePerUnit
    {
        get
        {
            if (ManualPrice is > 0) return ManualPrice.Value;
            if (PriceSource is "EMV" or null)
                return Entry.Quantity > 0 ? Entry.TotalAvgEstMarketValue / Entry.Quantity : 0;
            return 0; // Buy/Sell order prices require market data integration
        }
    }

    public double TotalValue => EffectivePricePerUnit * Entry.Quantity;
    public string PricePerUnitText => EffectivePricePerUnit > 0 ? EffectivePricePerUnit.ToString("N0") : "—";
    public string TotalValueText => EffectivePricePerUnit > 0 ? TotalValue.ToString("N0") : "—";
}

public class IslandConsumedPricingRow : BaseViewModel
{
    private readonly IslandManagementPreferences _prefs;

    public IslandConsumedEntry Entry { get; }

    public IslandConsumedPricingRow(IslandConsumedEntry entry, IslandManagementPreferences prefs)
    {
        Entry = entry;
        _prefs = prefs;
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    private string OverrideKey => Entry.Item?.UniqueName ?? string.Empty;

    private ItemPriceOverride GetOverride() =>
        !string.IsNullOrEmpty(OverrideKey) && _prefs.PriceOverrides.TryGetValue(OverrideKey, out var o) ? o : null;

    public string PriceSource
    {
        get => GetOverride()?.PriceSource ?? _prefs.GlobalPriceSource;
        set
        {
            if (string.IsNullOrEmpty(OverrideKey)) return;
            var current = GetOverride() ?? new ItemPriceOverride(null, null, null);
            _prefs.PriceOverrides[OverrideKey] = current with { PriceSource = value };
            _ = SettingsController.SaveSettingsAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectivePricePerUnit));
            OnPropertyChanged(nameof(TotalValue));
            OnPropertyChanged(nameof(PricePerUnitText));
            OnPropertyChanged(nameof(TotalValueText));
        }
    }

    public string City
    {
        get => GetOverride()?.City ?? _prefs.GlobalCity;
        set
        {
            if (string.IsNullOrEmpty(OverrideKey)) return;
            var current = GetOverride() ?? new ItemPriceOverride(null, null, null);
            _prefs.PriceOverrides[OverrideKey] = current with { City = value };
            _ = SettingsController.SaveSettingsAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectivePricePerUnit));
            OnPropertyChanged(nameof(TotalValue));
            OnPropertyChanged(nameof(PricePerUnitText));
            OnPropertyChanged(nameof(TotalValueText));
        }
    }

    public double? ManualPrice
    {
        get => GetOverride()?.ManualValue;
        set
        {
            if (string.IsNullOrEmpty(OverrideKey)) return;
            var current = GetOverride() ?? new ItemPriceOverride(null, null, null);
            _prefs.PriceOverrides[OverrideKey] = current with { ManualValue = value };
            _ = SettingsController.SaveSettingsAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ManualPriceText));
            OnPropertyChanged(nameof(EffectivePricePerUnit));
            OnPropertyChanged(nameof(TotalValue));
            OnPropertyChanged(nameof(PricePerUnitText));
            OnPropertyChanged(nameof(TotalValueText));
        }
    }

    public string ManualPriceText
    {
        get => ManualPrice.HasValue ? ManualPrice.Value.ToString("N0") : string.Empty;
        set
        {
            if (double.TryParse(value?.Replace(",", ""), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
                ManualPrice = d;
            else
                ManualPrice = null;
        }
    }

    public double EffectivePricePerUnit
    {
        get
        {
            if (ManualPrice is > 0) return ManualPrice.Value;
            if (PriceSource is "EMV" or null)
                return Entry.Quantity > 0 ? Entry.TotalAvgEstMarketValue / Entry.Quantity : 0;
            return 0; // Buy/Sell order prices require market data integration
        }
    }

    public double TotalValue => EffectivePricePerUnit * Entry.Quantity;
    public string PricePerUnitText => EffectivePricePerUnit > 0 ? EffectivePricePerUnit.ToString("N0") : "—";
    public string TotalValueText => EffectivePricePerUnit > 0 ? TotalValue.ToString("N0") : "—";
}
