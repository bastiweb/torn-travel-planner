namespace TornStockTravel.Services;

public sealed record DroqsRestockInfo(
    int ItemId,
    string Country,
    string? Confidence,
    string? EstimatedAtTct);
