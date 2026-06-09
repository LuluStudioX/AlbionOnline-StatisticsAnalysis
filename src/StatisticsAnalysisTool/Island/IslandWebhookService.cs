using StatisticsAnalysisTool.Views;
using System.Threading.Tasks;
using System.Windows;

namespace StatisticsAnalysisTool.Island;

// UI confirm + transport for the owner collection-ready Discord notification. Keeps the WPF dialog and
// the HTTP send out of IslandController — the controller decides WHEN to notify, builds the message, and
// owns owner-profile persistence; this service only shows the dialog and posts the webhook.
public sealed class IslandWebhookService
{
    // Outcome of the confirm dialog. Send=false means cancelled/declined. SaveNote=true means the user
    // chose "Save and send", so the caller should persist Notes/Emv to the owner's cycle history.
    public readonly record struct ConfirmOutcome(bool Send, bool SaveNote, string Notes, decimal? Emv);

    public Task<ConfirmOutcome> PromptAsync()
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new WebhookConfirmDialog
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() != true) return new ConfirmOutcome(false, false, null, null);
            if (dialog.Result == WebhookConfirmDialog.ConfirmResult.DontSend) return new ConfirmOutcome(false, false, null, null);

            var saveNote = dialog.Result == WebhookConfirmDialog.ConfirmResult.SaveAndSend;
            return new ConfirmOutcome(true, saveNote, dialog.DailyNotes, dialog.EmvAmount);
        }).Task;
    }

    public async Task<bool> SendAsync(string webhookUrl, string message)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl) || string.IsNullOrEmpty(message)) return false;
        return await DiscordWebhookService.SendAsync(webhookUrl, message).ConfigureAwait(false);
    }
}
