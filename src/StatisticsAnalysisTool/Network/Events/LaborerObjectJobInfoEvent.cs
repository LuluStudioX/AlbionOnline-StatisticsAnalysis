using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Diagnostics;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StatisticsAnalysisTool.Network.Events;

// EVENT [57]LaborerObjectJobInfo
//   Away on job:     map[0:<objectId> 1:false 2:<journalItemId> 3:<fameFill> 5:<jobStartTime:ticks> 252:57]
//   With loot ready: map[0:<objectId> 1:true  2:<journalItemId> 3:<fameFill> 252:57]
//   After collect:   map[0:<objectId> 252:57]  (all optional params absent)
//
// param 1 = false + param 2 > 0  →  away on job
// param 1 = true               →  returned home, loot ready
// all optional params absent    →  idle at home
public class LaborerObjectJobInfoEvent
{
    public long ObjectId { get; private set; } = -1;
    public bool IsLootReady { get; private set; }
    public bool IsAwayOnJob { get; private set; }
    public int JournalItemId { get; private set; }
    public FixPoint CurrentFameFill { get; private set; }
    public DateTime? JobStartTime { get; private set; }

    public LaborerObjectJobInfoEvent(Dictionary<byte, object> parameters)
    {
        try
        {
            if (parameters.TryGetValue(0, out var p0))
                ObjectId = p0.ObjectToLong() ?? -1;

            if (parameters.TryGetValue(1, out var p1))
                IsLootReady = p1.ObjectToBool();

            if (parameters.TryGetValue(2, out var p2))
                JournalItemId = p2.ObjectToInt();

            if (parameters.TryGetValue(3, out var p3))
            {
                var fill = p3.ObjectToLong();
                CurrentFameFill = FixPoint.FromInternalValue(fill ?? 0);
            }

            if (parameters.TryGetValue(5, out var p5))
            {
                var ticks5 = p5.ObjectToLong();
                if (ticks5.HasValue && ticks5.Value > 0)
                    JobStartTime = new DateTime(ticks5.Value, DateTimeKind.Utc);
            }

            IsAwayOnJob = JournalItemId > 0 && !IsLootReady;
        }
        catch (Exception e)
        {
            DebugConsole.WriteError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
        }
    }
}
