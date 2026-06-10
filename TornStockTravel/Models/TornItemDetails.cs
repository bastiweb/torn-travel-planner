namespace TornStockTravel.Services;

public sealed record TornItemDetails(
    int Id,
    string Name,
    decimal MarketValue,
    string? ImageUrl);
