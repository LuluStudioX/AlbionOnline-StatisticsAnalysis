using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using StatisticsAnalysisTool.Models;
using StatisticsAnalysisTool.ViewModels;

namespace StatisticsAnalysisTool.Overlay;

public class OverlayOptionsObject : BaseViewModel
{

    // Unique identifier for this OverlayOptionsObject instance, useful for debugging multiple instances
    private readonly Guid _instanceId = Guid.NewGuid();

    /// <summary>
    /// Starts the overlay server and integration if overlays are enabled in settings.
    /// This method is idempotent and can be called safely multiple times.
    /// </summary>
    public void StartOverlayIfEnabled()
    {
        // Only attempt to start if global setting allows it
        var settings = StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings;
        if (!settings.OverlayIsEnabled)
        {
            Serilog.Log.Debug("[OverlayOptionsObject] StartOverlayIfEnabled: OverlayIsEnabled is false in settings - skipping start");
            return;
        }

        // Also require the Streaming Overlay navigation tab to be active in settings; if the user hid the tab
        // we should not start the overlay background services.
        if (!settings.IsStreamingOverlayNaviTabActive)
        {
            Serilog.Log.Debug("[OverlayOptionsObject] StartOverlayIfEnabled: Streaming overlay tab is hidden in settings - skipping start");
            return;
        }

        lock (_overlayLock)
        {
            if (_isOverlayEnabled)
            {
                Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: overlay already enabled (shared)");
                return;
            }

            try
            {
                Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Creating overlay server on port {_overlayPort}");
                _overlayServer = new StatisticsAnalysisTool.Network.Overlay.OverlayServer(_overlayPort);
                Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Starting overlay server...");
                _overlayServer.Start();
                _isOverlayEnabled = true;
                Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Overlay server started successfully, IsOverlayEnabled = true (shared)");

                var mainVM = MainWindowViewModel.Instance;
                if (mainVM != null)
                {
                    Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Creating OverlayIntegration with MainWindowViewModel (shared)");
                    _overlayIntegration = new OverlayIntegration(mainVM);
                    _overlayIntegration.Start();
                    Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: OverlayIntegration started successfully (shared)");
                    // Force initial dashboard and damage payload broadcast
                    try
                    {
                        Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Forcing initial dashboard and damage payload broadcast...");
                        _overlayIntegration.ForceInitialOverlayPush();
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, $"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Failed to force initial overlay payload broadcast");
                    }
                }
                else
                {
                    Serilog.Log.Warning($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: MainWindowViewModel.Instance is null - cannot start overlay integration");
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, $"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Failed to start overlay server");
                _isOverlayEnabled = false;
            }
        }
    }
    public void RestoreDefaults()
    {
        DashboardTitleFontSize = 14;
        // PvP display removed from options
        DashboardTotalFontSize = 20;
        DashboardPerHourFontSize = 12;
        DashboardAutoHideZeroValues = false;
        DashboardFontSize = 14;
        DashboardIconSize = 32;
        GatheringFontSize = 14;
        GatheringIconSize = 32;
        DamageFontSize = 14;
        DamageIconSize = 32;
        ShowRepair = true;
        // Reset metrics
        Metrics.Clear();
        Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FAME, Icon = "FameIcon", Color = "#3399FF", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "123,456", PerHourValue = "1,234/h" });
        Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.SILVER, Icon = "SilverIcon", Color = "#CCCCCC", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "789,000", PerHourValue = "7,890/h" });
        Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.RESPEC, Icon = "ReSpecIcon", Color = "#FF9900", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "1,234", PerHourValue = "12/h" });
        Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.MIGHT, Icon = "MightIcon", Color = "#AA66CC", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "5,678", PerHourValue = "56/h" });
        Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FAVOR, Icon = "FavorIcon", Color = "#33CC33", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "2,345", PerHourValue = "23/h" });
        Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FACTION, Icon = "FactionIcon", Color = "#FF3333", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "9,876", PerHourValue = "98/h" });
        // Ensure damage preview toggles are enabled by default for new users
        DamageShowDps = true;
        DamageShowHeal = true;
        DamageShowIcons = true;
        // Repair visibility defaults
        _repairShowToday = true;
        _repairShowLast7Days = true;
        _repairShowLast30Days = true;
    }
    public ObservableCollection<MetricDisplayOption> Metrics { get; } = new ObservableCollection<MetricDisplayOption>
    {
        new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FAME, Icon = "FameIcon", Color = "#3399FF", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "123,456", PerHourValue = "1,234/h" },
        new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.SILVER, Icon = "SilverIcon", Color = "#CCCCCC", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "789,000", PerHourValue = "7,890/h" },
        new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.RESPEC, Icon = "ReSpecIcon", Color = "#FF9900", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "1,234", PerHourValue = "12/h" },
        new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.MIGHT, Icon = "MightIcon", Color = "#AA66CC", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "5,678", PerHourValue = "56/h" },
        new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FAVOR, Icon = "FavorIcon", Color = "#33CC33", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "2,345", PerHourValue = "23/h" },
        new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FACTION, Icon = "FactionIcon", Color = "#FF3333", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "9,876", PerHourValue = "98/h" },
    };
    // Overlay server state (shared across instances to prevent duplicate servers)
    private static bool _isOverlayEnabled = false;
    private int _overlayPort = 8080;
    private static readonly object _overlayLock = new object();
    private static StatisticsAnalysisTool.Network.Overlay.OverlayServer _overlayServer = null;
    private string _selectedTheme = "Dark";
    private List<string> _themeOptions = new List<string> { "Dark", "Light" };
    private bool _showGathering = true;
    private bool _showDamage = true;
    private bool _showDashboard = true;
    private bool _showRepair = true;
    private int _selectedSectionIndex = 0;
    private bool _isStreamingOverlayNaviTabActive = true;

    // Section font/icon/auto-hide settings
    private double _dashboardTitleFontSize = 14;
    private double _dashboardTotalFontSize = 20;
    private double _dashboardPerHourFontSize = 12;
    private bool _dashboardAutoHideZeroValues = false;
    private double _dashboardFontSize = 14;
    private double _dashboardIconSize = 32;
    // When true, the overlay will substitute sample/example metric strings when no live metrics are present.
    // Default: false to avoid sending preview/example metrics unless explicitly enabled by the user.
    private bool _dashboardUsePreviewMetrics = false;
    private double _gatheringFontSize = 14;
    private double _gatheringIconSize = 32;
    private double _damageFontSize = 14;
    private double _damageIconSize = 32;
    private double _damageTitleFontSize = 18;
    private double _damageValueFontSize = 16;
    private double _damageDpsFontSize = 12;
    private double _damageHealFontSize = 12;

    // Repair overlay defaults
    private double _repairTitleFontSize = 16;
    private double _repairIconSize = 32;
    // Font size for the numeric value displayed for repair entries
    private double _repairValueFontSize = 14;
    // Per-repair-line visibility toggles (allow hiding individual repair metrics)
    private bool _repairShowToday = true;
    private bool _repairShowLast7Days = true;
    private bool _repairShowLast30Days = true;

    // Overlay integration for pushing ViewModel changes to overlay system (shared)
    private static OverlayIntegration _overlayIntegration;

    // TODO: Add per-metric show/hide settings if needed
    // Damage preview settings
    private int _damagePreviewCount = 5;
    private bool _damageShowDps = true;
    private bool _damageShowHeal = true;
    private bool _damageShowIcons = true;
    // PvP display removed: no longer tracked in options
    private bool _damageForceHealer = false;
    private bool _damageShowSelf = true;

    // PvP display removed

    public OverlayOptionsObject()
    {
        Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] Constructor called");
        // Precompute weapon candidate list for overlay random weapon icons
        try
        {
            // Ensure the MetricNameToIconPathConverter cache is built early to avoid IO during UI rendering
            // Call the internal cache builder via reflection-safe approach
            var method = typeof(StatisticsAnalysisTool.UserControls.MetricNameToIconPathConverter).GetMethod("EnsureWeaponCandidatesCached", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            method?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, $"[OverlayOptionsObject:{_instanceId}] Failed to precompute weapon candidate cache");
        }
        // Do NOT auto-start overlay from constructor to avoid side-effects when multiple instances
        // are created. The owning viewmodel should call StartOverlayIfEnabled() explicitly.
    }

    // Overlay server state
    public bool IsOverlayEnabled
    {
        get => _isOverlayEnabled;
        set
        {
            Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] IsOverlayEnabled setter called: current={_isOverlayEnabled}, new={value}");
            lock (_overlayLock)
            {
                if (_isOverlayEnabled == value)
                {
                    Serilog.Log.Debug($"[OverlayOptionsObject] IsOverlayEnabled setter called but value unchanged ({value})");
                    return;
                }
                Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] IsOverlayEnabled changing from {_isOverlayEnabled} to {value}");
                // Update shared state immediately and notify bindings
                _isOverlayEnabled = value;
                OnPropertyChanged();
                Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] IsOverlayEnabled set to {value}");

                // Perform potentially blocking start/stop work on a background thread to avoid UI freeze
                System.Threading.Tasks.Task.Run(() =>
                {
                    lock (_overlayLock)
                    {
                        if (_isOverlayEnabled)
                        {
                            // Start shared server/integration
                            if (_overlayServer == null)
                            {
                                try
                                {
                                    Serilog.Log.Debug("[OverlayOptionsObject] Creating new overlay server (shared)...");
                                    _overlayServer = new StatisticsAnalysisTool.Network.Overlay.OverlayServer(_overlayPort);
                                    _overlayServer.Start();
                                    Serilog.Log.Debug("[OverlayOptionsObject] New overlay server started (shared)");
                                }
                                catch (Exception ex)
                                {
                                    Serilog.Log.Warning(ex, "[OverlayOptionsObject] Failed to start shared overlay server");
                                    _isOverlayEnabled = false;
                                    // notify on UI thread
                                    System.Windows.Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(IsOverlayEnabled)));
                                    return;
                                }
                            }

                            if (_overlayIntegration == null)
                            {
                                var mainVM = MainWindowViewModel.Instance;
                                if (mainVM != null)
                                {
                                    _overlayIntegration = new OverlayIntegration(mainVM);
                                    _overlayIntegration.Start();
                                    Serilog.Log.Debug("[OverlayOptionsObject] OverlayIntegration started (shared)");
                                    try { _overlayIntegration.ForceInitialOverlayPush(); } catch (Exception ex) { Serilog.Log.Warning(ex, "[OverlayOptionsObject] Failed to force initial overlay payload broadcast"); }
                                }
                                else
                                {
                                    Serilog.Log.Warning("[OverlayOptionsObject] Cannot start overlay integration - MainWindowViewModel.Instance is null");
                                }
                            }
                        }
                        else
                        {
                            // Stop shared integration/server
                            try
                            {
                                if (_overlayIntegration != null)
                                {
                                    _overlayIntegration.Dispose();
                                    _overlayIntegration = null;
                                    Serilog.Log.Debug("[OverlayOptionsObject] OverlayIntegration stopped (shared)");
                                }
                            }
                            catch (Exception ex) { Serilog.Log.Warning(ex, "[OverlayOptionsObject] Error while stopping overlay integration"); }

                            try
                            {
                                if (_overlayServer != null)
                                {
                                    _overlayServer.Stop();
                                    _overlayServer = null;
                                    Serilog.Log.Debug("[OverlayOptionsObject] Overlay server stopped (shared)");
                                }
                            }
                            catch (Exception ex) { Serilog.Log.Warning(ex, "[OverlayOptionsObject] Error while stopping overlay server"); }
                        }

                        // Persist the chosen overlay enabled state immediately so it survives restarts
                        try
                        {
                            StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings.OverlayIsEnabled = _isOverlayEnabled;
                            StatisticsAnalysisTool.Common.UserSettings.SettingsController.SaveSettings();
                            Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] Persisted OverlayIsEnabled={_isOverlayEnabled} to settings");
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, $"[OverlayOptionsObject:{_instanceId}] Failed to persist OverlayIsEnabled={_isOverlayEnabled}");
                        }
                    }
                });
            }
        }
    }
    public int OverlayPort
    {
        get => _overlayPort;
        set { _overlayPort = value; OnPropertyChanged(); }
    }
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (_selectedTheme != value)
            {
                _selectedTheme = value;
                OnPropertyChanged();
                // Immediately stage/broadcast settings so the overlay updates theme without requiring Apply
                try { BroadcastSettingsIfEnabled(); } catch { }
            }
        }
    }
    public List<string> ThemeOptions => _themeOptions;
    public bool ShowGathering
    {
        get => _showGathering;
        set { _showGathering = value; OnPropertyChanged(); }
    }
    public bool ShowDamage
    {
        get => _showDamage;
        set { _showDamage = value; OnPropertyChanged(); }
    }
    public bool ShowDashboard
    {
        get => _showDashboard;
        set { _showDashboard = value; OnPropertyChanged(); }
    }
    public bool ShowRepair
    {
        get => _showRepair;
        set { _showRepair = value; OnPropertyChanged(); }
    }
    public int SelectedSectionIndex
    {
        get => _selectedSectionIndex;
        set { _selectedSectionIndex = value; OnPropertyChanged(); }
    }
    public bool IsStreamingOverlayNaviTabActive
    {
        get => _isStreamingOverlayNaviTabActive;
        set { _isStreamingOverlayNaviTabActive = value; OnPropertyChanged(); }
    }

    // Section font/icon/auto-hide settings
    public double DashboardTitleFontSize
    {
        get => _dashboardTitleFontSize;
        set { _dashboardTitleFontSize = value; OnPropertyChanged(); }
    }
    public double DashboardTotalFontSize
    {
        get => _dashboardTotalFontSize;
        set { _dashboardTotalFontSize = value; OnPropertyChanged(); }
    }
    public double DashboardPerHourFontSize
    {
        get => _dashboardPerHourFontSize;
        set { _dashboardPerHourFontSize = value; OnPropertyChanged(); }
    }
    public bool DashboardAutoHideZeroValues
    {
        get => _dashboardAutoHideZeroValues;
        set { _dashboardAutoHideZeroValues = value; OnPropertyChanged(); }
    }
    public double DashboardFontSize
    {
        get => _dashboardFontSize;
        set { _dashboardFontSize = value; OnPropertyChanged(); }
    }
    public double DashboardIconSize
    {
        get => _dashboardIconSize;
        set { _dashboardIconSize = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// When true, the dashboard payload builder will substitute sample/example metrics from
    /// the OverlayOptions.Metrics collection when no live metrics are available. Default: false.
    /// </summary>
    public bool DashboardUsePreviewMetrics
    {
        get => _dashboardUsePreviewMetrics;
        set { _dashboardUsePreviewMetrics = value; OnPropertyChanged(); }
    }
    public double GatheringFontSize
    {
        get => _gatheringFontSize;
        set { _gatheringFontSize = value; OnPropertyChanged(); }
    }
    public double GatheringIconSize
    {
        get => _gatheringIconSize;
        set { _gatheringIconSize = value; OnPropertyChanged(); }
    }
    public double DamageFontSize
    {
        get => _damageFontSize;
        set { _damageFontSize = value; OnPropertyChanged(); }
    }
    public double DamageIconSize
    {
        get => _damageIconSize;
        set { _damageIconSize = value; OnPropertyChanged(); }
    }
    public double DamageTitleFontSize
    {
        get => _damageTitleFontSize;
        set { _damageTitleFontSize = value; OnPropertyChanged(); }
    }
    public double DamageValueFontSize
    {
        get => _damageValueFontSize;
        set { _damageValueFontSize = value; OnPropertyChanged(); }
    }
    public double RepairTitleFontSize
    {
        get => _repairTitleFontSize;
        set { _repairTitleFontSize = value; OnPropertyChanged(); }
    }

    public double RepairIconSize
    {
        get => _repairIconSize;
        set { _repairIconSize = value; OnPropertyChanged(); }
    }
    public double RepairValueFontSize
    {
        get => _repairValueFontSize;
        set { _repairValueFontSize = value; OnPropertyChanged(); }
    }
    // Per-repair metric visibility properties
    public bool RepairShowToday
    {
        get => _repairShowToday;
        set { _repairShowToday = value; OnPropertyChanged(); }
    }

    public bool RepairShowLast7Days
    {
        get => _repairShowLast7Days;
        set { _repairShowLast7Days = value; OnPropertyChanged(); }
    }

    public bool RepairShowLast30Days
    {
        get => _repairShowLast30Days;
        set { _repairShowLast30Days = value; OnPropertyChanged(); }
    }
    public double DamageDpsFontSize
    {
        get => _damageDpsFontSize;
        set { _damageDpsFontSize = value; OnPropertyChanged(); }
    }
    public double DamageHealFontSize
    {
        get => _damageHealFontSize;
        set { _damageHealFontSize = value; OnPropertyChanged(); }
    }
    // Damage preview settings
    public int DamagePreviewCount
    {
        get => _damagePreviewCount;
        set { _damagePreviewCount = value; OnPropertyChanged(); }
    }
    public bool DamageShowDps
    {
        get => _damageShowDps;
        set { _damageShowDps = value; OnPropertyChanged(); }
    }
    public bool DamageShowHeal
    {
        get => _damageShowHeal;
        set { _damageShowHeal = value; OnPropertyChanged(); }
    }
    public bool DamageShowIcons
    {
        get => _damageShowIcons;
        set { _damageShowIcons = value; OnPropertyChanged(); }
    }
    public bool DamageForceHealer
    {
        get => _damageForceHealer;
        set { _damageForceHealer = value; OnPropertyChanged(); }
    }
    public bool DamageShowSelf
    {
        get => _damageShowSelf;
        set { _damageShowSelf = value; OnPropertyChanged(); }
    }

    // New inverted helper property so the UI can present a "Hide yourself" checkbox while
    // keeping existing logic that expects DamageShowSelf (true = include self in previews).
    public bool DamageHideSelf
    {
        get => !_damageShowSelf;
        set { _damageShowSelf = !value; OnPropertyChanged(); OnPropertyChanged(nameof(DamageShowSelf)); }
    }
    public void BroadcastSettingsIfEnabled()
    {
        if (_isOverlayEnabled && _overlayServer != null)
        {
            var json = StatisticsAnalysisTool.Network.Overlay.OverlayServer.OverlaySettingsToJson(this);
            Serilog.Log.Debug($"[OverlayDebug] Staging settings to overlay server: {json}");
            _overlayServer.StageOverlaySettings(this);
        }
    }
}



// EARLIER BACKUP

// using System;
// using System.Collections.ObjectModel;
// using System.Collections.Generic;
// using StatisticsAnalysisTool.Models;
// using StatisticsAnalysisTool.ViewModels;

// namespace StatisticsAnalysisTool.Overlay;

// public class OverlayOptionsObject : BaseViewModel
// {

//     // Unique identifier for this OverlayOptionsObject instance, useful for debugging multiple instances
//     private readonly Guid _instanceId = Guid.NewGuid();

//     /// <summary>
//     /// Starts the overlay server and integration if overlays are enabled in settings.
//     /// This method is idempotent and can be called safely multiple times.
//     /// </summary>
//     public void StartOverlayIfEnabled()
//     {
//         // Only attempt to start if global setting allows it
//         if (!StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings.OverlayIsEnabled) return;

//         lock (_overlayLock)
//         {
//             if (_isOverlayEnabled)
//             {
//                 Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: overlay already enabled (shared)");
//                 return;
//             }

//             try
//             {
//                 Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Creating overlay server on port {_overlayPort}");
//                 _overlayServer = new StatisticsAnalysisTool.Network.Overlay.OverlayServer(_overlayPort);
//                 Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Starting overlay server...");
//                 _overlayServer.Start();
//                 _isOverlayEnabled = true;
//                 Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Overlay server started successfully, IsOverlayEnabled = true (shared)");

//                 var mainVM = MainWindowViewModel.Instance;
//                 if (mainVM != null)
//                 {
//                     Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Creating OverlayIntegration with MainWindowViewModel (shared)");
//                     _overlayIntegration = new OverlayIntegration(mainVM);
//                     _overlayIntegration.Start();
//                     Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: OverlayIntegration started successfully (shared)");
//                     // Force initial dashboard and damage payload broadcast
//                     try
//                     {
//                         Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Forcing initial dashboard and damage payload broadcast...");
//                         _overlayIntegration.ForceInitialOverlayPush();
//                     }
//                     catch (Exception ex)
//                     {
//                         Serilog.Log.Warning(ex, $"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Failed to force initial overlay payload broadcast");
//                     }
//                 }
//                 else
//                 {
//                     Serilog.Log.Warning($"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: MainWindowViewModel.Instance is null - cannot start overlay integration");
//                 }
//             }
//             catch (Exception ex)
//             {
//                 Serilog.Log.Warning(ex, $"[OverlayOptionsObject:{_instanceId}] StartOverlayIfEnabled: Failed to start overlay server");
//                 _isOverlayEnabled = false;
//             }
//         }
//     }
//     public void RestoreDefaults()
//     {
//         DashboardTitleFontSize = 14;
//         // PvP display removed from options
//         DashboardTotalFontSize = 20;
//         DashboardPerHourFontSize = 12;
//         DashboardAutoHideZeroValues = false;
//         DashboardFontSize = 14;
//         DashboardIconSize = 32;
//         GatheringFontSize = 14;
//         GatheringIconSize = 32;
//         DamageFontSize = 14;
//         DamageIconSize = 32;
//         // Reset metrics
//         Metrics.Clear();
//         Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FAME, Icon = "FameIcon", Color = "#3399FF", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "123,456", PerHourValue = "1,234/h" });
//         Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.SILVER, Icon = "SilverIcon", Color = "#CCCCCC", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "789,000", PerHourValue = "7,890/h" });
//         Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.RESPEC, Icon = "ReSpecIcon", Color = "#FF9900", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "1,234", PerHourValue = "12/h" });
//         Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.MIGHT, Icon = "MightIcon", Color = "#AA66CC", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "5,678", PerHourValue = "56/h" });
//         Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FAVOR, Icon = "FavorIcon", Color = "#33CC33", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "2,345", PerHourValue = "23/h" });
//         Metrics.Add(new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FACTION, Icon = "FactionIcon", Color = "#FF3333", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "9,876", PerHourValue = "98/h" });
//         // Ensure damage preview toggles are enabled by default for new users
//         DamageShowDps = true;
//         DamageShowHeal = true;
//         DamageShowIcons = true;
//     }
//     public ObservableCollection<MetricDisplayOption> Metrics { get; } = new ObservableCollection<MetricDisplayOption>
//     {
//     new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FAME, Icon = "FameIcon", Color = "#3399FF", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "123,456", PerHourValue = "1,234/h" },
//     new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.SILVER, Icon = "SilverIcon", Color = "#CCCCCC", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "789,000", PerHourValue = "7,890/h" },
//     new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.RESPEC, Icon = "ReSpecIcon", Color = "#FF9900", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "1,234", PerHourValue = "12/h" },
//     new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.MIGHT, Icon = "MightIcon", Color = "#AA66CC", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "5,678", PerHourValue = "56/h" },
//     new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FAVOR, Icon = "FavorIcon", Color = "#33CC33", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "2,345", PerHourValue = "23/h" },
//     new MetricDisplayOption { Name = StatisticsAnalysisTool.Models.TranslationModel.StreamingOverlayTranslation.FACTION, Icon = "FactionIcon", Color = "#FF3333", ShowTitle = true, ShowImage = true, ShowTotal = true, ShowPerHour = true, HideMetric = false, TotalValue = "9,876", PerHourValue = "98/h" },
//     };
//     // Overlay server state (shared across instances to prevent duplicate servers)
//     private static bool _isOverlayEnabled = false;
//     private int _overlayPort = 8080;
//     private static readonly object _overlayLock = new object();
//     private static StatisticsAnalysisTool.Network.Overlay.OverlayServer _overlayServer = null;
//     private string _selectedTheme = "Dark";
//     private List<string> _themeOptions = new List<string> { "Dark", "Light" };
//     private bool _showGathering = true;
//     private bool _showDamage = true;
//     private bool _showDashboard = true;
//     private int _selectedSectionIndex = 0;
//     private bool _isStreamingOverlayNaviTabActive = true;

//     // Section font/icon/auto-hide settings
//     private double _dashboardTitleFontSize = 14;
//     private double _dashboardTotalFontSize = 20;
//     private double _dashboardPerHourFontSize = 12;
//     private bool _dashboardAutoHideZeroValues = false;
//     private double _dashboardFontSize = 14;
//     private double _dashboardIconSize = 32;
//     // When true, the overlay will substitute sample/example metric strings when no live metrics are present.
//     // Default: false to avoid sending preview/example metrics unless explicitly enabled by the user.
//     private bool _dashboardUsePreviewMetrics = false;
//     private double _gatheringFontSize = 14;
//     private double _gatheringIconSize = 32;
//     private double _damageFontSize = 14;
//     private double _damageIconSize = 32;
//     private double _damageTitleFontSize = 18;
//     private double _damageValueFontSize = 16;
//     private double _damageDpsFontSize = 12;
//     private double _damageHealFontSize = 12;

//     // Overlay integration for pushing ViewModel changes to overlay system (shared)
//     private static OverlayIntegration _overlayIntegration;

//     // TODO: Add per-metric show/hide settings if needed
//     // Damage preview settings
//     private int _damagePreviewCount = 5;
//     private bool _damageShowDps = true;
//     private bool _damageShowHeal = true;
//     private bool _damageShowIcons = true;
//     // PvP display removed: no longer tracked in options
//     private bool _damageForceHealer = false;
//     private bool _damageShowSelf = true;

//     // PvP display removed

//     public OverlayOptionsObject()
//     {
//         Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] Constructor called");
//         // Precompute weapon candidate list for overlay random weapon icons
//         try
//         {
//             // Ensure the MetricNameToIconPathConverter cache is built early to avoid IO during UI rendering
//             // Call the internal cache builder via reflection-safe approach
//             var method = typeof(StatisticsAnalysisTool.UserControls.MetricNameToIconPathConverter).GetMethod("EnsureWeaponCandidatesCached", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
//             method?.Invoke(null, null);
//         }
//         catch (Exception ex)
//         {
//             Serilog.Log.Warning(ex, $"[OverlayOptionsObject:{_instanceId}] Failed to precompute weapon candidate cache");
//         }
//         // Do NOT auto-start overlay from constructor to avoid side-effects when multiple instances
//         // are created. The owning viewmodel should call StartOverlayIfEnabled() explicitly.
//     }

//     // Overlay server state
//     public bool IsOverlayEnabled
//     {
//         get => _isOverlayEnabled;
//         set
//         {
//             Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] IsOverlayEnabled setter called: current={_isOverlayEnabled}, new={value}");
//             lock (_overlayLock)
//             {
//                 if (_isOverlayEnabled == value)
//                 {
//                     Serilog.Log.Debug($"[OverlayOptionsObject] IsOverlayEnabled setter called but value unchanged ({value})");
//                     return;
//                 }
//                 Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] IsOverlayEnabled changing from {_isOverlayEnabled} to {value}");
//                 // Update shared state
//                 _isOverlayEnabled = value;
//                 OnPropertyChanged();
//                 Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] IsOverlayEnabled set to {value}");

//                 if (_isOverlayEnabled)
//                 {
//                     // Start shared server/integration
//                     if (_overlayServer == null)
//                     {
//                         try
//                         {
//                             Serilog.Log.Debug("[OverlayOptionsObject] Creating new overlay server (shared)...");
//                             _overlayServer = new StatisticsAnalysisTool.Network.Overlay.OverlayServer(_overlayPort);
//                             _overlayServer.Start();
//                             Serilog.Log.Debug("[OverlayOptionsObject] New overlay server started (shared)");
//                         }
//                         catch (Exception ex)
//                         {
//                             Serilog.Log.Warning(ex, "[OverlayOptionsObject] Failed to start shared overlay server");
//                             _isOverlayEnabled = false;
//                             return;
//                         }
//                     }

//                     if (_overlayIntegration == null)
//                     {
//                         var mainVM = MainWindowViewModel.Instance;
//                         if (mainVM != null)
//                         {
//                             _overlayIntegration = new OverlayIntegration(mainVM);
//                             _overlayIntegration.Start();
//                             Serilog.Log.Debug("[OverlayOptionsObject] OverlayIntegration started (shared)");
//                             try { _overlayIntegration.ForceInitialOverlayPush(); } catch (Exception ex) { Serilog.Log.Warning(ex, "[OverlayOptionsObject] Failed to force initial overlay payload broadcast"); }
//                         }
//                         else
//                         {
//                             Serilog.Log.Warning("[OverlayOptionsObject] Cannot start overlay integration - MainWindowViewModel.Instance is null");
//                         }
//                     }
//                 }
//                 else
//                 {
//                     // Stop shared integration/server
//                     try
//                     {
//                         if (_overlayIntegration != null)
//                         {
//                             _overlayIntegration.Dispose();
//                             _overlayIntegration = null;
//                             Serilog.Log.Debug("[OverlayOptionsObject] OverlayIntegration stopped (shared)");
//                         }
//                     }
//                     catch (Exception ex) { Serilog.Log.Warning(ex, "[OverlayOptionsObject] Error while stopping overlay integration"); }

//                     try
//                     {
//                         if (_overlayServer != null)
//                         {
//                             _overlayServer.Stop();
//                             _overlayServer = null;
//                             Serilog.Log.Debug("[OverlayOptionsObject] Overlay server stopped (shared)");
//                         }
//                     }
//                     catch (Exception ex) { Serilog.Log.Warning(ex, "[OverlayOptionsObject] Error while stopping overlay server"); }
//                 }
//                 // Persist the chosen overlay enabled state immediately so it survives restarts
//                 try
//                 {
//                     StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings.OverlayIsEnabled = _isOverlayEnabled;
//                     StatisticsAnalysisTool.Common.UserSettings.SettingsController.SaveSettings();
//                     Serilog.Log.Debug($"[OverlayOptionsObject:{_instanceId}] Persisted OverlayIsEnabled={_isOverlayEnabled} to settings");
//                 }
//                 catch (Exception ex)
//                 {
//                     Serilog.Log.Warning(ex, $"[OverlayOptionsObject:{_instanceId}] Failed to persist OverlayIsEnabled={_isOverlayEnabled}");
//                 }
//             }
//         }
//     }
//     public int OverlayPort
//     {
//         get => _overlayPort;
//         set { _overlayPort = value; OnPropertyChanged(); }
//     }
//     public string SelectedTheme
//     {
//         get => _selectedTheme;
//         set
//         {
//             if (_selectedTheme != value)
//             {
//                 _selectedTheme = value;
//                 OnPropertyChanged();
//                 // Immediately stage/broadcast settings so the overlay updates theme without requiring Apply
//                 try { BroadcastSettingsIfEnabled(); } catch { }
//             }
//         }
//     }
//     public List<string> ThemeOptions => _themeOptions;
//     public bool ShowGathering
//     {
//         get => _showGathering;
//         set { _showGathering = value; OnPropertyChanged(); }
//     }
//     public bool ShowDamage
//     {
//         get => _showDamage;
//         set { _showDamage = value; OnPropertyChanged(); }
//     }
//     public bool ShowDashboard
//     {
//         get => _showDashboard;
//         set { _showDashboard = value; OnPropertyChanged(); }
//     }
//     public int SelectedSectionIndex
//     {
//         get => _selectedSectionIndex;
//         set { _selectedSectionIndex = value; OnPropertyChanged(); }
//     }
//     public bool IsStreamingOverlayNaviTabActive
//     {
//         get => _isStreamingOverlayNaviTabActive;
//         set { _isStreamingOverlayNaviTabActive = value; OnPropertyChanged(); }
//     }

//     // Section font/icon/auto-hide settings
//     public double DashboardTitleFontSize
//     {
//         get => _dashboardTitleFontSize;
//         set { _dashboardTitleFontSize = value; OnPropertyChanged(); }
//     }
//     public double DashboardTotalFontSize
//     {
//         get => _dashboardTotalFontSize;
//         set { _dashboardTotalFontSize = value; OnPropertyChanged(); }
//     }
//     public double DashboardPerHourFontSize
//     {
//         get => _dashboardPerHourFontSize;
//         set { _dashboardPerHourFontSize = value; OnPropertyChanged(); }
//     }
//     public bool DashboardAutoHideZeroValues
//     {
//         get => _dashboardAutoHideZeroValues;
//         set { _dashboardAutoHideZeroValues = value; OnPropertyChanged(); }
//     }
//     public double DashboardFontSize
//     {
//         get => _dashboardFontSize;
//         set { _dashboardFontSize = value; OnPropertyChanged(); }
//     }
//     public double DashboardIconSize
//     {
//         get => _dashboardIconSize;
//         set { _dashboardIconSize = value; OnPropertyChanged(); }
//     }

//     /// <summary>
//     /// When true, the dashboard payload builder will substitute sample/example metrics from
//     /// the OverlayOptions.Metrics collection when no live metrics are available. Default: false.
//     /// </summary>
//     public bool DashboardUsePreviewMetrics
//     {
//         get => _dashboardUsePreviewMetrics;
//         set { _dashboardUsePreviewMetrics = value; OnPropertyChanged(); }
//     }
//     public double GatheringFontSize
//     {
//         get => _gatheringFontSize;
//         set { _gatheringFontSize = value; OnPropertyChanged(); }
//     }
//     public double GatheringIconSize
//     {
//         get => _gatheringIconSize;
//         set { _gatheringIconSize = value; OnPropertyChanged(); }
//     }
//     public double DamageFontSize
//     {
//         get => _damageFontSize;
//         set { _damageFontSize = value; OnPropertyChanged(); }
//     }
//     public double DamageIconSize
//     {
//         get => _damageIconSize;
//         set { _damageIconSize = value; OnPropertyChanged(); }
//     }
//     public double DamageTitleFontSize
//     {
//         get => _damageTitleFontSize;
//         set { _damageTitleFontSize = value; OnPropertyChanged(); }
//     }
//     public double DamageValueFontSize
//     {
//         get => _damageValueFontSize;
//         set { _damageValueFontSize = value; OnPropertyChanged(); }
//     }
//     public double DamageDpsFontSize
//     {
//         get => _damageDpsFontSize;
//         set { _damageDpsFontSize = value; OnPropertyChanged(); }
//     }
//     public double DamageHealFontSize
//     {
//         get => _damageHealFontSize;
//         set { _damageHealFontSize = value; OnPropertyChanged(); }
//     }
//     // Damage preview settings
//     public int DamagePreviewCount
//     {
//         get => _damagePreviewCount;
//         set { _damagePreviewCount = value; OnPropertyChanged(); }
//     }
//     public bool DamageShowDps
//     {
//         get => _damageShowDps;
//         set { _damageShowDps = value; OnPropertyChanged(); }
//     }
//     public bool DamageShowHeal
//     {
//         get => _damageShowHeal;
//         set { _damageShowHeal = value; OnPropertyChanged(); }
//     }
//     public bool DamageShowIcons
//     {
//         get => _damageShowIcons;
//         set { _damageShowIcons = value; OnPropertyChanged(); }
//     }
//     public bool DamageForceHealer
//     {
//         get => _damageForceHealer;
//         set { _damageForceHealer = value; OnPropertyChanged(); }
//     }
//     public bool DamageShowSelf
//     {
//         get => _damageShowSelf;
//         set { _damageShowSelf = value; OnPropertyChanged(); }
//     }

//     // New inverted helper property so the UI can present a "Hide yourself" checkbox while
//     // keeping existing logic that expects DamageShowSelf (true = include self in previews).
//     public bool DamageHideSelf
//     {
//         get => !_damageShowSelf;
//         set { _damageShowSelf = !value; OnPropertyChanged(); OnPropertyChanged(nameof(DamageShowSelf)); }
//     }
//     public void BroadcastSettingsIfEnabled()
//     {
//         if (_isOverlayEnabled && _overlayServer != null)
//         {
//             var json = StatisticsAnalysisTool.Network.Overlay.OverlayServer.OverlaySettingsToJson(this);
//             Serilog.Log.Debug($"[OverlayDebug] Staging settings to overlay server: {json}");
//             _overlayServer.StageOverlaySettings(this);
//         }
//     }
// }
