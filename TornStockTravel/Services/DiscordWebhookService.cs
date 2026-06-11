using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TornStockTravel.Services;

public sealed class DiscordWebhookService : IDisposable
{
    private readonly HttpClient _httpClient = new();

    public async Task SendAsync(string webhookUrl, string message)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

        string json = JsonSerializer.Serialize(new DiscordWebhookMessage(message));
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.PostAsync(webhookUrl.Trim(), content);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record DiscordWebhookMessage([property: JsonPropertyName("content")] string Content);
}
