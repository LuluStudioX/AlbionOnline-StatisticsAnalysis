using StatisticsAnalysisTool.Models.BindingModel;
using StatisticsAnalysisTool.Common.UserSettings;
using System.Linq;
using System;
using System.Text.Json;

namespace StatisticsAnalysisTool.Overlay;

public static class OverlayDashboardPayloadBuilder
{
    public static string Build(DashboardBindings bindings)
    {
        // Use the overlay-specific model as an intermediate so overlays don't need
        // to rely on DashboardBindings implementation details.
        if (bindings == null) return Build((OverlayDashboardModel) null);
        var overlayModel = new OverlayDashboardModel();
        overlayModel.CopyFrom(bindings);
        var result = Build(overlayModel);

        // Avoid logging when the builder produced nothing or an empty array; compute normalized result first.
        var normalizedResult = string.IsNullOrWhiteSpace(result) ? result : result.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedResult) && normalizedResult != "[]")
        {
            try
            {
                // Serilog.Log.Debug($"[OverlayDashboardPayloadBuilder] Outgoing metrics: " +
                //     $"Fame={overlayModel.TotalGainedFameInSession}, Fame/h={overlayModel.FamePerHour}, " +
                //     $"Silver={overlayModel.TotalGainedSilverInSession}, Silver/h={overlayModel.SilverPerHour}, " +
                //     $"Respec={overlayModel.TotalGainedReSpecPointsInSession}, Respec/h={overlayModel.ReSpecPointsPerHour}, " +
                //     $"Might={overlayModel.TotalGainedMightInSession}, Might/h={overlayModel.MightPerHour}, " +
                //     $"Favor={overlayModel.TotalGainedFavorInSession}, Favor/h={overlayModel.FavorPerHour}");
                //
                // Serilog.Log.Debug($"[OverlayDashboardPayloadBuilder] Outgoing dashboard payload: {result}");
            }
            catch { }
        }

        return result;
    }

    public static string Build(OverlayDashboardModel model)
    {
        // Use the overlay-specific model
        // Faction points are retrieved from MainWindowViewModel below (preferred source)
        double factionPoints = 0;
        double factionPointsPerHour = 0;

        // Get current faction as string (e.g., "lymhurst")
        string faction = "unknown";
        var vm = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance;
        if (vm != null && vm.FactionPointStats != null && vm.FactionPointStats.Count > 0)
        {
            var cityFactionObj = vm.FactionPointStats[0].CityFaction;
            string cityFactionStr = cityFactionObj.ToString();
            // Only use if it's a known city name (not a number)
            if (!string.IsNullOrWhiteSpace(cityFactionStr) && !int.TryParse(cityFactionStr, out _))
                faction = cityFactionStr.ToLower();
            else
                faction = "unknown";
        }

        // Prefer MainWindowViewModel's FactionPointStats values when available (they reflect the UI/LiveStatsTracker)
        if (vm != null && vm.FactionPointStats != null && vm.FactionPointStats.Count > 0)
        {
            try
            {
                var f = vm.FactionPointStats[0];
                // Prefer UI values (override bindings) so overlay matches what the UI shows
                factionPoints = f.Value;
                factionPointsPerHour = f.ValuePerHour;
            }
            catch { }
        }

        // Helper to sanitize numbers
        static object SafeNum(object val)
        {
            if (val is double d)
                return double.IsNaN(d) || double.IsInfinity(d) ? 0 : d;
            if (val is float f)
                return float.IsNaN(f) || float.IsInfinity(f) ? 0 : f;
            return val ?? 0;
        }

        // Metrics first - prefer overlay model, but fallback to MainWindowViewModel values when model values are zero/uninitialized
        var mwvm = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance;

        double fameVal = model?.TotalGainedFameInSession ?? 0.0;
        double famePerHourVal = model?.FamePerHour ?? 0.0;
        double silverVal = model?.TotalGainedSilverInSession ?? 0.0;
        double silverPerHourVal = model?.SilverPerHour ?? 0.0;
        double respecVal = model?.TotalGainedReSpecPointsInSession ?? 0.0;
        double respecPerHourVal = model?.ReSpecPointsPerHour ?? 0.0;
        double mightVal = model?.TotalGainedMightInSession ?? 0.0;
        double mightPerHourVal = model?.MightPerHour ?? 0.0;
        double favorVal = model?.TotalGainedFavorInSession ?? 0.0;
        double favorPerHourVal = model?.FavorPerHour ?? 0.0;

        // Diagnostic logging: print all metric sources at build time
        if (mwvm != null)
        {
            try
            {
                var db = mwvm.DashboardBindings;
                var fstat = mwvm.FactionPointStats != null && mwvm.FactionPointStats.Count > 0 ? mwvm.FactionPointStats[0] : null;
                // Only log errors, not every value
                if (db != null)
                {
                    if (fameVal == 0 && db.TotalGainedFameInSession != 0) { fameVal = db.TotalGainedFameInSession; }
                    if (famePerHourVal == 0 && db.FamePerHour != 0) { famePerHourVal = db.FamePerHour; }
                    if (silverVal == 0 && db.TotalGainedSilverInSession != 0) { silverVal = db.TotalGainedSilverInSession; }
                    if (silverPerHourVal == 0 && db.SilverPerHour != 0) { silverPerHourVal = db.SilverPerHour; }
                    if (respecVal == 0 && db.TotalGainedReSpecPointsInSession != 0) { respecVal = db.TotalGainedReSpecPointsInSession; }
                    if (respecPerHourVal == 0 && db.ReSpecPointsPerHour != 0) { respecPerHourVal = db.ReSpecPointsPerHour; }
                    if (mightVal == 0 && db.TotalGainedMightInSession != 0) { mightVal = db.TotalGainedMightInSession; }
                    if (mightPerHourVal == 0 && db.MightPerHour != 0) { mightPerHourVal = db.MightPerHour; }
                    if (favorVal == 0 && db.TotalGainedFavorInSession != 0) { favorVal = db.TotalGainedFavorInSession; }
                    if (favorPerHourVal == 0 && db.FavorPerHour != 0) { favorPerHourVal = db.FavorPerHour; }
                }
                if (fstat != null)
                {
                    factionPoints = fstat.Value;
                    factionPointsPerHour = fstat.ValuePerHour;
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[OverlayDiagnostics] Error building dashboard payload");
            }
        }

        // Create metrics array in the same order as the original pipe-delimited format
        object[] metrics = new object[]
        {
            SafeNum(fameVal),
            SafeNum(famePerHourVal),
            SafeNum(silverVal),
            SafeNum(silverPerHourVal),
            SafeNum(respecVal),
            SafeNum(respecPerHourVal),
            SafeNum(mightVal),
            SafeNum(mightPerHourVal),
            SafeNum(favorVal),
            SafeNum(favorPerHourVal),
            SafeNum(factionPoints),
            SafeNum(factionPointsPerHour),
            faction
        };

        // If no live data present (all primary metrics are zero)
        var overlayOptions = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.Instance?.OverlayOptions;
        bool noLiveMetrics = (Convert.ToDouble(fameVal) == 0.0 && Convert.ToDouble(silverVal) == 0.0 && Convert.ToDouble(respecVal) == 0.0 && Convert.ToDouble(mightVal) == 0.0 && Convert.ToDouble(favorVal) == 0.0 && Convert.ToDouble(factionPoints) == 0.0);
        // If there are no live metrics AND overlay preview is not enabled, do not produce a payload
        if (noLiveMetrics && (overlayOptions == null || !overlayOptions.DashboardUsePreviewMetrics))
        {
            // Return empty array string to indicate no payload should be produced/sent
            // (matches damage meter behavior which returns "[]" when nothing to send)
            return "[]";
        }

        // Optionally substitute preview sample/example strings from the OverlayOptions when requested.
        if (noLiveMetrics && overlayOptions != null && overlayOptions.DashboardUsePreviewMetrics && overlayOptions.Metrics != null && overlayOptions.Metrics.Count >= 6)
        {
            try
            {
                var m = overlayOptions.Metrics;
                metrics = new object[]
                {
                    // Fame
                    m[0].TotalValue ?? SafeNum(fameVal),
                    m[0].PerHourValue ?? SafeNum(famePerHourVal),
                    // Silver
                    m[1].TotalValue ?? SafeNum(silverVal),
                    m[1].PerHourValue ?? SafeNum(silverPerHourVal),
                    // ReSpec
                    m[2].TotalValue ?? SafeNum(respecVal),
                    m[2].PerHourValue ?? SafeNum(respecPerHourVal),
                    // Might
                    m[3].TotalValue ?? SafeNum(mightVal),
                    m[3].PerHourValue ?? SafeNum(mightPerHourVal),
                    // Favor
                    m[4].TotalValue ?? SafeNum(favorVal),
                    m[4].PerHourValue ?? SafeNum(favorPerHourVal),
                    // Faction (use as total and perHour)
                    m[5].TotalValue ?? SafeNum(factionPoints),
                    m[5].PerHourValue ?? SafeNum(factionPointsPerHour),
                    faction
                };
            }
            catch { /* ignore and fall back to numeric metrics */ }
        }

        // [OverlayPayload] log removed; logging is now handled in OverlaySectionManager only when a real update occurs.
        // Serialize all dashboard settings as JSON for the overlay frontend
        // Removed unused MetricSettingsToJson function.
        // Overlay section settings are now handled by OverlayOptionsObject. Remove legacy settings serialization.
        // If needed, add serialization of OverlayOptionsObject here.
        // Serialize overlay settings as JSON and append after metrics
        var overlayOptionsForSettings = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.Instance?.OverlayOptions;
        string settingsJson = "{}";
        if (overlayOptionsForSettings != null)
        {
            settingsJson = StatisticsAnalysisTool.Network.Overlay.OverlayServer.OverlaySettingsToJson(overlayOptionsForSettings);
        }

        // Return JSON object with metrics array and settings
        var result = new
        {
            metrics = metrics,
            settings = JsonSerializer.Deserialize<object>(settingsJson)
        };

        return JsonSerializer.Serialize(result);
    }
}
