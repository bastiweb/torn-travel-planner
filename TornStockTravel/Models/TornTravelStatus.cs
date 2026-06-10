namespace TornStockTravel.Services;

public sealed record TornTravelStatus(
    string? Destination,
    DateTimeOffset? DepartedAt,
    DateTimeOffset? ArrivalAt,
    int? TimeLeftSeconds,
    DateTimeOffset FetchedAt)
{
    public bool IsTraveling => !string.IsNullOrWhiteSpace(Destination) && GetRemaining(DateTimeOffset.Now) > TimeSpan.Zero;

    public TimeSpan? GetRemaining(DateTimeOffset now)
    {
        TimeSpan? remaining = ArrivalAt is not null
            ? ArrivalAt.Value - now
            : TimeLeftSeconds is null
                ? null
                : FetchedAt.AddSeconds(TimeLeftSeconds.Value) - now;

        if (remaining is null)
        {
            return null;
        }

        return remaining.Value > TimeSpan.Zero ? remaining.Value : TimeSpan.Zero;
    }

    public string DestinationText => string.IsNullOrWhiteSpace(Destination) ? "Not traveling" : Destination;

    public string DepartedText => DepartedAt is null ? "-" : DepartedAt.Value.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss");

    public string ArrivalText => ArrivalAt is null ? "-" : ArrivalAt.Value.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss");
}
