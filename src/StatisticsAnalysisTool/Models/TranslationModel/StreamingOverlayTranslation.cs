using StatisticsAnalysisTool.Localization;

namespace StatisticsAnalysisTool.Models.TranslationModel;

public class StreamingOverlayTranslation
{
<<<<<<< HEAD
    // General overlay labels
    public static string STREAMING_OVERLAY => LocalizationController.Translation("STREAMING_OVERLAY");
    public static string ENABLE_OVERLAY => LocalizationController.Translation("ENABLE_OVERLAY");
    public static string PORT_LABEL => LocalizationController.Translation("PORT_LABEL");
    // Apply / buttons (keep both names for backward compatibility)
    public static string APPLY => LocalizationController.Translation("APPLY");
    public static string APPLY_BUTTON => LocalizationController.Translation("APPLY_BUTTON");
    public static string RESET_BUTTON => LocalizationController.Translation("RESET_BUTTON");
    public static string CANCEL_BUTTON => LocalizationController.Translation("CANCEL_BUTTON");
    public static string CANCEL => LocalizationController.Translation("CANCEL");

    // URLs & theme
    public static string URL => LocalizationController.Translation("URL");
    public static string THEME => LocalizationController.Translation("THEME");

    // Metrics / display lists (both variants used in codebase)
    public static string OVERLAYS_TO_SHOW => LocalizationController.Translation("OVERLAYS_TO_SHOW");
    public static string METRICS_TO_SHOW => LocalizationController.Translation("METRICS_TO_SHOW");
    public static string METRICS_DISPLAY_SETTINGS => LocalizationController.Translation("METRICS_DISPLAY_SETTINGS");

    // Font / icon sizing and font-group labels
    public static string FONT_AND_SIZE_SETTINGS => LocalizationController.Translation("FONT_AND_SIZE_SETTINGS");
    public static string FONT_SIZE => LocalizationController.Translation("FONT_SIZE");
    public static string ICON_SIZE => LocalizationController.Translation("ICON_SIZE");

    // Metric labels
    public static string TITLE => LocalizationController.Translation("TITLE");
    public static string TOTAL => LocalizationController.Translation("TOTAL");
    public static string PER_HOUR => LocalizationController.Translation("PER_HOUR");
    public static string ICON => LocalizationController.Translation("ICON");

    // Domain-specific labels
    public static string FAME => LocalizationController.Translation("FAME");
    public static string SILVER => LocalizationController.Translation("SILVER");
    public static string RESPEC => LocalizationController.Translation("RESPEC");
    public static string MIGHT => LocalizationController.Translation("MIGHT");
    public static string FAVOR => LocalizationController.Translation("FAVOR");
    public static string FACTION => LocalizationController.Translation("FACTION");

    // Actions
    public static string RESET => LocalizationController.Translation("RESET");
    public static string RESET_DAMAGE_METER => LocalizationController.Translation("RESET_DAMAGE_METER");

    // Overlay preview and settings
    public static string OVERLAY_PREVIEW => LocalizationController.Translation("OVERLAY_PREVIEW");

    // User and copy (keep both possible keys)
    public static string USERNAME => LocalizationController.Translation("USERNAME");
    public static string NAME => LocalizationController.Translation("NAME");
    public static string COPY_BUTTON => LocalizationController.Translation("COPY_BUTTON");
    public static string COPY_TO_CLIPBOARD => LocalizationController.Translation("COPY_TO_CLIPBOARD");

    // Additional small labels
    public static string PREVIEW_COUNT => LocalizationController.Translation("PREVIEW_COUNT");

    // New small labels requested by UX
    public static string DPS => LocalizationController.Translation("DPS");
    public static string HEAL => LocalizationController.Translation("HEAL");
    public static string FORCE_HEALER => LocalizationController.Translation("FORCE_HEALER");
    public static string HIDE_YOURSELF => LocalizationController.Translation("HIDE_YOURSELF");

    // Auto-hide and metric toggles
    public static string AUTOHIDE_ZERO_VALUES => LocalizationController.Translation("AUTOHIDE_ZERO_VALUES");
    public static string HIDE_METRIC => LocalizationController.Translation("HIDE_METRIC");
    public static string PVP => LocalizationController.Translation("PVP");
    public static string ALL => LocalizationController.Translation("ALL");

    // Repair overlay labels
    public static string REPAIR_COSTS_TODAY => LocalizationController.Translation("REPAIR_COSTS_TODAY");
    public static string REPAIR_COSTS_LAST_7_DAYS => LocalizationController.Translation("REPAIR_COSTS_LAST_7_DAYS");
    public static string REPAIR_COSTS_LAST_30_DAYS => LocalizationController.Translation("REPAIR_COSTS_LAST_30_DAYS");
=======
    public static string STREAMING_OVERLAY => LocalizationController.Translation("STREAMING_OVERLAY");
    public static string ENABLE_OVERLAY => LocalizationController.Translation("ENABLE_OVERLAY");
    public static string PORT_LABEL => LocalizationController.Translation("PORT_LABEL");
    public static string APPLY_BUTTON => LocalizationController.Translation("APPLY_BUTTON");
    public static string URL => LocalizationController.Translation("URL");
    public static string COPY_BUTTON => LocalizationController.Translation("COPY_BUTTON");
    public static string THEME => LocalizationController.Translation("THEME");
    public static string METRICS_TO_SHOW => LocalizationController.Translation("METRICS_TO_SHOW");
    public static string FONT_SIZE => LocalizationController.Translation("FONT_SIZE");
    public static string ICON_SIZE => LocalizationController.Translation("ICON_SIZE");
    public static string RESET_BUTTON => LocalizationController.Translation("RESET_BUTTON");
    public static string CANCEL_BUTTON => LocalizationController.Translation("CANCEL_BUTTON");
    public static string OVERLAY_PREVIEW => LocalizationController.Translation("OVERLAY_PREVIEW");
>>>>>>> 08a701a6 (add localization support for streaming overlay controls and implement translation bindings)
}
