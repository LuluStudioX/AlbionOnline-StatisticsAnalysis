using Serilog;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Diagnostics;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StatisticsAnalysisTool.Network.Events;

// EVENT [56]LaborerObjectInfo
//   When home:   map[0:<objectId> 1:<firstName> 2:<lastName> 3:<fameFill:FixPoint> 4:<happiness:FixPoint> 5:<happiness:FixPoint (dup)>
//                    6:<nextReturnAt:ticks> 7:<lastJobStartedAt:ticks> 10:'' 252:56]
//   When on job: same + 8:<dispatchTicks> 9:<jobGuid:Byte[]> 10:<zoneName>
public class LaborerObjectInfoEvent
{
    public long ObjectId { get; private set; } = -1;
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public bool IsOnJob { get; private set; }
    public Guid? ActiveJobId { get; private set; }
    public DateTime? JobDispatchTime { get; private set; }
    public string SentByCharacter { get; private set; } = string.Empty;
    public FixPoint FameFill { get; private set; }
    public int Happiness { get; private set; }
    public DateTime? NextReturnAt { get; private set; }
    public DateTime? LastJobStartedAt { get; private set; }

    public LaborerObjectInfoEvent(Dictionary<byte, object> parameters)
    {
        try
        {
            if (parameters.TryGetValue(0, out var p0))
                ObjectId = p0.ObjectToLong() ?? -1;

            if (parameters.TryGetValue(1, out var p1))
                FirstName = p1?.ToString() ?? string.Empty;

            if (parameters.TryGetValue(2, out var p2))
                LastName = p2?.ToString() ?? string.Empty;

            if (parameters.TryGetValue(3, out var p3))
            {
                var raw3 = p3.ObjectToLong();
                FameFill = FixPoint.FromInternalValue(raw3 ?? 0);
            }

            if (parameters.TryGetValue(4, out var p4))
            {
                var raw4 = p4.ObjectToLong();
                Happiness = (int)FixPoint.FromInternalValue(raw4 ?? 0).DoubleValue;
            }

            if (parameters.TryGetValue(6, out var p6))
            {
                var ticks6 = p6.ObjectToLong();
                if (ticks6.HasValue && ticks6.Value > 0)
                {
                    NextReturnAt = new DateTime(ticks6.Value, DateTimeKind.Utc);
                    Log.Debug("[LaborerObjectInfoEvent] p6 raw={Raw} → NextReturnAt={Resolved} (now={Now})", ticks6.Value, NextReturnAt, DateTime.UtcNow);
                }
            }

            if (parameters.TryGetValue(7, out var p7))
            {
                var ticks7 = p7.ObjectToLong();
                if (ticks7.HasValue && ticks7.Value > 0)
                {
                    LastJobStartedAt = new DateTime(ticks7.Value, DateTimeKind.Utc);
                    Log.Debug("[LaborerObjectInfoEvent] p7 raw={Raw} → LastJobStartedAt={Resolved}", ticks7.Value, LastJobStartedAt);
                }
            }

            if (parameters.TryGetValue(8, out var p8))
            {
                var ticks = p8.ObjectToLong();
                if (ticks.HasValue && ticks.Value > 0)
                {
                    IsOnJob = true;
                    JobDispatchTime = new DateTime(ticks.Value, DateTimeKind.Utc);
                    Log.Debug("[LaborerObjectInfoEvent] p8 raw={Raw} → JobDispatchTime={Resolved}", ticks.Value, JobDispatchTime);
                }
            }

            if (parameters.TryGetValue(9, out var p9) && p9 is byte[] p9Bytes && p9Bytes.Length == 16)
                ActiveJobId = new Guid(p9Bytes);

            if (parameters.TryGetValue(10, out var p10))
                SentByCharacter = p10?.ToString() ?? string.Empty;
        }
        catch (Exception e)
        {
            DebugConsole.WriteError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
        }
    }
}
