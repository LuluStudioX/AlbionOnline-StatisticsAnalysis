using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network.Operations.Request;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class LaborerCollectRequestHandler(TrackingController trackingController)
    : RequestPacketHandler<LaborerCollectRequest>(257) // game operation code 257 — laborer collect request
{
    protected override Task OnActionAsync(LaborerCollectRequest value)
    {
        trackingController.IslandController?.NotifyLaborerCollect(value.LaborerObjectId);
        return Task.CompletedTask;
    }
}
