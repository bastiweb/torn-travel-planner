namespace TornStockTravel.Services;

public sealed record DroqsForecastSnapshot(
    DateTimeOffset? GeneratedAt,
    int HorizonHours,
    bool Stale,
    string? SnapshotFreshness,
    IReadOnlyDictionary<string, DroqsForecastInfo> Items)
{
    public int ItemCount => Items.Count;

    public int WindowCount => Items.Values.Sum(item => item.FlyOutWindows.Count);

    public string StatusText
    {
        get
        {
            string freshness = string.IsNullOrWhiteSpace(SnapshotFreshness)
                ? "unknown freshness"
                : SnapshotFreshness;
            string staleText = Stale ? "stale" : freshness;

            return $"Droqs forecast loaded: {ItemCount:N0} items, {WindowCount:N0} fly-out windows ({staleText}). Used for timing when it matches your strategy.";
        }
    }
}

public sealed record DroqsForecastInfo(
    int ItemId,
    string ItemName,
    string Country,
    string CountryCode,
    int CurrentStock,
    decimal? ProfitPerItem,
    decimal? ProfitPerMinute,
    string? Confidence,
    int? ConfidencePercent,
    IReadOnlyList<DroqsForecastWindow> FlyOutWindows);

public sealed record DroqsForecastWindow(
    DateTimeOffset? LeaveAt,
    DateTimeOffset? LeaveWindowEndAt,
    DateTimeOffset? ArrivalAt,
    DateTimeOffset? ArrivalWindowEndAt,
    DateTimeOffset? AvailabilityStartsAt,
    DateTimeOffset? AvailabilityEndsAt,
    string? Availability,
    string? Reason,
    string? Confidence,
    int? ConfidencePercent,
    int? SafetyMarginMinutes,
    int? MinimumSafetyMarginMinutes,
    bool TightWindow);
