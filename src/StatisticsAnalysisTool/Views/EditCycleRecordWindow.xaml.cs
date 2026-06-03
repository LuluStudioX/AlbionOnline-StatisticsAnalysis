using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Models.BindingModel;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StatisticsAnalysisTool.Views;

public partial class EditCycleRecordWindow : Window
{
    public OwnerCycleRecord Result { get; private set; }
    public OwnerWithdrawalEntry WithdrawalResult { get; private set; }
    public bool DeleteRequested { get; private set; }

    private readonly Guid _originalId;
    private readonly bool _isWithdrawal;
    private CycleRecordType _originalRecordType;

    public EditCycleRecordWindow(OwnerCycleRecord record)
    {
        InitializeComponent();
        _originalId = record.Id;
        _isWithdrawal = false;
        _originalRecordType = record.RecordType;
        DatePicker.SelectedDate = record.Date;
        IslandCountBox.Text = record.IslandCount.ToString();
        AmountBox.Text = record.EarnedAmount.ToString(CultureInfo.InvariantCulture);
        NotesBox.Text = record.Notes ?? string.Empty;
        ValidateInputs();
    }

    public EditCycleRecordWindow(OwnerWithdrawalEntry entry)
    {
        InitializeComponent();
        _originalId = entry.Id;
        _isWithdrawal = true;
        Title = "Edit Withdrawal";
        DatePicker.SelectedDate = entry.Timestamp.ToLocalTime().Date;
        IslandCountLabel.Visibility = Visibility.Collapsed;
        AmountBox.Text = entry.Amount.ToString(CultureInfo.InvariantCulture);
        NotesBox.Text = entry.Notes ?? string.Empty;
        ValidateInputs();
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAmountPreview();
        ValidateInputs();
    }

    private void Field_Changed(object sender, SelectionChangedEventArgs e)
        => ValidateInputs();

    private void UpdateAmountPreview()
    {
        if (AmountPreview == null) return;
        var v = IslandBindings.EvaluateSilverExpression(AmountBox?.Text ?? string.Empty);
        AmountPreview.Text = v.HasValue ? $"= {v.Value:N0}" : string.Empty;
    }

    private void ValidateInputs()
    {
        var dateOk = DatePicker?.SelectedDate.HasValue == true;
        var v = IslandBindings.EvaluateSilverExpression(AmountBox?.Text ?? string.Empty);
        var amountOk = v is { } val && val > 0;
        var countOk = _isWithdrawal || int.TryParse(IslandCountBox?.Text, out _);
        if (ConfirmButton != null)
            ConfirmButton.IsEnabled = dateOk && amountOk && countOk;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatePicker.SelectedDate is not { } date) return;
        var amount = IslandBindings.EvaluateSilverExpression(AmountBox.Text);
        if (amount == null || amount <= 0) return;

        if (_isWithdrawal)
        {
            WithdrawalResult = new OwnerWithdrawalEntry
            {
                Id = _originalId,
                Timestamp = date.Date.ToUniversalTime(),
                Amount = amount.Value,
                Notes = NotesBox.Text.Trim()
            };
        }
        else
        {
            if (!int.TryParse(IslandCountBox.Text, out var count)) return;
            Result = new OwnerCycleRecord
            {
                Id = _originalId,
                Date = date.Date,
                RecordType = _originalRecordType,
                IslandCount = count,
                EarnedAmount = amount.Value,
                Notes = NotesBox.Text.Trim()
            };
        }

        DialogResult = true;
        Close();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Delete this entry? This cannot be undone.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        DeleteRequested = true;
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
