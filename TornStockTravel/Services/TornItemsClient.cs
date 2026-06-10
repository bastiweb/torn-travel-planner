using System.Net.Http;
using System.Text.Json;
using System.IO;

namespace TornStockTravel.Services;

public sealed class TornItemsClient : IDisposable
{
    private static readonly Uri BaseUri = new("https://api.torn.com/torn/?selections=items");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;

    public TornItemsClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
    }

    public async Task<IReadOnlyDictionary<int, TornItemDetails>> FetchItemsAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new Dictionary<int, TornItemDetails>();
        }

        Uri requestUri = new($"{BaseUri}&key={Uri.EscapeDataString(apiKey.Trim())}");
        using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Torn API returned HTTP {(int)response.StatusCode}.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("error", out JsonElement errorElement))
        {
            string code = errorElement.TryGetProperty("code", out JsonElement codeElement)
                ? codeElement.ToString()
                : "unknown";
            string message = errorElement.TryGetProperty("error", out JsonElement messageElement)
                ? messageElement.ToString()
                : "Unknown Torn API error";

            throw new InvalidOperationException($"Torn API error {code}: {message}");
        }

        if (!root.TryGetProperty("items", out JsonElement itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<int, TornItemDetails>();
        }

        Dictionary<int, TornItemDetails> items = new();

        foreach (JsonProperty itemProperty in itemsElement.EnumerateObject())
        {
            if (!int.TryParse(itemProperty.Name, out int id)
                || itemProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            JsonElement itemElement = itemProperty.Value;
            string name = itemElement.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            decimal marketValue = itemElement.TryGetProperty("market_value", out JsonElement valueElement)
                && valueElement.TryGetDecimal(out decimal parsedValue)
                    ? parsedValue
                    : 0;
            string? imageUrl = itemElement.TryGetProperty("image", out JsonElement imageElement)
                ? imageElement.GetString()
                : null;

            items[id] = new TornItemDetails(id, name, marketValue, imageUrl);
        }

        return items;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
