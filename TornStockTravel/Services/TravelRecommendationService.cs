namespace TornStockTravel.Services;

public static class TravelRecommendationService
{
    private static readonly TimeSpan RestockWindowTolerance = TimeSpan.FromMinutes(2);

    public static TravelRecommendation? GetRecommendation(
        IReadOnlyList<TravelDestination> destinations,
        TornTravelStatus? travelStatus,
        TornMoneyStatus? moneyStatus,
        int maxSpendPercent,
        RestockAvailabilityMode availabilityMode,
        DateTimeOffset now)
    {
        DateTimeOffset readyAt = TravelAvailabilityService.GetAvailability(travelStatus, now).ReadyInTornAt;
        decimal? wallet = moneyStatus?.Wallet;
        decimal? vault = moneyStatus?.Vault;
        decimal? spendLimit = moneyStatus is null
            ? null
            : moneyStatus.Total * maxSpendPercent / 100m;

        return destinations
            .SelectMany(destination => destination.Items.Select(item => BuildCandidate(destination, item, readyAt, wallet, vault, spendLimit, maxSpendPercent, availabilityMode)))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderByDescending(candidate => candidate.ProfitPerHour)
            .ThenByDescending(candidate => candidate.Profit)
            .ThenBy(candidate => candidate.DepartAt)
            .FirstOrDefault();
    }

    private static TravelRecommendation? BuildCandidate(
        TravelDestination destination,
        TravelItem item,
        DateTimeOffset readyAt,
        decimal? wallet,
        decimal? vault,
        decimal? spendLimit,
        int maxSpendPercent,
        RestockAvailabilityMode availabilityMode)
    {
        if (item.FlightDuration is null
            || item.Profit is null
            || item.ProfitPerHour is null
            || item.Profit <= 0
            || item.ProfitPerHour <= 0)
        {
            return null;
        }

        if (spendLimit is not null && item.CashNeeded > spendLimit.Value)
        {
            return null;
        }

        DateTimeOffset departAt;
        string reason;

        if (item.Quantity > 0)
        {
            departAt = readyAt;
            DateTimeOffset projectedArrival = departAt.Add(item.FlightDuration.Value);
            DateTimeOffset? stockoutEstimate = GetEffectiveStockoutEstimate(item);

            if (stockoutEstimate is not null
                && projectedArrival > stockoutEstimate.Value)
            {
                return null;
            }

            reason = item.RestockEstimateUtc is null
                ? "Current stock is profitable"
                : "Current stock is profitable; restock data is available as backup";
        }
        else if (item.RestockEstimateUtc is not null)
        {
            DateTimeOffset targetArrival = item.RestockEstimateUtc.Value.Subtract(RestockWindowTolerance);
            DateTimeOffset latestArrival = GetEffectiveStockoutEstimate(item)
                ?? item.RestockWindowEndUtc
                ?? item.RestockEstimateUtc.Value.Add(RestockWindowTolerance);
            DateTimeOffset latestDeparture = latestArrival.Subtract(item.FlightDuration.Value);

            if (readyAt > latestDeparture)
            {
                return null;
            }

            departAt = Max(readyAt, targetArrival.Subtract(item.FlightDuration.Value));
            reason = item.RestockAvailabilityDuration is null
                ? "Restock timing fits your next Torn availability"
                : $"Restock timing fits the {FormatAvailabilityMode(availabilityMode)} availability window";
        }
        else
        {
            return null;
        }

        DateTimeOffset arriveAt = departAt.Add(item.FlightDuration.Value);

        return new TravelRecommendation(
            destination.Name,
            destination.Code,
            item.Name,
            readyAt,
            departAt,
            arriveAt,
            item.Profit.Value,
            item.ProfitPerHour.Value,
            item.CashNeeded,
            wallet,
            vault,
            maxSpendPercent,
            reason);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    private static DateTimeOffset? GetEffectiveStockoutEstimate(TravelItem item)
    {
        return item.Prediction?.PredictedStockoutUtc ?? item.StockoutEstimateUtc;
    }

    private static string FormatAvailabilityMode(RestockAvailabilityMode mode)
    {
        return mode == RestockAvailabilityMode.Median ? "median" : "conservative";
    }
}
