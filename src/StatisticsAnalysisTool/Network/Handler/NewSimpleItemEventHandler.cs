using StatisticsAnalysisTool.EstimatedMarketValue;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Manager;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class NewSimpleItemEventHandler(TrackingController trackingController) : EventPacketHandler<NewSimpleItemEvent>(27) // game event code 27 per packet sniffer (252:27)
{
    protected override async Task OnActionAsync(NewSimpleItemEvent value)
    {
        if (value.Item == null)
        {
            await Task.CompletedTask;
            return;
        }

        if (trackingController.IsTrackingAllowedByMainCharacter())
        {
            trackingController.VaultController.AddDiscoveredItem(value.Item);
        }

        EstimatedMarketValueController.Add(value.Item.ItemIndex, value.Item.EstimatedMarketValueInternal, value.Item.Quality);
        trackingController.LootController.AddDiscoveredItem(value.Item);
        trackingController.DungeonController.AddDiscoveredItem(value.Item);
        trackingController.GatheringController.AddFishedItem(value.Item);
        await Task.CompletedTask;
    }
}