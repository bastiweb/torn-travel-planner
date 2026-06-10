namespace TornStockTravel.Services;

public enum RestockAvailabilityMode
{
    Conservative,
    Median
}

public sealed record DroqsRestockInfo(
    int ItemId,
    string Country,
    string? Confidence,
    string? EstimatedAtTct,
    DateTimeOffset? EstimatedAtUtc,
    int? ConservativeAvailabilityMinutes,
    int? MedianAvailabilityMinutes,
    string? AvailabilityConfidence,
    string? StockoutConfidence,
    string? StockoutAtTct,
    DateTimeOffset? StockoutAtUtc,
    decimal? StockoutMinutes,
    string? AvailabilityState,
    bool? IsProjectedViable)
{
    public TimeSpan? GetAvailabilityDuration(RestockAvailabilityMode mode)
    {
        int? minutes = mode == RestockAvailabilityMode.Median
            ? MedianAvailabilityMinutes
            : ConservativeAvailabilityMinutes;

        return minutes is null || minutes.Value <= 0
            ? null
            : TimeSpan.FromMinutes(minutes.Value);
    }

    public string GetAvailabilityLabel(RestockAvailabilityMode mode)
    {
        int? minutes = mode == RestockAvailabilityMode.Median
            ? MedianAvailabilityMinutes
            : ConservativeAvailabilityMinutes;

        if (minutes is null || minutes.Value <= 0)
        {
            return "-";
        }

        string modeText = mode == RestockAvailabilityMode.Median ? "median" : "safe";
        string confidenceText = string.IsNullOrWhiteSpace(AvailabilityConfidence)
            ? string.Empty
            : $" ({AvailabilityConfidence})";

        return $"{modeText} {FormatMinutes(minutes.Value)}{confidenceText}";
    }

    public string StockoutEstimateText => StockoutAtTct ?? "-";

    public string StockoutConfidenceText => StockoutConfidence ?? "-";

    public DroqsRestockInfo Merge(DroqsRestockInfo other)
    {
        return this with
        {
            Confidence = Confidence ?? other.Confidence,
            EstimatedAtTct = EstimatedAtTct ?? other.EstimatedAtTct,
            EstimatedAtUtc = EstimatedAtUtc ?? other.EstimatedAtUtc,
            ConservativeAvailabilityMinutes = ConservativeAvailabilityMinutes ?? other.ConservativeAvailabilityMinutes,
            MedianAvailabilityMinutes = MedianAvailabilityMinutes ?? other.MedianAvailabilityMinutes,
            AvailabilityConfidence = AvailabilityConfidence ?? other.AvailabilityConfidence,
            StockoutConfidence = StockoutConfidence ?? other.StockoutConfidence,
            StockoutAtTct = StockoutAtTct ?? other.StockoutAtTct,
            StockoutAtUtc = StockoutAtUtc ?? other.StockoutAtUtc,
            StockoutMinutes = StockoutMinutes ?? other.StockoutMinutes,
            AvailabilityState = AvailabilityState ?? other.AvailabilityState,
            IsProjectedViable = IsProjectedViable ?? other.IsProjectedViable
        };
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes >= 60)
        {
            return $"{minutes / 60}h {minutes % 60}m";
        }

        return $"{minutes}m";
    }
}
