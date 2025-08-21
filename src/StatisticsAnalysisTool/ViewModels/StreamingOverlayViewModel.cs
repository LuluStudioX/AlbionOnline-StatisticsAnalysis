using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Models.TranslationModel;

namespace StatisticsAnalysisTool.ViewModels
{

    /// <summary>
    /// Holds font and icon size settings for each overlay section.
    /// </summary>
    [DataContract]
    public class OverlaySectionSettings
    {
        [DataMember] public double DashboardFontSize { get; set; } = 14;
        [DataMember] public double DashboardIconSize { get; set; } = 32;
        [DataMember] public double GatheringFontSize { get; set; } = 14;
        [DataMember] public double GatheringIconSize { get; set; } = 32;
        [DataMember] public double DamageFontSize { get; set; } = 14;
        [DataMember] public double DamageIconSize { get; set; } = 32;
    }


    /// <summary>
    /// ViewModel for the streaming overlay tab, manages overlay settings, theme, and section previews.
    /// </summary>
    public class StreamingOverlayViewModel : BaseViewModel
    {
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
        {
            get => _isOverlayEnabled;
            set { _isOverlayEnabled = value; OnPropertyChanged(); }
        }


        /// <summary>
        /// Port number for the overlay server.
        /// </summary>
        public int OverlayPort
        {
            get => _overlayPort;
            set {
                if (_overlayPort != value)
                {
                    _overlayPort = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(OverlayUrl));
                }
            }
        }


    /// <summary>
    /// The URL for the overlay server.
    /// </summary>
    public string OverlayUrl => $"http://localhost:{OverlayPort}";


        /// <summary>
        /// The currently selected overlay theme.
        /// </summary>
        public string SelectedTheme
        {
            get => _selectedTheme;
            set { _selectedTheme = value; OnPropertyChanged(); }
        }


        /// <summary>
        /// Whether the gathering section is visible in the overlay.
        /// </summary>
        public bool ShowGathering
        {
            get => _showGathering;
            set { _showGathering = value; OnPropertyChanged(); }
        }


        /// <summary>
        /// Whether the damage section is visible in the overlay.
        /// </summary>
        public bool ShowDamage
        {
            get => _showDamage;
            set { _showDamage = value; OnPropertyChanged(); }
        }


        /// <summary>
        /// Whether the dashboard section is visible in the overlay.
        /// </summary>
        public bool ShowDashboard
        {
            get => _showDashboard;
            set { _showDashboard = value; OnPropertyChanged(); }
        }


    /// <summary>
    /// List of available overlay themes.
    /// </summary>
    public List<string> ThemeOptions => _themeOptions;


    /// <summary>
    /// Command to apply overlay settings (start/stop server, update config, etc.).
    /// </summary>
    public CommandHandler ApplySettingsCommand { get; }
    /// <summary>
    /// Command to copy the overlay URL to clipboard.
    /// </summary>
    public CommandHandler CopyUrlCommand { get; }
    public CommandHandler CopySectionUrlCommand { get; }
    /// <summary>
    /// Command to apply dashboard section settings.
    /// </summary>
    public CommandHandler ApplyDashboardSettingsCommand { get; }
    /// <summary>
    /// Command to apply gathering section settings.
    /// </summary>
    public CommandHandler ApplyGatheringSettingsCommand { get; }
    /// <summary>
    /// Command to apply damage section settings.
    /// </summary>
    public CommandHandler ApplyDamageSettingsCommand { get; }
    /// <summary>
    /// Command to reset section settings to defaults.
    /// </summary>
    public CommandHandler ResetSectionSettingsCommand { get; }
    /// <summary>
    /// Command to cancel edits and revert to saved settings.
    /// </summary>
    public CommandHandler CancelSectionSettingsCommand { get; }


        /// <summary>
        /// Index of the currently selected overlay section for preview.
        /// </summary>
        public int SelectedSectionIndex
        {
            get => _selectedSectionIndex;
            set {
                if (_selectedSectionIndex != value)
                {
                    _selectedSectionIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(OverlayPreviewText));
                    OnPropertyChanged(nameof(SectionOverlayUrl));
                }
            }
        }
        
        /// <summary>
        /// The overlay URL for the currently selected section/tab.
        /// </summary>
        public string SectionOverlayUrl
        {
            get
            {
                string section = SelectedSectionIndex switch
                {
                    0 => "Dashboard",
                    1 => "Gathering",
                    2 => "Damage",
                    _ => "Dashboard"
                };
                return $"http://localhost:{OverlayPort}/{section}";
            }
    }


        // --- Edit buffer properties: changes are only applied on Apply ---
        /// <summary>
        /// Font size for the dashboard section (edit buffer).
        /// </summary>
        public double DashboardFontSize
        {
            get => _editSectionSettings.DashboardFontSize;
            set { _editSectionSettings.DashboardFontSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(OverlayPreviewText)); }
        }

        /// <summary>
        /// Icon size for the dashboard section (edit buffer).
        /// </summary>
        public double DashboardIconSize
        {
            get => _editSectionSettings.DashboardIconSize;
            set { _editSectionSettings.DashboardIconSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(OverlayPreviewText)); }
        }

        /// <summary>
        /// Font size for the gathering section (edit buffer).
        /// </summary>
        public double GatheringFontSize
        {
            get => _editSectionSettings.GatheringFontSize;
            set { _editSectionSettings.GatheringFontSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(OverlayPreviewText)); }
        }

        /// <summary>
        /// Icon size for the gathering section (edit buffer).
        /// </summary>
        public double GatheringIconSize
        {
            get => _editSectionSettings.GatheringIconSize;
            set { _editSectionSettings.GatheringIconSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(OverlayPreviewText)); }
        }

        /// <summary>
        /// Font size for the damage section (edit buffer).
        /// </summary>
        public double DamageFontSize
        {
            get => _editSectionSettings.DamageFontSize;
            set { _editSectionSettings.DamageFontSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(OverlayPreviewText)); }
        }

        /// <summary>
        /// Icon size for the damage section (edit buffer).
        /// </summary>
        public double DamageIconSize
        {
            get => _editSectionSettings.DamageIconSize;
            set { _editSectionSettings.DamageIconSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(OverlayPreviewText)); }
        }

        /// <summary>
        /// Preview text for the currently selected overlay section.
        /// </summary>
        public string OverlayPreviewText
        {
            get
            {
                switch (SelectedSectionIndex)
                {
                    case 0: return $"[Dashboard overlay preview: Font {DashboardFontSize}, Icon {DashboardIconSize}]";
                    case 1: return $"[Gathering overlay preview: Font {GatheringFontSize}, Icon {GatheringIconSize}]";
                    case 2: return $"[Damage overlay preview: Font {DamageFontSize}, Icon {DamageIconSize}]";
                    default: return "[Overlay preview will appear here]";
                }
            }
        }


        /// <summary>
        /// Initializes the StreamingOverlayViewModel and loads section settings.
        /// </summary>
        public StreamingOverlayViewModel()
        {
            ApplySettingsCommand = new CommandHandler(_ => ApplySettings(), true);
            CopyUrlCommand = new CommandHandler(_ => CopyUrl(), true);
            CopySectionUrlCommand = new CommandHandler(_ => CopySectionUrl(), true);
            ApplyDashboardSettingsCommand = new CommandHandler(_ => ApplyDashboardSettings(), true);
            ApplyGatheringSettingsCommand = new CommandHandler(_ => ApplyGatheringSettings(), true);
            ApplyDamageSettingsCommand = new CommandHandler(_ => ApplyDamageSettings(), true);
            ResetSectionSettingsCommand = new CommandHandler(_ => ResetSectionSettings(), true);
            CancelSectionSettingsCommand = new CommandHandler(_ => CancelSectionSettings(), true);
            CopySettingsToEditBuffer();
        }

        /// <summary>
        /// Copies the section-specific overlay URL to the clipboard.
        /// </summary>
        private void CopySectionUrl()
        {
            try
            {
                System.Windows.Clipboard.SetText(SectionOverlayUrl);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
        }
        /// <summary>
        /// Resets the edit buffer to default values.
        /// </summary>
        private void ResetSectionSettings()
        {
            _editSectionSettings = new OverlaySectionSettings();
            OnPropertyChanged(nameof(DashboardFontSize));
            OnPropertyChanged(nameof(DashboardIconSize));
            OnPropertyChanged(nameof(GatheringFontSize));
            OnPropertyChanged(nameof(GatheringIconSize));
            OnPropertyChanged(nameof(DamageFontSize));
            OnPropertyChanged(nameof(DamageIconSize));
            OnPropertyChanged(nameof(OverlayPreviewText));
        }

        /// <summary>
        /// Cancels edits and reverts the edit buffer to the last saved settings.
        /// </summary>
        private void CancelSectionSettings()
        {
            CopySettingsToEditBuffer();
        }

        /// <summary>
        /// Copies the persisted settings to the edit buffer for editing.
        /// </summary>
        private void CopySettingsToEditBuffer()
        {
            _editSectionSettings.DashboardFontSize = _sectionSettings.DashboardFontSize;
            _editSectionSettings.DashboardIconSize = _sectionSettings.DashboardIconSize;
            _editSectionSettings.GatheringFontSize = _sectionSettings.GatheringFontSize;
            _editSectionSettings.GatheringIconSize = _sectionSettings.GatheringIconSize;
            _editSectionSettings.DamageFontSize = _sectionSettings.DamageFontSize;
            _editSectionSettings.DamageIconSize = _sectionSettings.DamageIconSize;
            OnPropertyChanged(nameof(DashboardFontSize));
            OnPropertyChanged(nameof(DashboardIconSize));
            OnPropertyChanged(nameof(GatheringFontSize));
            OnPropertyChanged(nameof(GatheringIconSize));
            OnPropertyChanged(nameof(DamageFontSize));
            OnPropertyChanged(nameof(DamageIconSize));
            OnPropertyChanged(nameof(OverlayPreviewText));
        }



        /// <summary>
        /// Applies dashboard section settings and saves them.
        /// </summary>
        private void ApplyDashboardSettings()
        {
            CopyEditBufferToSettings();
            SaveSectionSettings();
            NotifyOverlayServerSettingsChanged();
        }
        private void ApplyGatheringSettings()
        {
            CopyEditBufferToSettings();
            SaveSectionSettings();
            NotifyOverlayServerSettingsChanged();
        }
        private void ApplyDamageSettings()
        {
            CopyEditBufferToSettings();
            SaveSectionSettings();
            NotifyOverlayServerSettingsChanged();
        }

        /// <summary>
        /// Copies the edit buffer to the persisted settings.
        /// </summary>
        private void CopyEditBufferToSettings()
        {
            _sectionSettings.DashboardFontSize = _editSectionSettings.DashboardFontSize;
            _sectionSettings.DashboardIconSize = _editSectionSettings.DashboardIconSize;
            _sectionSettings.GatheringFontSize = _editSectionSettings.GatheringFontSize;
            _sectionSettings.GatheringIconSize = _editSectionSettings.GatheringIconSize;
            _sectionSettings.DamageFontSize = _editSectionSettings.DamageFontSize;
            _sectionSettings.DamageIconSize = _editSectionSettings.DamageIconSize;
        }


        /// <summary>
        /// Saves the current section settings to disk.
        /// </summary>
        private void SaveSectionSettings()
        {
            try
            {
                StatisticsAnalysisTool.Common.UserSettings.SettingsController.SaveSettings();
            }
            catch (Exception ex)
            {
                // TODO: Localize error message and show to user (e.g., via error bar)
                // Example: ErrorBarText = Translation.ErrorSavingSettings + ": " + ex.Message;
                _ = ex; // Suppress unused variable warning for now
            }
        }


        /// <summary>
        /// Loads section settings from disk, if available.
        /// </summary>
    // No longer needed: settings are loaded with the main app settings
        /// <summary>
        /// Notifies the overlay server (if running) to reload/apply the latest settings.
        /// </summary>
        private void NotifyOverlayServerSettingsChanged()
        {
            // TODO: Implement overlay server update logic here (event, DI, or direct call)
        }


        /// <summary>
        /// Copies the overlay URL to the clipboard.
        /// </summary>
        private void CopyUrl()
        {
            try
            {
                System.Windows.Clipboard.SetText(OverlayUrl);
            }
            catch (Exception ex)
            {
                // TODO: Localize error message and show to user (e.g., via error bar)
                // Example: ErrorBarText = Translation.ErrorCopyingUrl + ": " + ex.Message;
                _ = ex; // Suppress unused variable warning for now
            }
        }

        /// <summary>
        /// Applies overlay settings (start/stop server, update config, etc.).
        /// </summary>
        private void ApplySettings()
        {
            // TODO: Apply overlay settings (start/stop server, update config, etc.)
        }
    }
}
