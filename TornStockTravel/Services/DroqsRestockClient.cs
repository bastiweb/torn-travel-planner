using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class DroqsRestockClient : IDisposable
{
    private static readonly Uri RestockingSoonUri = new("https://droqsdb.com/api/public/v1/restocking-soon");
    private static readonly Uri TopProfitsUri = new("https://droqsdb.com/api/public/v1/top-profits");
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
        Dictionary<string, DroqsRestockInfo> restocks = await FetchRestockInfoAsync(RestockingSoonUri, "items", cancellationToken);
        Dictionary<string, DroqsRestockInfo> topProfits;

        try
        {
            topProfits = await FetchRestockInfoAsync(TopProfitsUri, "runs", cancellationToken);
        }
        catch
        {
            return restocks;
        }

        foreach (KeyValuePair<string, DroqsRestockInfo> entry in topProfits)
        {
            restocks[entry.Key] = restocks.TryGetValue(entry.Key, out DroqsRestockInfo? existing)
                ? existing.Merge(entry.Value)
                : entry.Value;
        }

        return restocks;
    }

    private async Task<Dictionary<string, DroqsRestockInfo>> FetchRestockInfoAsync(
        Uri uri,
        string collectionPropertyName,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DroqsDB API {uri.AbsolutePath} returned HTTP {(int)response.StatusCode}.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty(collectionPropertyName, out JsonElement collectionElement)
            || collectionElement.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, DroqsRestockInfo>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, DroqsRestockInfo> restocks = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement itemElement in collectionElement.EnumerateArray())
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
            JsonElement stockoutEstimate = itemElement.TryGetProperty("stockoutEstimate", out JsonElement stockoutElement)
                ? stockoutElement
                : default;
            string? confidence = restockEstimate.ValueKind == JsonValueKind.Object
                ? ReadString(restockEstimate, "confidence")
                : null;
            string? estimatedAtTct = restockEstimate.ValueKind == JsonValueKind.Object
                ? ReadString(restockEstimate, "estimatedAtTct")
                : null;
            DateTimeOffset? estimatedAtUtc = restockEstimate.ValueKind == JsonValueKind.Object
                ? ReadDateTimeOffset(restockEstimate, "estimatedAt")
                : null;
            JsonElement availability = restockEstimate.ValueKind == JsonValueKind.Object
                && restockEstimate.TryGetProperty("postRestockAvailability", out JsonElement availabilityElement)
                    ? availabilityElement
                    : default;
            int? conservativeAvailabilityMinutes = availability.ValueKind == JsonValueKind.Object
                ? ReadNullableInt(availability, "conservativeMinutes")
                : null;
            int? medianAvailabilityMinutes = availability.ValueKind == JsonValueKind.Object
                ? ReadNullableInt(availability, "medianMinutes")
                : null;
            string? availabilityConfidence = availability.ValueKind == JsonValueKind.Object
                ? ReadString(availability, "confidence")
                : null;
            string? stockoutConfidence = stockoutEstimate.ValueKind == JsonValueKind.Object
                ? ReadString(stockoutEstimate, "confidence")
                : null;
            string? stockoutAtTct = stockoutEstimate.ValueKind == JsonValueKind.Object
                ? ReadString(stockoutEstimate, "estimatedAtTct")
                : null;
            DateTimeOffset? stockoutAtUtc = stockoutEstimate.ValueKind == JsonValueKind.Object
                ? ReadDateTimeOffset(stockoutEstimate, "estimatedAt")
                : null;
            decimal? stockoutMinutes = ReadNullableDecimal(itemElement, "stockoutMinutes")
                ?? (stockoutEstimate.ValueKind == JsonValueKind.Object
                    ? ReadNullableDecimal(stockoutEstimate, "estimatedMinutes")
                    : null);
            string? availabilityState = ReadString(itemElement, "availabilityState");
            bool? isProjectedViable = ReadNullableBool(itemElement, "isProjectedViable");

            DroqsRestockInfo info = new(
                itemId,
                country,
                confidence,
                estimatedAtTct,
                estimatedAtUtc,
                conservativeAvailabilityMinutes,
                medianAvailabilityMinutes,
                availabilityConfidence,
                stockoutConfidence,
                stockoutAtTct,
                stockoutAtUtc,
                stockoutMinutes,
                availabilityState,
                isProjectedViable);
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

    private static int? ReadNullableInt(JsonElement element, string propertyName)
    {
        int value = ReadInt(element, propertyName);
        return value > 0 ? value : null;
    }

    private static decimal? ReadNullableDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out decimal parsed)
            ? parsed
            : null;
    }

    private static bool? ReadNullableBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed)
            ? parsed
            : null;
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

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
