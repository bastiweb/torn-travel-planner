namespace TornStockTravel.Services;

public static class TravelAvailabilityService
{
    private static readonly TimeSpan LocalStayDuration = TimeSpan.FromSeconds(15);

    public static TravelAvailability GetAvailability(TornTravelStatus? travelStatus, DateTimeOffset now)
    {
        if (travelStatus is null || string.IsNullOrWhiteSpace(travelStatus.Destination))
        {
            return new TravelAvailability(now, "Available in Torn now", false);
        }

        if (TravelFlightTimes.IsTornDestination(travelStatus.Destination))
        {
            DateTimeOffset tornArrival = travelStatus.IsTraveling
                ? travelStatus.ArrivalAt ?? now.Add(travelStatus.GetRemaining(now) ?? TimeSpan.Zero)
                : now;
            return new TravelAvailability(tornArrival, tornArrival > now ? $"Returning to Torn at {FormatTime(tornArrival)}" : "Available in Torn now", false);
        }

        TimeSpan? returnFlightDuration = TravelFlightTimes.GetFlightDuration(travelStatus.Destination);
        if (returnFlightDuration is null)
        {
            return new TravelAvailability(now, "Travel destination unknown; assuming Torn availability now", false);
        }

        DateTimeOffset destinationReadyAt = travelStatus.IsTraveling
            ? travelStatus.ArrivalAt ?? now.Add(travelStatus.GetRemaining(now) ?? TimeSpan.Zero)
            : now;
        DateTimeOffset tornReadyAt = destinationReadyAt.Add(LocalStayDuration).Add(returnFlightDuration.Value);
        string description = travelStatus.IsTraveling
            ? $"Outbound to {travelStatus.DestinationText}; return to Torn estimated {FormatTime(tornReadyAt)}"
            : $"Currently in {travelStatus.DestinationText}; return to Torn estimated {FormatTime(tornReadyAt)}";

        return new TravelAvailability(tornReadyAt, description, true);
    }

    private static string FormatTime(DateTimeOffset value)
    {
        return value.LocalDateTime.ToString("HH:mm");
    }
}

public sealed record TravelAvailability(
    DateTimeOffset ReadyInTornAt,
    string Description,
    bool RequiresReturnFlight);
