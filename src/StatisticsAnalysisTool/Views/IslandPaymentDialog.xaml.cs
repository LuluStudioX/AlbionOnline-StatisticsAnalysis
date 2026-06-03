using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Localization;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace StatisticsAnalysisTool.Views;

public partial class IslandPaymentDialog : Window
{
    public string SelectedOwnerName { get; private set; }
    public decimal SilverAmount { get; }
    public string Notes => NotesTextBox.Text.Trim();
    public DateTime PaidForWeekStart => PaidForWeekPicker.SelectedDate?.Date ?? GetLastMonday();

    public IslandPaymentDialog(string partnerName, decimal silverAmount, IReadOnlyList<Island.Island> matchingIslands)
    {
        InitializeComponent();

        SilverAmount = silverAmount;

        TitleBarText.Text = LocalizationController.Translation("ISLAND_MANAGEMENT_PAYMENT_TITLE");
        AssignToIslandLabel.Text = LocalizationController.Translation("ISLAND_MANAGEMENT_PAYMENT_ASSIGN_TO_ISLAND");
        NotesLabel.Text = LocalizationController.Translation("ISLAND_MANAGEMENT_PAYMENT_NOTES_OPTIONAL");
        SkipText.Text = LocalizationController.Translation("ISLAND_MANAGEMENT_PAYMENT_SKIP");
        RecordPaymentText.Text = LocalizationController.Translation("ISLAND_MANAGEMENT_PAYMENT_RECORD");

        TradeDescriptionText.Text = string.Format(LocalizationController.Translation("ISLAND_MANAGEMENT_PAYMENT_OUTGOING_SILVER"), partnerName);
        TradeAmountText.Text = $"{silverAmount:N0} silver — {LocalizationController.Translation("ISLAND_MANAGEMENT_PAYMENT_IS_ISLAND_PAYMENT")}";

        IslandComboBox.ItemsSource = matchingIslands;

        if (matchingIslands.Count == 1)
            IslandComboBox.SelectedIndex = 0;

        PaidForWeekPicker.SelectedDate = GetLastMonday();
    }

    private static DateTime GetLastMonday()
    {
        var today = DateTime.Today;
        var delta = ((int) today.DayOfWeek - (int) DayOfWeek.Monday + 7) % 7;
        return delta == 0 ? today.AddDays(-7) : today.AddDays(-delta);
    }

    private void IslandComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IslandComboBox.SelectedItem is Island.Island island)
        {
            SelectedOwnerName = island.Owner;
            ConfirmButton.IsEnabled = true;
        }
        else
        {
            SelectedOwnerName = null;
            ConfirmButton.IsEnabled = false;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
}
