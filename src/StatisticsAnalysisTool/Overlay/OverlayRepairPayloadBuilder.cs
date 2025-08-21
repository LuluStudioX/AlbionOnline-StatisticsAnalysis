using StatisticsAnalysisTool.Models.BindingModel;
using System.Text.Json;
using System;

namespace StatisticsAnalysisTool.Overlay;

public static class OverlayRepairPayloadBuilder
{
    public static string Build(DashboardBindings bindings)
    {
        if (bindings == null) return "[]";
        // Build minimal payload: metrics array and settings
        object[] metrics = new object[]
        {
            bindings.RepairCostsToday,
            bindings.RepairCostsLast7Days,
            bindings.RepairCostsLast30Days
        };

        var overlayOptionsForSettings = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.Instance?.OverlayOptions;
        string settingsJson = "{}";
        if (overlayOptionsForSettings != null)
        {
            settingsJson = StatisticsAnalysisTool.Network.Overlay.OverlayServer.OverlaySettingsToJson(overlayOptionsForSettings);
        }

        var result = new
        {
            metrics = metrics,
            settings = JsonSerializer.Deserialize<object>(settingsJson)
        };

        return JsonSerializer.Serialize(result);
    }
}
