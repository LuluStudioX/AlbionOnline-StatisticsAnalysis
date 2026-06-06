using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Diagnostics;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StatisticsAnalysisTool.Network.Events;

// EVENT [57]LaborerObjectJobInfo — two forms observed across every island capture:
//   Active job:  map[0:<objectId> 1:true 2:<journalItemId> 3:<fameFill> 5:<jobStartTime:ticks> 252:57]
//   Idle/bare:   map[0:<objectId> 252:57]  (all optional params absent)
//
// Param 1 is ALWAYS true when present (never observed false) — it marks "has an active job", NOT
// loot-ready. Whether the job is still running or finished (loot ready) cannot be read from this
// event; it is derived downstream from the return time (LaborerObjectInfo param 8 / JobStartTime +
// cycle). See LaborerSnapshot.IsLootReady.
public class LaborerObjectJobInfoEvent
{
    public long ObjectId { get; private set; } = -1;
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

            // A non-zero journal id means the laborer holds an active job assignment (form A above).
            IsAwayOnJob = JournalItemId > 0;
        }
        catch (Exception e)
        {
            DebugConsole.WriteError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
        }
    }
}
