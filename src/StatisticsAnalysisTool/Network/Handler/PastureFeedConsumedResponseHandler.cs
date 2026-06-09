using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Network.Operations.Responses;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class PastureFeedConsumedResponseHandler : ResponsePacketHandler<FarmableHarvestResponse>
{
    private readonly TrackingController _trackingController;

    public PastureFeedConsumedResponseHandler(TrackingController trackingController) : base(77) // game operation code 77 per packet sniffer (253:77) — pasture feed consumed (pumpkins, etc.)
    {
        _trackingController = trackingController;
    }

    protected override Task OnActionAsync(FarmableHarvestResponse value)
    {
        if (ClusterController.CurrentCluster?.MapType == MapType.Island)
        {
            _trackingController.IslandController.HandlePastureFeedConsumed(value);
        }

        return Task.CompletedTask;
    }
}
