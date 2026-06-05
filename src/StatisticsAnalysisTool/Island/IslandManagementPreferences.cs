using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StatisticsAnalysisTool.Island;

public class IslandManagementPreferences : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _autoSelectIslandByMapChange = true;
    public bool AutoSelectIslandByMapChange
    {
        get => _autoSelectIslandByMapChange;
        set { _autoSelectIslandByMapChange = value; OnPropertyChanged(); }
    }

    private bool _autoStartCycleOnMapChange;
    public bool AutoStartCycleOnMapChange
    {
        get => _autoStartCycleOnMapChange;
        set { _autoStartCycleOnMapChange = value; OnPropertyChanged(); }
    }

    private bool _autoStartCycleOnIslandActivity;
    public bool AutoStartCycleOnIslandActivity
    {
        get => _autoStartCycleOnIslandActivity;
        set { _autoStartCycleOnIslandActivity = value; OnPropertyChanged(); }
    }

    private bool _discordIncludeIndividualIslands = true;
    public bool DiscordIncludeIndividualIslands
    {
        get => _discordIncludeIndividualIslands;
        set { _discordIncludeIndividualIslands = value; OnPropertyChanged(); }
    }

    private bool _aggregateAutoRecordsDaily = true;
    public bool AggregateAutoRecordsDaily
    {
        get => _aggregateAutoRecordsDaily;
        set { _aggregateAutoRecordsDaily = value; OnPropertyChanged(); }
    }

    private bool _discordIncludeSummaryWhenIslandsHidden = true;
    public bool DiscordIncludeSummaryWhenIslandsHidden
    {
        get => _discordIncludeSummaryWhenIslandsHidden;
        set { _discordIncludeSummaryWhenIslandsHidden = value; OnPropertyChanged(); }
    }

    private bool _showArrangementSection = true;
    public bool ShowArrangementSection
    {
        get => _showArrangementSection;
        set { _showArrangementSection = value; OnPropertyChanged(); }
    }

    private bool _showNotesSection = true;
    public bool ShowNotesSection
    {
        get => _showNotesSection;
        set { _showNotesSection = value; OnPropertyChanged(); }
    }

    private bool _showDiscordWebhookSection = true;
    public bool ShowDiscordWebhookSection
    {
        get => _showDiscordWebhookSection;
        set { _showDiscordWebhookSection = value; OnPropertyChanged(); }
    }

    private bool _showOwnerManagementSection = true;
    public bool ShowOwnerManagementSection
    {
        get => _showOwnerManagementSection;
        set { _showOwnerManagementSection = value; OnPropertyChanged(); }
    }

    private bool _showFinanceSettingsSection = true;
    public bool ShowFinanceSettingsSection
    {
        get => _showFinanceSettingsSection;
        set { _showFinanceSettingsSection = value; OnPropertyChanged(); }
    }

    private bool _showFinanceHistorySection = true;
    public bool ShowFinanceHistorySection
    {
        get => _showFinanceHistorySection;
        set { _showFinanceHistorySection = value; OnPropertyChanged(); }
    }

    private bool _islandSummaryVisible = true;
    public bool IslandSummaryVisible
    {
        get => _islandSummaryVisible;
        set { _islandSummaryVisible = value; OnPropertyChanged(); }
    }

    private bool _ownerProfileVisible = true;
    public bool OwnerProfileVisible
    {
        get => _ownerProfileVisible;
        set { _ownerProfileVisible = value; OnPropertyChanged(); }
    }

    private bool _financeSettingsVisible = true;
    public bool FinanceSettingsVisible
    {
        get => _financeSettingsVisible;
        set { _financeSettingsVisible = value; OnPropertyChanged(); }
    }

    private bool _financeGraphVisible = true;
    public bool FinanceGraphVisible
    {
        get => _financeGraphVisible;
        set { _financeGraphVisible = value; OnPropertyChanged(); }
    }

    private bool _financeRecordVisible = true;
    public bool FinanceRecordVisible
    {
        get => _financeRecordVisible;
        set { _financeRecordVisible = value; OnPropertyChanged(); }
    }

    private bool _financeHistoryVisible = true;
    public bool FinanceHistoryVisible
    {
        get => _financeHistoryVisible;
        set { _financeHistoryVisible = value; OnPropertyChanged(); }
    }

    private bool _discordAlertsVisible = true;
    public bool DiscordAlertsVisible
    {
        get => _discordAlertsVisible;
        set { _discordAlertsVisible = value; OnPropertyChanged(); }
    }

    private bool _showYieldSummarySection = true;
    public bool ShowYieldSummarySection
    {
        get => _showYieldSummarySection;
        set { _showYieldSummarySection = value; OnPropertyChanged(); }
    }

    private bool _isYieldSummarySectionCollapsed;
    public bool IsYieldSummarySectionCollapsed
    {
        get => _isYieldSummarySectionCollapsed;
        set { _isYieldSummarySectionCollapsed = value; OnPropertyChanged(); }
    }

    private bool _isIslandSummarySectionCollapsed;
    public bool IsIslandSummarySectionCollapsed
    {
        get => _isIslandSummarySectionCollapsed;
        set { _isIslandSummarySectionCollapsed = value; OnPropertyChanged(); }
    }

    private bool _isFinanceRecordSectionCollapsed;
    public bool IsFinanceRecordSectionCollapsed
    {
        get => _isFinanceRecordSectionCollapsed;
        set { _isFinanceRecordSectionCollapsed = value; OnPropertyChanged(); }
    }

    private bool _isRecordCycleCollapsed;
    public bool IsRecordCycleCollapsed
    {
        get => _isRecordCycleCollapsed;
        set { _isRecordCycleCollapsed = value; OnPropertyChanged(); }
    }

    private bool _isRecordPayoutCollapsed;
    public bool IsRecordPayoutCollapsed
    {
        get => _isRecordPayoutCollapsed;
        set { _isRecordPayoutCollapsed = value; OnPropertyChanged(); }
    }

    private bool _showIslandPayOverride = true;
    public bool ShowIslandPayOverride
    {
        get => _showIslandPayOverride;
        set { _showIslandPayOverride = value; OnPropertyChanged(); }
    }

    private bool _useDailyPayoutMode;
    public bool UseDailyPayoutMode
    {
        get => _useDailyPayoutMode;
        set { _useDailyPayoutMode = value; OnPropertyChanged(); }
    }

    private bool _autoNotifyOwnerWhenAllDone;
    public bool AutoNotifyOwnerWhenAllDone
    {
        get => _autoNotifyOwnerWhenAllDone;
        set { _autoNotifyOwnerWhenAllDone = value; OnPropertyChanged(); }
    }

    private bool _autoPrefillPayouts;
    public bool AutoPrefillPayouts
    {
        get => _autoPrefillPayouts;
        set { _autoPrefillPayouts = value; OnPropertyChanged(); }
    }

    private bool _isOwnerOverviewVisible;
    public bool IsOwnerOverviewVisible
    {
        get => _isOwnerOverviewVisible;
        set { _isOwnerOverviewVisible = value; OnPropertyChanged(); }
    }

    private bool _isIslandSettingsVisible;
    public bool IsIslandSettingsVisible
    {
        get => _isIslandSettingsVisible;
        set { _isIslandSettingsVisible = value; OnPropertyChanged(); }
    }

    private bool _isOwnerOverviewSettingsOpen;
    public bool IsOwnerOverviewSettingsOpen
    {
        get => _isOwnerOverviewSettingsOpen;
        set { _isOwnerOverviewSettingsOpen = value; OnPropertyChanged(); }
    }

    private string _selectedOverviewOwner = string.Empty;
    public string SelectedOverviewOwner
    {
        get => _selectedOverviewOwner;
        set { _selectedOverviewOwner = value ?? string.Empty; OnPropertyChanged(); }
    }

    private bool _allowMultiOwnerSelection;
    public bool AllowMultiOwnerSelection
    {
        get => _allowMultiOwnerSelection;
        set { _allowMultiOwnerSelection = value; OnPropertyChanged(); }
    }

    private int _defaultVisitDurationMinutes = 5;
    public int DefaultVisitDurationMinutes
    {
        get => _defaultVisitDurationMinutes;
        set { _defaultVisitDurationMinutes = value; OnPropertyChanged(); }
    }

    private bool _showTier = true;
    public bool ShowTier
    {
        get => _showTier;
        set { _showTier = value; OnPropertyChanged(); }
    }

    private bool _showPremium = true;
    public bool ShowPremium
    {
        get => _showPremium;
        set { _showPremium = value; OnPropertyChanged(); }
    }

    private bool _isOwnerSectionCollapsed;
    public bool IsOwnerSectionCollapsed
    {
        get => _isOwnerSectionCollapsed;
        set { _isOwnerSectionCollapsed = value; OnPropertyChanged(); }
    }

    private bool _isUnassignedPanelCollapsed;
    public bool IsUnassignedPanelCollapsed
    {
        get => _isUnassignedPanelCollapsed;
        set { _isUnassignedPanelCollapsed = value; OnPropertyChanged(); }
    }

    private int _financeHistoryMaxVisible = 20;
    public int FinanceHistoryMaxVisible
    {
        get => _financeHistoryMaxVisible;
        set { _financeHistoryMaxVisible = Math.Max(1, value); OnPropertyChanged(); }
    }

    private DayOfWeek _weekStartDay = DayOfWeek.Monday;
    public DayOfWeek WeekStartDay
    {
        get => _weekStartDay;
        set { _weekStartDay = value; OnPropertyChanged(); }
    }

    private string _globalPriceSource = "EMV";
    public string GlobalPriceSource
    {
        get => _globalPriceSource;
        set { _globalPriceSource = value ?? "EMV"; OnPropertyChanged(); }
    }

    private string _globalCity = "Caerleon";
    public string GlobalCity
    {
        get => _globalCity;
        set { _globalCity = value ?? "Caerleon"; OnPropertyChanged(); }
    }

    public Dictionary<string, ItemPriceOverride> PriceOverrides { get; set; } = new();
}

public record ItemPriceOverride(string PriceSource, string City, double? ManualValue);
