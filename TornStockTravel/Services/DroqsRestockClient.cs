using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class DroqsRestockClient : IDisposable
{
    private static readonly Uri RestockingSoonUri = new("https://droqsdb.com/api/public/v1/restocking-soon");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, string> CountryAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mex"] = "mex",
            ["mexico"] = "mex",
            ["cay"] = "cay",
            ["cayman islands"] = "cay",
            ["can"] = "can",
            ["canada"] = "can",
            ["haw"] = "haw",
            ["hawaii"] = "haw",
            ["uni"] = "uni",
            ["united kingdom"] = "uni",
            ["arg"] = "arg",
            ["argentina"] = "arg",
            ["swi"] = "swi",
            ["switzerland"] = "swi",
            ["jap"] = "jap",
            ["japan"] = "jap",
            ["chi"] = "chi",
            ["china"] = "chi",
            ["uae"] = "uae",
            ["united arab emirates"] = "uae",
            ["sou"] = "sou",
            ["south africa"] = "sou"
        };

    private readonly HttpClient _httpClient;

    public DroqsRestockClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
    }

    public async Task<IReadOnlyDictionary<string, DroqsRestockInfo>> FetchRestockingSoonAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, RestockingSoonUri);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DroqsDB restocking API returned HTTP {(int)response.StatusCode}.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("items", out JsonElement itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, DroqsRestockInfo>();
        }

        Dictionary<string, DroqsRestockInfo> restocks = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement itemElement in itemsElement.EnumerateArray())
        {
            if (itemElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int itemId = ReadInt(itemElement, "itemId");
            string country = ReadString(itemElement, "country") ?? string.Empty;

            if (itemId <= 0 || string.IsNullOrWhiteSpace(country))
            {
                continue;
            }

            JsonElement restockEstimate = itemElement.TryGetProperty("restockEstimate", out JsonElement estimateElement)
                ? estimateElement
                : default;
            string? confidence = restockEstimate.ValueKind == JsonValueKind.Object
                ? ReadString(restockEstimate, "confidence")
                : null;
            string? estimatedAtTct = restockEstimate.ValueKind == JsonValueKind.Object
                ? ReadString(restockEstimate, "estimatedAtTct")
                : null;

            DroqsRestockInfo info = new(itemId, country, confidence, estimatedAtTct);
            restocks[BuildKey(itemId, country)] = info;
        }

        return restocks;
    }

    public static string BuildKey(int itemId, string country)
    {
        string normalizedCountry = NormalizeCountry(country);
        return $"{itemId}:{normalizedCountry}";
    }

    private static string NormalizeCountry(string country)
    {
        string trimmed = country.Trim();
        return CountryAliases.TryGetValue(trimmed, out string? code)
            ? code
            : trimmed.ToLowerInvariant();
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
