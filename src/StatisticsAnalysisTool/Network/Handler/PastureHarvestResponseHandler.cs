using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Network.Operations.Responses;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class PastureHarvestResponseHandler : ResponsePacketHandler<FarmableHarvestResponse>
{
    private readonly TrackingController _trackingController;

    public PastureHarvestResponseHandler(TrackingController trackingController) : base(74) // game operation code 74 per packet sniffer (253:74)
    {
        _trackingController = trackingController;
    }

    protected override Task OnActionAsync(FarmableHarvestResponse value)
    {
        if (ClusterController.CurrentCluster?.MapType == MapType.Island)
        {
            _trackingController.IslandController.HandlePastureHarvestResponse(value);
        }

        return Task.CompletedTask;
    }
}
