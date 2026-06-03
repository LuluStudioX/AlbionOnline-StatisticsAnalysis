using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Island;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StatisticsAnalysisTool.Network.Events;

// EVENT [201] FarmableObjectInfo — sent for each farm/pasture/herb plot when entering an island.
// Confirmed param map (from live capture 2026-05-23, cross-checked vs in-game timer):
//   Farm plots:  elapsed = param 4 (100µs units), server now = param 5
//   Pasture/herb: elapsed = param 1 (100µs units), server now = param 2
// PlantedAt = serverNow - elapsed.
public class FarmableObjectInfoEvent
{
    public long ObjectId { get; }
    public IReadOnlyDictionary<byte, object> Parameters { get; }
    public string Signature { get; }

    // The planted item's unique name (e.g. "T4_FARMABLE_CROPS_CARROT").
    // Resolved by scanning all string-valued parameters for one containing
    // "FARMABLE" — robust to param-key changes across game patches.
    public string FarmableUniqueName { get; }

    // Derived from param 4 (remaining) and param 5 (server now). Null if crops are not growing or already ready.
    public DateTime? PlantedAt { get; }

    public FarmableObjectInfoEvent(Dictionary<byte, object> parameters)
    {
        Parameters = new Dictionary<byte, object>(parameters ?? []);
        ObjectId = parameters?.TryGetValue(0, out var p0) == true ? p0.ObjectToLong() ?? -1 : -1;
        Signature = BuildSignature(Parameters);
        FarmableUniqueName = TryExtractFarmableUniqueName(Parameters);
        PlantedAt = TryResolvePlantedAt(Parameters);
    }

    private static DateTime? TryResolvePlantedAt(IReadOnlyDictionary<byte, object> parameters)
    {
        if (parameters == null) return null;

        // Farm plots (code 201): elapsed = param 4, server now = param 5.
        // Pasture/herb (code 201): elapsed = param 1, server now = param 2.
        // Try farm layout first, fall back to pasture layout.
        var elapsed100us = parameters.TryGetValue(4, out var p4) ? p4.ObjectToLong() : null;
        var serverTicks  = parameters.TryGetValue(5, out var p5) ? p5.ObjectToLong() : null;

        if (elapsed100us is null or <= 0 || serverTicks is null or <= 0)
        {
            elapsed100us = parameters.TryGetValue(1, out var p1) ? p1.ObjectToLong() : null;
            serverTicks  = parameters.TryGetValue(2, out var p2) ? p2.ObjectToLong() : null;
        }

        if (elapsed100us is null or <= 0 || serverTicks is null or <= 0)
            return null;

        try
        {
            var serverNow = new DateTime(serverTicks.Value, DateTimeKind.Utc);
            var elapsedMs = elapsed100us.Value / 10.0;
            var cycleMs   = IslandConstants.LaborerBaseCycleHours * 3_600_000.0;
            var plantedAt = serverNow.AddMilliseconds(-elapsedMs);

            if (plantedAt < serverNow && elapsedMs >= 0 && elapsedMs <= cycleMs)
                return plantedAt;
        }
        catch
        {
            // invalid tick value
        }

        return null;
    }

    public DateTime? TryResolveActivityTimestampUtc()
    {
        foreach (var key in new byte[] { 9, 5, 2 })
        {
            if (!Parameters.TryGetValue(key, out var rawValue))
                continue;

            var ticks = rawValue.ObjectToLong();
            if (!ticks.HasValue || ticks.Value <= 0)
                continue;

            try
            {
                var resolved = new DateTime(ticks.Value, DateTimeKind.Utc);
                if (resolved.Year >= 2020 && resolved <= DateTime.UtcNow.AddMinutes(5))
                    return resolved;
            }
            catch
            {
                // not a valid CLR DateTime tick value
            }
        }

        return null;
    }


    // Scans all parameter values for a string that looks like a farmable item unique name.
    // "FARMABLE" appears in every plantable item name (T4_FARMABLE_CROPS_CARROT, etc.).
    // Herb garden events often encode the name as a byte[] (UTF-8), so we also attempt
    // to decode small byte arrays and validate them against the expected pattern.
    private static string TryExtractFarmableUniqueName(IReadOnlyDictionary<byte, object> parameters)
    {
        if (parameters == null)
            return string.Empty;

        foreach (var kv in parameters)
        {
            string s = null;

            if (kv.Value is string str)
            {
                s = str;
            }
            else if (kv.Value is byte[] bytes && bytes.Length is > 0 and < 128)
            {
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(bytes);
                    if (decoded.Length > 4 && decoded[0] == 'T' && char.IsDigit(decoded[1]) && decoded[2] == '_')
                        s = decoded;
                }
                catch
                {
                    // byte array is binary data, not a UTF-8 string
                }
            }

            if (s != null && s.Length > 0 && s.IndexOf("FARMABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                return s;
        }

        return string.Empty;
    }


    private static string BuildSignature(IReadOnlyDictionary<byte, object> parameters)
    {
        if (parameters == null || parameters.Count == 0)
            return string.Empty;

        return string.Join("|", parameters.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{FormatValue(pair.Value)}"));
    }

    private static string FormatValue(object value)
    {
        if (value == null)
            return "null";

        if (value is byte[] bytes)
            return $"[{string.Join(',', bytes)}]";

        if (value is Array array && value is not char[])
        {
            var items = array.Cast<object>().Select(FormatValue);
            return $"[{string.Join(',', items)}]";
        }

        return value.ToString() ?? string.Empty;
    }
}
