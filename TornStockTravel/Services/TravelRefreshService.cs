using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Threading;

namespace TornStockTravel.Services;

public sealed class TravelRefreshService : IDisposable
{
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly YataTravelClient _client;
    private readonly TornItemsClient _tornItemsClient;
    private readonly TornInventoryClient _tornInventoryClient;
    private readonly TornTravelStatusClient _tornTravelStatusClient;
    private readonly TornMoneyClient _tornMoneyClient;
    private readonly DroqsRestockClient _droqsRestockClient;
    private readonly DispatcherTimer _timer;
    private string _tornApiKey;
    private IReadOnlyDictionary<int, decimal> _bazaarPrices;
    private int _buyCapacity;
    private RestockAvailabilityMode _availabilityMode;
    private TimeSpan _refreshInterval;
    private bool _isRefreshing;
    private TravelRefreshState _state;

    public event EventHandler<TravelRefreshState>? StateChanged;

    public TravelRefreshService(
        string tornApiKey = "",
        IReadOnlyDictionary<int, decimal>? bazaarPrices = null,
        int buyCapacity = 0,
        RestockAvailabilityMode availabilityMode = RestockAvailabilityMode.Conservative,
        TimeSpan? refreshInterval = null)
    {
        _client = new YataTravelClient();
        _tornItemsClient = new TornItemsClient();
        _tornInventoryClient = new TornInventoryClient();
        _tornTravelStatusClient = new TornTravelStatusClient();
        _tornMoneyClient = new TornMoneyClient();
        _droqsRestockClient = new DroqsRestockClient();
        _tornApiKey = tornApiKey;
        _bazaarPrices = bazaarPrices ?? new Dictionary<int, decimal>();
        _buyCapacity = buyCapacity;
        _availabilityMode = availabilityMode;
        _refreshInterval = NormalizeRefreshInterval(refreshInterval ?? DefaultRefreshInterval);
        _timer = new DispatcherTimer
        {
            Interval = _refreshInterval
        };
        _timer.Tick += Timer_Tick;

        _state = new TravelRefreshState(
            YataTravelClient.TravelExportUri,
            TravelRefreshStatus.Idle,
            null,
            Array.Empty<TravelDestination>(),
            null,
            null,
            null,
            null,
            null,
            null,
            _refreshInterval);
    }

    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }

        _ = RefreshNowAsync();
        ScheduleNextRefresh();
    }

    public TravelRefreshState GetState()
    {
        return _state;
    }

    public void SetTornApiKey(string apiKey)
    {
        _tornApiKey = apiKey;
    }

    public void SetBazaarPrices(IReadOnlyDictionary<int, decimal> bazaarPrices)
    {
        _bazaarPrices = bazaarPrices;
    }

    public void SetBuyCapacity(int buyCapacity)
    {
        _buyCapacity = buyCapacity;
    }

    public void SetRestockAvailabilityMode(RestockAvailabilityMode availabilityMode)
    {
        _availabilityMode = availabilityMode;
    }

    public void SetRefreshInterval(TimeSpan refreshInterval)
    {
        _refreshInterval = NormalizeRefreshInterval(refreshInterval);
        _timer.Interval = _refreshInterval;
        PatchState(_state with
        {
            RefreshInterval = _refreshInterval
        });
        ScheduleNextRefresh();
    }

    public async Task<TravelRefreshState> RefreshNowAsync()
    {
        if (_isRefreshing)
        {
            return _state;
        }

        _isRefreshing = true;
        PatchState(_state with
        {
            Status = TravelRefreshStatus.Loading,
            Error = null,
            LastStarted = DateTimeOffset.Now
        });

        try
        {
            ConcurrentBag<string> warnings = new();
            Task<JsonElement> dataTask = _client.FetchTravelExportAsync();
            Task<IReadOnlyDictionary<int, TornItemDetails>> itemDetailsTask = string.IsNullOrWhiteSpace(_tornApiKey)
                ? Task.FromResult<IReadOnlyDictionary<int, TornItemDetails>>(new Dictionary<int, TornItemDetails>())
                : FetchOptionalAsync(
                    "Torn item details",
                    () => _tornItemsClient.FetchItemsAsync(_tornApiKey),
                    new Dictionary<int, TornItemDetails>(),
                    warnings);
            Task<IReadOnlyDictionary<int, TornInventoryItem>> inventoryTask = string.IsNullOrWhiteSpace(_tornApiKey)
                ? Task.FromResult<IReadOnlyDictionary<int, TornInventoryItem>>(new Dictionary<int, TornInventoryItem>())
                : FetchOptionalAsync(
                    "Torn inventory",
                    () => _tornInventoryClient.FetchInventoryAsync(_tornApiKey),
                    new Dictionary<int, TornInventoryItem>(),
                    warnings);
            Task<TornTravelStatus?> travelStatusTask = string.IsNullOrWhiteSpace(_tornApiKey)
                ? Task.FromResult<TornTravelStatus?>(null)
                : FetchOptionalAsync(
                    "Torn travel status",
                    () => _tornTravelStatusClient.FetchTravelStatusAsync(_tornApiKey),
                    _state.TravelStatus,
                    warnings);
            Task<TornMoneyStatus?> moneyStatusTask = string.IsNullOrWhiteSpace(_tornApiKey)
                ? Task.FromResult<TornMoneyStatus?>(null)
                : FetchOptionalAsync(
                    "Torn money",
                    () => _tornMoneyClient.FetchMoneyAsync(_tornApiKey),
                    _state.MoneyStatus,
                    warnings);
            Task<IReadOnlyDictionary<string, DroqsRestockInfo>> restocksTask =
                FetchOptionalAsync(
                    "DroqsDB restocks",
                    () => _droqsRestockClient.FetchRestockingSoonAsync(),
                    new Dictionary<string, DroqsRestockInfo>(),
                    warnings);

            await Task.WhenAll(dataTask, itemDetailsTask, inventoryTask, travelStatusTask, moneyStatusTask, restocksTask);

            JsonElement data = await dataTask;
            IReadOnlyDictionary<int, TornItemDetails> itemDetails = await itemDetailsTask;
            IReadOnlyDictionary<int, TornInventoryItem> inventory = await inventoryTask;
            TornTravelStatus? travelStatus = await travelStatusTask;
            TornMoneyStatus? moneyStatus = await moneyStatusTask;
            IReadOnlyDictionary<string, DroqsRestockInfo> restocks = await restocksTask;
            IReadOnlyList<TravelDestination> destinations =
                TravelDashboardParser.Parse(data, itemDetails, inventory, _bazaarPrices, _buyCapacity, _availabilityMode, restocks);

            PatchState(_state with
            {
                Status = TravelRefreshStatus.Success,
                Data = data,
                Destinations = destinations,
                TravelStatus = travelStatus,
                MoneyStatus = moneyStatus,
                Error = warnings.IsEmpty
                    ? null
                    : string.Join(Environment.NewLine, warnings.OrderBy(warning => warning, StringComparer.CurrentCultureIgnoreCase)),
                LastUpdated = DateTimeOffset.Now
            });
        }
        catch (Exception ex)
        {
            PatchState(_state with
            {
                Status = TravelRefreshStatus.Error,
                Error = ex.Message
            });
        }
        finally
        {
            _isRefreshing = false;
            ScheduleNextRefresh();
        }

        return _state;
    }

    private static async Task<T> FetchOptionalAsync<T>(
        string label,
        Func<Task<T>> fetch,
        T fallback,
        ConcurrentBag<string> warnings)
    {
        try
        {
            return await fetch();
        }
        catch (Exception ex)
        {
            warnings.Add($"{label}: {ex.Message}");
            return fallback;
        }
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        await RefreshNowAsync();
    }

    private void ScheduleNextRefresh()
    {
        PatchState(_state with
        {
            RefreshInterval = _refreshInterval,
            NextRefreshAt = DateTimeOffset.Now.Add(_refreshInterval)
        });
    }

    private static TimeSpan NormalizeRefreshInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromMinutes(1))
        {
            return TimeSpan.FromMinutes(1);
        }

        return interval > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : interval;
    }

    private void PatchState(TravelRefreshState nextState)
    {
        _state = nextState;
        StateChanged?.Invoke(this, _state);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _client.Dispose();
        _tornItemsClient.Dispose();
        _tornInventoryClient.Dispose();
        _tornTravelStatusClient.Dispose();
        _tornMoneyClient.Dispose();
        _droqsRestockClient.Dispose();
    }
}
