using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Manager;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class LaborerObjectJobInfoEventHandler : EventPacketHandler<LaborerObjectJobInfoEvent>
{
    private readonly TrackingController _trackingController;

    public LaborerObjectJobInfoEventHandler(TrackingController trackingController) : base((int) EventCodes.LaborerObjectJobInfo)
    {
        _trackingController = trackingController;
    }

    protected override Task OnActionAsync(LaborerObjectJobInfoEvent value)
    {
        if (ClusterController.CurrentCluster?.MapType == MapType.Island)
        {
            _trackingController.IslandController.HandleLaborerObjectJobInfo(value);
        }
        return Task.CompletedTask;
    }
}
