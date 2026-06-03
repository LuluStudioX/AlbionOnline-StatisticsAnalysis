using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Network.Operations.Responses;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class PastureProductHarvestResponseHandler : ResponsePacketHandler<FarmableHarvestResponse>
{
    private readonly TrackingController _trackingController;

    public PastureProductHarvestResponseHandler(TrackingController trackingController) : base(76) // game operation code 76 per packet sniffer (253:76) — pasture product collect (milk, eggs, etc.)
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
