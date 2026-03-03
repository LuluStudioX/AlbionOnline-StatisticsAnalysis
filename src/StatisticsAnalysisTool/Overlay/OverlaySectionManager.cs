using StatisticsAnalysisTool.Models.BindingModel;
using StatisticsAnalysisTool.ViewModels;
using System;
using System.Linq;
using System.Reflection;

namespace StatisticsAnalysisTool.Overlay;

/// <summary>
/// Central manager for all overlay section updates (dashboard, gathering, damage, etc).
/// Handles payload building and server push for all overlay types.
/// </summary>
public class OverlaySectionManager
{
    // Cache the last dashboard metrics and settings separately to avoid redundant pushes
    private string _lastDashboardMetrics = null;
    private string _lastDashboardSettings = null;
    // Track last damage payload to avoid duplicate broadcasts
    private string _lastDamagePayload = null;
    private readonly object _damageLock = new object();
    // Throttle map to avoid duplicate rapid-fire broadcasts caused by multiple callers
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastBroadcastUtc = new(System.StringComparer.OrdinalIgnoreCase);
    public static OverlaySectionManager Instance { get; } = new OverlaySectionManager();
    private OverlaySectionManager() { }

    // Expose last dashboard payload for overlay server to push to new clients
    public string GetLastDashboardPayloadJson()
    {
        return _lastDashboardMetrics;
    }

    // Expose last damage payload for overlay server to push to new clients
    public string GetLastDamagePayloadJson()
    {
        lock (_damageLock)
        {
            return _lastDamagePayload;
        }
    }

    // Expose last repair payload
    private string _lastRepairPayload = null;
    private readonly object _repairLock = new object();
    public string GetLastRepairPayloadJson()
    {
        lock (_repairLock)
        {
            return _lastRepairPayload;
        }
    }

    public void UpdateDashboard(DashboardBindings bindings, bool force = false)
    {
        // Respect global overlay enabled flag and per-section toggles: if overlays are disabled or this section is hidden, do nothing.
        var overlayOptions = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.AppInstance?.OverlayOptions;
        if (overlayOptions == null || !overlayOptions.IsOverlayEnabled || !overlayOptions.ShowDashboard)
        {
            // No overlays allowed; skip building or sending any payloads.
            return;
        }

        var payload = OverlayDashboardPayloadBuilder.Build(bindings);
        if (string.IsNullOrWhiteSpace(payload) || payload.Trim() == "[]")
        {
            //Serilog.Log.Debug("[OverlaySectionManager] Dashboard payload builder returned empty/null or [] ; skipping broadcast");
            return;
        }
        string normalizedPayload = NormalizePayload(payload);
        // For dashboard, the payload is now JSON, so we use the entire payload as-is
        // No need to split into metrics and settings parts since they're combined in JSON
        string metricsPart = normalizedPayload;
        string settingsPart = "{}"; // Settings are embedded in the JSON payload
                                    // Suppression logic: use the entire JSON payload for comparison
                                    // Always force broadcast for real-time overlay updates
                                    // bool metricsChanged = _lastDashboardMetrics == null || _lastDashboardMetrics != metricsPart;
                                    // if (!force && !metricsChanged)
                                    // {
                                    //     return;
                                    // }

        // Only log [OverlayPayload] when a real push occurs
        // Only log at Information level if payload is unusually large
        // Log the normalized payload only when we're actually going to consider broadcasting it
        Serilog.Log.Debug($"[OverlaySectionManager] Dashboard payload (normalized): {normalizedPayload}");

        if ((metricsPart?.Length ?? 0) > 2000)
            Serilog.Log.Information($"[OverlayPayload] Large dashboard JSON payload length={(metricsPart?.Length ?? 0)}");
        // If the user has enabled auto-hide for zero values, and the payload contains
        // only zero/empty primary metrics, skip broadcasting to avoid showing an empty dashboard.
        try
        {
            // If the caller specifically forced a broadcast, always send regardless of zeros.
            if (!force)
            {
                var overlayOpts = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.AppInstance?.OverlayOptions;
                // Try to parse JSON and inspect metrics array; if all primary metrics are zero and preview is disabled, skip broadcast.
                using var doc = System.Text.Json.JsonDocument.Parse(metricsPart);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("metrics", out var metricsEl) && metricsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    bool anyNonZero = false;
                    // Primary metric indices: 0,2,4,6,8,10 (totals for Fame,Silver,ReSpec,Might,Favor,Faction)
                    int[] primaryIdx = new[] { 0, 2, 4, 6, 8, 10 };
                    foreach (var idx in primaryIdx)
                    {
                        if (idx < metricsEl.GetArrayLength())
                        {
                            var el = metricsEl[idx];
                            if (el.ValueKind == System.Text.Json.JsonValueKind.Number)
                            {
                                try { if (el.GetDouble() != 0.0) { anyNonZero = true; break; } } catch { anyNonZero = true; break; }
                            }
                            else if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                var s = el.GetString() ?? "";
                                var norm = s.Replace(",", "").Replace(" ", "").Replace("/h", "").Trim();
                                if (double.TryParse(norm, out var dv))
                                {
                                    if (dv != 0.0) { anyNonZero = true; break; }
                                }
                                else
                                {
                                    if (System.Text.RegularExpressions.Regex.IsMatch(norm, "[0-9]")) { anyNonZero = true; break; }
                                }
                            }
                            else if (el.ValueKind == System.Text.Json.JsonValueKind.Null)
                            {
                                // null -> treat as zero
                            }
                            else
                            {
                                anyNonZero = true; break;
                            }
                        }
                    }
                    // If there are no non-zero primary metrics, and preview metrics are not enabled, skip broadcasting
                    if (!anyNonZero && (overlayOpts == null || !overlayOpts.DashboardUsePreviewMetrics))
                    {
                        Serilog.Log.Debug("[OverlaySectionManager] Dashboard payload contains only zeros and preview metrics are disabled — skipping broadcast");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[OverlaySectionManager] Error while evaluating dashboard metrics content");
        }

        // At this point the payload passed auto-hide checks (if enabled). Cache it so
        // the server can push it to newly connecting clients on startup. We only cache
        // when we intend to broadcast, to avoid seeding initial clients with zero-only data.
        _lastDashboardMetrics = metricsPart;
        _lastDashboardSettings = settingsPart;

        // Push stats payload to overlay server (web view)
        // At this point overlayOptions is known to be non-null and enabled.
        var serverField = overlayOptions.GetType().GetField("_overlayServer", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        var server = serverField?.GetValue(overlayOptions) as StatisticsAnalysisTool.Network.Overlay.OverlayServer;
        if (server != null)
        {
            try
            {
                // Throttle duplicate immediate broadcasts: if we broadcasted for 'dashboard' within the
                // last 200ms, skip this request. This prevents duplicate frames when multiple parts of the
                // app simultaneously force an overlay push (subscriptions + forced initial pushes).
                var now = DateTime.UtcNow;
                var last = _lastBroadcastUtc.GetOrAdd("dashboard", DateTime.MinValue);
                if ((now - last) < TimeSpan.FromMilliseconds(200))
                {
                    Serilog.Log.Debug($"[OverlaySectionManager] Suppressing duplicate dashboard broadcast (delta={(now - last).TotalMilliseconds}ms)");
                    return;
                }
                _lastBroadcastUtc["dashboard"] = now;

                // Try to read active contexts count if available for debug, but do not change behavior
                var active = server.GetType().GetField("_wsModule", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(server);
                if (active != null)
                {
                    var acProp = active.GetType().GetMethod("GetActiveContexts");
                    if (acProp != null)
                    {
                        var contexts = acProp.Invoke(active, null) as System.Collections.IEnumerable;
                        int cnt = 0;
                        if (contexts != null)
                        {
                            foreach (var _ in contexts) cnt++;
                        }
                        Serilog.Log.Debug($"[OverlaySectionManager] Broadcasting dashboard payload. Active contexts: {cnt}");
                    }
                }
            }
            catch { }
            // Normal broadcast. Add a tiny 'sentAt' timestamp to the payload so the
            // outgoing JSON differs even if the metrics are identical. This is a
            // minimal-bandwidth way to avoid suppression by string-equality on some
            // hosts and ensures overlays receive a fresh frame.
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(normalizedPayload);
                var root = doc.RootElement;
                // Build a new object that includes sentAt to avoid mutating the canonical cached payload
                var obj = new System.Collections.Generic.Dictionary<string, object>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object || prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        obj[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                    }
                    else
                    {
                        obj[prop.Name] = prop.Value.ToString();
                    }
                }
                obj["sentAt"] = DateTime.UtcNow.ToString("o");
                var outgoing = System.Text.Json.JsonSerializer.Serialize(obj);
                server.BroadcastOverlayUpdate(outgoing, true, "stats");
            }
            catch (Exception ex)
            {
                // Fallback: if parsing failed, send the original payload
                Serilog.Log.Warning(ex, "[OverlaySectionManager] Failed to inject sentAt timestamp into dashboard payload; sending original payload");
                server.BroadcastOverlayUpdate(normalizedPayload ?? payload, true, "stats");
            }
        }
    }

    public void UpdateDamage(DamageMeter.DamageMeterBindings bindings, bool force = false)
    {
        var payload = OverlayDamagePayloadBuilder.Build(bindings);
        var normalized = NormalizePayload(payload);
        Serilog.Log.Debug($"[OverlayPayload][Damage] Outgoing payload: {normalized}");
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "[]")
        {
            Serilog.Log.Information("[OverlayPayload][Damage] Skipping: payload empty or []");
            return;
        }
        lock (_damageLock)
        {
            // Always force broadcast for troubleshooting
            _lastDamagePayload = normalized;
        }
        if (normalized.Length > 2000)
            Serilog.Log.Information($"[OverlayPayload][Damage] Large payload (len={normalized.Length})");
        // Respect global overlay enabled flag and per-section toggles: if overlays are disabled or this section is hidden, do nothing.
        var overlayOptions = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.AppInstance?.OverlayOptions;
        if (overlayOptions == null || !overlayOptions.IsOverlayEnabled || !overlayOptions.ShowDamage)
        {
            return;
        }

        var serverField = overlayOptions.GetType().GetField("_overlayServer", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        var server = serverField?.GetValue(overlayOptions) as StatisticsAnalysisTool.Network.Overlay.OverlayServer;
        if (server != null)
        {
            try
            {
                // Normal broadcast; do not force. Server will deduplicate and respect staged settings.
                server.BroadcastOverlayUpdate(normalized ?? payload, false, "damage");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OverlayPayload][Damage] BroadcastOverlayUpdate failed");
            }
        }
    }

    public void UpdateRepair(DashboardBindings bindings, bool force = false)
    {
        try
        {
            // Quick-out: when overlay is disabled and this is not a forced send, avoid building the payload
            var overlayOpts = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.AppInstance?.OverlayOptions;
            if (!force && (overlayOpts == null || !overlayOpts.IsOverlayEnabled || !overlayOpts.ShowRepair))
            {
                return;
            }

            var payload = OverlayRepairPayloadBuilder.Build(bindings);
            var normalized = NormalizePayload(payload);
            if (string.IsNullOrWhiteSpace(normalized) || normalized == "[]")
            {
                Serilog.Log.Information("[OverlayPayload][Repair] Skipping: payload empty or []");
                return;
            }

            // Throttle rapid repeated checks to avoid noisy logs when nothing changed.
            // If we've checked recently and were unchanged, silently skip.
            var now = DateTime.UtcNow;
            var last = _lastBroadcastUtc.GetOrAdd("repair", DateTime.MinValue);
            if (!force && (now - last) < TimeSpan.FromMilliseconds(500))
            {
                // Too-frequent checks; silently drop to avoid log spam
                return;
            }

            // Only broadcast the initial payload or when it changed (unless force==true)
            bool shouldBroadcast = force;
            lock (_repairLock)
            {
                if (_lastRepairPayload == null)
                {
                    // first time: broadcast
                    shouldBroadcast = true;
                }
                else if (!string.Equals(_lastRepairPayload, normalized, StringComparison.Ordinal))
                {
                    shouldBroadcast = true;
                }
                if (shouldBroadcast)
                {
                    _lastRepairPayload = normalized;
                }
            }
            if (!shouldBroadcast)
            {
                // update the last-check time so throttling prevents repeat rapid checks
                _lastBroadcastUtc["repair"] = now;
                // Avoid debug log on every unchanged check to reduce noise
                return;
            }
            var serverField = overlayOpts.GetType().GetField("_overlayServer", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            var server = serverField?.GetValue(overlayOpts) as StatisticsAnalysisTool.Network.Overlay.OverlayServer;
            if (server != null)
            {
                try
                {
                    server.BroadcastOverlayUpdate(normalized ?? payload, force, "repair");
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[OverlayPayload][Repair] BroadcastOverlayUpdate failed");
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[OverlayPayload][Repair] Failed to build or broadcast payload");
        }
    }

    // Normalize payload by trimming leading/trailing whitespace
    private static string NormalizePayload(string payload)
    {
        return string.IsNullOrEmpty(payload) ? payload : payload.Trim();
    }
}