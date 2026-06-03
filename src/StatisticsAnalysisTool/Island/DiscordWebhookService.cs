using Serilog;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Island;

public static class DiscordWebhookService
{
    private const int WebhookEmbedColor = 0x2280BF;
    private const string WebhookFooterText = "SAT Island Notifier";

    private static readonly HttpClient _httpClient = new();

    public static async Task SendAsync(string webhookUrl, string message)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl) || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            // Split title (first line) from body for the embed
            var newline = message.IndexOf('\n');
            var title = newline > 0 ? message[..newline].Trim() : message.Trim();
            var description = newline > 0 ? message[(newline + 1)..].Trim() : string.Empty;

            var embed = new
            {
                title,
                description,
                color = WebhookEmbedColor,
                footer = new { text = WebhookFooterText },
                timestamp = DateTime.UtcNow.ToString("o")
            };

            var payload = JsonSerializer.Serialize(new { embeds = new[] { embed } });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(webhookUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Log.Warning("[DiscordWebhookService] Send failed: {StatusCode}", response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "[DiscordWebhookService] Send exception");
        }
    }
}
