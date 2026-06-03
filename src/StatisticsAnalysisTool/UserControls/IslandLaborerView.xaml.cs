using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Localization;
using StatisticsAnalysisTool.Network.Manager;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace StatisticsAnalysisTool.UserControls;

public partial class IslandLaborerView : UserControl
{
    private const double LaborerBaseCycleHours = 22.0;

    public IslandLaborerView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshFromViewModel();
    }

    public void RefreshFromViewModel()
    {
        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        var snapshots = controller?.GetCurrentSnapshots() ?? Array.Empty<LaborerSnapshot>();

        var rows = snapshots
            .OrderBy(s => s.DetectionOrder)
            .Select(BuildRow)
            .ToList();

        if (rows.Count == 0)
        {
            LaborersGrid.Visibility = Visibility.Collapsed;
            NoLaborersPlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            LaborersGrid.ItemsSource = rows;
            LaborersGrid.Visibility = Visibility.Visible;
            NoLaborersPlaceholder.Visibility = Visibility.Collapsed;
        }

        var lootReady = rows.Count(r => r.StatusCode == "loot_ready");
        var onJob = rows.Count(r => r.StatusCode == "on_job");
        var home = rows.Count(r => r.StatusCode == "home");

        LootReadyText.Text = $"{lootReady} {LocalizationController.Translation("ISLAND_MANAGEMENT_LEGEND_LOOT_READY")}";
        OnJobText.Text = $"{onJob} {LocalizationController.Translation("ISLAND_MANAGEMENT_LEGEND_ON_JOB")}";
        HomeText.Text = $"{home} {LocalizationController.Translation("ISLAND_MANAGEMENT_LEGEND_HOME")}";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshFromViewModel();
    }

    private static LaborerRowEntry BuildRow(LaborerSnapshot s)
    {
        var statusCode = s.IsLootReady ? "loot_ready"
            : s.IsOnJob ? "on_job"
            : "home";
        var status = statusCode switch
        {
            "loot_ready" => LocalizationController.Translation("ISLAND_MANAGEMENT_LEGEND_LOOT_READY"),
            "on_job" => LocalizationController.Translation("ISLAND_MANAGEMENT_LEGEND_ON_JOB"),
            _ => LocalizationController.Translation("ISLAND_MANAGEMENT_LEGEND_HOME")
        };

        var dispatchText = "—";
        var returnsInText = "—";

        // p8 (JobDispatchTime) = when loot is ready (future). Job started 22h before that.
        // p6/p7 carry previous-cycle timestamps — do not use for current job display.
        if (s.IsOnJob && s.JobDispatchTime.HasValue)
        {
            var jobStarted = s.JobDispatchTime.Value.AddHours(-LaborerBaseCycleHours);
            var elapsed = DateTime.UtcNow - jobStarted;
            var elapsedH = (int)elapsed.TotalHours;
            var elapsedM = elapsed.Minutes;
            dispatchText = elapsedH > 0 ? $"{elapsedH}h {elapsedM}m ago" : $"{elapsedM}m ago";

            var remaining = s.JobDispatchTime.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                var remH = (int)remaining.TotalHours;
                var remM = remaining.Minutes;
                returnsInText = remH > 0 ? $"{remH}h {remM}m" : $"{remM}m";
            }
            else
            {
                returnsInText = LocalizationController.Translation("ISLAND_MANAGEMENT_STATUS_SOON");
            }
        }

        var fameFillText = $"{s.CurrentFameFill.DoubleValue:F0}";
        var famePointsText = $"{s.FameFillValue}";
        var happinessText = $"{s.Happiness}/800";

        var tier = s.BuildingTier > 0 ? $"T{s.BuildingTier}" : "—";
        var laborerType = FormatLaborerType(s.LaborerType);

        return new LaborerRowEntry(
            FullName: s.FullName,
            LaborerType: laborerType,
            Tier: tier,
            Status: status,
            StatusCode: statusCode,
            DispatchText: dispatchText,
            ReturnsInText: returnsInText,
            FameFillText: fameFillText,
            FamePointsText: famePointsText,
            HappinessText: happinessText,
            SentBy: string.IsNullOrWhiteSpace(s.SentByCharacter) ? "—" : s.SentByCharacter,
            HasPremium: s.HasPremium,
            Happiness: s.Happiness
        );
    }

    private static string FormatLaborerType(string rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType)) return "—";
        return char.ToUpper(rawType[0]) + rawType[1..].ToLower();
    }
}

internal sealed record LaborerRowEntry(
    string FullName,
    string LaborerType,
    string Tier,
    string Status,
    string StatusCode,
    string DispatchText,
    string ReturnsInText,
    string FameFillText,
    string FamePointsText,
    string HappinessText,
    string SentBy,
    bool HasPremium,
    int Happiness
);
