using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network.Operations.Request;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class PastureCollectRequestHandler(TrackingController trackingController)
    : RequestPacketHandler<FarmableCollectRequest>(74) // game operation code 74 — pasture animal harvest request
{
    protected override Task OnActionAsync(FarmableCollectRequest value)
    {
        trackingController.IslandController?.HandleFarmableCollect(value.PlotObjectId);
        return Task.CompletedTask;
    }
}
