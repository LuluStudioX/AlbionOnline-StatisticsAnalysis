using Serilog;
using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.GameFileData;
using StatisticsAnalysisTool.Common.UserSettings;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Models.BindingModel;
using StatisticsAnalysisTool.Models.NetworkModel;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Operations.Responses;
using StatisticsAnalysisTool.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace StatisticsAnalysisTool.Network.Manager;

// Session/cluster identification, current-island resolution and session-suggestion application for IslandController.
public partial class IslandController
{
    public void HandleIslandClusterEntry(ClusterInfo cluster)
    {
        if (cluster.MapType != MapType.Island) return;
        _sessionIslandName = cluster.InstanceName;
        _sessionWorldMapDataType = cluster.WorldMapDataType;
        _sessionSourceClusterIndex = cluster.SourceClusterIndex;
        _sessionOwner = SettingsController.CurrentSettings.MainTrackingCharacterName
            ?? _trackingController?.EntityController?.LocalUserData?.Username;
        // Only yield baselines reset on entry; _collectedTilesAwaitingReplant persists so re-joining an
        // already-handled island does not re-count its existing plantings as consumed.
        _lastItemQty.Clear();
        _lastJournalQty.Clear();
        Log.Information("[IslandController] Entered island cluster: name={Name} wmd={Wmd} src={Src} owner={Owner}",
            _sessionIslandName, _sessionWorldMapDataType, _sessionSourceClusterIndex, _sessionOwner);

        var island = FindCurrentIsland();
        if (island != null)
        {
            island.LastVisited = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(_sessionSourceClusterIndex)
                && !string.Equals(island.SourceClusterIndex, _sessionSourceClusterIndex, StringComparison.OrdinalIgnoreCase))
            {
                island.SourceClusterIndex = _sessionSourceClusterIndex;
            }
            if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType)
                && !string.Equals(island.WorldMapDataType, _sessionWorldMapDataType, StringComparison.OrdinalIgnoreCase))
            {
                island.WorldMapDataType = _sessionWorldMapDataType;
            }
            island.UpdateModificationDate();
            _ = SaveToFileAsync();
        }
    }

    private Island.Island FindCurrentIsland()
    {
        var name = ClusterController.CurrentCluster?.InstanceName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        lock (_islandsLock)
        {
            return FindCurrentIslandNoLock(name);
        }
    }

    // Must be called with _islandsLock already held.
    private Island.Island FindCurrentIslandNoLock(string name = null)
    {
        name ??= ClusterController.CurrentCluster?.InstanceName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        // 1. Exact name match — only when unambiguous (multiple same-named islands fall through to cluster index).
        var nameMatches = _islands
            .Where(i => !string.IsNullOrWhiteSpace(i.Name)
                     && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nameMatches.Count == 1) return nameMatches[0];

        // 2. GUID match — scoped to same-named islands to prevent cross-island GUID pollution.
        if (!string.IsNullOrWhiteSpace(_sessionSourceClusterIndex))
        {
            var pool = _islands.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var srcMatches = pool
                .Where(i => string.Equals(i.SourceClusterIndex, _sessionSourceClusterIndex, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (srcMatches.Count == 1) return srcMatches[0];
        }

        // 3. WMD match scoped to same-named islands.
        if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType))
        {
            var wmdMatches = _islands
                .Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(i.WorldMapDataType, _sessionWorldMapDataType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (wmdMatches.Count == 1) return wmdMatches[0];
        }

        // 4. City from WMD biome — among same-named Player islands only.
        if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType))
        {
            var sessionCity = ParseCityFromWorldMapDataType(_sessionWorldMapDataType);
            if (!string.IsNullOrWhiteSpace(sessionCity))
            {
                var cityMatches = _islands
                    .Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(i.City, sessionCity, StringComparison.OrdinalIgnoreCase)
                             && i.IslandType == IslandType.Player)
                    .ToList();
                if (cityMatches.Count == 1) return cityMatches[0];
            }
        }

        // 5. Partial name + city — handles app name "OrangeZones Lymhurst" vs game instance "OrangeZones".
        if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType))
        {
            var sessionCity5 = ParseCityFromWorldMapDataType(_sessionWorldMapDataType);
            if (!string.IsNullOrWhiteSpace(sessionCity5))
            {
                var partialMatches = _islands
                    .Where(i => !string.IsNullOrWhiteSpace(i.Name)
                             && i.IslandType == IslandType.Player
                             && string.Equals(i.City, sessionCity5, StringComparison.OrdinalIgnoreCase)
                             && (i.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                                 || name.StartsWith(i.Name, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (partialMatches.Count == 1) return partialMatches[0];
            }
        }

        // 6. SourceClusterIndex alone — last resort when name is completely mismatched but island
        //    was previously identified and had SourceClusterIndex backfilled.
        if (!string.IsNullOrWhiteSpace(_sessionSourceClusterIndex))
        {
            var srcOnlyMatches = _islands
                .Where(i => string.Equals(i.SourceClusterIndex, _sessionSourceClusterIndex, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (srcOnlyMatches.Count == 1) return srcOnlyMatches[0];
        }

        return null;
    }

    private bool TryBackfillClusterIdentifiers(Island.Island island)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(_sessionSourceClusterIndex)
            && !string.Equals(island.SourceClusterIndex, _sessionSourceClusterIndex, StringComparison.OrdinalIgnoreCase))
        {
            island.SourceClusterIndex = _sessionSourceClusterIndex;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(_sessionWorldMapDataType)
            && !string.Equals(island.WorldMapDataType, _sessionWorldMapDataType, StringComparison.OrdinalIgnoreCase))
        {
            island.WorldMapDataType = _sessionWorldMapDataType;
            changed = true;
        }
        return changed;
    }

    public void AutoSelectCurrentIsland()
    {
        var name = ClusterController.CurrentCluster?.InstanceName?.Trim();

        Island.Island match;
        lock (_islandsLock)
            match = FindCurrentIslandNoLock(name);

        if (match == null) return;

        // Backfill SourceClusterIndex and WorldMapDataType so future visits resolve via step 2.
        if (TryBackfillClusterIdentifiers(match))
        {
            match.UpdateModificationDate();
            _ = SaveToFileAsync();
            Log.Information("[IslandController] Backfilled cluster identifiers on '{Name}' after auto-select.", match.Name);
        }

        var islandId = match.Id;
        var islandName = match.Name;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var bindings = _mainWindowViewModel?.IslandBindings;
            if (bindings == null) return;

            // Resolve the entry ON the UI thread: a binding rebuild can replace the entry instance
            // between cluster entry and this tick. Selecting a captured (now-orphaned) instance leaves
            // the list highlighting the wrong row — or none — so look it up against the live collection.
            var entry = bindings.Islands?.FirstOrDefault(e => e.IslandId == islandId);
            if (entry == null) return;

            bindings.SelectedIsland = entry;
            Log.Information("[IslandController] Auto-selected island '{Name}' on cluster entry.", islandName);
        });
    }

    public void OnIslandManuallySelected(Guid islandId)
    {
        Island.Island island;
        lock (_islandsLock)
        {
            island = _islands.FirstOrDefault(i => i.Id == islandId);
        }

        if (island == null) return;

        // Only backfill session identifiers when the selected island matches the current session island name.
        // Prevents stamping the wrong island's GUID onto an unrelated island the user clicks while visiting elsewhere.
        if (string.IsNullOrWhiteSpace(_sessionIslandName)
            || !string.Equals(island.Name, _sessionIslandName, StringComparison.OrdinalIgnoreCase))
            return;

        if (!TryBackfillClusterIdentifiers(island)) return;

        island.UpdateModificationDate();
        _ = SaveToFileAsync();
        Log.Information("[IslandController] Backfilled cluster identifiers on '{Name}' after manual selection.", island.Name);
    }

    private static bool IsIslandInRoyalCity(Island.Island island)
    {
        if (island == null) return false;
        var city = island.City ?? string.Empty;
        var biome = island.Biome ?? string.Empty;
        return city.IndexOf("royal", StringComparison.OrdinalIgnoreCase) >= 0
               || biome.IndexOf("royal", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public IslandSessionSuggestion BuildSessionSuggestion()
    {
        if (string.IsNullOrWhiteSpace(_sessionIslandName) && _sessionBuildingCounts.IsEmpty)
            return _lastIslandSuggestion;

        var plotCounts = new Dictionary<PlotType, int>();
        foreach (var (uniqueName, count) in _sessionBuildingCounts)
        {
            if (TryResolveIslandPlotType(uniqueName, out var plotType))
                plotCounts[plotType] = plotCounts.TryGetValue(plotType, out var existing) ? existing + count : count;
        }

        var suggestion = new IslandSessionSuggestion(
            _sessionIslandName ?? string.Empty,
            _sessionOwner ?? string.Empty,
            _sessionWorldMapDataType ?? string.Empty,
            _sessionHasPremium,
            plotCounts,
            ParseCityFromWorldMapDataType(_sessionWorldMapDataType),
            ParseTierFromWorldMapDataType(_sessionWorldMapDataType),
            ParseIslandTypeFromWorldMapDataType(_sessionWorldMapDataType),
            _sessionSourceClusterIndex ?? string.Empty
        );

        if (!string.IsNullOrWhiteSpace(suggestion.City))
            _lastIslandSuggestion = suggestion;

        return suggestion;
    }

    private static bool IsIslandBuildingUniqueName(string uniqueName)
    {
        var upper = uniqueName.ToUpperInvariant();
        return upper.StartsWith("ISLAND_") || upper.StartsWith("HOUSE_");
    }

    public async Task ApplyOrSuggestSessionAsync(IslandSessionSuggestion suggestion)
    {
        if (suggestion == null) return;

        Log.Information("[IslandController] ApplyOrSuggest: name='{Name}' owner='{Owner}' plots={PlotCount}",
            suggestion.IslandName, suggestion.Owner, suggestion.DetectedPlotCounts.Count);

        var matchedIsland = FindIslandForSuggestion(suggestion);

        if (matchedIsland != null)
        {
            var metaChanged = false;
            if (string.IsNullOrWhiteSpace(matchedIsland.City) && !string.IsNullOrWhiteSpace(suggestion.City))
            {
                matchedIsland.City = suggestion.City;
                if (string.IsNullOrWhiteSpace(matchedIsland.Biome))
                    matchedIsland.Biome = IslandMapping.CityToDefaultBiome(suggestion.City);
                metaChanged = true;
            }
            if (string.IsNullOrWhiteSpace(matchedIsland.Owner) && !string.IsNullOrWhiteSpace(suggestion.Owner))
            { matchedIsland.Owner = suggestion.Owner; metaChanged = true; }
            if (matchedIsland.Tier <= 0 && suggestion.Tier > 0)
            { matchedIsland.Tier = suggestion.Tier; metaChanged = true; }
            if (matchedIsland.IslandType == IslandType.Other && suggestion.IslandType != IslandType.Other)
            { matchedIsland.IslandType = suggestion.IslandType; metaChanged = true; }
            if (string.IsNullOrWhiteSpace(matchedIsland.SourceClusterIndex) && !string.IsNullOrWhiteSpace(suggestion.SourceClusterIndex))
            { matchedIsland.SourceClusterIndex = suggestion.SourceClusterIndex; metaChanged = true; }

            var plotsChanged = false;
            if ((matchedIsland.Plots == null || matchedIsland.Plots.Count == 0)
                && suggestion.DetectedPlotCounts.Count > 0)
            {
                foreach (var (plotType, count) in suggestion.DetectedPlotCounts)
                    matchedIsland.AddPlot(new Island.IslandPlot(plotType, count));

                matchedIsland.HasPremium = matchedIsland.HasPremium || suggestion.HasPremium;
                plotsChanged = true;
                Log.Information("[IslandController] Auto-applied {Count} plot types to island '{Name}'.",
                    suggestion.DetectedPlotCounts.Count, matchedIsland.Name);
            }
            else if (suggestion.DetectedPlotCounts.Count > 0)
            {
                Log.Information("[IslandController] Island '{Name}' already has plots, skipping auto-apply: existing={PlotCount} detected={DetectedCount}",
                    matchedIsland.Name, matchedIsland.Plots?.Count ?? 0, suggestion.DetectedPlotCounts.Count);
            }

            if (metaChanged || plotsChanged)
            {
                matchedIsland.UpdateModificationDate();
                await SaveToFileAsync();
                RefreshBindingsAsync();
            }
        }
        else
        {
            Log.Information("[IslandController] No island matched for name='{Name}' owner='{Owner}'. Islands in list: {IslandList}",
                suggestion.IslandName, suggestion.Owner,
                string.Join(", ", Islands.Select(i => $"'{i.Name}'(owner='{i.Owner}')")));
        }

        await Task.CompletedTask;
    }

    private Island.Island FindIslandForSuggestion(IslandSessionSuggestion suggestion)
    {
        lock (_islandsLock)
        {
            // 1. SourceClusterIndex exact match — most reliable (unique GUID per island instance).
            if (!string.IsNullOrWhiteSpace(suggestion.SourceClusterIndex))
            {
                var bySrc = _islands.FirstOrDefault(i =>
                    string.Equals(i.SourceClusterIndex, suggestion.SourceClusterIndex, StringComparison.OrdinalIgnoreCase));
                if (bySrc != null) return bySrc;
            }

            // 2. Name + city — unambiguous when name is unique OR city disambiguates same-name islands.
            if (!string.IsNullOrWhiteSpace(suggestion.IslandName))
            {
                if (!string.IsNullOrWhiteSpace(suggestion.City))
                {
                    var byNameCity = _islands.FirstOrDefault(i =>
                        string.Equals(i.Name, suggestion.IslandName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(i.City, suggestion.City, StringComparison.OrdinalIgnoreCase));
                    if (byNameCity != null) return byNameCity;
                }

                var byNameOnly = _islands.Where(i =>
                    string.Equals(i.Name, suggestion.IslandName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byNameOnly.Count == 1) return byNameOnly[0];
            }

            // 3. Single player island for owner.
            if (!string.IsNullOrWhiteSpace(suggestion.Owner))
            {
                var byOwner = _islands.Where(i =>
                    string.Equals(i.Owner, suggestion.Owner, StringComparison.OrdinalIgnoreCase)
                    && i.IslandType == IslandType.Player).ToList();
                if (byOwner.Count == 1) return byOwner[0];
            }

            return null;
        }
    }

    public static string ParseCityFromWorldMapDataType(string worldMapDataType)
    {
        if (string.IsNullOrWhiteSpace(worldMapDataType)) return string.Empty;
        var upper = worldMapDataType.ToUpperInvariant();
        // Full city name matches (guild islands, named clusters)
        if (upper.Contains("BRIDGEWATCH"))  return "Bridgewatch";
        if (upper.Contains("LYMHURST"))     return "Lymhurst";
        if (upper.Contains("MARTLOCK"))     return "Martlock";
        if (upper.Contains("FORTSTERLING") || upper.Contains("FORT_STERLING") || upper.Contains("STERLING")) return "Fort Sterling";
        if (upper.Contains("THETFORD"))     return "Thetford";
        if (upper.Contains("CAERLEON"))     return "Caerleon";
        if (upper.Contains("BRECILIEN") || upper.Contains("_MI_") || upper.Contains("MISTS")) return "Brecilien";
        // Biome code matches — short codes (ISL_ST_AUTO) and full words (ISLAND-PLAYER-STEPPE-0001f)
        if (upper.Contains("_ST_") || upper.Contains("STEPPE")) return "Bridgewatch";
        if (upper.Contains("_FR_") || upper.Contains("FOREST")) return "Lymhurst";
        if (upper.Contains("_SW_") || upper.Contains("SWAMP")) return "Thetford";
        if (upper.Contains("_MN_") || upper.Contains("MOUNTAIN")) return "Fort Sterling";
        if (upper.Contains("_HL_DEAD") || upper.Contains("DEAD")) return "Caerleon";
        if (upper.Contains("_HL_") || upper.Contains("HIGHLAND")) return "Martlock";
        return string.Empty;
    }

    public static int ParseTierFromWorldMapDataType(string worldMapDataType)
    {
        if (string.IsNullOrWhiteSpace(worldMapDataType)) return 6;
        var upper = worldMapDataType.ToUpperInvariant();
        for (var t = 6; t >= 1; t--)
        {
            if (upper.Contains($"_T{t}_") || upper.Contains($"_T{t}NON") || upper.EndsWith($"_T{t}"))
                return t;
        }
        return 6;
    }

    public static IslandType ParseIslandTypeFromWorldMapDataType(string worldMapDataType)
    {
        if (string.IsNullOrWhiteSpace(worldMapDataType)) return IslandType.Player;
        var upper = worldMapDataType.ToUpperInvariant();
        if (upper.Contains("GUILD")) return IslandType.Guild;
        return IslandType.Player;
    }
}
