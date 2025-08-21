using EmbedIO.WebSockets;
using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Files;
using EmbedIO.WebApi;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Properties;
using StatisticsAnalysisTool.ViewModels;
using System.Net.Http;
using StatisticsAnalysisTool.Models.BindingModel;

namespace StatisticsAnalysisTool.Network.Overlay;
/// <summary>
/// Lightweight HTTP/WebSocket server for streaming overlay.
/// Serves overlay HTML/JS/CSS and streams real-time metrics.
/// </summary>
public class OverlayServer : IDisposable
{
    // Pending forced payloads saved when force==true but no registered clients exist yet.
    // Key: section ("dashboard" or "damage"); Value: (wrappedJson, expiry)
    private readonly object _pendingLock = new object();
    private readonly Dictionary<string, (string wrapped, DateTime expiry)> _pendingForcedPayloads = new Dictionary<string, (string, DateTime)>();

    // Track last payload per section (e.g. "dashboard", "damage") to avoid
    // cross-section suppression when payloads differ across sections.
    private readonly Dictionary<string, string> _lastOverlayPayloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    // Track which sections have received their initial payload to ensure each
    // section gets an initial push when available.
    private readonly HashSet<string> _hasSentInitialPayloadSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly int _port;
    private bool _isRunning = false;
    private WebServer _server;
    internal OverlayWebSocketModule _wsModule;

    public OverlayServer(int port)
    {
        _port = port;
    }

    public void Start()
    {
        Serilog.Log.Debug($"[OverlayServer] Start() called. IsRunning: {_isRunning}");
        if (_isRunning) return;
        var url = $"http://127.0.0.1:{_port}/";
        Serilog.Log.Debug($"[OverlayServer] Creating server for URL: {url}");
        _server = CreateServer(url);
        var runTask = _server.RunAsync();
        // Observe the RunAsync task for faults so we log reasons why the server may not be listening
        runTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                try
                {
                    Serilog.Log.Error(t.Exception, "[OverlayServer] WebServer RunAsync faulted");
                }
                catch { }
            }
            else if (t.IsCanceled)
            {
                Serilog.Log.Warning("[OverlayServer] WebServer RunAsync cancelled");
            }
        }, TaskContinuationOptions.NotOnRanToCompletion);

        Serilog.Log.Debug($"[OverlayServer] Server starting at {url}");
        _isRunning = true;


        // --- Initial dashboard payload push to all dashboard clients ---
        try
        {
            var mgr = StatisticsAnalysisTool.Overlay.OverlaySectionManager.Instance;
            var lastDashboardPayload = mgr?.GetLastDashboardPayloadJson();
            if (!string.IsNullOrWhiteSpace(lastDashboardPayload))
            {
                // Force broadcast to all dashboard clients
                BroadcastOverlayUpdate(lastDashboardPayload, true, "dashboard");
                Serilog.Log.Information("[OverlayServer] Forcing initial dashboard overlay push on server start.");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[OverlayServer] Error during initial dashboard payload push on server start");
        }
    }

    private WebServer CreateServer(string url)
    {
        _wsModule = new OverlayWebSocketModule("/ws", this);
        var server = new WebServer(o => o.WithUrlPrefix(url));
        server.WithModule(_wsModule);

        // Serve favicon.ico
        server.WithModule(new ActionModule("/favicon.ico", HttpVerbs.Any, async ctx =>
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Network", "Overlay", "favicon.ico");
            if (!File.Exists(iconPath))
            {
                await ctx.SendStringAsync("Not Found", "text/plain", System.Text.Encoding.UTF8);
                return;
            }
            ctx.Response.ContentType = "image/x-icon";
            using (var fs = File.OpenRead(iconPath))
            {
                await fs.CopyToAsync(ctx.Response.OutputStream);
            }
        }));

        // Serve static files under /Network/Overlay directly from output folder (JS/CSS/HTML)
        // This ensures requests like /Network/Overlay/overlay-common.js succeed when files are present in output.
        var overlayStaticDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Network", "Overlay");
        if (Directory.Exists(overlayStaticDir))
        {
            // Serve static files under /Network/Overlay/* directly from output folder
            server.WithModule(new ActionModule("/Network/Overlay", HttpVerbs.Any, async ctx =>
            {
                var reqPath = ctx.RequestedPath.TrimStart('/'); // e.g. Network/Overlay/overlay-common.js
                var name = reqPath.Substring(reqPath.LastIndexOf('/') + 1).Replace('/', Path.DirectorySeparatorChar);
                // Primary attempt: direct mapping from baseDir
                var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, reqPath.Replace('/', Path.DirectorySeparatorChar));
                // Fallback: search ancestor directories for Network/Overlay/name (handles x64 vs Debug output differences)
                if (!File.Exists(fullPath))
                {
                    try
                    {
                        // 1) Try sibling non-x64 Debug path: replace "bin\\x64\\Debug" with "bin\\Debug"
                        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        string attempt1 = null;
                        try
                        {
                            attempt1 = baseDir.Replace(Path.Combine("bin", "x64", "Debug") + Path.DirectorySeparatorChar,
                                                          Path.Combine("bin", "Debug") + Path.DirectorySeparatorChar,
                                                          StringComparison.OrdinalIgnoreCase);
                        }
                        catch { attempt1 = null; }
                        if (!string.IsNullOrEmpty(attempt1))
                        {
                            var candidate = Path.Combine(attempt1, reqPath.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(candidate)) fullPath = candidate;
                        }

                        // 2) If still not found, walk upward ancestors and look for a sibling Network/Overlay folder
                        if (!File.Exists(fullPath))
                        {
                            var start = new DirectoryInfo(baseDir);
                            for (int i = 0; i < 8 && start != null; i++)
                            {
                                try
                                {
                                    var found = Directory.EnumerateFiles(start.FullName, name, SearchOption.AllDirectories)
                                        .FirstOrDefault(p => p.IndexOf(Path.Combine("Network", "Overlay"), StringComparison.OrdinalIgnoreCase) >= 0);
                                    if (!string.IsNullOrEmpty(found))
                                    {
                                        fullPath = found;
                                        break;
                                    }
                                }
                                catch { }
                                start = start.Parent;
                            }
                        }
                    }
                    catch { }
                }

                if (File.Exists(fullPath))
                {
                    try { Serilog.Log.Debug($"[ActionModule] Serving static overlay file: {fullPath}"); } catch { }
                    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                    string ct = "application/octet-stream";
                    if (ext == ".js") ct = "application/javascript";
                    else if (ext == ".css") ct = "text/css";
                    else if (ext == ".html") ct = "text/html; charset=utf-8";
                    else if (ext == ".png") ct = "image/png";
                    else if (ext == ".ico") ct = "image/x-icon";
                    ctx.Response.ContentType = ct;
                    using (var fs = File.OpenRead(fullPath))
                    {
                        await fs.CopyToAsync(ctx.Response.OutputStream).ConfigureAwait(false);
                    }
                    return;
                }

                ctx.Response.StatusCode = 404;
                await ctx.SendStringAsync("Not Found", "text/plain", System.Text.Encoding.UTF8).ConfigureAwait(false);
            }));
        }

        // Single ActionModule for all other routes (HTML and /img)
        server.WithModule(new ActionModule("/", HttpVerbs.Any, async ctx =>
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (ctx.RequestedPath.StartsWith("/img", StringComparison.OrdinalIgnoreCase))
                {
                    // Serve embedded image
                    string name = ctx.RequestedPath.Substring("/img/".Length).TrimStart('/');
                    var asm = typeof(OverlayServer).Assembly;
                    var allNames = asm.GetManifestResourceNames();
                    var resName = allNames.FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
                    LogOverlayDebug($"[ActionModule] Image request: {name} => {resName}");
                    if (resName != null)
                    {
                        using (var stream = asm.GetManifestResourceStream(resName))
                        {
                            // Try to guess content type from name
                            var ext = Path.GetExtension(name).ToLowerInvariant();
                            var ct = ext == ".svg" ? "image/svg+xml" : (ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : "image/png");
                            ctx.Response.ContentType = ct;
                            await stream.CopyToAsync(ctx.Response.OutputStream);
                        }
                        return;
                    }

                    // Fallback: check common output folders for image files (Resources, Assets, or root)
                    // Also check configured ImageResources folder (where the app caches render.albiononline images)
                    var imageResourcesDir = Path.Combine(baseDir, Settings.Default.ImageResources ?? "ImageResources");
                    var candidates = new[] {
                    Path.Combine(imageResourcesDir, name),
                    Path.Combine(baseDir, "Resources", name),
                    Path.Combine(baseDir, "Assets", name),
                    Path.Combine(baseDir, name),
                    Path.Combine(baseDir, "Network", "Overlay", name)
                    };
                    var found = candidates.FirstOrDefault(p => File.Exists(p));
                    // If not found, try on-demand fetching from render.albiononline.com and cache locally
                    if (found == null)
                    {
                        try
                        {
                            // Only attempt for simple names (no path traversal)
                            if (!string.IsNullOrWhiteSpace(name) && !name.Contains("/") && !name.Contains(".."))
                            {
                                var http = new HttpClient();
                                var remoteUrl = $"https://render.albiononline.com/v1/item/{Uri.EscapeDataString(name)}";
                                LogOverlayDebug($"[ActionModule] Attempting remote fetch: {remoteUrl}");
                                var resp = http.GetAsync(remoteUrl).GetAwaiter().GetResult();
                                if (resp.IsSuccessStatusCode)
                                {
                                    var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                                    try
                                    {
                                        Directory.CreateDirectory(imageResourcesDir);
                                        var savePath = Path.Combine(imageResourcesDir, name);
                                        File.WriteAllBytes(savePath, bytes);
                                        LogOverlayDebug($"[ActionModule] Cached remote image to: {savePath}");
                                        found = savePath;
                                    }
                                    catch (Exception ex)
                                    {
                                        LogOverlayDebug($"[ActionModule] Failed to save cached image: {ex.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogOverlayDebug($"[ActionModule] Remote fetch failed: {ex.Message}");
                        }
                    }
                    if (found != null)
                    {
                        var ext = Path.GetExtension(found).ToLowerInvariant();
                        var ct = ext == ".svg" ? "image/svg+xml" : (ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : "image/png");
                        ctx.Response.ContentType = ct;
                        using (var fs = File.OpenRead(found))
                        {
                            await fs.CopyToAsync(ctx.Response.OutputStream);
                        }
                        return;
                    }

                    ctx.Response.StatusCode = 404;
                    await ctx.SendStringAsync($"Not Found: {name}", "text/plain", System.Text.Encoding.UTF8);
                    return;
                }
                string reqPath = ctx.RequestedPath.Trim('/').ToLowerInvariant();
                // Provide a simple JSON endpoint for the overlay UI to query which sections are enabled
                if (reqPath == "overlay-status")
                {
                    try
                    {
                        var overlayOptions = StatisticsAnalysisTool.Common.ServiceLocator.IsServiceInDictionary<StatisticsAnalysisTool.Overlay.OverlayOptionsObject>()
                            ? StatisticsAnalysisTool.Common.ServiceLocator.Resolve<StatisticsAnalysisTool.Overlay.OverlayOptionsObject>()
                            : null;
                        var status = new
                        {
                            isOverlayEnabled = overlayOptions?.IsOverlayEnabled ?? false,
                            showDashboard = overlayOptions?.ShowDashboard ?? false,
                            showDamage = overlayOptions?.ShowDamage ?? false,
                            showGathering = overlayOptions?.ShowGathering ?? false,
                            showRepair = overlayOptions?.ShowRepair ?? false
                        };
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        await ctx.SendStringAsync(System.Text.Json.JsonSerializer.Serialize(status), "application/json", System.Text.Encoding.UTF8).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogOverlayDebug($"[ActionModule] overlay-status error: {ex.Message}");
                    }
                }
                // Provide localization JSON for overlay UI: /localization?lang=en-US
                if (reqPath == "localization")
                {
                    try
                    {
                        var locPath = Path.Combine(baseDir, "Localization", "localization.json");
                        if (File.Exists(locPath))
                        {
                            var json = File.ReadAllText(locPath);
                            ctx.Response.ContentType = "application/json; charset=utf-8";
                            await ctx.SendStringAsync(json, "application/json", System.Text.Encoding.UTF8).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogOverlayDebug($"[ActionModule] localization error: {ex.Message}");
                    }
                }
                // Note: overlay control actions such as resetting PVP/all totals are handled via the
                // application UI and routed through the app (no public HTTP endpoints are exposed here).
                string htmlPath = null;
                if (reqPath == "" || reqPath == "overlay")
                    htmlPath = Path.Combine(baseDir, "Network", "Overlay", "overlay.html");
                else if (reqPath == "dashboard")
                    htmlPath = Path.Combine(baseDir, "Network", "Overlay", "dashboard.html");
                else if (reqPath == "damage")
                    htmlPath = Path.Combine(baseDir, "Network", "Overlay", "damage.html");
                else if (reqPath == "repair")
                    htmlPath = Path.Combine(baseDir, "Network", "Overlay", "repair.html");
                else if (reqPath == "gathering")
                    htmlPath = Path.Combine(baseDir, "Network", "Overlay", "gathering.html");
                LogOverlayDebug($"Request: {ctx.RequestedPath} | baseDir: {baseDir} | htmlPath: {htmlPath} | Exists: {(htmlPath != null && File.Exists(htmlPath))}");
                // If file is not found directly in base output, attempt to search ancestor directories for a Network/Overlay folder (handles Debug vs Release, x64 paths)
                if ((htmlPath == null || !File.Exists(htmlPath)) && !string.IsNullOrEmpty(htmlPath))
                {
                    try
                    {
                        var name = Path.GetFileName(htmlPath);
                        var start = new DirectoryInfo(baseDir);
                        for (int i = 0; i < 8 && start != null; i++)
                        {
                            try
                            {
                                var found = Directory.EnumerateFiles(start.FullName, name, SearchOption.AllDirectories)
                                    .FirstOrDefault(p => p.IndexOf(Path.Combine("Network", "Overlay"), StringComparison.OrdinalIgnoreCase) >= 0);
                                if (!string.IsNullOrEmpty(found))
                                {
                                    htmlPath = found;
                                    break;
                                }
                            }
                            catch { }
                            start = start.Parent;
                        }
                    }
                    catch { }
                }

                if (htmlPath != null && File.Exists(htmlPath))
                {
                    // Respect per-section visibility toggles: if the overlay section is disabled, return a small disabled page
                    try
                    {
                        var overlayOptions = StatisticsAnalysisTool.Common.ServiceLocator.IsServiceInDictionary<StatisticsAnalysisTool.Overlay.OverlayOptionsObject>()
                            ? StatisticsAnalysisTool.Common.ServiceLocator.Resolve<StatisticsAnalysisTool.Overlay.OverlayOptionsObject>()
                            : null;
                        bool allowed = true;
                        if (reqPath.Equals("dashboard", StringComparison.OrdinalIgnoreCase) && overlayOptions != null && !overlayOptions.ShowDashboard) allowed = false;
                        if (reqPath.Equals("damage", StringComparison.OrdinalIgnoreCase) && overlayOptions != null && !overlayOptions.ShowDamage) allowed = false;
                        if (reqPath.Equals("gathering", StringComparison.OrdinalIgnoreCase) && overlayOptions != null && !overlayOptions.ShowGathering) allowed = false;
                        if (!allowed)
                        {
                            // Return a minimal HTML page indicating overlay is disabled
                            var disabledHtml = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Overlay Disabled</title></head><body style=\"font-family:Segoe UI,Arial;margin:24px;color:#DDD;background:#222\">" +
                                "<h2>Overlay Disabled</h2><p>This overlay section has been turned off in the application settings.</p></body></html>";
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            await ctx.SendStringAsync(disabledHtml, "text/html", System.Text.Encoding.UTF8).ConfigureAwait(false);
                            return;
                        }

                    }
                    catch { }
                    // Read file and remove any leading UTF-8 BOM to ensure DOCTYPE is the first bytes
                    var raw = File.ReadAllText(htmlPath, System.Text.Encoding.UTF8);
                    if (!string.IsNullOrEmpty(raw) && raw[0] == '\uFEFF')
                    {
                        raw = raw.Substring(1);
                    }
                    // Ensure charset is present so browser correctly interprets encoding
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.SendStringAsync(raw, "text/html", System.Text.Encoding.UTF8);
                    return;
                }
                ctx.Response.StatusCode = 404;
                await ctx.SendStringAsync("Not Found", "text/html", System.Text.Encoding.UTF8);
            }));

        return server;
    }

    private void LogOverlayDebug(string message)
    {
        Serilog.Log.Debug(message);
    }

    /// <summary>
    /// Stops the overlay server if running, but does not dispose the object.
    /// </summary>
    public void Stop()
    {
        if (_isRunning)
        {
            _server?.Dispose();
            _isRunning = false;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>
    /// Broadcasts a JSON string to all connected WebSocket clients.
    /// </summary>
    public void BroadcastOverlayUpdate(string json, bool force = false, string type = "stats")
    {
        object dataObj = json;
        // For damage and repair payloads, parse the JSON string to an object/array so the frontend receives a real object, not a string
        if (type == "damage" || type == "repair")
        {
            try
            {
                dataObj = System.Text.Json.JsonSerializer.Deserialize<object>(json);
            }
            catch
            {
                // fallback: send as string if parsing fails
                dataObj = json;
            }
        }
        var wrapper = new { type = type, data = dataObj };
        string wrapped = System.Text.Json.JsonSerializer.Serialize(wrapper);
        string section = type == "damage" ? "damage" : (type == "repair" ? "repair" : "dashboard");
        int connectedClients = _wsModule?.GetActiveContexts()?.Count(ctx =>
        {
            string clientSection = null;
            _wsModule._clientSections.TryGetValue(ctx.Id, out clientSection);
            return ctx.WebSocket?.State == System.Net.WebSockets.WebSocketState.Open && clientSection == section;
        }) ?? 0;
        Serilog.Log.Debug($"[OverlayServer] BroadcastOverlayUpdate called: type={type}, section={section}, force={force}, connectedClients={connectedClients}");
        if (type == "repair")
        {
            try
            {
                var snippet = json ?? "";
                if (snippet.Length > 400) snippet = snippet.Substring(0, 400) + "...";
                Serilog.Log.Debug($"[OverlayServer] Repair payload json snippet: {snippet}");
            }
            catch { }
        }
        // Per-section initial-payload handling: ensure each section gets an initial push
        if (!_hasSentInitialPayloadSections.Contains(section))
        {
            Serilog.Log.Information($"[OverlayServer] Initial payload broadcast to section '{section}'");
            _wsModule?.Broadcast(wrapped, section);
            _lastOverlayPayloads[section] = json;
            _hasSentInitialPayloadSections.Add(section);
            return;
        }
        if (force)
        {
            if (connectedClients == 0)
            {
                // No clients explicitly registered for this section yet. Use wildcard to reach any open sockets.
                Serilog.Log.Warning($"[OverlayServer] Forced payload requested but no registered clients for section '{section}'. Using wildcard broadcast.");
                _wsModule?.Broadcast(wrapped, string.Empty);
                // Cache pending forced payload for a short window so late-registering clients can receive it once they register.
                try
                {
                    lock (_pendingLock)
                    {
                        _pendingForcedPayloads[section] = (wrapped, DateTime.UtcNow.AddSeconds(5));
                    }
                    Serilog.Log.Debug($"[OverlayServer] Cached forced payload for section '{section}' until {DateTime.UtcNow.AddSeconds(5):O}");
                }
                catch { }
            }
            else
            {
                Serilog.Log.Information($"[OverlayServer] Forced payload broadcast to section '{section}'");
                _wsModule?.Broadcast(wrapped, section);
            }
            _lastOverlayPayloads[section] = json;
            return;
        }
        // Compare per-section last payload and broadcast only if changed
        if (!_lastOverlayPayloads.TryGetValue(section, out var lastPayloadForSection) || !string.Equals(json, lastPayloadForSection, StringComparison.Ordinal))
        {
            Serilog.Log.Information($"[OverlayServer] Payload changed, broadcast to section '{section}'");
            // If any staged settings exist, broadcast them first for this section
            try
            {
                TryBroadcastStagedSettings(section);
            }
            catch { }
            _wsModule?.Broadcast(wrapped, section);
            _lastOverlayPayloads[section] = json;
        }
        else
        {
            // Payload unchanged since last broadcast for this section — log for diagnostics.
            try
            {
                Serilog.Log.Debug($"[OverlayServer] Skipping broadcast for section '{section}' because payload is identical to last sent payload. ConnectedClients={connectedClients}");
            }
            catch { }

            // Fallback: if a force broadcast was requested and there are no explicitly
            // registered clients for this section, broadcast to all open contexts (wildcard)
            // so late-joining clients still receive the payload.
            if (connectedClients == 0 && force)
            {
                Serilog.Log.Warning($"[OverlayServer] No clients in section '{section}', force broadcast fallback to wildcard");
                _wsModule?.Broadcast(wrapped, string.Empty);
                _lastOverlayPayloads[section] = json;
            }
        }
    }
    /// <summary>
    /// Broadcasts overlay settings as a JSON message to all connected clients.
    /// </summary>
    public void BroadcastOverlaySettings(StatisticsAnalysisTool.Overlay.OverlayOptionsObject settings)
    {
        var json = OverlaySettingsToJson(settings);
        var msg = $"{{\"type\":\"settings\",\"data\":{json}}}";
        // Legacy immediate broadcast kept for explicit use. Prefer StageOverlaySettings()
        _wsModule?.Broadcast(msg, "dashboard");
    }

    /// <summary>
    /// Stage overlay settings on the server. Staged settings will be emitted
    /// together with the next real metrics broadcast to avoid showing zero-only
    /// dashboards when only settings are changed during initialization.
    /// </summary>
    public void StageOverlaySettings(StatisticsAnalysisTool.Overlay.OverlayOptionsObject settings)
    {
        try
        {
            var json = OverlaySettingsToJson(settings);
            var msg = $"{{\"type\":\"settings\",\"data\":{json}}}";
            lock (_pendingLock)
            {
                // Stage settings and allow them to be drained for any section (dashboard, damage, gathering)
                _pendingForcedPayloads["settings"] = (msg, DateTime.UtcNow.AddSeconds(10));
            }
            Serilog.Log.Debug($"[OverlayServer] Staged overlay settings until next metrics broadcast");
            // Also proactively attempt to broadcast settings to damage section so webviews get updated appearance
            try
            {
                _wsModule?.Broadcast(msg, "damage");
                Serilog.Log.Debug("[OverlayServer] Proactively broadcast staged settings to damage section");
            }
            catch { }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[OverlayServer] Failed to stage overlay settings");
        }
    }

    /// <summary>
    /// Broadcast staged settings if present for the given section. Returns true if a staged settings
    /// payload was found and broadcast.
    /// </summary>
    internal bool TryBroadcastStagedSettings(string section)
    {
        try
        {
            (string wrapped, DateTime expiry) pending;
            lock (_pendingLock)
            {
                if (_pendingForcedPayloads.TryGetValue("settings", out pending))
                {
                    if (pending.expiry > DateTime.UtcNow)
                    {
                        _wsModule?.Broadcast(pending.wrapped, section);
                        _pendingForcedPayloads.Remove("settings");
                        Serilog.Log.Information($"[OverlayServer] Broadcast staged settings to section '{section}'");
                        return true;
                    }
                    else
                    {
                        _pendingForcedPayloads.Remove("settings");
                    }
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Serializes overlay settings to a JSON object string for the overlay frontend.
    /// </summary>
    public static string OverlaySettingsToJson(StatisticsAnalysisTool.Overlay.OverlayOptionsObject s)
    {
        // Include per-metric settings for all metrics
        var metrics = s.Metrics;
        string metricSettings = "{" +
            string.Join(",",
                metrics.Select(m => $"\"{m.Name.ToLower()}\":{{\"showTitle\":{m.ShowTitle.ToString().ToLower()},\"showImage\":{m.ShowImage.ToString().ToLower()},\"showTotal\":{m.ShowTotal.ToString().ToLower()},\"showPerHour\":{m.ShowPerHour.ToString().ToLower()},\"hide\":{m.HideMetric.ToString().ToLower()}}}"))
            + "}";
        // Add theme property
        string theme = s.SelectedTheme ?? "Dark";
        // Use invariant culture for numbers to prevent comma issues
        var fontSize = s.DashboardFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var iconSize = s.DashboardIconSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var titleFontSize = s.DashboardTitleFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var totalFontSize = s.DashboardTotalFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var perHourFontSize = s.DashboardPerHourFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Damage preview settings: include previewCount and boolean flags so damage.html can decide what to render
        var damagePreviewCount = s.DamagePreviewCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var damageShowDps = s.DamageShowDps.ToString().ToLower();
        var damageShowHeal = s.DamageShowHeal.ToString().ToLower();
        var damageShowIcons = s.DamageShowIcons.ToString().ToLower();
        // Include whether the overlay should hide the local player's own entry (inverted helpers exist on the options object)
        var damageHideSelf = s.DamageHideSelf.ToString().ToLower();
        // Current tracked username (if available) - escape quotes/backslashes
        var usernameRaw = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance?.UserTrackingBindings?.Username ?? string.Empty;
        var username = usernameRaw.Replace("\\", "\\\\").Replace("\"", "\\\"");

        return $"{{\"fontSize\":{fontSize},\"iconSize\":{iconSize}," +
            $"\"titleFontSize\":{titleFontSize},\"totalFontSize\":{totalFontSize},\"perHourFontSize\":{perHourFontSize},\"autoHideZero\":{s.DashboardAutoHideZeroValues.ToString().ToLower()},\"theme\":\"{theme}\",\"metricSettings\":{metricSettings}," +
            $"\"username\":\"{username}\",\"hideSelf\":{damageHideSelf},\"damageSettings\":{{\"previewCount\":{damagePreviewCount},\"showDps\":{damageShowDps},\"showHeal\":{damageShowHeal},\"showIcons\":{damageShowIcons}}}}}";
    }

    private static string MetricSettingsToJson(object ms)
    {
        // Assumes ms is MetricDisplaySettings
        var t = ms.GetType();
        bool showTitle = (bool) t.GetProperty("ShowTitle").GetValue(ms);
        bool showImage = (bool) t.GetProperty("ShowImage").GetValue(ms);
        bool showTotal = (bool) t.GetProperty("ShowTotal").GetValue(ms);
        bool showPerHour = (bool) t.GetProperty("ShowPerHour").GetValue(ms);
        bool hide = (bool) t.GetProperty("HideMetric").GetValue(ms);
        return $"{{\"showTitle\":{showTitle.ToString().ToLower()},\"showImage\":{showImage.ToString().ToLower()},\"showTotal\":{showTotal.ToString().ToLower()},\"showPerHour\":{showPerHour.ToString().ToLower()},\"hide\":{hide.ToString().ToLower()}}}";
    }

    /// <summary>
    /// WebSocket module for overlay push updates.
    /// </summary>
    internal class OverlayWebSocketModule : WebSocketModule
    {
        private readonly OverlayServer _owner;
        // Track section for each client. Keyed by context.Id (string) to avoid relying on object identity
        internal readonly Dictionary<string, string> _clientSections = new Dictionary<string, string>();

        public OverlayWebSocketModule(string urlPath, OverlayServer owner) : base(urlPath, true) { _owner = owner; }


        // Expose active contexts for integration
        public IEnumerable<IWebSocketContext> GetActiveContexts()
        {
            return base.ActiveContexts;
        }

        public void Broadcast(string message, string section)
        {
            var activeContexts = this.ActiveContexts?
                .Where(ctx => ctx.WebSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                .ToArray() ?? Array.Empty<IWebSocketContext>();
            int sent = 0;
            try
            {
                Serilog.Log.Debug($"[OverlayWebSocket] Broadcast invoked for section='{section}'. ActiveContexts={activeContexts.Length}, RegisteredClients={_clientSections.Count}");
            }
            catch { }

            var sendTasks = new System.Collections.Generic.List<Task>();
            foreach (var ctx in activeContexts)
            {
                // If section is null or empty, treat as wildcard: send to all open contexts
                if (string.IsNullOrEmpty(section))
                {
                    try
                    {
                        sendTasks.Add(base.SendAsync(ctx, message));
                        sent++;
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "[OverlayWebSocket] Send failed for wildcard context");
                    }
                    continue;
                }
                string clientSection = null;
                _clientSections.TryGetValue(ctx.Id, out clientSection);
                if (clientSection == section)
                {
                    try
                    {
                        sendTasks.Add(base.SendAsync(ctx, message));
                        sent++;
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, $"[OverlayWebSocket] Send failed for context {ctx?.Id}");
                    }
                }
            }

            // Observe send tasks to catch and log send failures while not blocking the caller
            if (sendTasks.Count > 0)
            {
                Task.WhenAll(sendTasks).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        try { Serilog.Log.Error(t.Exception, "[OverlayWebSocket] One or more send tasks failed during Broadcast"); } catch { }
                    }
                });
            }

            // Always log summary for diagnostics
            Serilog.Log.Information($"[OverlayWebSocket] Broadcasted to {sent} client(s) for section '{section}'");
        }

        protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
        {
            try
            {
                var msg = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                // Avoid noisy debug logs for heartbeat 'ping' messages (they appear frequently)
                if (!msg.Contains("\"type\":\"ping\""))
                {
                    Serilog.Log.Debug($"[OverlayWebSocket] Received raw message from context={context?.Id}: {msg}");
                }
                var json = System.Text.Json.JsonDocument.Parse(msg);
                var root = json.RootElement;
                if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "register")
                {
                    var section = root.TryGetProperty("section", out var secEl) ? secEl.GetString() : null;
                    if (!string.IsNullOrEmpty(section))
                    {
                        _clientSections[context.Id] = section;
                        Serilog.Log.Information($"[OverlayWebSocket] Registered client for section: {section} (context={context?.Id})");
                        try
                        {
                            Serilog.Log.Debug($"[OverlayWebSocket] Registered clients count={_clientSections.Count}");
                        }
                        catch { }
                        // processed above
                        try
                        {
                            var ack = System.Text.Json.JsonSerializer.Serialize(new { type = "registered", section = section });
                            base.SendAsync(context, ack);
                        }
                        catch { }

                        // Slight delay / polling to reduce race between connect/registration and when the OverlaySectionManager produces the payload.
                        var mgr = StatisticsAnalysisTool.Overlay.OverlaySectionManager.Instance;
                        if (section == "damage")
                        {
                            var lastPayload = mgr?.GetLastDamagePayloadJson();
                            if (!string.IsNullOrWhiteSpace(lastPayload))
                            {
                                var wrapper = new { type = "damage", data = System.Text.Json.JsonSerializer.Deserialize<object>(lastPayload) };
                                var wrapped = System.Text.Json.JsonSerializer.Serialize(wrapper);
                                Task.Delay(100).ContinueWith(_ =>
                                {
                                    try { base.SendAsync(context, wrapped); Serilog.Log.Information($"[OverlayWebSocket] Sent initial damage payload to new client (context={context?.Id}) after delay."); } catch { }
                                });
                            }
                            else
                            {
                                // Poll for a short time (20 * 100ms = 2s) for the manager to have the payload, then send it
                                Task.Run(async () =>
                                {
                                    for (int i = 0; i < 20; i++)
                                    {
                                        try
                                        {
                                            await Task.Delay(100).ConfigureAwait(false);
                                            var p = mgr?.GetLastDamagePayloadJson();
                                            if (!string.IsNullOrWhiteSpace(p))
                                            {
                                                var wrapper = new { type = "damage", data = System.Text.Json.JsonSerializer.Deserialize<object>(p) };
                                                var wrapped = System.Text.Json.JsonSerializer.Serialize(wrapper);
                                                try { await base.SendAsync(context, wrapped).ConfigureAwait(false); Serilog.Log.Information($"[OverlayWebSocket] Sent delayed damage payload to new client (context={context?.Id}) after polling."); } catch { }
                                                break;
                                            }
                                        }
                                        catch { }
                                    }
                                });
                            }
                        }
                        else if (section == "dashboard")
                        {
                            var lastPayload = mgr?.GetLastDashboardPayloadJson();
                            if (!string.IsNullOrWhiteSpace(lastPayload))
                            {
                                // The frontend expects type: 'stats' and the metrics payload as a string
                                var wrapper = new { type = "stats", data = lastPayload };
                                var wrapped = System.Text.Json.JsonSerializer.Serialize(wrapper);
                                Task.Delay(100).ContinueWith(_ =>
                                {
                                    try { base.SendAsync(context, wrapped); Serilog.Log.Information($"[OverlayWebSocket] Sent initial dashboard payload to new client (context={context?.Id}) after delay."); } catch { }
                                });
                            }
                            else
                            {
                                Task.Run(async () =>
                                {
                                    for (int i = 0; i < 20; i++)
                                    {
                                        try
                                        {
                                            await Task.Delay(100).ConfigureAwait(false);
                                            var p = mgr?.GetLastDashboardPayloadJson();
                                            if (!string.IsNullOrWhiteSpace(p))
                                            {
                                                var wrapper = new { type = "stats", data = p };
                                                var wrapped = System.Text.Json.JsonSerializer.Serialize(wrapper);
                                                try { await base.SendAsync(context, wrapped).ConfigureAwait(false); Serilog.Log.Information($"[OverlayWebSocket] Sent delayed dashboard payload to new client (context={context?.Id}) after polling."); } catch { }
                                                break;
                                            }
                                        }
                                        catch { }
                                    }
                                });
                            }
                            // If server has a pending forced payload cached for this section, send it now and remove it
                            try
                            {
                                if (_owner != null)
                                {
                                    (string wrapped, DateTime expiry) pending;
                                    lock (_owner._pendingLock)
                                    {
                                        if (_owner._pendingForcedPayloads.TryGetValue("dashboard", out pending))
                                        {
                                            if (pending.expiry > DateTime.UtcNow)
                                            {
                                                // send immediately
                                                try { base.SendAsync(context, pending.wrapped); Serilog.Log.Information($"[OverlayWebSocket] Sent pending forced payload to new dashboard client (context={context?.Id})."); } catch { }
                                            }
                                            _owner._pendingForcedPayloads.Remove("dashboard");
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                        else if (section == "repair")
                        {
                            var lastPayload = mgr?.GetLastRepairPayloadJson();
                            if (!string.IsNullOrWhiteSpace(lastPayload))
                            {
                                var wrapper = new { type = "repair", data = System.Text.Json.JsonSerializer.Deserialize<object>(lastPayload) };
                                var wrapped = System.Text.Json.JsonSerializer.Serialize(wrapper);
                                Task.Delay(100).ContinueWith(_ =>
                                {
                                    try { base.SendAsync(context, wrapped); Serilog.Log.Information($"[OverlayWebSocket] Sent initial repair payload to new client (context={context?.Id}) after delay."); } catch { }
                                });
                            }
                            else
                            {
                                Task.Run(async () =>
                                {
                                    for (int i = 0; i < 20; i++)
                                    {
                                        try
                                        {
                                            await Task.Delay(100).ConfigureAwait(false);
                                            var p = mgr?.GetLastRepairPayloadJson();
                                            if (!string.IsNullOrWhiteSpace(p))
                                            {
                                                var wrapper = new { type = "repair", data = System.Text.Json.JsonSerializer.Deserialize<object>(p) };
                                                var wrapped = System.Text.Json.JsonSerializer.Serialize(wrapper);
                                                try { await base.SendAsync(context, wrapped).ConfigureAwait(false); Serilog.Log.Information($"[OverlayWebSocket] Sent delayed repair payload to new client (context={context?.Id}) after polling."); } catch { }
                                                break;
                                            }
                                        }
                                        catch { }
                                    }
                                });
                            }
                        }
                        else
                        {
                            // For other sections, attempt to drain pending payloads that match the section name
                            try
                            {
                                if (_owner != null)
                                {
                                    (string wrapped, DateTime expiry) pending;
                                    lock (_owner._pendingLock)
                                    {
                                        if (_owner._pendingForcedPayloads.TryGetValue(section, out pending))
                                        {
                                            if (pending.expiry > DateTime.UtcNow)
                                            {
                                                try { base.SendAsync(context, pending.wrapped); Serilog.Log.Information($"[OverlayWebSocket] Sent pending forced payload to new client for section '{section}' (context={context?.Id})."); } catch { }
                                            }
                                            _owner._pendingForcedPayloads.Remove(section);
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"[OverlayWebSocket] Error parsing message: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
        {
            try { _clientSections.Remove(context.Id); } catch { }
            var ws = context.WebSocket;
            string reason;
            if (ws != null)
            {
                reason = $"State: {ws.State}";
            }
            else
            {
                reason = "WebSocket null";
            }

            // Normal client disconnects are informational; keep the detailed stack trace at Debug level only.
            Serilog.Log.Information("[OverlayWebSocket] Client disconnected. Context: {contextId}, Reason: {reason}", context?.Id, reason);
            Serilog.Log.Debug("[OverlayWebSocket] Disconnect stack: {stack}", Environment.StackTrace);
            return Task.CompletedTask;
        }

        protected override Task OnClientConnectedAsync(IWebSocketContext context)
        {
            Serilog.Log.Information($"[OverlayWebSocket] Client connected.");
            return Task.CompletedTask;
        }
    }

    // All WebSocket overrides and Broadcast are defined above. Removed duplicates and misplaced methods.
}
