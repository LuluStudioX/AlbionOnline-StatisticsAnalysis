using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network.Operations.Request;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class PastureProductCollectRequestHandler(TrackingController trackingController)
    : RequestPacketHandler<FarmableCollectRequest>(76) // game operation code 76 — pasture product (milk/eggs) request
{
    protected override Task OnActionAsync(FarmableCollectRequest value)
    {
        trackingController.IslandController?.HandleFarmableCollect(value.PlotObjectId);
        return Task.CompletedTask;
    }
}
