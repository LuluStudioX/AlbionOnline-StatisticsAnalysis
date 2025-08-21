using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using System.Linq;

namespace StatisticsAnalysisTool.Network.Overlay;

public class ResourceImageController : WebApiController
{
    private static void LogOverlayDebug(string message)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "overlay-debug.log");
            File.AppendAllText(logPath, $"[ResourceImageController] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { /* ignore logging errors */ }
    }

    [Route(HttpVerbs.Get, "/img/{name}")]
    public async Task GetImage(string name)
    {
        LogOverlayDebug($"ResourceImageController: GetImage CALLED with name='{name}'");
        var asm = Assembly.GetExecutingAssembly();
        var allNames = asm.GetManifestResourceNames();
        var resName = allNames.FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        if (resName != null)
        {
            using (var stream = asm.GetManifestResourceStream(resName))
            {
                HttpContext.Response.ContentType = "image/png";
                await stream.CopyToAsync(HttpContext.Response.OutputStream);
                return;
            }
        }

        // Check local cache directory
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var cacheDir = Path.Combine(baseDir, "ImageResources");
        var localPath = Path.Combine(cacheDir, name);
        if (File.Exists(localPath))
        {
            HttpContext.Response.ContentType = "image/png";
            using (var fs = File.OpenRead(localPath))
            {
                await fs.CopyToAsync(HttpContext.Response.OutputStream);
                return;
            }
        }

        // Try to fetch from Albion Online CDN and cache
        try
        {
            if (!string.IsNullOrWhiteSpace(name) && !name.Contains("/") && !name.Contains(".."))
            {
                var http = new System.Net.Http.HttpClient();
                var remoteUrl = $"https://render.albiononline.com/v1/item/{Uri.EscapeDataString(name)}";
                var resp = await http.GetAsync(remoteUrl);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    Directory.CreateDirectory(cacheDir);
                    File.WriteAllBytes(localPath, bytes);
                    HttpContext.Response.ContentType = "image/png";
                    await HttpContext.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    LogOverlayDebug($"Fetched and cached remote image: {remoteUrl} -> {localPath}");
                    return;
                }
                else
                {
                    LogOverlayDebug($"Remote fetch failed: {remoteUrl} status={resp.StatusCode}");
                }
            }
        }
        catch (Exception ex)
        {
            LogOverlayDebug($"Exception during remote fetch: {ex.Message}");
        }

        // Fallback: serve generic image
        var genericPath = Path.Combine(cacheDir, "generic.png");
        if (File.Exists(genericPath))
        {
            HttpContext.Response.ContentType = "image/png";
            using (var fs = File.OpenRead(genericPath))
            {
                await fs.CopyToAsync(HttpContext.Response.OutputStream);
                return;
            }
        }
        LogOverlayDebug($"Image not found, serving 404: {name}");
        HttpContext.Response.StatusCode = 404;
        await HttpContext.SendStringAsync($"Not Found: {name}", "text/plain", System.Text.Encoding.UTF8);
    }
}
