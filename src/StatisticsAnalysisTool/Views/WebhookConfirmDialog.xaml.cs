using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace StatisticsAnalysisTool.Views;

public partial class WebhookConfirmDialog : Window
{
    public enum ConfirmResult
    {
        DontSend,
        Send,
        SaveAndSend
    }

    /// <summary>Fired when user submits (SaveAndSend or Send). Args: notes, emv.</summary>
    public event Func<string, decimal?, System.Threading.Tasks.Task> Submitted;

    public ConfirmResult Result { get; private set; } = ConfirmResult.DontSend;
    public string DailyNotes => NotesTextBox.Text.Trim();
    public decimal? EmvAmount => decimal.TryParse(EmvTextBox.Text.Trim(), out var v) && v > 0 ? v : null;

    public WebhookConfirmDialog() : this(null) { }

    public WebhookConfirmDialog(string ownerName)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(ownerName))
            Title = $"Payment Ready — {ownerName}";
        NotesTextBox.Focus();
    }

    private async void SaveAndSendButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.SaveAndSend;
        var notes = DailyNotes;
        var emv = EmvAmount;
        Close();
        if (Submitted != null)
            await Submitted.Invoke(notes, emv);
        else
            DialogResult = true;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.Send;
        Close();
        if (Submitted != null)
            await Submitted.Invoke(string.Empty, null);
        else
            DialogResult = true;
    }

    private void DontSendButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.DontSend;
        if (Submitted == null) DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.DontSend;
        if (Submitted == null) DialogResult = false;
        Close();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void EmvTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"[\d.]");
    }
}
