using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Island;

// House / laborer plot matching, slot assignment and de-duplication for IslandController.
// Split from IslandController.cs to keep the main controller focused on session + event handling.
public partial class IslandController
{
    // Detects whether the mixed-use region's house sits at the TOP (alt) or BOTTOM (base) from its
    // real world position, so the small S1/S2 slots render on the opposite end. This replaces the
    // occupancy-only guess, which wrongly pushed S1/S2 down whenever the slot was occupied at all.
    private void TryDetectMixedRegionPlacement(Island island, LaborerSnapshot snapshot)
    {
        if (!snapshot.WorldPosition.HasValue) return;
        var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
        if (layout == null) return;

        var alt = layout.ClassifyMixedRegionHouseAlt(snapshot.WorldPosition.Value.X, snapshot.WorldPosition.Value.Y);
        if (!alt.HasValue || island.MixedRegionAltActive == alt.Value) return;

        island.MixedRegionAltActive = alt.Value;
        island.UpdateModificationDate();
        RequestSaveToFile();
        RefreshBindingsAsync();
        Log.Information("[IslandController] Mixed-region placement detected: island={Island}, altActive={Alt}", island.Name, alt.Value);
    }

    // Resolves a laborer's world position to the nearest LARGE map slot. Houses are large-footprint
    // plots, so the small S1/S2 slots are excluded — a house must never resolve onto them.
    private int? ResolveHouseSlot(Island island, LaborerSnapshot snapshot)
    {
        if (!snapshot.WorldPosition.HasValue) return null;
        var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
        if (layout == null)
        {
            WarnPositionMatchUnavailableOnce(island);
            return null;
        }
        return layout.WorldToNearestSlot(snapshot.WorldPosition.Value.X, snapshot.WorldPosition.Value.Y, requireLarge: true);
    }

    // Guild islands have no calibrated layout yet, so laborer position → slot matching is disabled for them.
    // Warn once per island so the silent null is visible without spamming on every status push (G8a).
    private void WarnPositionMatchUnavailableOnce(Island island)
    {
        if (island == null) return;
        lock (_positionMatchWarnedLock)
        {
            if (!_positionMatchWarnedIslandIds.Add(island.Id)) return;
        }
        Log.Warning("[IslandController] Position-based slot matching unavailable for island '{Name}' " +
                    "(no calibrated layout — guild islands not yet supported); using non-positional matching.",
            island.Name);
    }

    // A house plot's MapSlotIndex (its physical-position number, used by the map AND the "#N" card label)
    // can desync from where its laborers actually stand — seeding/recalibration left stored values as a
    // permutation of the true slots, so the same physical house showed a different number than the one
    // collected. Re-derive each name-matched plot's slot from its live laborers' world positions
    // (majority vote) and apply the corrected, collision-free assignment. Converges in one pass, then
    // makes no further changes (idempotent), so it is safe to run on every status push.
    private void HealHouseMapSlots(Island island,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, LaborerSnapshot>> assignments)
    {
        if (island?.Plots == null || assignments == null || assignments.Count == 0) return;
        var (layout, _) = IslandLayouts.ResolveForIsland(island.IslandType, island.City);
        if (layout == null)
        {
            WarnPositionMatchUnavailableOnce(island);
            return;
        }

        var desired = new Dictionary<Guid, int>();
        foreach (var (plotId, slotMap) in assignments)
        {
            var votes = new Dictionary<int, int>();
            foreach (var snap in slotMap.Values)
            {
                if (snap?.WorldPosition is not { } pos) continue;
                var s = layout.WorldToNearestSlot(pos.X, pos.Y, requireLarge: true);
                if (s.HasValue) votes[s.Value] = votes.GetValueOrDefault(s.Value) + 1;
            }
            if (votes.Count > 0)
                desired[plotId] = votes.Aggregate((a, b) => b.Value > a.Value ? b : a).Key;
        }
        if (desired.Count == 0) return;

        // Only act on a slot a single plot wants (clean bijection); skip contested slots to avoid churn.
        var contested = desired.Values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var changed = false;
        foreach (var (plotId, slot) in desired)
        {
            if (contested.Contains(slot)) continue;
            var plot = island.Plots.FirstOrDefault(p => p.Id == plotId);
            if (plot == null || plot.MapSlotIndex == slot) continue;

            // Free this physical slot from any other house plot so two cards never share a position.
            foreach (var other in island.Plots)
                if (other.Id != plotId && other.PlotType == PlotType.House && other.MapSlotIndex == slot)
                    other.MapSlotIndex = null;

            plot.MapSlotIndex = slot;
            changed = true;
            Log.Information("[IslandController] Healed house map slot from live position: island={Island}, plot#{Plot}, slot={Slot}",
                island.Name, plot.PlotNumber, slot);
        }
        if (changed)
        {
            island.UpdateModificationDate();
            RequestSaveToFile();
        }
    }

    private void TryAutoAssignHousePlotMapSlot(Island island, LaborerSnapshot snapshot)
    {
        var slotIndex = ResolveHouseSlot(island, snapshot);
        if (!slotIndex.HasValue) return;

        // Skip if a house plot already claims this slot.
        if (island.Plots.Any(p => p.PlotType == PlotType.House && p.MapSlotIndex == slotIndex.Value))
            return;

        var unassigned = island.Plots.FirstOrDefault(p =>
            p.PlotType == PlotType.House && !p.MapSlotIndex.HasValue);
        if (unassigned == null) return;

        unassigned.MapSlotIndex = slotIndex.Value;
        island.UpdateModificationDate();
        RequestSaveToFile();
        Log.Information("[IslandController] Auto-assigned house map slot {Slot} from laborer world pos", slotIndex.Value);
        RefreshIslandStatusAsync(island);
    }

    // Non-mutating lookup: the house plot owning this slot, else the first unassigned house plot.
    // The slot is committed by the caller only when an actual config write succeeds (avoids orphan
    // cards that carry a slot number but no laborer).
    private static IslandPlot FindHousePlotBySlot(Island island, int slotIndex)
    {
        var existing = island.Plots.FirstOrDefault(p =>
            p.PlotType == PlotType.House && p.MapSlotIndex == slotIndex);
        if (existing != null) return existing;

        return island.Plots.FirstOrDefault(p =>
            p.PlotType == PlotType.House && !p.MapSlotIndex.HasValue);
    }

    private bool TryEnsureHousePlotConfiguration(Island island, LaborerSnapshot snapshot)
    {
        if (island?.Plots == null || snapshot.BuildingTier <= 0 || string.IsNullOrWhiteSpace(snapshot.LaborerType))
            return false;

        // Name match first: config-stored names are reliable ground truth and survive slot resets.
        // World position match second: assigns MapSlotIndex once name confirms the right plot.
        if (TryMatchHousePlotByLaborerName(island, snapshot))
            return true;

        if (TryMatchHousePlotByWorldPosition(island, snapshot))
            return true;

        return TryEnrichHousePlotByTypeMatch(island, snapshot);
    }

    private bool TryMatchHousePlotByWorldPosition(Island island, LaborerSnapshot snapshot)
    {
        if (!snapshot.WorldPosition.HasValue)
            return false;

        // HousePlotGuid (param 9) is shared across ALL houses on the same island, so it cannot
        // uniquely identify a house. World position is the only per-house discriminator available.
        var slotIndex = ResolveHouseSlot(island, snapshot);
        if (!slotIndex.HasValue)
            return false;

        var slotPlot = FindHousePlotBySlot(island, slotIndex.Value);
        if (slotPlot == null) return false;

        if (HousePlotHasEmptySlot(slotPlot.Configuration))
        {
            // Don't duplicate a laborer that already lives in another card.
            if (!string.IsNullOrWhiteSpace(snapshot.FullName) && IsLaborerNameInAnyOtherHousePlot(island, slotPlot, snapshot.FullName))
                return true;

            if (TryAutofillHousePlot(slotPlot, snapshot))
            {
                slotPlot.MapSlotIndex = slotIndex.Value; // commit slot only after a successful write
                PurgeDuplicateLaborerName(island, slotPlot, snapshot.FullName);
                island.UpdateModificationDate();
                RequestSaveToFile();
                RefreshIslandStatusAsync(island);
                Log.Information("[IslandController] Position-matched house on live detection: island={Island}, laborer={Laborer}, type={Type}, tier=T{Tier}, slot={Slot}",
                    island.Name, snapshot.FullName, snapshot.LaborerType, snapshot.BuildingTier, slotIndex.Value);
                return true;
            }
            return false;
        }

        // Seed model: a manual slot whose type matches but carries no name yet — stamp the live
        // laborer's real name onto it (live truth fills the placeholder). Each laborer fills the
        // first empty-name slot of its type; the written name then blocks the next laborer from it.
        if (TryFillLiveNameOntoSeedSlot(island, slotPlot, snapshot))
        {
            slotPlot.MapSlotIndex = slotIndex.Value;
            PurgeDuplicateLaborerName(island, slotPlot, snapshot.FullName);
            island.UpdateModificationDate();
            RequestSaveToFile();
            RefreshIslandStatusAsync(island);
            Log.Information("[IslandController] Stamped live name on seed slot at position-matched house: island={Island}, laborer={Laborer}, type={Type}, tier=T{Tier}, slot={Slot}",
                island.Name, snapshot.FullName, snapshot.LaborerType, snapshot.BuildingTier, slotIndex.Value);
            return true;
        }

        // Card fully configured for this slot. If the detected laborer no longer matches, the user
        // swapped a laborer — overwrite the stale slot.
        if (!HousePlotMatchesLaborer(slotPlot, snapshot) && !HousePlotMatchesLaborerByName(slotPlot, snapshot))
        {
            if (TryOverwriteHousePlotSlotForSwap(slotPlot, snapshot))
            {
                slotPlot.MapSlotIndex = slotIndex.Value; // commit slot only after a successful write
                PurgeDuplicateLaborerName(island, slotPlot, snapshot.FullName);
                island.UpdateModificationDate();
                RequestSaveToFile();
                RefreshIslandStatusAsync(island);
                Log.Information("[IslandController] Laborer swap detected at position-matched house: island={Island}, laborer={Laborer}, type={Type}, tier=T{Tier}, slot={Slot}",
                    island.Name, snapshot.FullName, snapshot.LaborerType, snapshot.BuildingTier, slotIndex.Value);
            }
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.FullName))
        {
            if (PurgeDuplicateLaborerName(island, slotPlot, snapshot.FullName))
            {
                island.UpdateModificationDate();
                RequestSaveToFile();
            }
        }
        return true;
    }

    private bool TryMatchHousePlotByLaborerName(Island island, LaborerSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.FullName))
            return false;

        var namePlot = island.Plots.FirstOrDefault(p =>
            p.PlotType == PlotType.House && HousePlotMatchesLaborerByName(p, snapshot));
        if (namePlot == null) return false;

        var changed = PurgeDuplicateLaborerName(island, namePlot, snapshot.FullName);

        // Also assign MapSlotIndex from world position when it's missing (e.g. after a slot reset).
        var slotAssigned = false;
        if (!namePlot.MapSlotIndex.HasValue && snapshot.WorldPosition.HasValue)
        {
            var slotIndex = ResolveHouseSlot(island, snapshot);
            if (slotIndex.HasValue && !island.Plots.Any(p => p.MapSlotIndex == slotIndex.Value))
            {
                namePlot.MapSlotIndex = slotIndex.Value;
                changed = true;
                slotAssigned = true;
                Log.Information("[IslandController] Name-matched house re-anchored to slot {Slot} for laborer {Name}", slotIndex.Value, snapshot.FullName);
            }
        }

        if (changed)
        {
            island.UpdateModificationDate();
            RequestSaveToFile();
            // Full binding rebuild needed when slot was re-assigned so cards re-sort and labels update.
            if (slotAssigned)
                RefreshBindingsAsync();
            else
                RefreshIslandStatusAsync(island);
        }

        return true;
    }

    private bool TryEnrichHousePlotByTypeMatch(Island island, LaborerSnapshot snapshot)
    {
        try
        {
            // Secondary: type+name match against already-configured cards (useful on re-visit when position
            // resolves to a card that is already fully filled, so we only need to update tier/name if changed).
            foreach (var plot in island.Plots.Where(p => p.PlotType == PlotType.House))
            {
                var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);

                for (var slot = 1; slot <= 3; slot++)
                {
                    if (!config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var laborerValue)
                        || string.IsNullOrWhiteSpace(laborerValue)
                        || string.Equals(laborerValue, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var configuredType = LaborerConfigHelper.NormalizeLaborerType(laborerValue);
                    var detectedType = LaborerConfigHelper.NormalizeLaborerType(snapshot.LaborerType);
                    if (!string.Equals(configuredType, detectedType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var digits = new string((config.TryGetValue(LaborerConfigHelper.JournalTierKey(slot), out var tierVal) ? tierVal : string.Empty).Where(char.IsDigit).ToArray());
                    var tierChanged = !int.TryParse(digits, out var configuredTier) || configuredTier != snapshot.BuildingTier;
                    var nameKey = LaborerConfigHelper.LaborerNameKey(slot);
                    var storedName = config.TryGetValue(nameKey, out var sn) ? sn : string.Empty;
                    var nameChanged = !string.IsNullOrWhiteSpace(snapshot.FullName)
                        && !string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(storedName),
                            LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName),
                            StringComparison.OrdinalIgnoreCase);

                    // Don't overwrite with a name that already exists in a different house card —
                    // that would duplicate the laborer across two cards.
                    if (nameChanged && IsLaborerNameInAnyOtherHousePlot(island, plot, snapshot.FullName))
                        nameChanged = false;

                    if (tierChanged || nameChanged)
                    {
                        if (tierChanged)
                            config[LaborerConfigHelper.JournalTierKey(slot)] = $"Tier {snapshot.BuildingTier}";
                        if (nameChanged)
                            config[nameKey] = snapshot.FullName;
                        plot.Configuration = LaborerConfigHelper.BuildConfiguration(config);
                        PurgeDuplicateLaborerName(island, plot, snapshot.FullName);
                        island.UpdateModificationDate();
                        RequestSaveToFile();
                        RefreshIslandStatusAsync(island);
                        Log.Information("[IslandController] Enriched house plot config from type-match: island={Island}, laborer={Laborer}, slot={Slot}",
                            island.Name, snapshot.FullName, slot);
                    }
                    else if (!string.IsNullOrWhiteSpace(snapshot.FullName))
                    {
                        // No write needed, but purge stale duplicates if this card is the authority for the name.
                        if (PurgeDuplicateLaborerName(island, plot, snapshot.FullName))
                        {
                            island.UpdateModificationDate();
                            RequestSaveToFile();
                        }
                    }
                    return true; // type (and tier) matched — this snapshot belongs to this plot
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to auto-adjust house tier for island {Island}", island?.Name);
        }

        return false;
    }

    private static bool HousePlotMatchesLaborerByName(IslandPlot plot, LaborerSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.FullName)) return false;
        var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
        var normalizedName = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);
        for (var slot = 1; slot <= 3; slot++)
        {
            if (config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var storedName)
                && !string.IsNullOrWhiteSpace(storedName)
                && string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(storedName), normalizedName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsLaborerNameInAnyOtherHousePlot(Island island, IslandPlot excludePlot, string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        var normalized = LaborerConfigHelper.NormalizeLaborerFullName(fullName);
        foreach (var plot in island.Plots.Where(p => p.PlotType == PlotType.House && p.Id != excludePlot.Id))
        {
            var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
            for (var slot = 1; slot <= 3; slot++)
            {
                if (config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var storedName)
                    && !string.IsNullOrWhiteSpace(storedName)
                    && string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(storedName), normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // Removes fullName from all house plots OTHER than authorityPlot.
    // Returns true if any config was changed.
    private static bool PurgeDuplicateLaborerName(Island island, IslandPlot authorityPlot, string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        var normalized = LaborerConfigHelper.NormalizeLaborerFullName(fullName);
        var changed = false;
        foreach (var plot in island.Plots.Where(p => p.PlotType == PlotType.House && p.Id != authorityPlot.Id))
        {
            var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
            var plotChanged = false;
            for (var slot = 1; slot <= 3; slot++)
            {
                if (!config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var storedName)
                    || string.IsNullOrWhiteSpace(storedName))
                    continue;
                if (!string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(storedName), normalized, StringComparison.OrdinalIgnoreCase))
                    continue;
                config[LaborerConfigHelper.LaborerNameKey(slot)] = string.Empty;
                plotChanged = true;
            }
            if (plotChanged)
            {
                plot.Configuration = LaborerConfigHelper.BuildConfiguration(config);
                changed = true;
                Log.Information("[IslandController] Purged duplicate laborer name '{Name}' from house plot {PlotId}", fullName, plot.Id);
            }
        }
        return changed;
    }

    private static bool HousePlotMatchesLaborer(IslandPlot plot, LaborerSnapshot snapshot)
    {
        var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
        for (var slot = 1; slot <= 3; slot++)
        {
            if (!config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var laborerValue)
                || !config.TryGetValue(LaborerConfigHelper.JournalTierKey(slot), out var tierValue))
                continue;

            var configuredType = LaborerConfigHelper.NormalizeLaborerType(laborerValue);
            var detectedType = LaborerConfigHelper.NormalizeLaborerType(snapshot.LaborerType);
            if (!string.Equals(configuredType, detectedType, StringComparison.OrdinalIgnoreCase))
                continue;

            var digits = new string(tierValue.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var configuredTier) && configuredTier == snapshot.BuildingTier)
                return true;
        }
        return false;
    }

    private static bool HousePlotHasEmptySlot(string configuration)
    {
        var config = LaborerConfigHelper.ParseConfiguration(configuration);
        for (var slot = 1; slot <= 3; slot++)
        {
            if (!config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var laborerValue)
                || string.IsNullOrWhiteSpace(laborerValue)
                || string.Equals(laborerValue, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryAutofillHousePlot(IslandPlot plot, LaborerSnapshot snapshot)
    {
        var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
        for (var slot = 1; slot <= 3; slot++)
        {
            if (config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var laborerValue)
                && !string.IsNullOrWhiteSpace(laborerValue)
                && !string.Equals(laborerValue, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                continue;

            var displayType = LaborerConfigHelper.ToDisplayLaborerType(snapshot.LaborerType);
            config[LaborerConfigHelper.LaborerKey(slot)] = displayType;
            config[LaborerConfigHelper.JournalKey(slot)] = LaborerConfigHelper.GetJournalName(snapshot.LaborerType, displayType);
            config[LaborerConfigHelper.LaborerNameKey(slot)] = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);
            config[LaborerConfigHelper.JournalTierKey(slot)] = $"Tier {snapshot.BuildingTier}";
            plot.Configuration = LaborerConfigHelper.BuildConfiguration(config);
            return true;
        }
        return false;
    }

    // Seed model: writes the live laborer's name onto the first type-matching slot that has no name
    // yet (a manual placeholder). Returns false when no such slot exists, the laborer already owns a
    // slot here, or the name lives in another house plot. Each laborer claims one empty-name slot;
    // the written name then prevents the next same-type laborer from reusing it.
    private static bool TryFillLiveNameOntoSeedSlot(Island island, IslandPlot plot, LaborerSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.FullName) || string.IsNullOrWhiteSpace(snapshot.LaborerType))
            return false;
        if (IsLaborerNameInAnyOtherHousePlot(island, plot, snapshot.FullName))
            return false;

        var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
        var detectedType = LaborerConfigHelper.NormalizeLaborerType(snapshot.LaborerType);
        var detectedName = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);

        for (var slot = 1; slot <= 3; slot++)
        {
            if (!config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var typeVal)
                || string.IsNullOrWhiteSpace(typeVal)
                || string.Equals(typeVal, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(LaborerConfigHelper.NormalizeLaborerType(typeVal), detectedType, StringComparison.OrdinalIgnoreCase))
                continue;

            var existingName = config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var nv) ? nv : string.Empty;
            if (!string.IsNullOrWhiteSpace(existingName))
            {
                // Already this laborer — nothing to do; matching/purge handles it elsewhere.
                if (string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(existingName), detectedName, StringComparison.OrdinalIgnoreCase))
                    return false;
                // A different named laborer owns this slot — skip to the next.
                continue;
            }

            config[LaborerConfigHelper.LaborerNameKey(slot)] = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);
            config[LaborerConfigHelper.JournalTierKey(slot)] = $"Tier {snapshot.BuildingTier}";
            if (!config.ContainsKey(LaborerConfigHelper.JournalKey(slot)))
            {
                var displayType = LaborerConfigHelper.ToDisplayLaborerType(snapshot.LaborerType);
                config[LaborerConfigHelper.JournalKey(slot)] = LaborerConfigHelper.GetJournalName(snapshot.LaborerType, displayType);
            }
            plot.Configuration = LaborerConfigHelper.BuildConfiguration(config);
            return true;
        }

        return false;
    }

    // Overwrites the first slot whose laborer name or type doesn't match the incoming snapshot.
    // Called when a position-matched house has no empty slots but the detected laborer is unknown —
    // indicating the user swapped a laborer. Stale dispatch/loot data is cleared for the replaced slot.
    private static bool TryOverwriteHousePlotSlotForSwap(IslandPlot plot, LaborerSnapshot snapshot)
    {
        var config = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
        var detectedType = LaborerConfigHelper.NormalizeLaborerType(snapshot.LaborerType);
        var detectedName = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);

        for (var slot = 1; slot <= 3; slot++)
        {
            var storedType = config.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var tv)
                ? LaborerConfigHelper.NormalizeLaborerType(tv) : string.Empty;
            var storedName = config.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var nv)
                ? LaborerConfigHelper.NormalizeLaborerFullName(nv) : string.Empty;

            var typeMatches = !string.IsNullOrEmpty(storedType) && string.Equals(storedType, detectedType, StringComparison.OrdinalIgnoreCase);
            var nameMatches = !string.IsNullOrEmpty(storedName) && !string.IsNullOrEmpty(detectedName)
                && string.Equals(storedName, detectedName, StringComparison.OrdinalIgnoreCase);

            if (typeMatches || nameMatches) continue;

            var displayType = LaborerConfigHelper.ToDisplayLaborerType(snapshot.LaborerType);
            config[LaborerConfigHelper.LaborerKey(slot)] = displayType;
            config[LaborerConfigHelper.JournalKey(slot)] = LaborerConfigHelper.GetJournalName(snapshot.LaborerType, displayType);
            config[LaborerConfigHelper.LaborerNameKey(slot)] = LaborerConfigHelper.NormalizeLaborerFullName(snapshot.FullName);
            config[LaborerConfigHelper.JournalTierKey(slot)] = $"Tier {snapshot.BuildingTier}";
            config.Remove(LaborerConfigHelper.DispatchTimeKey(slot));
            config.Remove(LaborerConfigHelper.LootReadyKey(slot));
            plot.Configuration = LaborerConfigHelper.BuildConfiguration(config);
            return true;
        }
        return false;
    }
}
