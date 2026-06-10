using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class DroqsForecastClient : IDisposable
{
    private static readonly Uri DailyForecastUri = new("https://droqsdb.com/api/public/v1/daily-forecast");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;

    public DroqsForecastClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
    }

    public async Task<DroqsForecastSnapshot?> FetchDailyForecastAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, DailyForecastUri);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DroqsDB daily forecast API returned HTTP {(int)response.StatusCode}.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;

        if (!ReadBool(root, "ok") || !root.TryGetProperty("items", out JsonElement itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Dictionary<string, DroqsForecastInfo> items = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement itemElement in itemsElement.EnumerateArray())
        {
            DroqsForecastInfo? info = ReadForecastInfo(itemElement);
            if (info is null)
            {
                continue;
            }

            items[DroqsRestockClient.BuildKey(info.ItemId, info.CountryCode)] = info;
        }

        return new DroqsForecastSnapshot(
            ReadDateTimeOffset(root, "generatedAt"),
            ReadInt(root, "horizonHours"),
            ReadBool(root, "stale"),
            ReadString(root, "snapshotFreshness"),
            items);
    }

    private static DroqsForecastInfo? ReadForecastInfo(JsonElement itemElement)
    {
        if (itemElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int itemId = ReadInt(itemElement, "itemId");
        string country = ReadString(itemElement, "country") ?? string.Empty;
        string countryCode = DroqsRestockClient.NormalizeCountry(country);

        if (itemId <= 0 || string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        List<DroqsForecastWindow> windows = new();
        if (itemElement.TryGetProperty("flyOutWindows", out JsonElement windowsElement)
            && windowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement windowElement in windowsElement.EnumerateArray())
            {
                if (windowElement.ValueKind == JsonValueKind.Object)
                {
                    windows.Add(ReadForecastWindow(windowElement));
                }
            }
        }

        return new DroqsForecastInfo(
            itemId,
            ReadString(itemElement, "itemName") ?? string.Empty,
            country,
            countryCode,
            ReadInt(itemElement, "currentStock"),
            ReadNullableDecimal(itemElement, "profitPerItem"),
            ReadNullableDecimal(itemElement, "profitPerMinute"),
            ReadString(itemElement, "confidence"),
            ReadNullableInt(itemElement, "confidencePercent"),
            windows);
    }

    private static DroqsForecastWindow ReadForecastWindow(JsonElement windowElement)
    {
        return new DroqsForecastWindow(
            ReadDateTimeOffset(windowElement, "leaveAt"),
            ReadDateTimeOffset(windowElement, "leaveWindowEndAt"),
            ReadDateTimeOffset(windowElement, "arrivalAt"),
            ReadDateTimeOffset(windowElement, "arrivalWindowEndAt"),
            ReadDateTimeOffset(windowElement, "availabilityStartsAt"),
            ReadDateTimeOffset(windowElement, "availabilityEndsAt"),
            ReadString(windowElement, "availability"),
            ReadString(windowElement, "reason"),
            ReadString(windowElement, "confidence"),
            ReadNullableInt(windowElement, "confidencePercent"),
            ReadNullableInt(windowElement, "safetyMarginMinutes"),
            ReadNullableInt(windowElement, "minimumSafetyMarginMinutes"),
            ReadBool(windowElement, "tightWindow"));
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

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return ReadNullableInt(element, propertyName) ?? 0;
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

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : null;
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

        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
                ? parsed
                : null;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed) && parsed;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
                ? parsed
                : null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
