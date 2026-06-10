using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class TornBarsClient : IDisposable
{
    private static readonly Uri BarsUri = new("https://api.torn.com/v2/user/bars");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;

    public TornBarsClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
    }

    public async Task<TornBarsStatus?> FetchBarsAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, BarsUri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", apiKey.Trim());
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Torn bars API returned HTTP {(int)response.StatusCode}.");
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
                : "Unknown Torn bars API error";

            throw new InvalidOperationException($"Torn bars API error {code}: {message}");
        }

        JsonElement barsElement = root.TryGetProperty("bars", out JsonElement nestedBars)
            ? nestedBars
            : root;

        TornBarStatus? energy = ReadBar(barsElement, "energy");
        TornBarStatus? nerve = ReadBar(barsElement, "nerve");

        return energy is null || nerve is null ? null : new TornBarsStatus(energy, nerve);
    }

    private static TornBarStatus? ReadBar(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement barElement)
            || barElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new TornBarStatus(
            ReadInt(barElement, "current"),
            ReadInt(barElement, "maximum"),
            ReadInt(barElement, "interval"),
            ReadInt(barElement, "tick_time"),
            ReadInt(barElement, "full_time"));
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed)
            ? parsed
            : 0;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
