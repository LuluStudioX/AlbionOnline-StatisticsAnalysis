using StatisticsAnalysisTool.Properties;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Serilog;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;

namespace StatisticsAnalysisTool.Common;

internal static class ImageController
{
    private static readonly HttpClient httpClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
    private static readonly string ItemImagesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Settings.Default.ImageResources);
    private static readonly string SpellImagesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Settings.Default.SpellImageResources);

    private static ConcurrentDictionary<string, BitmapImage> downloading = new();

    #region Item image

    public static BitmapImage GetItemImage(string uniqueName = null, int pixelHeight = 100, int pixelWidth = 100, bool freeze = false)
    {
        string defaultImagePath = @"pack://application:,,,/" + Assembly.GetExecutingAssembly().GetName().Name + ";component/" + "Resources/Trash.png";
        try
        {
            Serilog.Log.Debug("[ImageController] GetItemImage called for uniqueName={name}", uniqueName);
            try
            {
                var dir = ItemImagesDirectory;
                bool exists = Directory.Exists(dir);
                int filesCount = 0;
                try { filesCount = exists ? Directory.GetFiles(dir, "*.png", SearchOption.TopDirectoryOnly).Length : 0; } catch { }
                Serilog.Log.Debug("[ImageController] ItemImagesDirectory={dir} exists={exists} pngFiles={count}", dir, exists, filesCount);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[ImageController] Error while inspecting ItemImagesDirectory");
            }
            if (string.IsNullOrEmpty(uniqueName) || downloading.ContainsKey(uniqueName))
            {
                Serilog.Log.Debug("[ImageController] GetItemImage returning default for empty name or in-progress download: {name}", uniqueName);
                return CreateBitmapImage(defaultImagePath);
            }

            var localFilePath = Path.Combine(ItemImagesDirectory, uniqueName + ".png");

            if (File.Exists(localFilePath))
            {
                var img = GetImageFromLocal(localFilePath, pixelHeight, pixelWidth, freeze);
                Serilog.Log.Debug("[ImageController] Local file exists for {path}, decoded? {hasImage}", localFilePath, img != null);
                if (img != null)
                {
                    Serilog.Log.Debug("[ImageController] Returning local image for {name}", uniqueName);
                    return img;
                }
                // if local file exists but failed to decode, try re-download and overwrite
                try
                {
                    File.Delete(localFilePath);
                }
                catch { }
            }

            // download from web and cache
            var webUrl = $"https://render.albiononline.com/v1/item/{uniqueName}";
            var downloaded = FetchImageFromWebAndCache(webUrl, uniqueName, localFilePath, pixelHeight, pixelWidth, freeze);
            Serilog.Log.Debug("[ImageController] Download attempted for {name}, success? {hasImage}", uniqueName, downloaded != null);
            if (downloaded != null)
            {
                Serilog.Log.Debug("[ImageController] Returning downloaded image for {name}", uniqueName);
                return downloaded;
            }
            // fallback to previous behavior: return default
            return CreateBitmapImage(defaultImagePath);
        }
        catch
        {
            return CreateBitmapImage(defaultImagePath);
        }
    }

    private static BitmapImage CreateBitmapImage(string path)
    {
        return new BitmapImage(new Uri(path, UriKind.Absolute));
    }

    public static async Task<int> LocalImagesCounterAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                return Directory.Exists(ItemImagesDirectory) ? Directory.GetFiles(ItemImagesDirectory, "*", SearchOption.TopDirectoryOnly).Length : 0;
            }
            catch
            {
                return 0;
            }
        });
    }

    #endregion

    #region Spell image

    public static BitmapImage GetSpellImage(string uniqueName = null, int pixelHeight = 100, int pixelWidth = 100, bool freeze = false)
    {
        try
        {
            if (string.IsNullOrEmpty(uniqueName))
            {
                return null;
            }

            BitmapImage image;
            var localFilePath = Path.Combine(SpellImagesDirectory, uniqueName + ".png");

            if (File.Exists(localFilePath))
            {
                image = GetImageFromLocal(localFilePath, pixelHeight, pixelWidth, freeze);
                if (image != null)
                {
                    return image;
                }
                try { File.Delete(localFilePath); } catch { }
            }
            var webUrl = $"https://render.albiononline.com/v1/spell/{uniqueName}";
            var downloaded = FetchImageFromWebAndCache(webUrl, uniqueName, localFilePath, pixelHeight, pixelWidth, freeze);
            return downloaded;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Utilities
    private static BitmapImage GetImageFromLocal(string localResourcePath, int pixelHeight, int pixelWidth, bool freeze)
    {
        try
        {
            // Use a FileStream + OnLoad to force synchronous decoding and allow closing the file afterwards.
            // This is more robust than setting UriSource which can defer decoding and cause "No imaging component" or file-lock issues.
            using var fs = new FileStream(localResourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // decode now
            bmp.DecodePixelHeight = pixelHeight;
            bmp.DecodePixelWidth = pixelWidth;
            bmp.StreamSource = fs;
            bmp.EndInit();

            if (freeze)
            {
                bmp.Freeze();
            }

            return bmp;
        }
        catch (Exception e)
        {
            // Log detailed info for diagnostics and return null so callers can fallback to default image
            ConsoleManager.WriteLineForError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
            Log.Error(e, "ImageController.GetImageFromLocal failed for {path}: {message}", localResourcePath, e.Message);
            return null;
        }
    }

    private static BitmapImage FetchImageFromWebAndCache(string webPath, string uniqueName, string localFilePath, int pixelHeight, int pixelWidth, bool freeze)
    {
        if (string.IsNullOrEmpty(webPath)) return null;

        try
        {
            // Ensure directory exists
            var dir = Path.GetDirectoryName(localFilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

            byte[] bytes = null;
            try
            {
                // synchronous fetch (helper). This avoids DownloadCompleted handlers and ensures we store valid image bytes
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                bytes = httpClient.GetByteArrayAsync(webPath, cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception httpEx)
            {
                Log.Warning(httpEx, "Failed to download image from {url}", webPath);
                return null;
            }

            if (bytes == null || bytes.Length == 0)
            {
                Log.Warning("Downloaded zero-length image from {url}", webPath);
                return null;
            }

            // Save raw bytes to disk (so cache contains original image)
            try
            {
                // Validate common image signatures before saving
                if (IsValidImageBytes(bytes))
                {
                    File.WriteAllBytes(localFilePath, bytes);
                }
                else
                {
                    Log.Warning("Downloaded content for {url} does not appear to be a valid image", webPath);
                }
            }
            catch (Exception writeEx)
            {
                Log.Warning(writeEx, "Failed to write image to {path}", localFilePath);
            }

            // Create BitmapImage from bytes (OnLoad) to avoid deferred decoding
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = pixelHeight;
            bmp.DecodePixelWidth = pixelWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            if (freeze)
            {
                try { bmp.Freeze(); } catch { Log.Debug("Could not freeze bitmap for {name}", uniqueName); }
            }

            return bmp;
        }
        catch (Exception e)
        {
            Log.Error(e, "FetchImageFromWebAndCache failed for {url}", webPath);
            return null;
        }
    }

    private static bool IsValidImageBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 8) return false;
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;
        // JPG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;
        // GIF: 47 49 46 38
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38) return true;
        return false;
    }

    private static void SaveImageLocal(BitmapSource image, string uniqueName, string localFilePath, string localDirectory)
    {
        // Deprecated: older save-by-listening-to-DownloadCompleted approach was unreliable.
        // Keep method for compatibility but attempt a best-effort synchronous save if we have pixel data.
        try
        {
            if (!DirectoryController.CreateDirectoryWhenNotExists(localDirectory) && !Directory.Exists(localDirectory))
            {
                return;
            }

            if (image is BitmapSource bmp)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(fileStream);
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "SaveImageLocal failed for {path}", localFilePath);
        }
    }

    private static BitmapImage SetImage(string webPath, string uniqueName, int pixelHeight, int pixelWidth, bool freeze)
    {
        if (webPath == null)
        {
            return null;
        }

        try
        {
            var userImage = new BitmapImage
            {
                CacheOption = BitmapCacheOption.OnDemand
            };

            downloading.TryAdd(uniqueName, userImage);
            userImage.BeginInit();
            userImage.DecodePixelHeight = pixelHeight;
            userImage.DecodePixelWidth = pixelWidth;
            userImage.UriSource = new Uri(webPath);
            userImage.EndInit();

            if (freeze)
            {
                userImage.Freeze();
            }

            return userImage;
        }
        catch (Exception e)
        {
            DebugConsole.WriteError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
            Log.Error($"{MethodBase.GetCurrentMethod()?.DeclaringType}: {e.Message}");
            return null;
        }
    }

    #endregion
}