using System.Text.Json;

namespace TornStockTravel;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true
    };
}
