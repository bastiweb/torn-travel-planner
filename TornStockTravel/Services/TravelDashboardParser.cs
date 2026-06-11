using System.Globalization;
using System.Text.Json;

namespace TornStockTravel.Services;

public static class TravelDashboardParser
{
    private static readonly IReadOnlyDictionary<string, string> DestinationNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mex"] = "Mexico",
            ["cay"] = "Cayman Islands",
            ["can"] = "Canada",
            ["haw"] = "Hawaii",
            ["uni"] = "United Kingdom",
            ["arg"] = "Argentina",
            ["swi"] = "Switzerland",
            ["jap"] = "Japan",
            ["chi"] = "China",
            ["uae"] = "United Arab Emirates",
            ["sou"] = "South Africa"
        };

    private static readonly IReadOnlyDictionary<string, int> DestinationOrder =
        DestinationNames.Keys
            .Select((code, index) => new { code, index })
            .ToDictionary(entry => entry.code, entry => entry.index, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TravelDestination> Parse(
        JsonElement data,
        IReadOnlyDictionary<int, TornItemDetails>? itemDetails = null,
        IReadOnlyDictionary<int, TornInventoryItem>? inventory = null,
        IReadOnlyDictionary<int, decimal>? bazaarPrices = null,
        int buyCapacity = 0,
        RestockAvailabilityMode availabilityMode = RestockAvailabilityMode.Conservative,
        IReadOnlyDictionary<string, DroqsRestockInfo>? restocks = null)
    {
        if (data.ValueKind == JsonValueKind.Object
            && TryGetProperty(data, "stocks", out JsonElement stocksElement)
            && stocksElement.ValueKind == JsonValueKind.Object)
        {
            data = stocksElement;
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<TravelDestination>();
        }

        List<TravelDestination> destinations = new();

        foreach (JsonProperty destinationProperty in data.EnumerateObject())
        {
            string code = destinationProperty.Name.ToLowerInvariant();
            string destinationName = DestinationNames.TryGetValue(code, out string? knownName)
                ? knownName
                : code.ToUpperInvariant();
            TimeSpan? flightDuration = TravelFlightTimes.GetFlightDuration(code);
            List<TravelItem> items = ParseItems(
                destinationProperty.Value,
                itemDetails,
                inventory,
                bazaarPrices,
                flightDuration,
                buyCapacity,
                availabilityMode,
                restocks,
                code);

            destinations.Add(new TravelDestination(
                code,
                destinationName,
                items
                    .Where(item => item.UnitProfit > 0)
                    .OrderByDescending(item => item.ProfitPerHour ?? decimal.MinValue)
                    .ThenByDescending(item => item.Profit ?? decimal.MinValue)
                    .ThenByDescending(item => item.Quantity)
                    .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()));
        }

        return destinations
            .Where(destination => destination.ItemCount > 0)
            .OrderBy(destination => DestinationOrder.TryGetValue(destination.Code, out int order) ? order : int.MaxValue)
            .ThenBy(destination => destination.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<TravelItem> ParseItems(
        JsonElement value,
        IReadOnlyDictionary<int, TornItemDetails>? itemDetails,
        IReadOnlyDictionary<int, TornInventoryItem>? inventory,
        IReadOnlyDictionary<int, decimal>? bazaarPrices,
        TimeSpan? flightDuration,
        int buyCapacity,
        RestockAvailabilityMode availabilityMode,
        IReadOnlyDictionary<string, DroqsRestockInfo>? restocks,
        string destinationCode)
    {
        List<TravelItem> items = new();

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement itemElement in value.EnumerateArray())
            {
                AddItem(items, itemElement, itemDetails, inventory, bazaarPrices, flightDuration, buyCapacity, availabilityMode, restocks, destinationCode);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(value, "stocks", out JsonElement nestedStocks))
            {
                return ParseItems(nestedStocks, itemDetails, inventory, bazaarPrices, flightDuration, buyCapacity, availabilityMode, restocks, destinationCode);
            }

            if (LooksLikeItem(value))
            {
                AddItem(items, value, itemDetails, inventory, bazaarPrices, flightDuration, buyCapacity, availabilityMode, restocks, destinationCode);
            }
            else
            {
                foreach (JsonProperty itemProperty in value.EnumerateObject())
                {
                    AddItem(items, itemProperty.Value, itemDetails, inventory, bazaarPrices, flightDuration, buyCapacity, availabilityMode, restocks, destinationCode);
                }
            }
        }

        return items;
    }

    private static bool LooksLikeItem(JsonElement value)
    {
        return TryGetProperty(value, "name", out _)
            || TryGetProperty(value, "quantity", out _)
            || TryGetProperty(value, "cost", out _);
    }

    private static void AddItem(
        List<TravelItem> items,
        JsonElement value,
        IReadOnlyDictionary<int, TornItemDetails>? itemDetails,
        IReadOnlyDictionary<int, TornInventoryItem>? inventory,
        IReadOnlyDictionary<int, decimal>? bazaarPrices,
        TimeSpan? flightDuration,
        int buyCapacity,
        RestockAvailabilityMode availabilityMode,
        IReadOnlyDictionary<string, DroqsRestockInfo>? restocks,
        string destinationCode)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        int id = TryGetProperty(value, "id", out JsonElement idElement)
            ? ReadInt(idElement)
            : 0;

        TornItemDetails? details = id > 0
            && itemDetails is not null
            && itemDetails.TryGetValue(id, out TornItemDetails? foundDetails)
                ? foundDetails
                : null;
        TornInventoryItem? inventoryItem = id > 0
            && inventory is not null
            && inventory.TryGetValue(id, out TornInventoryItem? foundInventoryItem)
                ? foundInventoryItem
                : null;
        decimal? bazaarPrice = id > 0
            && bazaarPrices is not null
            && bazaarPrices.TryGetValue(id, out decimal foundBazaarPrice)
                ? foundBazaarPrice
                : null;
        DroqsRestockInfo? restockInfo = id > 0
            && restocks is not null
            && restocks.TryGetValue(DroqsRestockClient.BuildKey(id, destinationCode), out DroqsRestockInfo? foundRestock)
                ? foundRestock
                : null;

        string name = TryGetProperty(value, "name", out JsonElement nameElement)
            ? ReadString(nameElement)
            : details?.Name ?? "Unknown item";

        int quantity = TryGetProperty(value, "quantity", out JsonElement quantityElement)
            ? ReadInt(quantityElement)
            : 0;

        decimal cost = TryGetProperty(value, "cost", out JsonElement costElement)
            ? ReadDecimal(costElement)
            : 0;

        items.Add(new TravelItem(
            id,
            name,
            quantity,
            cost,
            details?.MarketValue,
            bazaarPrice,
            details?.ImageUrl,
            inventoryItem?.Amount ?? 0,
            flightDuration,
            buyCapacity,
            availabilityMode,
            restockInfo));
    }

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement property)
    {
        foreach (JsonProperty jsonProperty in value.EnumerateObject())
        {
            if (string.Equals(jsonProperty.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = jsonProperty.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string ReadString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static int ReadInt(JsonElement value)
    {
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

    private static decimal ReadDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
        {
            return parsed;
        }

        return 0;
    }
}
