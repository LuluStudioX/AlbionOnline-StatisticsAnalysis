using System.Collections.Generic;
using System;
using System.Windows.Input;
using StatisticsAnalysisTool.Network.Overlay;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Models.TranslationModel;
<<<<<<< HEAD
using StatisticsAnalysisTool.ViewModels;
using System.Linq;
using System.Collections.Specialized;
using System.ComponentModel;
using StatisticsAnalysisTool.Models;
=======
>>>>>>> 08a701a6 (add localization support for streaming overlay controls and implement translation bindings)

namespace StatisticsAnalysisTool.ViewModels;

public class StreamingOverlayViewModel : BaseViewModel
{
    // Expose metrics collection for XAML binding
    public System.Collections.ObjectModel.ObservableCollection<StatisticsAnalysisTool.Models.MetricDisplayOption> Metrics => EditBuffer.Metrics;
    // Overlay settings edit buffer
    public static StreamingOverlayViewModel Instance { get; private set; }

    // AppInstance resolves the ServiceLocator-registered VM when available, otherwise falls back to the singleton Instance.
    public static StreamingOverlayViewModel AppInstance
    {
        get
        {
            try
            {
                if (StatisticsAnalysisTool.Common.ServiceLocator.IsServiceInDictionary<StreamingOverlayViewModel>())
                    return StatisticsAnalysisTool.Common.ServiceLocator.Resolve<StreamingOverlayViewModel>();
            }
            catch { }
            return Instance;
        }
    }

    // Centralized overlay options object
    private Overlay.OverlayOptionsObject _overlayOptions;
    // Cached picks for preview weapon icons to keep preview icons stable across refreshes
    private System.Collections.Generic.List<string> _cachedPreviewWeaponPicks;

    // Dashboard font/icon/auto-hide settings are now handled by OverlayOptionsObject only
    public StreamingOverlayTranslation Translation { get; set; } = new StreamingOverlayTranslation();

    public bool IsOverlayEnabled
    {
        get => _overlayOptions.IsOverlayEnabled;
        set
        {
            if (_overlayOptions.IsOverlayEnabled != value)
            {
                _overlayOptions.IsOverlayEnabled = value;
                OnPropertyChanged();
                Serilog.Log.Debug($"[Overlay] IsOverlayEnabled set to {value}");
                try
                {
                    if (value)
                    {
                        // NOTE: Overlay now uses the application's DamageMeter as authoritative.
                        // The internal OverlayEntityDeltaTracker and external aggregator are disabled
                        // to avoid duplicate/contradictory metrics when multiple apps run.
                        Serilog.Log.Information("[Overlay] Enabled - using DamageMeter as authoritative source (internal tracker disabled)");
                    }
                    else
                    {
                        // Nothing to stop; ensure any previously running tracker is not used.
                        // Overlay entity tracker disabled
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[Overlay] Failed to start/stop OverlayEntityDeltaTracker");
                }
            }
        }
    }
    // Removed stray brace

    public int OverlayPort
    {
        get => _overlayOptions.OverlayPort;
        set
        {
            if (_overlayOptions.OverlayPort != value)
            {
                _overlayOptions.OverlayPort = value;
                OnPropertyChanged();
            }
        }
    }

    // Expose overlay options for XAML binding
    public Overlay.OverlayOptionsObject OverlayOptions => _overlayOptions;

    // Overlay base URL and section URLs
    public string BaseUrl => $"http://127.0.0.1:{_overlayOptions.OverlayPort}/";
    public string DashboardUrl => $"http://127.0.0.1:{_overlayOptions.OverlayPort}/dashboard";
    public string GatheringUrl => $"http://127.0.0.1:{_overlayOptions.OverlayPort}/gathering";
    public string DamageUrl => $"http://127.0.0.1:{_overlayOptions.OverlayPort}/damage";
    public string RepairUrl => $"http://127.0.0.1:{_overlayOptions.OverlayPort}/repair";
    // Returns the URL for the currently selected section/tab
    public string SectionOverlayUrl
    {
        get
        {
            switch (_overlayOptions.SelectedSectionIndex)
            {
                case 0: return DashboardUrl;
                case 1: return GatheringUrl;
                case 2: return DamageUrl;
                case 3: return RepairUrl;
                default: return BaseUrl;
            }
        }
    }

    // Theme options
    public List<string> ThemeOptions => _overlayOptions.ThemeOptions;
    public string SelectedTheme
    {
        get => _overlayOptions.SelectedTheme;
        set { _overlayOptions.SelectedTheme = value; OnPropertyChanged(); }
    }

    // Section toggles
    public bool ShowGathering
    {
        get => _overlayOptions.ShowGathering;
        set { _overlayOptions.ShowGathering = value; OnPropertyChanged(); }
    }
    public bool ShowDamage
    {
        get => _overlayOptions.ShowDamage;
        set { _overlayOptions.ShowDamage = value; OnPropertyChanged(); }
    }
    public bool ShowDashboard
    {
        get => _overlayOptions.ShowDashboard;
        set { _overlayOptions.ShowDashboard = value; OnPropertyChanged(); }
    }
    public bool ShowRepair
    {
        get => _overlayOptions.ShowRepair;
        set { _overlayOptions.ShowRepair = value; OnPropertyChanged(); }
    }
    public int SelectedSectionIndex
    {
        get => _overlayOptions.SelectedSectionIndex;
        set
        {
            if (_overlayOptions.SelectedSectionIndex != value)
            {
                _overlayOptions.SelectedSectionIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SectionOverlayUrl));
                // When section changes, update preview binding and ensure we subscribe to damage updates
                EnsureDamageSubscription();
                EnsureDashboardSubscription();
                OnPropertyChanged(nameof(PreviewMetrics));
            }
        }
    }

    // Dashboard font/icon/auto-hide settings
    public double DashboardTitleFontSize
    {
        get => _overlayOptions.DashboardTitleFontSize;
        set { _overlayOptions.DashboardTitleFontSize = value; OnPropertyChanged(); }
    }
    public double DashboardTotalFontSize
    {
        get => _overlayOptions.DashboardTotalFontSize;
        set { _overlayOptions.DashboardTotalFontSize = value; OnPropertyChanged(); }
    }
    public double DashboardPerHourFontSize
    {
        get => _overlayOptions.DashboardPerHourFontSize;
        set { _overlayOptions.DashboardPerHourFontSize = value; OnPropertyChanged(); }
    }
    public bool DashboardAutoHideZeroValues
    {
        get => _overlayOptions.DashboardAutoHideZeroValues;
        set { _overlayOptions.DashboardAutoHideZeroValues = value; OnPropertyChanged(); }
    }
    public double DashboardFontSize
    {
        get => _overlayOptions.DashboardFontSize;
        set { _overlayOptions.DashboardFontSize = value; OnPropertyChanged(); }
    }
    public double DashboardIconSize
    {
        get => _overlayOptions.DashboardIconSize;
        set { _overlayOptions.DashboardIconSize = value; OnPropertyChanged(); }
    }
    public double GatheringFontSize
    {
        get => _overlayOptions.GatheringFontSize;
        set { _overlayOptions.GatheringFontSize = value; OnPropertyChanged(); }
    }
    public double GatheringIconSize
    {
        get => _overlayOptions.GatheringIconSize;
        set { _overlayOptions.GatheringIconSize = value; OnPropertyChanged(); }
    }
    public double DamageFontSize
    {
        get => _overlayOptions.DamageFontSize;
        set { _overlayOptions.DamageFontSize = value; OnPropertyChanged(); }
    }
    public double DamageIconSize
    {
        get => _overlayOptions.DamageIconSize;
        set { _overlayOptions.DamageIconSize = value; OnPropertyChanged(); }
    }
    public double DamageTitleFontSize
    {
        get => _overlayOptions.DamageTitleFontSize;
        set { _overlayOptions.DamageTitleFontSize = value; OnPropertyChanged(); }
    }
    public double DamageValueFontSize
    {
        get => _overlayOptions.DamageValueFontSize;
        set { _overlayOptions.DamageValueFontSize = value; OnPropertyChanged(); }
    }
    // Per-line font sizes exposed as DamageDpsFontSize and DamageHealFontSize
    public double DamageDpsFontSize
    {
        get => _overlayOptions.DamageDpsFontSize;
        set { _overlayOptions.DamageDpsFontSize = value; OnPropertyChanged(); }
    }
    public double DamageHealFontSize
    {
        get => _overlayOptions.DamageHealFontSize;
        set { _overlayOptions.DamageHealFontSize = value; OnPropertyChanged(); }
    }


    // Overlay preview text (stub)
    public string OverlayPreviewText => "Overlay Preview";

    // Preview items for the overlay panel. Returns dashboard metrics or a damage preview depending on selected section.
    public System.Collections.IEnumerable PreviewMetrics
    {
<<<<<<< HEAD
        get
=======
        public StreamingOverlayTranslation Translation { get; set; } = new StreamingOverlayTranslation();
    // Holds all section font/icon settings (persisted, from main settings)
    private OverlaySectionSettings _sectionSettings => StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings.OverlaySectionSettings;
    // Temporary buffer for editing (not yet applied)
    private OverlaySectionSettings _editSectionSettings = new OverlaySectionSettings();
        // Overlay enabled/disabled
        private bool _isOverlayEnabled = false;
        // Overlay server port
        private int _overlayPort = 8080;
        // Selected theme name
        private string _selectedTheme = "Dark";
        // Section visibility toggles
        private bool _showGathering = true;
        private bool _showDamage = true;
        private bool _showDashboard = true;
        // Theme options
        private List<string> _themeOptions = new List<string> { "Dark", "Light" };
        // Index of selected overlay section for preview
        private int _selectedSectionIndex;


        /// <summary>
        /// Enables or disables the overlay server.
        /// </summary>
        public bool IsOverlayEnabled
>>>>>>> 08a701a6 (add localization support for streaming overlay controls and implement translation bindings)
        {
            // Helper: compact number formatting (e.g., 1.2k)
            static string CompactNumber(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) return "0";
                var abs = Math.Abs(value);
                if (abs < 1000) return value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                if (abs < 1_000_000) return (value / 1000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k";
                if (abs < 1_000_000_000) return (value / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
                return (value / 1_000_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "B";
            }
            // Use the current viewmodel instance so the preview follows the UI-bound tab selection and local edit buffer
            var mwGlobal = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance;
            var globalDmg = mwGlobal?.DamageMeterBindings?.DamageMeter;
            bool hasGlobalDamage = globalDmg != null && globalDmg.Count > 0;

            var selectedIndex = this.SelectedSectionIndex;
            var useBuffer = this.EditBuffer ?? _overlayOptions;
            try
            {
                // Serilog.Log.Debug("[PreviewMetrics] selectedIndex={selectedIndex} bufferType={bufferType} hasEditBuffer={hasEdit}", selectedIndex, (useBuffer?.GetType().Name ?? "null"), this.EditBuffer != null);
            }
            catch { }
            // Only show damage preview when the Damage section is selected (index 2).
            // Previously this also showed damage whenever any global damage existed which caused the
            // dashboard preview to incorrectly display damage metrics. Remove that accidental fallback.
            if (selectedIndex == 2)
            {
                try
                {
                    var mwLocal = mwGlobal ?? StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance;
                    var dmg = mwLocal?.DamageMeterBindings?.DamageMeter;
                    if (dmg != null && dmg.Count > 0)
                    {
                        var buffer = useBuffer;
                        int count = buffer.DamagePreviewCount;
                        bool showDps = buffer.DamageShowDps;
                        bool showHeal = buffer.DamageShowHeal;
                        bool showIcons = buffer.DamageShowIcons;
                        bool forceHealer = buffer.DamageForceHealer;
                        bool showSelf = buffer.DamageShowSelf;

                        // Determine top DPS entries
                        var ordered = dmg.OrderByDescending(x => x.Damage).ToList();
                        try
                        {
                            var sampleIcons = ordered.Take(10).Select(x => x.CauserMainHand?.UniqueName ?? x.Name ?? "(no-unique)").ToArray();
                            // Serilog.Log.Debug("[PreviewMetrics] Damage preview entries uniqueNames sample={sample}", string.Join(',', sampleIcons));
                        }
                        catch { }

                        // Apply force healer logic: if requested, try to include one healer (highest heal) if present
                        List<StatisticsAnalysisTool.DamageMeter.DamageMeterFragment> result = new();
                        if (forceHealer)
                        {
                            var healer = dmg.OrderByDescending(x => x.Heal).FirstOrDefault(h => h.Heal > 0);
                            if (healer != null)
                            {
                                // include top (count-1) damage dealers excluding healer
                                var topDps = ordered.Where(x => x != healer).Take(Math.Max(0, count - 1)).ToList();
                                result.AddRange(topDps);
                                result.Add(healer);
                            }
                            else
                            {
                                result.AddRange(ordered.Take(count));
                            }
                        }
                        else
                        {
                            result.AddRange(ordered.Take(count));
                        }
                        // Ensure self is included if requested
                        if (showSelf)
                        {
                            // Track user's own character name via MainWindowViewModel.UserTrackingBindings.Username.
                            // Use a case-insensitive, trimmed comparison to be robust to casing/whitespace differences.
                            var selfName = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance?.UserTrackingBindings?.Username;
                            if (!string.IsNullOrWhiteSpace(selfName))
                            {
                                var selfNameTrim = selfName.Trim();
                                // skip if already present in the result (case-insensitive)
                                if (!result.Any(x => string.Equals(x.Name?.Trim(), selfNameTrim, System.StringComparison.OrdinalIgnoreCase)))
                                {
                                    var self = dmg.FirstOrDefault(x => string.Equals(x.Name?.Trim(), selfNameTrim, System.StringComparison.OrdinalIgnoreCase));
                                    if (self != null)
                                    {
                                        // replace last slot with self or add if there is room
                                        if (result.Count >= count)
                                        {
                                            result[result.Count - 1] = self;
                                        }
                                        else
                                        {
                                            result.Add(self);
                                        }
                                    }
                                }
                            }
                        }

                        return result.Select(f => new MetricDisplayOption
                        {
                            Name = f.Name ?? string.Empty,
                            Icon = f.CauserMainHand?.UniqueName,
                            TotalValue = CompactNumber(f.Damage),
                            // legacy single-line value
                            PerHourValue = showDps && f.Dps is double d ? CompactNumber(d) + "/dps" : (showHeal && f.Hps is double h ? CompactNumber(h) + "/hps" : ""),
                            // explicit values for DPS and HPS lines
                            DpsValue = f.Dps is double dd ? CompactNumber(dd) + "/dps" : string.Empty,
                            HpsValue = f.Hps is double hh ? CompactNumber(hh) + "/hps" : string.Empty,
                            // PvP values come from overlay aggregator totals (if available) — use empty otherwise
                            // PvP fields removed
                            IsDamagePreview = true,
                            ShowTitle = true,
                            ShowImage = showIcons,
                            ShowTotal = true,
                            ShowPerHour = (showDps || showHeal),
                            HideMetric = false,
                            TitleFontSize = useBuffer is Overlay.OverlayOptionsObject ob ? ob.DamageTitleFontSize : _overlayOptions.DamageTitleFontSize,
                            ValueFontSize = useBuffer is Overlay.OverlayOptionsObject ob2 ? ob2.DamageValueFontSize : _overlayOptions.DamageValueFontSize,
                            DpsFontSize = useBuffer is Overlay.OverlayOptionsObject ob3 ? ob3.DamageDpsFontSize : _overlayOptions.DamageDpsFontSize,
                            HealFontSize = useBuffer is Overlay.OverlayOptionsObject ob4 ? ob4.DamageHealFontSize : _overlayOptions.DamageHealFontSize
                        }).Cast<object>().ToList();
                    }
                    else
                    {
                        // No real damage data available — produce a fake/sample preview that imitates damage entries
                        var buffer = EditBuffer ?? _overlayOptions;
                        int sampleCount = Math.Max(1, buffer.DamagePreviewCount);
                        // Ensure cached preview picks exist and match the requested sample count
                        if (_cachedPreviewWeaponPicks == null || _cachedPreviewWeaponPicks.Count != sampleCount)
                        {
                            _cachedPreviewWeaponPicks = new System.Collections.Generic.List<string>(sampleCount);
                            for (int i = 0; i < sampleCount; i++)
                            {
                                var pick = StatisticsAnalysisTool.UserControls.MetricNameToIconPathConverter.PickRandomWeaponUniqueName();
                                _cachedPreviewWeaponPicks.Add(pick ?? string.Empty);
                            }
                        }

                        var samples = new List<object>();
                        for (int i = 1; i <= sampleCount; i++)
                        {
                            // Create believable decreasing DPS values for preview
                            long dmgVal = Math.Max(1, 200000 - i * 1234);
                            long dpsVal = Math.Max(1, 800 - i * 12);
                            long hpsVal = Math.Max(0, 400 - i * 8);
                            samples.Add(new MetricDisplayOption
                            {
                                Name = $"Player{i}",
                                TotalValue = CompactNumber(dmgVal),
                                PerHourValue = (buffer.DamageShowDps ? CompactNumber(dpsVal) + "/dps" : (buffer.DamageShowHeal ? CompactNumber(hpsVal) + "/hps" : "")),
                                // pack URI for a resource image shipped with the application
                                // Use a concrete random weapon unique name from ImageResources for preview entries
                                // Use the cached preview pick to ensure stable preview icons and avoid repeated lookups
                                Icon = (_cachedPreviewWeaponPicks != null && _cachedPreviewWeaponPicks.Count >= i)
                                    ? (_cachedPreviewWeaponPicks[i - 1] ?? "randomweapon")
                                    : (StatisticsAnalysisTool.UserControls.MetricNameToIconPathConverter.PickRandomWeaponUniqueName() ?? "randomweapon"),
                                DpsValue = CompactNumber(dpsVal) + "/dps",
                                HpsValue = CompactNumber(hpsVal) + "/hps",
                                // PvP preview removed
                                IsDamagePreview = true,
                                ShowTitle = true,
                                ShowImage = buffer.DamageShowIcons,
                                ShowTotal = true,
                                ShowPerHour = (buffer.DamageShowDps || buffer.DamageShowHeal),
                                HideMetric = false,
                                DpsFontSize = buffer.DamageDpsFontSize,
                                HealFontSize = buffer.DamageHealFontSize
                            });
                        }
                        try
                        {
                            // Serilog.Log.Debug("[PreviewMetrics] Sample preview created count={count} showIcons={show}", samples.Count, buffer.DamageShowIcons);
                            // Log the actual icon unique names used for the preview samples
                            var sampleIcons = samples.OfType<MetricDisplayOption>().Select(s => s.Icon ?? string.Empty).ToArray();
                            // Serilog.Log.Debug("[PreviewMetrics] Sample preview icons sample={sample}", string.Join(',', sampleIcons));
                        }
                        catch { }
                        return samples;
                    }
                }
                catch
                {
                    // swallow and fall back to metrics
                }
            }

            // Default: dashboard preview - prefer live DashboardBindings values if available
            var bufferMetrics = EditBuffer?.Metrics;
            var mw = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance;
            var db = mw?.DashboardBindings;

            // If Repair section is selected, prefer showing repair preview
            if (this.SelectedSectionIndex == 3)
            {
                try
                {
                    var list = new System.Collections.Generic.List<object>();
                    // Use live dashboard repair values if available
                    if (db != null && (db.RepairCostsToday != 0 || db.RepairCostsLast7Days != 0 || db.RepairCostsLast30Days != 0))
                    {
                        list.Add(new StatisticsAnalysisTool.Models.MetricDisplayOption
                        {
                            Name = StreamingOverlayTranslation.REPAIR_COSTS_TODAY,
                            Icon = "repair_cross.png",
                            TotalValue = CompactNumber((double) db.RepairCostsToday),
                            PerHourValue = "",
                            ShowTitle = true,
                            ShowImage = true,
                            ShowTotal = true,
                            ShowPerHour = false,
                            HideMetric = false,
                            TitleFontSize = _overlayOptions.RepairTitleFontSize,
                            ValueFontSize = _overlayOptions.RepairIconSize
                        });
                        list.Add(new StatisticsAnalysisTool.Models.MetricDisplayOption
                        {
                            Name = StreamingOverlayTranslation.REPAIR_COSTS_LAST_7_DAYS,
                            Icon = "repair_cross.png",
                            TotalValue = CompactNumber((double) db.RepairCostsLast7Days),
                            PerHourValue = "",
                            ShowTitle = true,
                            ShowImage = true,
                            ShowTotal = true,
                            ShowPerHour = false,
                            HideMetric = false,
                            TitleFontSize = _overlayOptions.RepairTitleFontSize,
                            ValueFontSize = _overlayOptions.RepairIconSize
                        });
                        list.Add(new StatisticsAnalysisTool.Models.MetricDisplayOption
                        {
                            Name = StreamingOverlayTranslation.REPAIR_COSTS_LAST_30_DAYS,
                            Icon = "repair_cross.png",
                            TotalValue = CompactNumber((double) db.RepairCostsLast30Days),
                            PerHourValue = "",
                            ShowTitle = true,
                            ShowImage = true,
                            ShowTotal = true,
                            ShowPerHour = false,
                            HideMetric = false,
                            TitleFontSize = _overlayOptions.RepairTitleFontSize,
                            ValueFontSize = _overlayOptions.RepairIconSize
                        });
                        return list;
                    }

                    // No live data -> sample using overlay buffer
                    var buffer = EditBuffer ?? _overlayOptions;
                    list.Add(new StatisticsAnalysisTool.Models.MetricDisplayOption
                    {
                        Name = StreamingOverlayTranslation.REPAIR_COSTS_TODAY,
                        Icon = "repair_cross.png",
                        TotalValue = "123",
                        PerHourValue = "",
                        ShowTitle = true,
                        ShowImage = true,
                        ShowTotal = true,
                        ShowPerHour = false,
                        HideMetric = false,
                        TitleFontSize = buffer.RepairTitleFontSize,
                        ValueFontSize = buffer.RepairIconSize
                    });
                    list.Add(new StatisticsAnalysisTool.Models.MetricDisplayOption
                    {
                        Name = StreamingOverlayTranslation.REPAIR_COSTS_LAST_7_DAYS,
                        Icon = "repair_cross.png",
                        TotalValue = "682.74K",
                        PerHourValue = "",
                        ShowTitle = true,
                        ShowImage = true,
                        ShowTotal = true,
                        ShowPerHour = false,
                        HideMetric = false,
                        TitleFontSize = buffer.RepairTitleFontSize,
                        ValueFontSize = buffer.RepairIconSize
                    });
                    list.Add(new StatisticsAnalysisTool.Models.MetricDisplayOption
                    {
                        Name = StreamingOverlayTranslation.REPAIR_COSTS_LAST_30_DAYS,
                        Icon = "repair_cross.png",
                        TotalValue = "9.11M",
                        PerHourValue = "",
                        ShowTitle = true,
                        ShowImage = true,
                        ShowTotal = true,
                        ShowPerHour = false,
                        HideMetric = false,
                        TitleFontSize = buffer.RepairTitleFontSize,
                        ValueFontSize = buffer.RepairIconSize
                    });
                    return list;
                }
                catch { }
            }

            bool IsFiniteNonZero(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v != 0.0;

            bool HasLiveDashboardData()
            {
                if (db == null) return false;
                if (IsFiniteNonZero(db.TotalGainedFameInSession)) return true;
                if (IsFiniteNonZero(db.FamePerHour)) return true;
                if (IsFiniteNonZero(db.TotalGainedSilverInSession)) return true;
                if (IsFiniteNonZero(db.SilverPerHour)) return true;
                if (IsFiniteNonZero(db.TotalGainedReSpecPointsInSession)) return true;
                if (IsFiniteNonZero(db.ReSpecPointsPerHour)) return true;
                if (IsFiniteNonZero(db.TotalGainedMightInSession)) return true;
                if (IsFiniteNonZero(db.MightPerHour)) return true;
                if (IsFiniteNonZero(db.TotalGainedFavorInSession)) return true;
                if (IsFiniteNonZero(db.FavorPerHour)) return true;
                // Also check for faction points via MainWindowViewModel
                if (mw?.FactionPointStats != null && mw.FactionPointStats.Count > 0 && IsFiniteNonZero(mw.FactionPointStats[0].Value)) return true;
                return false;
            }

            if (HasLiveDashboardData())
            {
                // Map live values into MetricDisplayOption preserving UI settings from buffer if possible
                var list = new System.Collections.Generic.List<object>();
                // We'll use names/icons from buffer if present, otherwise fallback to defaults
                string[] defaultNames = { "Fame", "Silver", "ReSpec", "Might", "Favor", "Faction" };
                for (int i = 0; i < 6; i++)
                {
                    var src = (bufferMetrics != null && i < bufferMetrics.Count) ? bufferMetrics[i] : null;
                    string name = src?.Name ?? defaultNames[i];
                    string icon = src?.Icon ?? string.Empty;
                    string color = src?.Color ?? "#FFFFFF";
                    bool showTitle = src?.ShowTitle ?? true;
                    bool showImage = src?.ShowImage ?? true;
                    bool showTotal = src?.ShowTotal ?? true;
                    bool showPerHour = src?.ShowPerHour ?? true;
                    bool hideMetric = src?.HideMetric ?? false;

                    string totalVal = "0";
                    string perHourVal = "0/h";
                    double formatTotal(double v) => (double.IsNaN(v) || double.IsInfinity(v)) ? 0.0 : v;

                    switch (i)
                    {
                        case 0:
                            totalVal = CompactNumber(formatTotal(db.TotalGainedFameInSession));
                            perHourVal = CompactNumber(formatTotal(db.FamePerHour)) + "/h";
                            break;
                        case 1:
                            totalVal = CompactNumber(formatTotal(db.TotalGainedSilverInSession));
                            perHourVal = CompactNumber(formatTotal(db.SilverPerHour)) + "/h";
                            break;
                        case 2:
                            totalVal = CompactNumber(formatTotal(db.TotalGainedReSpecPointsInSession));
                            perHourVal = CompactNumber(formatTotal(db.ReSpecPointsPerHour)) + "/h";
                            break;
                        case 3:
                            totalVal = CompactNumber(formatTotal(db.TotalGainedMightInSession));
                            perHourVal = CompactNumber(formatTotal(db.MightPerHour)) + "/h";
                            break;
                        case 4:
                            totalVal = CompactNumber(formatTotal(db.TotalGainedFavorInSession));
                            perHourVal = CompactNumber(formatTotal(db.FavorPerHour)) + "/h";
                            break;
                        case 5:
                            // faction points come from MainWindowViewModel if available
                            if (mw?.FactionPointStats != null && mw.FactionPointStats.Count > 0)
                            {
                                var f = mw.FactionPointStats[0];
                                totalVal = CompactNumber(formatTotal(f.Value));
                                perHourVal = CompactNumber(formatTotal(f.ValuePerHour)) + "/h";
                                // Use faction name as title when available
                                name = string.IsNullOrEmpty(src?.Name) ? f.CityFaction.ToString() : name;
                            }
                            break;
                    }

                    list.Add(new StatisticsAnalysisTool.Models.MetricDisplayOption
                    {
                        Name = name,
                        Icon = icon,
                        Color = color,
                        ShowTitle = showTitle,
                        ShowImage = showImage,
                        ShowTotal = showTotal,
                        ShowPerHour = showPerHour,
                        HideMetric = hideMetric,
                        TotalValue = totalVal,
                        PerHourValue = perHourVal,
                        TitleFontSize = src?.TitleFontSize ?? _overlayOptions.DashboardTitleFontSize,
                        ValueFontSize = src?.ValueFontSize ?? _overlayOptions.DashboardTotalFontSize
                    });
                }
                return list;
            }

            // No live dashboard data -> produce sample/example metrics using buffer names/icons if present
            if (bufferMetrics != null && bufferMetrics.Count > 0)
            {
                var samples = new System.Collections.Generic.List<object>();
                for (int i = 0; i < bufferMetrics.Count; i++)
                {
                    var src = bufferMetrics[i];
                    long sampleTotal = Math.Max(1, 123456 - i * 1111);
                    long samplePerHour = Math.Max(1, 1234 - i * 11);
                    samples.Add(new StatisticsAnalysisTool.Models.MetricDisplayOption
                    {
                        Name = string.IsNullOrEmpty(src.Name) ? $"Metric{i + 1}" : src.Name,
                        Icon = src.Icon,
                        Color = src.Color,
                        ShowTitle = src.ShowTitle,
                        ShowImage = src.ShowImage,
                        ShowTotal = src.ShowTotal,
                        ShowPerHour = src.ShowPerHour,
                        HideMetric = src.HideMetric,
                        TotalValue = sampleTotal.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
                        PerHourValue = samplePerHour.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "/h",
                        TitleFontSize = src.TitleFontSize,
                        ValueFontSize = src.ValueFontSize
                    });
                }
                return samples;
            }

            return new System.Collections.Generic.List<object>();
        }
    }

    // Keep a weak reference to the current collection and item handlers so we can detach when needed
    private System.Collections.ObjectModel.ObservableCollection<StatisticsAnalysisTool.DamageMeter.DamageMeterFragment> _subscribedCollection = null;
    // Dashboard bindings subscription
    private StatisticsAnalysisTool.Models.BindingModel.DashboardBindings _subscribedDashboardBindings = null;
    private INotifyCollectionChanged _subscribedFactionCollection = null;

    private void EnsureDamageSubscription()
    {
        try
        {
            var mw = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance;
            var coll = mw?.DamageMeterBindings?.DamageMeter;
            if (coll == null)
                return;

            if (_subscribedCollection == coll)
                return; // already subscribed to this collection

            // Unsubscribe previous
            if (_subscribedCollection != null)
            {
                _subscribedCollection.CollectionChanged -= OnDamageCollectionChanged;
                foreach (var item in _subscribedCollection)
                {
                    if (item is INotifyPropertyChanged npc)
                        npc.PropertyChanged -= OnDamageItemPropertyChanged;
                }
            }

            _subscribedCollection = coll;
            _subscribedCollection.CollectionChanged += OnDamageCollectionChanged;
            foreach (var item in _subscribedCollection)
            {
                if (item is INotifyPropertyChanged npc)
                    npc.PropertyChanged += OnDamageItemPropertyChanged;
            }
            // mark subscribed implicitly by storing _subscribedCollection
        }
        catch
        {
            // ignore subscription failures
        }
    }

    // Overlay-only polling tracker (for non-invasive PvP/total aggregation)
    // Overlay entity tracker removed

    private void EnsureDashboardSubscription()
    {
        try
        {
            var mw = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance;
            var db = mw?.DashboardBindings;
            if (db == null)
                return;

            if (_subscribedDashboardBindings == db && (_subscribedFactionCollection != null || mw.FactionPointStats == null))
                return; // already subscribed

            // Unsubscribe previous
            if (_subscribedDashboardBindings != null)
            {
                _subscribedDashboardBindings.PropertyChanged -= OnDashboardPropertyChanged;
            }
            if (_subscribedFactionCollection != null)
            {
                _subscribedFactionCollection.CollectionChanged -= OnFactionCollectionChanged;
                _subscribedFactionCollection = null;
            }

            _subscribedDashboardBindings = db;
            _subscribedDashboardBindings.PropertyChanged += OnDashboardPropertyChanged;

            // Subscribe to faction points collection if available
            var fps = mw.FactionPointStats as INotifyCollectionChanged;
            if (fps != null)
            {
                fps.CollectionChanged += OnFactionCollectionChanged;
                _subscribedFactionCollection = fps;
            }
        }
        catch
        {
            // ignore
        }
    }

    private void OnDamageCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        // Attach handlers for new items
        if (e.NewItems != null)
        {
            foreach (var obj in e.NewItems)
            {
                if (obj is INotifyPropertyChanged npc)
                    npc.PropertyChanged += OnDamageItemPropertyChanged;
            }
        }
        // Detach handlers for removed items
        if (e.OldItems != null)
        {
            foreach (var obj in e.OldItems)
            {
                if (obj is INotifyPropertyChanged npc)
                    npc.PropertyChanged -= OnDamageItemPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(PreviewMetrics));
    }

    private void OnDamageItemPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        // Any property change on a fragment should refresh the preview
        try
        {
            if (sender is StatisticsAnalysisTool.DamageMeter.DamageMeterFragment frag)
            {
                // Track delta for Damage property and feed it into overlay aggregator
                if (string.Equals(e.PropertyName, nameof(StatisticsAnalysisTool.DamageMeter.DamageMeterFragment.Damage), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Determine keys matching OverlayDamagePayloadBuilder selection: UniqueId (if present on fragment), CauserGuid, then name
                        string guidKey = frag.CauserGuid != Guid.Empty ? frag.CauserGuid.ToString() : null;
                        string uniqueIdKey = null;
                        try
                        {
                            var prop = frag.GetType().GetProperty("UniqueId");
                            if (prop != null)
                            {
                                var uv = prop.GetValue(frag);
                                if (uv != null) uniqueIdKey = uv.ToString();
                            }
                        }
                        catch { }

                        var nameKey = (frag.Name ?? string.Empty).Trim();

                        // Choose a stable track key to compute deltas: prefer UniqueId, then GUID, then name
                        var trackKey = uniqueIdKey ?? guidKey ?? nameKey;
                        long previous = 0;
                        if (!string.IsNullOrWhiteSpace(trackKey)) _lastDamageValues.TryGetValue(trackKey, out previous);
                        long now = frag.Damage;
                        long delta = now - previous;
                        if (delta > 0)
                        {
                            bool isPvp = false;
                            try
                            {
                                var trackingController = StatisticsAnalysisTool.Common.ServiceLocator.Resolve<StatisticsAnalysisTool.Network.Manager.TrackingController>();
                                if (trackingController?.EntityController != null)
                                {
                                    // Try GUID lookup first when available
                                    System.Collections.Generic.KeyValuePair<Guid, StatisticsAnalysisTool.Models.NetworkModel.PlayerGameObject> ent = default;
                                    bool found = false;
                                    if (!string.IsNullOrWhiteSpace(guidKey))
                                    {
                                        try
                                        {
                                            ent = trackingController.EntityController.GetEntity(new Guid(guidKey));
                                            found = ent.Value != null;
                                        }
                                        catch { found = false; }
                                    }

                                    // Fallback: try to locate an entity by display name (best-effort). Names are not unique; prefer Player subtypes.
                                    if (!found && !string.IsNullOrWhiteSpace(nameKey))
                                    {
                                        try
                                        {
                                            var all = trackingController.EntityController.GetAllEntities(false);
                                            if (all != null)
                                            {
                                                // Prefer exact name match with Player/PvpPlayer subtype
                                                var byName = all.FirstOrDefault(kv => string.Equals(kv.Value?.Name?.Trim(), nameKey, StringComparison.OrdinalIgnoreCase) && (kv.Value.ObjectSubType == StatisticsAnalysisTool.Enumerations.GameObjectSubType.Player || kv.Value.ObjectSubType == StatisticsAnalysisTool.Enumerations.GameObjectSubType.PvpPlayer));
                                                if (byName.Value != null)
                                                {
                                                    ent = byName;
                                                    found = true;
                                                }
                                                else
                                                {
                                                    // As a last resort, accept any entity with matching name
                                                    var anyByName = all.FirstOrDefault(kv => string.Equals(kv.Value?.Name?.Trim(), nameKey, StringComparison.OrdinalIgnoreCase));
                                                    if (anyByName.Value != null)
                                                    {
                                                        ent = anyByName;
                                                        found = true;
                                                    }
                                                }
                                            }
                                        }
                                        catch { found = false; }
                                    }

                                    if (found && ent.Value != null)
                                    {
                                        var subtype = ent.Value.ObjectSubType;
                                        var localGuid = trackingController.EntityController.GetLocalEntity()?.Key;
                                        var inParty = trackingController.EntityController.IsEntityInParty(ent.Key);
                                        isPvp = subtype == StatisticsAnalysisTool.Enumerations.GameObjectSubType.PvpPlayer
                                                || (subtype == StatisticsAnalysisTool.Enumerations.GameObjectSubType.Player && !inParty && (localGuid == null || ent.Key != localGuid));
                                        Serilog.Log.Debug("Overlay PvP detection: causerKey={key} name={name} foundEntity=true subtype={subtype} isPvp={isPvp} inParty={inParty} localGuid={local}", guidKey ?? "(none)", frag.Name, subtype, isPvp, inParty, localGuid);
                                    }
                                    else
                                    {
                                        Serilog.Log.Debug("Overlay PvP detection: no matching entity found for fragment name={name} guid={guid}", frag.Name, guidKey ?? "(none)");
                                    }
                                }
                                else
                                {
                                    Serilog.Log.Debug("Overlay PvP detection: trackingController or EntityController is null");
                                }
                            }
                            catch (Exception ex)
                            {
                                isPvp = false;
                                Serilog.Log.Warning(ex, "Overlay PvP detection: exception while looking up entity for causer {key}", guidKey);
                            }

                            try
                            {
                                Serilog.Log.Debug("[Overlay] Forwarding delta for fragment: trackKey={trackKey} uniqueId={uniqueId} guid={guid} name={name} delta={delta}", trackKey, uniqueIdKey, guidKey, nameKey, delta);
                                // Forward under UniqueId (if available), GUID, and display name to maximize matching probability in payload builder
                                // Aggregator removed: rely directly on the app's DamageMeter for authoritative totals
                                // Use a HashSet to avoid forwarding the same key twice
                                var forwarded = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                if (!string.IsNullOrWhiteSpace(uniqueIdKey) && forwarded.Add(uniqueIdKey))
                                {
                                    try
                                    {
                                        Serilog.Log.Information("[OverlayForward] Forwarding delta pre-check key=UniqueId keyVal={key} name={name} delta={delta} isPvp={isPvp}", uniqueIdKey, frag.Name, delta, isPvp);
                                        // also append to a simple diagnostic file to make cross-process comparison easier
                                        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "overlay-forward.log"), $"{DateTime.UtcNow:o}\tUNIQUEID\t{uniqueIdKey}\t{frag.Name}\t{delta}\t{isPvp}\r\n"); } catch { }
                                    }
                                    catch { }
                                    // Aggregator forwarding removed per request: rely on DamageMeter only
                                    // ag.ProcessDamageDelta(uniqueIdKey, frag.Name ?? string.Empty, delta, isPvp, DateTime.UtcNow);
                                }
                                if (!string.IsNullOrWhiteSpace(guidKey) && forwarded.Add(guidKey))
                                {
                                    try
                                    {
                                        Serilog.Log.Information("[OverlayForward] Forwarding delta pre-check key=Guid keyVal={key} name={name} delta={delta} isPvp={isPvp}", guidKey, frag.Name, delta, isPvp);
                                        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "overlay-forward.log"), $"{DateTime.UtcNow:o}\tGUID\t{guidKey}\t{frag.Name}\t{delta}\t{isPvp}\r\n"); } catch { }
                                    }
                                    catch { }
                                    // ag.ProcessDamageDelta(guidKey, frag.Name ?? string.Empty, delta, isPvp, DateTime.UtcNow);
                                }
                                if (!string.IsNullOrWhiteSpace(nameKey) && forwarded.Add(nameKey))
                                {
                                    try
                                    {
                                        Serilog.Log.Information("[OverlayForward] Forwarding delta pre-check key=Name keyVal={key} name={name} delta={delta} isPvp={isPvp}", nameKey, frag.Name, delta, isPvp);
                                        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "overlay-forward.log"), $"{DateTime.UtcNow:o}\tNAME\t{nameKey}\t{frag.Name}\t{delta}\t{isPvp}\r\n"); } catch { }
                                    }
                                    catch { }
                                    // ag.ProcessDamageDelta(nameKey, frag.Name ?? string.Empty, delta, isPvp, DateTime.UtcNow);
                                }
                            }
                            catch (Exception ex)
                            {
                                Serilog.Log.Warning(ex, "[Overlay] Failed to process damage delta for fragment");
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(trackKey)) _lastDamageValues[trackKey] = now;
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "[Overlay] Failed to process damage delta for fragment");
                    }
                }
            }
        }
        catch { }
        OnPropertyChanged(nameof(PreviewMetrics));
    }

    // Keep last-seen damage values per fragment to compute deltas for overlay aggregator
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastDamageValues = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

    private void OnDashboardPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        // Any dashboard binding change should refresh preview
        OnPropertyChanged(nameof(PreviewMetrics));
    }

    private void OnFactionCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(PreviewMetrics));
    }

    // Overlay settings edit buffer
    private Overlay.OverlayOptionsObject _editBuffer = null;
    // Expose edit buffer for live preview binding
    public Overlay.OverlayOptionsObject EditBuffer => _editBuffer;

    // Expose damage preview settings for binding convenience
    public int DamagePreviewCount
    {
        get => _overlayOptions.DamagePreviewCount;
        set { _overlayOptions.DamagePreviewCount = value; OnPropertyChanged(); }
    }
    public bool DamageShowDps
    {
        get => _overlayOptions.DamageShowDps;
        set { _overlayOptions.DamageShowDps = value; OnPropertyChanged(); }
    }
    // PvP display removed
    public bool DamageShowHeal
    {
        get => _overlayOptions.DamageShowHeal;
        set { _overlayOptions.DamageShowHeal = value; OnPropertyChanged(); }
    }
    public bool DamageShowIcons
    {
        get => _overlayOptions.DamageShowIcons;
        set { _overlayOptions.DamageShowIcons = value; OnPropertyChanged(); }
    }
    public bool DamageForceHealer
    {
        get => _overlayOptions.DamageForceHealer;
        set { _overlayOptions.DamageForceHealer = value; OnPropertyChanged(); }
    }
    public bool DamageShowSelf
    {
        get => _overlayOptions.DamageShowSelf;
        set { _overlayOptions.DamageShowSelf = value; OnPropertyChanged(); }
    }

    public System.Windows.Input.ICommand ApplySettingsCommand { get; }
    public System.Windows.Input.ICommand ResetAllCommand { get; }
    public System.Windows.Input.ICommand CopyUrlCommand { get; }
    public System.Windows.Input.ICommand CopySectionUrlCommand { get; }
    public System.Windows.Input.ICommand ApplyDashboardSettingsCommand { get; }
    public System.Windows.Input.ICommand ApplyGatheringSettingsCommand { get; }
    public System.Windows.Input.ICommand ApplyDamageSettingsCommand { get; }
    public System.Windows.Input.ICommand ApplySectionSettingsCommand { get; }
    public System.Windows.Input.ICommand ResetSectionSettingsCommand { get; }
    public System.Windows.Input.ICommand CancelSectionSettingsCommand { get; }

    private void CopyFromBufferToOptions()
    {
        if (_editBuffer == null) return;
        // Also copy the enabled flag from the edit buffer so Apply will honor the UI toggle
        _overlayOptions.IsOverlayEnabled = _editBuffer.IsOverlayEnabled;
        // Copy all relevant properties from buffer to live options
        // Dashboard
        _overlayOptions.DashboardTitleFontSize = _editBuffer.DashboardTitleFontSize;
        _overlayOptions.DashboardTotalFontSize = _editBuffer.DashboardTotalFontSize;
        _overlayOptions.DashboardPerHourFontSize = _editBuffer.DashboardPerHourFontSize;
        _overlayOptions.DashboardAutoHideZeroValues = _editBuffer.DashboardAutoHideZeroValues;
        _overlayOptions.DashboardFontSize = _editBuffer.DashboardFontSize;
        _overlayOptions.DashboardIconSize = _editBuffer.DashboardIconSize;
        // Gathering
        _overlayOptions.GatheringFontSize = _editBuffer.GatheringFontSize;
        _overlayOptions.GatheringIconSize = _editBuffer.GatheringIconSize;
        // Damage
        _overlayOptions.DamageFontSize = _editBuffer.DamageFontSize;
        _overlayOptions.DamageIconSize = _editBuffer.DamageIconSize;
        // Damage preview settings
        _overlayOptions.DamagePreviewCount = _editBuffer.DamagePreviewCount;
        _overlayOptions.DamageShowDps = _editBuffer.DamageShowDps;
        _overlayOptions.DamageShowHeal = _editBuffer.DamageShowHeal;
        _overlayOptions.DamageShowIcons = _editBuffer.DamageShowIcons;
        _overlayOptions.DamageForceHealer = _editBuffer.DamageForceHealer;
        _overlayOptions.DamageShowSelf = _editBuffer.DamageShowSelf;
        // Deep copy per-metric settings
        if (_editBuffer.Metrics.Count == _overlayOptions.Metrics.Count)
        {
            for (int i = 0; i < _editBuffer.Metrics.Count; i++)
            {
                var src = _editBuffer.Metrics[i];
                var dst = _overlayOptions.Metrics[i];
                dst.ShowTitle = src.ShowTitle;
                dst.ShowImage = src.ShowImage;
                dst.ShowTotal = src.ShowTotal;
                dst.ShowPerHour = src.ShowPerHour;
                dst.HideMetric = src.HideMetric;
            }
        }
        // Serilog.Log.Debug($"[OverlayDebug] Metrics copied to live options: {System.Text.Json.JsonSerializer.Serialize(_overlayOptions.Metrics)}");
    }

    // Copy only dashboard-related settings from edit buffer to live options
    private void CopyDashboardFromBufferToOptions()
    {
        if (_editBuffer == null) return;
        _overlayOptions.DashboardTitleFontSize = _editBuffer.DashboardTitleFontSize;
        _overlayOptions.DashboardTotalFontSize = _editBuffer.DashboardTotalFontSize;
        _overlayOptions.DashboardPerHourFontSize = _editBuffer.DashboardPerHourFontSize;
        _overlayOptions.DashboardAutoHideZeroValues = _editBuffer.DashboardAutoHideZeroValues;
        _overlayOptions.DashboardFontSize = _editBuffer.DashboardFontSize;
        _overlayOptions.DashboardIconSize = _editBuffer.DashboardIconSize;
        // Copy per-metric visibility settings
        if (_editBuffer.Metrics.Count == _overlayOptions.Metrics.Count)
        {
            for (int i = 0; i < _editBuffer.Metrics.Count; i++)
            {
                var src = _editBuffer.Metrics[i];
                var dst = _overlayOptions.Metrics[i];
                dst.ShowTitle = src.ShowTitle;
                dst.ShowImage = src.ShowImage;
                dst.ShowTotal = src.ShowTotal;
                dst.ShowPerHour = src.ShowPerHour;
                dst.HideMetric = src.HideMetric;
            }
        }
    }

    // Copy only gathering-related settings
    private void CopyGatheringFromBufferToOptions()
    {
        if (_editBuffer == null) return;
        _overlayOptions.GatheringFontSize = _editBuffer.GatheringFontSize;
        _overlayOptions.GatheringIconSize = _editBuffer.GatheringIconSize;
    }

    // Copy only damage-related settings
    private void CopyDamageFromBufferToOptions()
    {
        if (_editBuffer == null) return;
        _overlayOptions.DamageFontSize = _editBuffer.DamageFontSize;
        _overlayOptions.DamageIconSize = _editBuffer.DamageIconSize;
        _overlayOptions.DamageTitleFontSize = _editBuffer.DamageTitleFontSize;
        _overlayOptions.DamageValueFontSize = _editBuffer.DamageValueFontSize;
        _overlayOptions.DamageDpsFontSize = _editBuffer.DamageDpsFontSize;
        _overlayOptions.DamageHealFontSize = _editBuffer.DamageHealFontSize;
        // Damage preview settings
        _overlayOptions.DamagePreviewCount = _editBuffer.DamagePreviewCount;
        _overlayOptions.DamageShowDps = _editBuffer.DamageShowDps;
        _overlayOptions.DamageShowHeal = _editBuffer.DamageShowHeal;
        _overlayOptions.DamageShowIcons = _editBuffer.DamageShowIcons;
        _overlayOptions.DamageForceHealer = _editBuffer.DamageForceHealer;
        _overlayOptions.DamageShowSelf = _editBuffer.DamageShowSelf;
        // PVP display toggle removed
    }

    private void CopyFromOptionsToBuffer()
    {
        if (_editBuffer == null) _editBuffer = new Overlay.OverlayOptionsObject();
        _editBuffer.DashboardTitleFontSize = _overlayOptions.DashboardTitleFontSize;
        _editBuffer.DashboardTotalFontSize = _overlayOptions.DashboardTotalFontSize;
        _editBuffer.DashboardPerHourFontSize = _overlayOptions.DashboardPerHourFontSize;
        _editBuffer.DashboardAutoHideZeroValues = _overlayOptions.DashboardAutoHideZeroValues;
        _editBuffer.DashboardFontSize = _overlayOptions.DashboardFontSize;
        _editBuffer.DashboardIconSize = _overlayOptions.DashboardIconSize;
        _editBuffer.GatheringFontSize = _overlayOptions.GatheringFontSize;
        _editBuffer.GatheringIconSize = _overlayOptions.GatheringIconSize;
        _editBuffer.DamageFontSize = _overlayOptions.DamageFontSize;
        _editBuffer.DamageIconSize = _overlayOptions.DamageIconSize;
        // Damage preview settings
        _editBuffer.DamagePreviewCount = _overlayOptions.DamagePreviewCount;
        _editBuffer.DamageShowDps = _overlayOptions.DamageShowDps;
        _editBuffer.DamageShowHeal = _overlayOptions.DamageShowHeal;
        _editBuffer.DamageShowIcons = _overlayOptions.DamageShowIcons;
        _editBuffer.DamageForceHealer = _overlayOptions.DamageForceHealer;
        _editBuffer.DamageShowSelf = _overlayOptions.DamageShowSelf;
        // PVP display toggle removed
        // Add more as needed
    }

    // Copy only dashboard options from live options into the edit buffer
    private void CopyDashboardFromOptionsToBuffer()
    {
        if (_editBuffer == null) _editBuffer = new Overlay.OverlayOptionsObject();
        _editBuffer.DashboardTitleFontSize = _overlayOptions.DashboardTitleFontSize;
        _editBuffer.DashboardTotalFontSize = _overlayOptions.DashboardTotalFontSize;
        _editBuffer.DashboardPerHourFontSize = _overlayOptions.DashboardPerHourFontSize;
        _editBuffer.DashboardAutoHideZeroValues = _overlayOptions.DashboardAutoHideZeroValues;
        _editBuffer.DashboardFontSize = _overlayOptions.DashboardFontSize;
        _editBuffer.DashboardIconSize = _overlayOptions.DashboardIconSize;
        // Copy per-metric settings
        if (_editBuffer.Metrics.Count == _overlayOptions.Metrics.Count)
        {
            for (int i = 0; i < _overlayOptions.Metrics.Count; i++)
            {
                var src = _overlayOptions.Metrics[i];
                var dst = _editBuffer.Metrics[i];
                dst.ShowTitle = src.ShowTitle;
                dst.ShowImage = src.ShowImage;
                dst.ShowTotal = src.ShowTotal;
                dst.ShowPerHour = src.ShowPerHour;
                dst.HideMetric = src.HideMetric;
            }
        }
    }

    // Copy only gathering options from live options into the edit buffer
    private void CopyGatheringFromOptionsToBuffer()
    {
        if (_editBuffer == null) _editBuffer = new Overlay.OverlayOptionsObject();
        _editBuffer.GatheringFontSize = _overlayOptions.GatheringFontSize;
        _editBuffer.GatheringIconSize = _overlayOptions.GatheringIconSize;
    }

    // Copy only damage options from live options into the edit buffer
    private void CopyDamageFromOptionsToBuffer()
    {
        if (_editBuffer == null) _editBuffer = new Overlay.OverlayOptionsObject();
        _editBuffer.DamageFontSize = _overlayOptions.DamageFontSize;
        _editBuffer.DamageIconSize = _overlayOptions.DamageIconSize;
        _editBuffer.DamageTitleFontSize = _overlayOptions.DamageTitleFontSize;
        _editBuffer.DamageValueFontSize = _overlayOptions.DamageValueFontSize;
        _editBuffer.DamageDpsFontSize = _overlayOptions.DamageDpsFontSize;
        _editBuffer.DamageHealFontSize = _overlayOptions.DamageHealFontSize;
        // Damage preview settings
        _editBuffer.DamagePreviewCount = _overlayOptions.DamagePreviewCount;
        _editBuffer.DamageShowDps = _overlayOptions.DamageShowDps;
        _editBuffer.DamageShowHeal = _overlayOptions.DamageShowHeal;
        _editBuffer.DamageShowIcons = _overlayOptions.DamageShowIcons;
        _editBuffer.DamageForceHealer = _overlayOptions.DamageForceHealer;
        _editBuffer.DamageShowSelf = _overlayOptions.DamageShowSelf;
    }

    private void ApplySettings(object obj)
    {
        CopyFromBufferToOptions();
        // Persist overlay settings to SettingsObject
        var s = StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings;
        s.OverlayDashboardTitleFontSize = _overlayOptions.DashboardTitleFontSize;
        s.OverlayDashboardTotalFontSize = _overlayOptions.DashboardTotalFontSize;
        s.OverlayDashboardPerHourFontSize = _overlayOptions.DashboardPerHourFontSize;
        s.OverlayDashboardAutoHideZeroValues = _overlayOptions.DashboardAutoHideZeroValues;
        s.OverlayDashboardFontSize = _overlayOptions.DashboardFontSize;
        s.OverlayDashboardIconSize = _overlayOptions.DashboardIconSize;
        s.OverlayGatheringFontSize = _overlayOptions.GatheringFontSize;
        s.OverlayGatheringIconSize = _overlayOptions.GatheringIconSize;
        s.OverlayDamageFontSize = _overlayOptions.DamageFontSize;
        s.OverlayDamageIconSize = _overlayOptions.DamageIconSize;
        s.OverlayIsEnabled = _overlayOptions.IsOverlayEnabled;
        // Serialize per-metric settings as JSON
        var metricSettingsList = _overlayOptions.Metrics.Select(m => new
        {
            m.Name,
            m.ShowTitle,
            m.ShowImage,
            m.ShowTotal,
            m.ShowPerHour,
            m.HideMetric,
            Order = _overlayOptions.Metrics.IndexOf(m),
            m.Icon,
            m.Color
        }).ToList();
        s.OverlayMetricSettingsJson = System.Text.Json.JsonSerializer.Serialize(metricSettingsList);
        StatisticsAnalysisTool.Common.UserSettings.SettingsController.SaveSettings();
        Serilog.Log.Information("[Overlay] Settings applied: {Settings}", System.Text.Json.JsonSerializer.Serialize(_overlayOptions));
        // Only broadcast settings to overlay server when Apply is pressed
        _overlayOptions.BroadcastSettingsIfEnabled();
        // If the user enabled overlay via Apply, ensure the overlay server starts
        if (_overlayOptions.IsOverlayEnabled)
        {
            try { _overlayOptions.StartOverlayIfEnabled(); } catch { }
        }
    }

    private void ApplyDashboardSettings(object obj)
    {
        CopyDashboardFromBufferToOptions();
        // Persist only dashboard settings
        var s = StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings;
        s.OverlayDashboardTitleFontSize = _overlayOptions.DashboardTitleFontSize;
        s.OverlayDashboardTotalFontSize = _overlayOptions.DashboardTotalFontSize;
        s.OverlayDashboardPerHourFontSize = _overlayOptions.DashboardPerHourFontSize;
        s.OverlayDashboardAutoHideZeroValues = _overlayOptions.DashboardAutoHideZeroValues;
        s.OverlayDashboardFontSize = _overlayOptions.DashboardFontSize;
        s.OverlayDashboardIconSize = _overlayOptions.DashboardIconSize;
        // Persist metric settings
        var metricSettingsList = _overlayOptions.Metrics.Select(m => new
        {
            m.Name,
            m.ShowTitle,
            m.ShowImage,
            m.ShowTotal,
            m.ShowPerHour,
            m.HideMetric,
            Order = _overlayOptions.Metrics.IndexOf(m),
            m.Icon,
            m.Color
        }).ToList();
        s.OverlayMetricSettingsJson = System.Text.Json.JsonSerializer.Serialize(metricSettingsList);
        StatisticsAnalysisTool.Common.UserSettings.SettingsController.SaveSettings();
        _overlayOptions.BroadcastSettingsIfEnabled();
    }

    private void ApplyGatheringSettings(object obj)
    {
        CopyGatheringFromBufferToOptions();
        var s = StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings;
        s.OverlayGatheringFontSize = _overlayOptions.GatheringFontSize;
        s.OverlayGatheringIconSize = _overlayOptions.GatheringIconSize;
        StatisticsAnalysisTool.Common.UserSettings.SettingsController.SaveSettings();
        _overlayOptions.BroadcastSettingsIfEnabled();
    }

    private void ApplyDamageSettings(object obj)
    {
        CopyDamageFromBufferToOptions();
        // Ensure the HideSelf flag from the edit buffer is applied to the live options
        _overlayOptions.DamageHideSelf = _editBuffer.DamageHideSelf;
        var s = StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings;
        s.OverlayDamageFontSize = _overlayOptions.DamageFontSize;
        s.OverlayDamageIconSize = _overlayOptions.DamageIconSize;
        StatisticsAnalysisTool.Common.UserSettings.SettingsController.SaveSettings();
        _overlayOptions.BroadcastSettingsIfEnabled();
    }
    private void ResetSettings(object obj)
    {
        _overlayOptions.RestoreDefaults();
        CopyFromOptionsToBuffer();
        // Optionally notify overlay server/UI here
    }
    // Reset only the currently selected section's settings back to defaults
    private void ResetSectionSettings(object obj)
    {
        // Preview-only reset: copy defaults into the edit buffer for the selected section
        // so the user can Inspect and then Apply to commit. Do NOT persist or broadcast here.
        var defaults = new Overlay.OverlayOptionsObject();
        if (_editBuffer == null) _editBuffer = new Overlay.OverlayOptionsObject();
        switch (SelectedSectionIndex)
        {
            case 0: // Dashboard: copy dashboard defaults into buffer
                _editBuffer.DashboardTitleFontSize = defaults.DashboardTitleFontSize;
                _editBuffer.DashboardTotalFontSize = defaults.DashboardTotalFontSize;
                _editBuffer.DashboardPerHourFontSize = defaults.DashboardPerHourFontSize;
                _editBuffer.DashboardAutoHideZeroValues = defaults.DashboardAutoHideZeroValues;
                _editBuffer.DashboardFontSize = defaults.DashboardFontSize;
                _editBuffer.DashboardIconSize = defaults.DashboardIconSize;
                // per-metric visibility defaults
                if (defaults.Metrics.Count == _editBuffer.Metrics.Count)
                {
                    for (int i = 0; i < defaults.Metrics.Count; i++)
                    {
                        var src = defaults.Metrics[i];
                        var dst = _editBuffer.Metrics[i];
                        dst.ShowTitle = src.ShowTitle;
                        dst.ShowImage = src.ShowImage;
                        dst.ShowTotal = src.ShowTotal;
                        dst.ShowPerHour = src.ShowPerHour;
                        dst.HideMetric = src.HideMetric;
                    }
                }
                break;
            case 1: // Gathering defaults to buffer
                _editBuffer.GatheringFontSize = defaults.GatheringFontSize;
                _editBuffer.GatheringIconSize = defaults.GatheringIconSize;
                break;
            case 2: // Damage defaults to buffer
                _editBuffer.DamageFontSize = defaults.DamageFontSize;
                _editBuffer.DamageIconSize = defaults.DamageIconSize;
                _editBuffer.DamageTitleFontSize = defaults.DamageTitleFontSize;
                _editBuffer.DamageValueFontSize = defaults.DamageValueFontSize;
                _editBuffer.DamageDpsFontSize = defaults.DamageDpsFontSize;
                _editBuffer.DamageHealFontSize = defaults.DamageHealFontSize;
                _editBuffer.DamagePreviewCount = defaults.DamagePreviewCount;
                _editBuffer.DamageShowDps = defaults.DamageShowDps;
                _editBuffer.DamageShowHeal = defaults.DamageShowHeal;
                _editBuffer.DamageShowIcons = defaults.DamageShowIcons;
                _editBuffer.DamageForceHealer = defaults.DamageForceHealer;
                _editBuffer.DamageShowSelf = defaults.DamageShowSelf;
                break;
            default:
                // For any unknown tab, just copy full defaults into the edit buffer
                CopyFromOptionsToBuffer();
                break;
        }
        // Notify UI to refresh preview bindings
        OnPropertyChanged(nameof(EditBuffer));
        OnPropertyChanged(nameof(PreviewMetrics));
    }
    private void CancelSettings(object obj)
    {
        CopyFromOptionsToBuffer();
        // Optionally notify overlay server/UI here
    }

    // Cancel only the currently selected section changes (revert buffer to live options for that section)
    private void CancelSectionSettings(object obj)
    {
        // Re-copy current live options into buffer so UI shows current live state for the selected section
        switch (SelectedSectionIndex)
        {
            case 0: CopyDashboardFromOptionsToBuffer(); break;
            case 1: CopyGatheringFromOptionsToBuffer(); break;
            case 2: CopyDamageFromOptionsToBuffer(); break;
            default: CopyFromOptionsToBuffer(); break;
        }
        // No broadcast or persistence required for cancel
    }

    private void CopyUrl(object obj)
    {
        // Copy the base URL (http://127.0.0.1:PORT/) by default
        System.Windows.Clipboard.SetText(BaseUrl);
    }

    private void CopySectionUrl(object obj)
    {
        // Copy the correct overlay URL for the selected tab
        System.Windows.Clipboard.SetText(SectionOverlayUrl);
    }

    public StreamingOverlayViewModel()
    {
        // Resolve the shared OverlayOptionsObject if registered, otherwise create one and register it
        bool resolvedFromServiceLocator = false;
        try
        {
            if (StatisticsAnalysisTool.Common.ServiceLocator.IsServiceInDictionary<Overlay.OverlayOptionsObject>())
            {
                _overlayOptions = StatisticsAnalysisTool.Common.ServiceLocator.Resolve<Overlay.OverlayOptionsObject>();
                resolvedFromServiceLocator = true;
            }
            else
            {
                // Do NOT create-and-register a fallback instance here to avoid duplicate global instances.
                // Create a local non-shared instance only for UI preview purposes; do not register it.
                _overlayOptions = new Overlay.OverlayOptionsObject();
            }
        }
        catch
        {
            // If ServiceLocator throws, fall back to a local instance but keep it non-shared.
            _overlayOptions = new Overlay.OverlayOptionsObject();
        }
        Instance = this;
        // Load overlay settings from SettingsObject
        var s = StatisticsAnalysisTool.Common.UserSettings.SettingsController.CurrentSettings;
        _overlayOptions.DashboardTitleFontSize = s.OverlayDashboardTitleFontSize;
        _overlayOptions.DashboardTotalFontSize = s.OverlayDashboardTotalFontSize;
        _overlayOptions.DashboardPerHourFontSize = s.OverlayDashboardPerHourFontSize;
        _overlayOptions.DashboardAutoHideZeroValues = s.OverlayDashboardAutoHideZeroValues;
        _overlayOptions.DashboardFontSize = s.OverlayDashboardFontSize;
        _overlayOptions.DashboardIconSize = s.OverlayDashboardIconSize;
        _overlayOptions.GatheringFontSize = s.OverlayGatheringFontSize;
        _overlayOptions.GatheringIconSize = s.OverlayGatheringIconSize;
        _overlayOptions.DamageFontSize = s.OverlayDamageFontSize;
        _overlayOptions.DamageIconSize = s.OverlayDamageIconSize;
        // Only activate overlays if enabled in settings
        _overlayOptions.IsOverlayEnabled = s.OverlayIsEnabled;
        // --- Ensure overlay server/integration is started if enabled at startup ---
        // Only start overlay from the shared service-locator instance to avoid multiple servers.
        if (s.OverlayIsEnabled && resolvedFromServiceLocator)
        {
            try { _overlayOptions.StartOverlayIfEnabled(); } catch { }
            // Overlay entity tracker disabled - relying on app DamageMeter for authoritative totals
        }
        // Restore per-metric settings from JSON if available
        if (!string.IsNullOrWhiteSpace(s.OverlayMetricSettingsJson))
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(s.OverlayMetricSettingsJson))
                {
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            try
                            {
                                string name = null;
                                if (el.TryGetProperty("Name", out var nameEl) && nameEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                    name = nameEl.GetString();

                                if (string.IsNullOrEmpty(name))
                                    continue;

                                var metric = _overlayOptions.Metrics.FirstOrDefault(m => m.Name == name);
                                if (metric != null)
                                {
                                    if (el.TryGetProperty("ShowTitle", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True)
                                        metric.ShowTitle = true;
                                    else if (p.ValueKind == System.Text.Json.JsonValueKind.False)
                                        metric.ShowTitle = false;

                                    if (el.TryGetProperty("ShowImage", out var p2) && p2.ValueKind == System.Text.Json.JsonValueKind.True)
                                        metric.ShowImage = true;
                                    else if (p2.ValueKind == System.Text.Json.JsonValueKind.False)
                                        metric.ShowImage = false;

                                    if (el.TryGetProperty("ShowTotal", out var p3) && p3.ValueKind == System.Text.Json.JsonValueKind.True)
                                        metric.ShowTotal = true;
                                    else if (p3.ValueKind == System.Text.Json.JsonValueKind.False)
                                        metric.ShowTotal = false;

                                    if (el.TryGetProperty("ShowPerHour", out var p4) && p4.ValueKind == System.Text.Json.JsonValueKind.True)
                                        metric.ShowPerHour = true;
                                    else if (p4.ValueKind == System.Text.Json.JsonValueKind.False)
                                        metric.ShowPerHour = false;

                                    if (el.TryGetProperty("HideMetric", out var p5) && p5.ValueKind == System.Text.Json.JsonValueKind.True)
                                        metric.HideMetric = true;
                                    else if (p5.ValueKind == System.Text.Json.JsonValueKind.False)
                                        metric.HideMetric = false;
                                }
                            }
                            catch (Exception inner)
                            {
                                Serilog.Log.Warning($"[Overlay] Skipping invalid metric setting entry: {inner.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"[Overlay] Failed to restore per-metric settings: {ex.Message}");
            }
        }
        CopyFromOptionsToBuffer();
        ApplySettingsCommand = new RelayCommand(ApplySettings);
        // Use section-scoped reset/cancel so bottom buttons only affect active tab
        ResetSectionSettingsCommand = new RelayCommand(ResetSectionSettings);
        CancelSectionSettingsCommand = new RelayCommand(CancelSectionSettings);
        CopyUrlCommand = new RelayCommand(CopyUrl);
        // For now, wire ApplyDashboardSettingsCommand etc. to ApplySettings
        ApplyDashboardSettingsCommand = new RelayCommand(ApplyDashboardSettings);
        ApplyGatheringSettingsCommand = new RelayCommand(ApplyGatheringSettings);
        ApplyDamageSettingsCommand = new RelayCommand(ApplyDamageSettings);
        ApplySectionSettingsCommand = new RelayCommand(obj =>
        {
            // Debug: log which section is active when Apply is invoked
            int requestedIndex = SelectedSectionIndex;
            if (obj is int p) requestedIndex = p;
            else if (obj is string s && int.TryParse(s, out var pi)) requestedIndex = pi;
            // Serilog.Log.Debug("[Overlay] ApplySectionSettingsCommand invoked. SelectedSectionIndex(current)={current} requested={requested}", SelectedSectionIndex, requestedIndex);
            // Apply only for the requested/selected section
            switch (requestedIndex)
            {
                case 0:
                    Serilog.Log.Debug("[Overlay] Routing Apply to Dashboard");
                    ApplyDashboardSettings(null);
                    break;
                case 1:
                    Serilog.Log.Debug("[Overlay] Routing Apply to Gathering");
                    ApplyGatheringSettings(null);
                    break;
                case 2:
                    Serilog.Log.Debug("[Overlay] Routing Apply to Damage");
                    ApplyDamageSettings(null);
                    break;
                default:
                    Serilog.Log.Debug("[Overlay] Routing Apply to Full Overlay");
                    ApplySettings(null);
                    break;
            }
        });
        CopySectionUrlCommand = new RelayCommand(CopySectionUrl);
        ResetAllCommand = new RelayCommand(obj => { PerformResetAll(); });
        // Ensure we have subscriptions active for preview updates
        EnsureDamageSubscription();
        EnsureDashboardSubscription();
    }

    // PvP reset removed

    private void PerformResetAll()
    {
        try
        {
            // Aggregator was removed; reset the app damage meter if possible
            try
            {
                var vm = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance;
                if (vm != null)
                {
                    var mi = vm.GetType().GetMethod("ResetDamageMeter");
                    if (mi != null) mi.Invoke(vm, null);
                }
            }
            catch (Exception ex2)
            {
                Serilog.Log.Warning(ex2, "[Overlay] Reset all: app reset invocation failed");
            }
            Serilog.Log.Information("[Overlay] Reset all invoked (aggregator removed)");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Overlay] Reset all failed from UI");
        }
    }
}
