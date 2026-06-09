using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Manager;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class LaborerObjectInfoEventHandler : EventPacketHandler<LaborerObjectInfoEvent>
{
    private readonly TrackingController _trackingController;

    public LaborerObjectInfoEventHandler(TrackingController trackingController) : base((int) EventCodes.LaborerObjectInfo)
    {
        _trackingController = trackingController;
    }

    protected override Task OnActionAsync(LaborerObjectInfoEvent value)
    {
        if (ClusterController.CurrentCluster?.MapType == MapType.Island)
        {
            _trackingController.IslandController.HandleLaborerObjectInfo(value);
        }
        return Task.CompletedTask;
    }
}
