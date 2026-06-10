namespace TornStockTravel.Services;

public static class TravelFlightTimes
{
    private static readonly IReadOnlyDictionary<string, string> DestinationAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mex"] = "mex",
            ["mexico"] = "mex",
            ["cay"] = "cay",
            ["cayman islands"] = "cay",
            ["can"] = "can",
            ["canada"] = "can",
            ["haw"] = "haw",
            ["hawaii"] = "haw",
            ["uni"] = "uni",
            ["united kingdom"] = "uni",
            ["arg"] = "arg",
            ["argentina"] = "arg",
            ["swi"] = "swi",
            ["switzerland"] = "swi",
            ["jap"] = "jap",
            ["japan"] = "jap",
            ["chi"] = "chi",
            ["china"] = "chi",
            ["uae"] = "uae",
            ["united arab emirates"] = "uae",
            ["sou"] = "sou",
            ["south africa"] = "sou"
        };

    private static readonly IReadOnlyDictionary<string, TimeSpan> FlightDurations =
        new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            ["mex"] = TimeSpan.FromMinutes(18),
            ["cay"] = TimeSpan.FromMinutes(25),
            ["can"] = TimeSpan.FromMinutes(29),
            ["haw"] = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(34),
            ["uni"] = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(51),
            ["arg"] = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(57),
            ["swi"] = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3),
            ["jap"] = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(38),
            ["chi"] = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(49),
            ["uae"] = TimeSpan.FromHours(3) + TimeSpan.FromMinutes(10),
            ["sou"] = TimeSpan.FromHours(3) + TimeSpan.FromMinutes(28)
        };

    public static TimeSpan? GetFlightDuration(string destinationCode)
    {
        string normalizedCode = NormalizeDestination(destinationCode);
        return FlightDurations.TryGetValue(normalizedCode, out TimeSpan duration)
            ? duration
            : null;
    }

    public static string NormalizeDestination(string destination)
    {
        string trimmed = destination.Trim();
        return DestinationAliases.TryGetValue(trimmed, out string? code)
            ? code
            : trimmed.ToLowerInvariant();
    }

    public static bool IsTornDestination(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return false;
        }

        string trimmed = destination.Trim();
        return trimmed.Equals("torn", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("torn city", StringComparison.OrdinalIgnoreCase);
    }
}
