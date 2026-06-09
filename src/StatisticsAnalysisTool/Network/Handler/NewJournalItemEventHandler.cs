using StatisticsAnalysisTool.EstimatedMarketValue;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network.Manager;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class NewJournalItemEventHandler(TrackingController trackingController) : EventPacketHandler<NewJournalItemEvent>((int) EventCodes.NewJournalItem)
{
    protected override async Task OnActionAsync(NewJournalItemEvent value)
    {
        if (trackingController.IsTrackingAllowedByMainCharacter())
        {
            trackingController.VaultController.AddDiscoveredItem(value.Item);
        }

        EstimatedMarketValueController.Add(value.Item.ItemIndex, value.Item.EstimatedMarketValueInternal, value.Item.Quality);
        trackingController.LootController.AddDiscoveredItem(value.Item);
        trackingController.DungeonController.AddDiscoveredItem(value.Item);
        // Laborer journals: empty stacks rise = collected, full stacks fall = consumed (island-guarded).
        trackingController.IslandController?.HandleLaborerJournalDetail(value.Item);
        await Task.CompletedTask;
    }
}