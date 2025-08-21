using StatisticsAnalysisTool.DamageMeter;
using System;
using System.Linq;
using System.Text.Json;
using System.IO;
using StatisticsAnalysisTool.Properties;

namespace StatisticsAnalysisTool.Overlay;

public static class OverlayDamagePayloadBuilder
{
    // Builds a JSON string representing the current damage meter state.
    // The result is a JSON array of fragments with { name, damage, dps, heal, hps, damagePercent, takenDamage }
    public static string Build(DamageMeterBindings bindings)
    {
        if (bindings == null) return "[]";

        // Respect overlay option 'hide self' to show a waiting placeholder when the user hides their own metric
        var overlayOptions = (StatisticsAnalysisTool.Overlay.OverlayOptionsObject) null;
        try { if (StatisticsAnalysisTool.Common.ServiceLocator.IsServiceInDictionary<StatisticsAnalysisTool.Overlay.OverlayOptionsObject>()) overlayOptions = StatisticsAnalysisTool.Common.ServiceLocator.Resolve<StatisticsAnalysisTool.Overlay.OverlayOptionsObject>(); } catch { }

        var list = bindings.DamageMeter?
            .Select(f =>
            {
                // Determine a stable key for this entry. Prefer a unique id if available (reflectively),
                // then prefer the CauserGuid (so it matches the key used by ProcessDamageDelta),
                // otherwise fall back to the display name.
                string key = null;
                try
                {
                    var prop = f.GetType().GetProperty("UniqueId");
                    if (prop != null)
                    {
                        var v = prop.GetValue(f);
                        if (v != null) key = v.ToString();
                    }
                }
                catch { }

                // If UniqueId wasn't available, try CauserGuid (DamageMeterFragment.CauserGuid -> GUID string)
                if (string.IsNullOrWhiteSpace(key))
                {
                    try
                    {
                        var causerProp = f.GetType().GetProperty("CauserGuid");
                        if (causerProp != null)
                        {
                            var cv = causerProp.GetValue(f);
                            if (cv is Guid g && g != Guid.Empty)
                            {
                                key = g.ToString();
                            }
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(key)) key = (f.Name ?? string.Empty).Trim();

                // Aggregator removed: use DamageMeterFragment values directly for payload totals.

                // (Note) Integration with app Damage Meter authoritative totals (OnlyDamageToPlayersCount)
                // will be implemented in OverlaySectionManager or via a service call where application state is available.

                // Build the payload object including optional totals fields
                return (object) new
                {
                    name = f.Name ?? string.Empty,
                    damage = f.Damage,
                    damageShort = f.DamageShortString,
                    dps = f.Dps,
                    dpsShort = f.DpsString,
                    heal = f.Heal,
                    healShort = f.HealShortString,
                    hps = f.Hps,
                    damagePercent = f.DamageInPercent,
                    takenDamage = f.TakenDamage,
                    takenDamageShort = f.TakenDamageShortString,
                    // Provide a local icon path if the image is cached, otherwise include the remote render URL as a fallback
                    icon = (f.CauserMainHand != null && !string.IsNullOrEmpty(f.CauserMainHand.UniqueName)) ?
                        (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Settings.Default.ImageResources ?? "ImageResources", f.CauserMainHand.UniqueName)) ?
                            $"/img/{f.CauserMainHand.UniqueName}" : null)
                        : null,
                    iconUrl = f.CauserMainHand != null && !string.IsNullOrEmpty(f.CauserMainHand.UniqueName) ?
                        $"https://render.albiononline.com/v1/item/{f.CauserMainHand.UniqueName}" : null,
                    // Return application DamageMeter values as authoritative (no aggregator)
                    totalDamage = f.Damage,
                    totalDamageShort = f.DamageShortString,
                    totalDps = f.Dps
                };
            })
            .ToList() ?? new System.Collections.Generic.List<object>();

        // If overlay options request hiding self and the only non-hidden entry would be the local player, replace payload with a single placeholder
        try
        {
            bool hideSelf = overlayOptions?.DamageHideSelf ?? false;
            var username = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance?.UserTrackingBindings?.Username?.Trim();
            if (hideSelf && !string.IsNullOrWhiteSpace(username))
            {
                // Count entries that are not the local user
                var nonSelfEntries = list.Where(o =>
                {
                    try
                    {
                        var nameProp = o.GetType().GetProperty("name");
                        if (nameProp != null)
                        {
                            var val = nameProp.GetValue(o) as string;
                            return !string.Equals(val?.Trim(), username, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch { }
                    return true;
                }).ToList();

                if (nonSelfEntries.Count == 0)
                {
                    // Return a single placeholder informing the user the overlay is waiting for metrics
                    var placeholder = new[] { new { name = "Waiting for metrics", damage = 0, damageShort = "0", dps = 0.0, dpsShort = "0", heal = 0, healShort = "0", hps = 0.0, damagePercent = 0.0, takenDamage = 0, takenDamageShort = "0", icon = (string) null, iconUrl = (string) null, totalDamage = 0, totalDamageShort = string.Empty, totalDps = 0.0 } };
                    return JsonSerializer.Serialize(placeholder);
                }
            }
        }
        catch { }

        return JsonSerializer.Serialize(list);
    }
}
