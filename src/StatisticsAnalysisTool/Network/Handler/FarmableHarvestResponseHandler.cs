using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Network.Operations.Responses;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class FarmableHarvestResponseHandler : ResponsePacketHandler<FarmableHarvestResponse>
{
    private readonly TrackingController _trackingController;

    public FarmableHarvestResponseHandler(TrackingController trackingController) : base(73) // game operation code 73 per packet sniffer (253:73)
    {
        _trackingController = trackingController;
    }

    protected override Task OnActionAsync(FarmableHarvestResponse value)
    {
        if (ClusterController.CurrentCluster?.MapType == MapType.Island)
        {
            _trackingController.IslandController.HandleFarmableHarvestResponse(value);
        }

        return Task.CompletedTask;
    }

}
