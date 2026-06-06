using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Diagnostics;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StatisticsAnalysisTool.Network.Operations.Request;

// REQUEST [73 herb/crop harvest][74 pasture harvest][76 pasture product][77 pasture feed] — the collect
// action the player triggers on a single farmable plot. Param [0] is the collected plot/plant ObjectId
// (verified: every collect-request [0] resolves to a NewBuilding(45) plant and a FarmableObjectInfo(201)).
// The matching RESPONSE (same op, correlated by [255]) carries the harvested items; this request carries
// the plot identity the response lacks, so per-plot timer clearing keys off it.
public class FarmableCollectRequest
{
    public long PlotObjectId { get; private set; } = -1;

    public FarmableCollectRequest(Dictionary<byte, object> parameters)
    {
        try
        {
            if (parameters.TryGetValue(0, out var p0))
                PlotObjectId = p0.ObjectToLong() ?? -1;
        }
        catch (Exception e)
        {
            DebugConsole.WriteError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
        }
    }
}
