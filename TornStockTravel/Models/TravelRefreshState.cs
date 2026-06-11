using System.Text.Json;

namespace TornStockTravel.Services;

public enum TravelRefreshStatus
{
    Idle,
    Loading,
    Success,
    Stale,
    Error
}

public sealed record TravelRefreshState(
    Uri Endpoint,
    TravelRefreshStatus Status,
    JsonElement? Data,
    IReadOnlyList<TravelDestination> Destinations,
    TornTravelStatus? TravelStatus,
    TornMoneyStatus? MoneyStatus,
    TornBarsStatus? BarsStatus,
    DroqsForecastSnapshot? ForecastSnapshot,
    string? Error,
    DateTimeOffset? LastUpdated,
    DateTimeOffset? StaleSince,
    DateTimeOffset? LastStarted,
    DateTimeOffset? NextRefreshAt,
    TimeSpan RefreshInterval);
