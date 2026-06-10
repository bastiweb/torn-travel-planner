using System.Net.Http;
using System.Text.Json;
using System.IO;

namespace TornStockTravel.Services;

public sealed class YataTravelClient : IDisposable
{
    public static readonly Uri TravelExportUri = new("https://yata.yt/api/v1/travel/export/");

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;

    public YataTravelClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
    }

    public async Task<JsonElement> FetchTravelExportAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, TravelExportUri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"YATA API returned HTTP {(int)response.StatusCode}.");
        }

        string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"YATA API returned unexpected content type '{contentType}'.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
