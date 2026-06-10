using System;
using System.Collections.Generic;
using System.Linq;

namespace StatisticsAnalysisTool.Island;

/// <summary>
/// Resolves live laborer snapshots to house plot slots across an entire island in a single pass.
/// A snapshot is claimed at most once globally, so one laborer can never light more than one card.
/// Match priority: laborer name (unique per laborer) > type+tier > type-only. Greedy by count, so
/// several identical laborers (same type+tier, no name) still light the correct number of slots.
/// House plots only — farmable plots track state from their own collection timer, not from snapshots.
/// </summary>
public static class IslandLaborerResolver
{
    /// <param name="positionAffinity">
    /// Optional per-(plot, snapshot) distance score (smaller = closer). When supplied, ties among several
    /// identical un-named same-type laborers are broken by preferring the snapshot physically nearest the
    /// plot's slot, so a laborer can't bind the wrong house's card (G8c). Null disables the tiebreak and
    /// falls back to greedy-by-detection-order.
    /// </param>
    public static IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, LaborerSnapshot>> Resolve(
        IReadOnlyList<IslandPlot> housePlots,
        IReadOnlyList<LaborerSnapshot> snapshots,
        Func<IslandPlot, LaborerSnapshot, double?> positionAffinity = null)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<int, LaborerSnapshot>>();
        if (housePlots == null || housePlots.Count == 0 || snapshots == null || snapshots.Count == 0)
            return result;

        var plotById = housePlots.ToDictionary(p => p.Id);

        // Flatten every configured house slot into a candidate keyed by (plotId, slot).
        var slots = new List<SlotKey>();
        foreach (var plot in housePlots)
        {
            if (plot.PlotType != PlotType.House) continue;
            var cfg = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
            for (var slot = 1; slot <= 3; slot++)
            {
                var name = cfg.TryGetValue(LaborerConfigHelper.LaborerNameKey(slot), out var nv)
                    ? LaborerConfigHelper.NormalizeLaborerFullName(nv) : string.Empty;
                var type = cfg.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var tv)
                    && !string.IsNullOrWhiteSpace(tv)
                    && !string.Equals(tv, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase)
                    ? LaborerConfigHelper.NormalizeLaborerType(tv) : string.Empty;
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(type)) continue;

                int? tier = null;
                if (cfg.TryGetValue(LaborerConfigHelper.JournalTierKey(slot), out var tierText))
                {
                    var digits = new string(tierText.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out var t)) tier = t;
                }
                slots.Add(new SlotKey(plot.Id, slot, name, type, tier));
            }
        }
        if (slots.Count == 0) return result;

        // Newest snapshot first: on a tier upgrade the laborer building respawns with a new ObjectId,
        // leaving a stale pre-upgrade snapshot with the same name. Preferring the highest DetectionOrder
        // claims the live (upgraded, on-job) snapshot instead of the stale one.
        var ordered = snapshots.OrderByDescending(s => s.DetectionOrder).ToList();

        var claimed = new HashSet<LaborerSnapshot>(ReferenceEqualityComparer.Instance);
        var assigned = new Dictionary<(Guid PlotId, int Slot), LaborerSnapshot>();

        // Pass 1: name — strong identity, unique per laborer.
        foreach (var s in slots.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            var snap = ordered.FirstOrDefault(x => !claimed.Contains(x)
                && string.Equals(LaborerConfigHelper.NormalizeLaborerFullName(x.FullName), s.Name, StringComparison.OrdinalIgnoreCase));
            if (snap != null)
            {
                claimed.Add(snap);
                assigned[(s.PlotId, s.Slot)] = snap;
            }
        }

        // Pass 2a: type + tier — greedy by count (several identical laborers each pop one).
        foreach (var s in slots.Where(s => !assigned.ContainsKey((s.PlotId, s.Slot))
                                        && !string.IsNullOrWhiteSpace(s.Type) && s.Tier.HasValue))
        {
            var snap = PickCandidate(s, ordered, claimed, plotById, positionAffinity,
                x => string.Equals(LaborerConfigHelper.NormalizeLaborerType(x.LaborerType), s.Type, StringComparison.OrdinalIgnoreCase)
                     && x.BuildingTier == s.Tier.Value);
            if (snap != null)
            {
                claimed.Add(snap);
                assigned[(s.PlotId, s.Slot)] = snap;
            }
        }

        // Pass 2b: type-only — leftover slots; tier mismatch tolerated (laborer upgraded since config).
        foreach (var s in slots.Where(s => !assigned.ContainsKey((s.PlotId, s.Slot))
                                        && !string.IsNullOrWhiteSpace(s.Type)))
        {
            var snap = PickCandidate(s, ordered, claimed, plotById, positionAffinity,
                x => string.Equals(LaborerConfigHelper.NormalizeLaborerType(x.LaborerType), s.Type, StringComparison.OrdinalIgnoreCase));
            if (snap != null)
            {
                claimed.Add(snap);
                assigned[(s.PlotId, s.Slot)] = snap;
            }
        }

        foreach (var grp in assigned.GroupBy(kv => kv.Key.PlotId))
            result[grp.Key] = grp.ToDictionary(kv => kv.Key.Slot, kv => kv.Value);

        return result;
    }

    // Selects the snapshot for a slot among unclaimed type-matching candidates. With a position-affinity
    // scorer and a known plot, prefers the physically nearest candidate (G8c); otherwise keeps the original
    // greedy-by-detection-order pick (ordered is already sorted newest-first).
    private static LaborerSnapshot PickCandidate(
        SlotKey s,
        List<LaborerSnapshot> ordered,
        HashSet<LaborerSnapshot> claimed,
        Dictionary<Guid, IslandPlot> plotById,
        Func<IslandPlot, LaborerSnapshot, double?> positionAffinity,
        Func<LaborerSnapshot, bool> typeMatch)
    {
        var candidates = ordered.Where(x => !claimed.Contains(x) && typeMatch(x)).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1 || positionAffinity == null || !plotById.TryGetValue(s.PlotId, out var plot))
            return candidates[0];

        LaborerSnapshot best = null;
        var bestScore = double.MaxValue;
        foreach (var c in candidates)
        {
            var score = positionAffinity(plot, c);
            if (!score.HasValue) continue;
            if (score.Value < bestScore)
            {
                bestScore = score.Value;
                best = c;
            }
        }
        // No candidate had a usable position score — fall back to the greedy newest-first pick.
        return best ?? candidates[0];
    }

    private readonly record struct SlotKey(Guid PlotId, int Slot, string Name, string Type, int? Tier);
}
