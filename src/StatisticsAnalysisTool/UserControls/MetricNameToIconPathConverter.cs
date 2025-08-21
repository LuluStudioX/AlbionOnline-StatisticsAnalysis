using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Serilog;
using System.Collections.Concurrent;
using StatisticsAnalysisTool.Common;
using System.Collections.Generic;
using System.IO;
using StatisticsAnalysisTool.Properties;
using System.Linq;

namespace StatisticsAnalysisTool.UserControls;

public class MetricNameToIconPathConverter : IValueConverter
{
    // simple in-memory cache to avoid recreating BitmapImage instances repeatedly
    private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new ConcurrentDictionary<string, BitmapImage>(StringComparer.Ordinal);
    // Cached weapon candidate unique names (file names in ImageResources)
    private static readonly object _weaponCandidatesLock = new object();
    private static string[] _weaponCandidates = null;
    // Random number generator for selecting random weapons. Protected by _randLock for thread-safety.
    private static readonly Random _rand = new Random();
    private static readonly object _randLock = new object();
    // Recent picks to avoid duplicates across quick successive random requests
    private static readonly object _recentPicksLock = new object();
    private static readonly Queue<string> _recentPicks = new Queue<string>(capacity: 64);
    private const int RecentPicksCapacity = 64;
    // Short cooldown to avoid repeating expensive random selection on every WPF binding tick
    private static readonly TimeSpan RandomPickCooldown = TimeSpan.FromSeconds(5);
    private static DateTime _lastRandomPickTimestamp = DateTime.MinValue;
    private static string _lastRandomPickUniqueName = null;
    // Rate-limit Convert-enter verbose logging to avoid spamming identical messages repeatedly
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastVerboseLogged = new(System.StringComparer.Ordinal);
    // A simple round-robin index used as a fallback to provide stable cycling through candidates
    private static int _roundRobinIndex = 0;
    // Files we should never pick as random weapon icons
    private static readonly HashSet<string> s_blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "empty_icon.png",
        "trash.png",
        "fame.png",
        "silver.png",
        "respec.png",
        "brecilien_standing_coin.png",
        "unknown.png"
    };

    private static void EnsureWeaponCandidatesCached()
    {
        // Fast path: if we've already populated (or attempted to populate) the candidates array,
        // return immediately. We intentionally cache an empty array when no resources are found
        // so we don't repeatedly scan the filesystem on every Convert call.
        if (_weaponCandidates != null) return;

        lock (_weaponCandidatesLock)
        {
            if (_weaponCandidates != null && _weaponCandidates.Length > 0) return;
            try
            {
                var imagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Settings.Default.ImageResources);
                if (!Directory.Exists(imagesDir))
                {
                    // Try a few reasonable alternative locations by walking up the folder tree and checking for an ImageResources folder
                    var baseDir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                    string found = null;
                    var attempts = 0;
                    var cur = baseDir;
                    while (cur != null && attempts < 8)
                    {
                        // Common locations to check relative to repository layout
                        var candidate1 = Path.Combine(cur.FullName, Settings.Default.ImageResources);
                        var candidate2 = Path.Combine(cur.FullName, "ImageResources");
                        var candidate3 = Path.Combine(cur.FullName, "Random folder", "ImageResources");
                        if (Directory.Exists(candidate1)) { found = candidate1; break; }
                        if (Directory.Exists(candidate2)) { found = candidate2; break; }
                        if (Directory.Exists(candidate3)) { found = candidate3; break; }
                        cur = cur.Parent;
                        attempts++;
                    }

                    if (!string.IsNullOrEmpty(found))
                    {
                        imagesDir = found;
                        Serilog.Log.Debug("MetricIcon: EnsureWeaponCandidatesCached found alternative ImageResources directory {dir}", imagesDir);
                    }
                    else
                    {
                        // No ImageResources directory present - remember empty candidates to avoid repeated checks
                        Serilog.Log.Debug("MetricIcon: EnsureWeaponCandidatesCached - ImageResources not found under base path {basePath}", AppDomain.CurrentDomain.BaseDirectory);
                        _weaponCandidates = Array.Empty<string>();
                        return;
                    }
                }

                // Only log when we actually find a directory and perform the file enumeration
                Serilog.Log.Debug("MetricIcon: EnsureWeaponCandidatesCached scanning directory {dir}", imagesDir);

                var files = Directory.GetFiles(imagesDir);
                var fileNames = files.Select(f => Path.GetFileName(f)).ToArray();
                // Emit a small sample of filenames for diagnostics
                try
                {
                    var sample = string.Join(", ", fileNames.Take(12));
                    Serilog.Log.Debug("MetricIcon: ImageResources sample (first 12): {sample}", sample);
                }
                catch { }

                // Start with all filenames
                var candidates = fileNames.ToArray();

                // Exclude known non-item icons from random picks (resources, placeholders)

                // Prefer ItemController metadata if available
                if (ItemController.Items != null && ItemController.Items.Count > 0)
                {
                    var weaponCandidates = fileNames.Where(fileName =>
                    {
                        try
                        {
                            // Item unique names in ItemController do not include the file extension
                            var unique = Path.GetFileNameWithoutExtension(fileName);
                            var item = ItemController.GetItemByUniqueName(unique);
                            if (item == null) return false;
                            if (item.FullItemInformation == null)
                            {
                                ItemController.SetFullItemInfoToItems();
                            }
                            return item.FullItemInformation != null && item.FullItemInformation.GetType().Name.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                        catch
                        {
                            return false;
                        }
                    }).ToArray();

                    if (weaponCandidates.Length > 0)
                    {
                        candidates = weaponCandidates;
                    }
                }

                // Fallback heuristics if nothing found via metadata
                if ((ItemController.Items == null || ItemController.Items.Count == 0) && (candidates == null || candidates.Length == 0))
                {
                    candidates = fileNames.Where(n =>
                                                      {
                                                          var nameNoExt = Path.GetFileNameWithoutExtension(n);
                                                          if (s_blacklist.Contains(n)) return false;
                                                          // Exclude gathering / tool images explicitly - many tool filenames include "TOOL"
                                                          if (nameNoExt.IndexOf("TOOL", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                                                          // Prefer obvious weapon-like names
                                                          return nameNoExt.StartsWith("T", StringComparison.OrdinalIgnoreCase)
                                                                 || nameNoExt.IndexOf("WEAPON", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 || nameNoExt.IndexOf("SWORD", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 || nameNoExt.IndexOf("AXE", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 || nameNoExt.IndexOf("BOW", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 || nameNoExt.IndexOf("DAGGER", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 || nameNoExt.IndexOf("MACE", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 || nameNoExt.IndexOf("HAMMER", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 || nameNoExt.IndexOf("_1H_", StringComparison.OrdinalIgnoreCase) >= 0
                                                                 || nameNoExt.IndexOf("_2H_", StringComparison.OrdinalIgnoreCase) >= 0;
                                                      }).ToArray();

                    // Always filter out any candidates that look like tools or blacklisted names
                    candidates = candidates.Where(n =>
                    {
                        var noExt = Path.GetFileNameWithoutExtension(n);
                        if (string.IsNullOrWhiteSpace(noExt)) return false;
                        if (s_blacklist.Contains(n)) return false;
                        if (noExt.IndexOf("TOOL", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                        // Avoid any obvious non-weapon markers
                        if (noExt.IndexOf("QUESTITEM", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                        return true;
                    }).ToArray();
                }

                _weaponCandidates = candidates ?? Array.Empty<string>();
                Serilog.Log.Debug("MetricIcon: EnsureWeaponCandidatesCached completed, weaponCandidates={count}", _weaponCandidates.Length);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MetricIcon: failed building weapon candidates cache");
                _weaponCandidates = Array.Empty<string>();
                Serilog.Log.Debug(ex, "MetricIcon: EnsureWeaponCandidatesCached exception");
            }
        }
    }

    /// <summary>
    /// Return a concrete random weapon unique name (without file extension) suitable for assigning
    /// to a preview item's Icon so the converter will load a stable image for that item.
    /// Returns null when no candidates are available.
    /// </summary>
    public static string PickRandomWeaponUniqueName()
    {
        try
        {
            EnsureWeaponCandidatesCached();
            if (_weaponCandidates == null || _weaponCandidates.Length == 0) return null;
            string pick;
            lock (_randLock)
            {
                pick = _weaponCandidates[_rand.Next(_weaponCandidates.Length)];
            }
            if (string.IsNullOrWhiteSpace(pick)) return null;
            return Path.GetFileNameWithoutExtension(pick);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MetricIcon: PickRandomWeaponUniqueName failed");
            return null;
        }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            // Emit a single verbose "Convert-enter" message per unique input per process.
            // The previous implementation used a short cooldown which still produced
            // frequent repeated messages under heavy binding ticks. Using TryAdd here
            // makes the log one-shot for the given value/type and prevents spam.
            var valString = value == null ? "null" : (value is string sVal ? sVal : value.ToString());
            var logKey = (value == null ? "null" : value.GetType().Name) + ":" + valString;
            if (_lastVerboseLogged.TryAdd(logKey, DateTime.UtcNow))
            {
                Serilog.Log.Verbose("MetricIcon: Convert-enter valueType={type} value={value}", value == null ? "null" : value.GetType().Name, valString);
            }
        }
        catch { }

        if (value is string name)
        {
            var s = name.Trim();

            // Normalize common overlay icon names early (allow e.g. "FameIcon" or "fame") so we can treat
            // random/weapon requests specially (we don't want to cache the literal "randomweapon" key)
            var keyForLookup = s;
            if (keyForLookup.EndsWith("Icon", StringComparison.OrdinalIgnoreCase))
            {
                keyForLookup = keyForLookup.Substring(0, keyForLookup.Length - "Icon".Length);
            }

            var isRandomWeaponRequest = keyForLookup.Equals("randomweapon", StringComparison.OrdinalIgnoreCase)
                                         || keyForLookup.StartsWith("weapon", StringComparison.OrdinalIgnoreCase);

            // For non-random requests, return cached image when available.
            if (!isRandomWeaponRequest && _cache.TryGetValue(s, out var cached))
            {
                return cached;
            }
            try
            {
                // If it's already a full HTTP(S) URL, return a BitmapImage from it
                if (s.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {

                        var bitmap = new BitmapImage(new Uri(s, UriKind.Absolute));
                        bitmap.Freeze();
                        _cache.TryAdd(s, bitmap);
                        return bitmap;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "MetricIcon: failed loading HTTP URL {url}", s);
                        return null;
                    }
                }

                // If it's a render shorthand like "/img/T6_..." or raw unique id "T6_...", use the ImageController which handles caching
                if (s.StartsWith("/img/", StringComparison.OrdinalIgnoreCase))
                {
                    var id = s.Substring(5);

                    var img = ImageController.GetItemImage(id, 64, 64, true);
                    if (img != null)
                    {
                        _cache.TryAdd(s, img);
                    }
                    return img;
                }

                if (s.StartsWith("T", StringComparison.OrdinalIgnoreCase) && s.Contains("_"))
                {

                    var img = ImageController.GetItemImage(s, 64, 64, true);
                    if (img != null)
                    {
                        _cache.TryAdd(s, img);
                    }
                    return img;
                }

                // Use the already-normalized keyForLookup
                var key = keyForLookup;

                // Fallback: metric-name -> local resource mapping (existing behavior)
                string file = key.ToLowerInvariant() switch
                {
                    "fame" => "fame.png",
                    "silver" => "silver.png",
                    "respec" => "respec.png",
                    "might" => "might.png",
                    "favor" => "favor.png",
                    "faction" => "brecilien_standing_coin.png",
                    _ => "empty_icon.png"
                };

                // Special case: if caller requested a random weapon icon, try to pick one from ImageResources.
                // We intentionally do NOT lookup the cache with the generic request key (e.g. "randomweapon") so
                // repeated requests can return different images. We will cache the concrete image under its uniqueName.
                if (isRandomWeaponRequest)
                {
                    // If we've recently chosen a concrete random pick, prefer reusing it for a short cooldown
                    try
                    {
                        if (!string.IsNullOrEmpty(_lastRandomPickUniqueName) && (DateTime.UtcNow - _lastRandomPickTimestamp) < RandomPickCooldown)
                        {
                            if (_cache.TryGetValue(_lastRandomPickUniqueName, out var cachedBitmap))
                            {
                                return cachedBitmap;
                            }
                            // If it's not in cache, attempt to load it quickly
                            var fastBitmap = StatisticsAnalysisTool.Common.ImageController.GetItemImage(_lastRandomPickUniqueName, 64, 64, true);
                            if (fastBitmap != null)
                            {
                                _cache.TryAdd(_lastRandomPickUniqueName, fastBitmap);
                                return fastBitmap;
                            }
                        }
                    }
                    catch { }
                    try
                    {
                        // First, prefer the player's actual weapon if we have damage meter data
                        try
                        {
                            var mw = StatisticsAnalysisTool.ViewModels.MainWindowViewModel.Instance;
                            var username = mw?.UserTrackingBindings?.Username?.Trim();
                            var dmgBindings = mw?.DamageMeterBindings;

                            // Look in the live damage meter first, then in the snapshot selection if present
                            var dmgList = dmgBindings?.DamageMeter; // ObservableCollection<DamageMeterFragment>
                            // Snapshot damage meter uses a different fragment type
                            List<StatisticsAnalysisTool.DamageMeter.DamageMeterSnapshotFragment> snapshotList = null;
                            if (dmgBindings?.DamageMeterSnapshotSelection?.DamageMeter != null)
                            {
                                snapshotList = dmgBindings.DamageMeterSnapshotSelection.DamageMeter.ToList();
                            }

                            if (!string.IsNullOrEmpty(username) && (dmgList != null || snapshotList != null))
                            {
                                // Try to find the fragment that corresponds to the tracked user (case-insensitive)
                                StatisticsAnalysisTool.DamageMeter.DamageMeterFragment selfFrag = null;
                                StatisticsAnalysisTool.DamageMeter.DamageMeterSnapshotFragment selfSnapFrag = null;
                                if (dmgList != null)
                                {
                                    selfFrag = dmgList.FirstOrDefault(f => string.Equals(f?.Name?.Trim(), username, StringComparison.OrdinalIgnoreCase));
                                }
                                if (selfFrag == null && snapshotList != null)
                                {
                                    selfSnapFrag = snapshotList.FirstOrDefault(f => string.Equals(f?.Name?.Trim(), username, StringComparison.OrdinalIgnoreCase));
                                }

                                var unique = selfFrag?.CauserMainHand?.UniqueName ?? selfSnapFrag?.CauserMainHandItemUniqueName;
                                if (!string.IsNullOrEmpty(unique))
                                {
                                    try
                                    {
                                        // ImageController.GetItemImage expects unique id (without extension)
                                        var bitmap = StatisticsAnalysisTool.Common.ImageController.GetItemImage(unique, 64, 64, true);
                                        Serilog.Log.Debug("MetricIcon: requested image for unique={unique}, found={found}", unique, bitmap != null);
                                        if (bitmap != null)
                                        {
                                            _cache.TryAdd(unique, bitmap);
                                            return bitmap;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Warning(ex, "MetricIcon: failed loading player's weapon image {unique}", unique);
                                    }
                                }
                            }

                            // If we reach here we didn't have a weapon from damage data - fall back to random selection from resources
                            EnsureWeaponCandidatesCached();

                            if (_weaponCandidates != null && _weaponCandidates.Length > 0)
                            {
                                // Try to prefer picks not chosen recently to avoid immediate duplication across multiple random requests
                                var attempts = Math.Min(16, _weaponCandidates.Length);
                                for (int i = 0; i < attempts; i++)
                                {
                                    string pick;
                                    // Hybrid selection: prefer round-robin to give more stable cycling, but fall back to randomness
                                    lock (_randLock)
                                    {
                                        // compute next index using round-robin seeded by Random to avoid always starting at same point
                                        var startIndex = (_roundRobinIndex = (_roundRobinIndex + 1) % _weaponCandidates.Length);
                                        // Attempt a few offsets from the startIndex
                                        var offset = _rand.Next(0, Math.Min(4, _weaponCandidates.Length));
                                        var idx = (startIndex + offset) % _weaponCandidates.Length;
                                        pick = _weaponCandidates[idx];
                                    }

                                    var pickFileName = Path.GetFileName(pick);
                                    if (s_blacklist.Contains(pickFileName)) continue;
                                    var uniqueName = Path.GetFileNameWithoutExtension(pickFileName);

                                    // Avoid recent picks queue
                                    lock (_recentPicksLock)
                                    {
                                        if (_recentPicks.Contains(uniqueName))
                                        {
                                            // skip this pick and try another
                                            continue;
                                        }
                                    }

                                    var bitmap = StatisticsAnalysisTool.Common.ImageController.GetItemImage(uniqueName, 64, 64, true);
                                    Serilog.Log.Debug("MetricIcon: random pick requested image for unique={uniqueName}, found={found}", uniqueName, bitmap != null);
                                    if (bitmap != null)
                                    {
                                        // Cache by concrete id so future requests for that id are fast.
                                        _cache.TryAdd(uniqueName, bitmap);

                                        // record as the last concrete random pick and timestamp to avoid re-running selection repeatedly
                                        _lastRandomPickUniqueName = uniqueName;
                                        _lastRandomPickTimestamp = DateTime.UtcNow;

                                        // record recent pick to avoid immediate duplicates
                                        lock (_recentPicksLock)
                                        {
                                            _recentPicks.Enqueue(uniqueName);
                                            while (_recentPicks.Count > RecentPicksCapacity)
                                            {
                                                _recentPicks.Dequeue();
                                            }
                                        }

                                        return bitmap;
                                    }
                                }
                            }
                        }
                        catch (Exception innerEx)
                        {
                            Log.Warning(innerEx, "MetricIcon: error while attempting to prefer player's weapon");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "MetricIcon: failed selecting random weapon image");
                    }
                }
                string relPath = (file == "brecilien_standing_coin.png" || file == "empty_icon.png")
                    ? $"Assets/{file}"
                    : $"Resources/{file}";
                string packUri = $"pack://application:,,,/StatisticsAnalysisTool;component/{relPath}";
                try
                {
                    var bitmap = new BitmapImage(new Uri(packUri, UriKind.Absolute));
                    bitmap.Freeze(); // Freeze for cross-thread access
                    _cache.TryAdd(s, bitmap);
                    return bitmap;
                }
                catch (Exception ex)
                {
                    // Log the error for debugging
                    Log.Warning(ex, "MetricIcon: failed to load pack resource {packUri}", packUri);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"MetricNameToIconPathConverter failed for '{name}': {ex.Message}");
                return null;
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
