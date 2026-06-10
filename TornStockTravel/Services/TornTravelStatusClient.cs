using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class TornTravelStatusClient : IDisposable
{
    private static readonly Uri TravelStatusUri = new("https://api.torn.com/v2/user/travel");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;

    public TornTravelStatusClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
    }

    public async Task<TornTravelStatus?> FetchTravelStatusAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, TravelStatusUri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", apiKey.Trim());
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Torn travel API returned HTTP {(int)response.StatusCode}.");
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
                : "Unknown Torn travel API error";

            throw new InvalidOperationException($"Torn travel API error {code}: {message}");
        }

        JsonElement travelElement = root.TryGetProperty("travel", out JsonElement nestedTravel)
            ? nestedTravel
            : root;

        if (travelElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? destination = ReadString(travelElement, "destination");
        DateTimeOffset? departedAt = ReadUnixTime(travelElement, "departed_at");
        DateTimeOffset? arrivalAt = ReadUnixTime(travelElement, "arrival_at");
        int? timeLeftSeconds = ReadNullableInt(travelElement, "time_left");

        return new TornTravelStatus(destination, departedAt, arrivalAt, timeLeftSeconds, DateTimeOffset.Now);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static DateTimeOffset? ReadUnixTime(JsonElement element, string propertyName)
    {
        int? seconds = ReadNullableInt(element, propertyName);
        return seconds is null or <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
    }

    private static int? ReadNullableInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed)
            ? parsed
            : null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
