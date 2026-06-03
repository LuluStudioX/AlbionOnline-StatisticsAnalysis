using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Diagnostics;
using StatisticsAnalysisTool.Island;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StatisticsAnalysisTool.Network.Events;

// EVENT [54] FarmBuildingInfo — sent when entering an island with farmable plots.
// Confirmed param map (from live capture 2026-05-22):
//   0  : long  — ObjectId
//   4  : long  — elapsed grow time in units of 100 microseconds (same encoding as FarmableObjectInfo)
//   5  : long  — server DateTime ticks (UTC); the server's "now" at time of send
// PlantedAt = serverNow - elapsed.
public class FarmBuildingInfoEvent
{
    public long ObjectId { get; private set; } = -1;

    // Derived from param 4 (remaining) and param 5 (server now). Null if crops are not actively growing.
    public DateTime? PlantedAt { get; private set; }

    public FarmBuildingInfoEvent(Dictionary<byte, object> parameters)
    {
        try
        {
            if (parameters.TryGetValue(0, out var p0))
                ObjectId = p0.ObjectToLong() ?? -1;

            // Param 4 = elapsed grow time in 100µs units (same encoding as FarmableObjectInfo code 201).
            var elapsed100us = parameters.TryGetValue(4, out var p4) ? p4.ObjectToLong() : null;
            var serverTicks  = parameters.TryGetValue(5, out var p5) ? p5.ObjectToLong() : null;

            if (elapsed100us is > 0 && serverTicks is > 0)
            {
                var serverNow = new DateTime(serverTicks.Value, DateTimeKind.Utc);
                var elapsedMs = elapsed100us.Value / 10.0;
                var cycleMs   = IslandConstants.LaborerBaseCycleHours * 3_600_000.0;
                var plantedAt = serverNow.AddMilliseconds(-elapsedMs);

                if (plantedAt < serverNow && elapsedMs >= 0 && elapsedMs <= cycleMs)
                    PlantedAt = plantedAt;
            }
        }
        catch (Exception e)
        {
            DebugConsole.WriteError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
        }
    }
}
