using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Diagnostics;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StatisticsAnalysisTool.Network.Operations.Request;

// REQUEST [257] — the collect action the player triggers on a laborer house. Param [0] is the collected
// laborer ObjectId; param [1] is the island storage container the loot is deposited into. Verified across
// captures: a 257 precedes the storage stack growth (NewLaborerItem code 32 / journal code 35) by ~1-3s.
// Used to gate laborer yield: only stack growth in the brief window after a 257 is real collected loot;
// outside that window code-32/35 broadcasts are storage repaints/streaming/object-id reuse, not collections.
public class LaborerCollectRequest
{
    public long LaborerObjectId { get; private set; } = -1;

    public LaborerCollectRequest(Dictionary<byte, object> parameters)
    {
        try
        {
            if (parameters.TryGetValue(0, out var p0))
                LaborerObjectId = p0.ObjectToLong() ?? -1;
        }
        catch (Exception e)
        {
            DebugConsole.WriteError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
        }
    }
}
