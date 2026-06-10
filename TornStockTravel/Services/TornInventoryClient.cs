using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class TornInventoryClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private const int PageSize = 20;
    private const int MaxPagesPerCategory = 50;
    private static readonly string[] InventoryCategories = { "Flower", "Plushie", "Tool", "Other", "Material" };

    private readonly HttpClient _httpClient;

    public TornInventoryClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
    }

    public async Task<IReadOnlyDictionary<int, TornInventoryItem>> FetchInventoryAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new Dictionary<int, TornInventoryItem>();
        }

        Dictionary<int, TornInventoryItem> inventory = new();

        foreach (string category in InventoryCategories)
        {
            IReadOnlyList<TornInventoryItem> categoryItems =
                await FetchAllCategoryInventoryAsync(apiKey, category, cancellationToken);

            foreach (TornInventoryItem item in categoryItems)
            {
                if (inventory.TryGetValue(item.Id, out TornInventoryItem? existing))
                {
                    inventory[item.Id] = existing with
                    {
                        Amount = existing.Amount + item.Amount
                    };
                }
                else
                {
                    inventory[item.Id] = item;
                }
            }
        }

        return inventory;
    }

    private async Task<IReadOnlyList<TornInventoryItem>> FetchAllCategoryInventoryAsync(
        string apiKey,
        string category,
        CancellationToken cancellationToken)
    {
        List<TornInventoryItem> items = new();
        int offset = 0;

        for (int page = 0; page < MaxPagesPerCategory; page++)
        {
            IReadOnlyList<TornInventoryItem> pageItems =
                await FetchCategoryInventoryAsync(apiKey, category, offset, cancellationToken);
            items.AddRange(pageItems);

            if (pageItems.Count < PageSize)
            {
                return items;
            }

            offset += PageSize;
        }

        return items;
    }

    private async Task<IReadOnlyList<TornInventoryItem>> FetchCategoryInventoryAsync(
        string apiKey,
        string category,
        int offset,
        CancellationToken cancellationToken)
    {
        Uri requestUri = new(
            $"https://api.torn.com/v2/user/inventory?cat={Uri.EscapeDataString(category)}&offset={offset}&limit={PageSize}");

        using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", apiKey.Trim());
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Torn inventory API returned HTTP {(int)response.StatusCode}.");
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
                : "Unknown Torn inventory API error";

            throw new InvalidOperationException($"Torn inventory API error {code}: {message}");
        }

        if (!root.TryGetProperty("inventory", out JsonElement inventoryElement)
            || !inventoryElement.TryGetProperty("items", out JsonElement itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TornInventoryItem>();
        }

        List<TornInventoryItem> items = new();

        foreach (JsonElement itemElement in itemsElement.EnumerateArray())
        {
            if (itemElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int id = ReadInt(itemElement, "id");
            int amount = ReadInt(itemElement, "amount");
            string name = itemElement.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            if (id > 0 && amount > 0)
            {
                items.Add(new TornInventoryItem(id, name, amount));
            }
        }

        return items;
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

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return 0;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
