using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Network.Operations.Request;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class PastureFeedCollectRequestHandler(TrackingController trackingController)
    : RequestPacketHandler<FarmableCollectRequest>(77) // game operation code 77 — pasture feed-consumed request
{
    protected override Task OnActionAsync(FarmableCollectRequest value)
    {
        trackingController.IslandController?.HandleFarmableCollect(value.PlotObjectId);
        return Task.CompletedTask;
    }
}
