using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Diagnostics;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StatisticsAnalysisTool.Network.Operations.Responses;

public class FarmableHarvestResponse
{
    public IReadOnlyList<(string UniqueName, int Quantity)> Items { get; }

    public FarmableHarvestResponse(Dictionary<byte, object> parameters)
    {
        var result = new List<(string, int)>();
        try
        {
            var names = ExtractNames(parameters);
            var quantities = ExtractQuantities(parameters);

            if (names != null && quantities != null)
            {
                var count = Math.Min(names.Length, quantities.Length);
                for (var i = 0; i < count; i++)
                {
                    if (!string.IsNullOrEmpty(names[i]) && quantities[i] > 0)
                        result.Add((names[i], quantities[i]));
                }
            }
        }
        catch (Exception e)
        {
            DebugConsole.WriteError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
        }

        Items = result;
    }

    private static string[] ExtractNames(Dictionary<byte, object> parameters)
    {
        if (!parameters.TryGetValue(0, out var p0)) return null;
        if (p0 is string[] arr) return arr;
        if (p0 is string s) return [s];
        return null;
    }

    private static int[] ExtractQuantities(Dictionary<byte, object> parameters)
    {
        if (!parameters.TryGetValue(1, out var p1)) return null;

        if (p1 is byte[] bArr)
        {
            var result = new int[bArr.Length];
            for (var i = 0; i < bArr.Length; i++) result[i] = bArr[i];
            return result;
        }

        if (p1 is short[] sArr)
        {
            var result = new int[sArr.Length];
            for (var i = 0; i < sArr.Length; i++) result[i] = sArr[i];
            return result;
        }

        if (p1 is int[] iArr) return iArr;

        // Single value (single-item harvest)
        var qty = p1.ObjectToInt();
        if (qty > 0) return [qty];

        return null;
    }
}
