using System.IO;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class TravelCacheService
{
    private readonly string _cachePath;

    public TravelCacheService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDirectory = Path.Combine(appData, "TornStockTravel");
        Directory.CreateDirectory(appDirectory);
        _cachePath = Path.Combine(appDirectory, "travel-cache.json");
    }

    public TravelCacheSnapshot? Load()
    {
        if (!File.Exists(_cachePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<TravelCacheSnapshot>(json);
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Travel cache could not be loaded: {ex.Message}");
            return null;
        }
    }

    public void Save(TravelCacheSnapshot snapshot)
    {
        try
        {
            string json = JsonSerializer.Serialize(snapshot, JsonOptions.Pretty);
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Travel cache could not be saved: {ex.Message}");
        }
    }
}

public sealed record TravelCacheSnapshot(
    DateTimeOffset SavedAt,
    IReadOnlyList<TravelDestination> Destinations,
    TornTravelStatus? TravelStatus,
    TornMoneyStatus? MoneyStatus,
    TornBarsStatus? BarsStatus,
    TornCooldownStatus? CooldownStatus,
    DroqsForecastSnapshot? ForecastSnapshot);
