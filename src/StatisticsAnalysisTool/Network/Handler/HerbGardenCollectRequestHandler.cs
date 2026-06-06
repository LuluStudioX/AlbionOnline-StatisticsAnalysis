using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Network.Operations.Request;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class HerbGardenCollectRequestHandler(TrackingController trackingController)
    : RequestPacketHandler<FarmableCollectRequest>(73) // game operation code 73 — herb/crop harvest request
{
    protected override Task OnActionAsync(FarmableCollectRequest value)
    {
        trackingController.IslandController?.HandleFarmableCollect(value.PlotObjectId);
        return Task.CompletedTask;
    }
}
