using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TornStockTravel.Services;

namespace TornStockTravel;

public partial class MainWindow : Window
{
    private static readonly TimeSpan DefaultPlannerWindowDuration = TimeSpan.FromHours(12);

    private readonly AppSettingsService _settingsService;
    private readonly TravelRefreshService _travelRefreshService;
    private readonly UpdateCheckerService _updateCheckerService;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _errorToastTimer;
    private AppSettings _settings;
    private TornTravelStatus? _travelStatus;
    private TornMoneyStatus? _moneyStatus;
    private TornBarsStatus? _barsStatus;
    private DroqsForecastSnapshot? _forecastSnapshot;
    private TravelPlannerStrategy _plannerStrategy = TravelPlannerStrategy.Balanced;
    private DateTimeOffset _plannerWindowStart;
    private DateTimeOffset _plannerWindowEnd;
    private string? _lastShownError;
    private IReadOnlyList<TravelDestination> _allDestinations = Array.Empty<TravelDestination>();

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new AppSettingsService();
        _updateCheckerService = new UpdateCheckerService();
        _settings = _settingsService.Load();
        ApiKeyBox.Password = _settings.TornApiKey;
        BuyCapacityBox.Text = _settings.BuyCapacity > 0
            ? _settings.BuyCapacity.ToString("N0")
            : string.Empty;
        RefreshIntervalBox.Text = _settings.RefreshIntervalMinutes.ToString("N0");
        MaxSpendPercentBox.Text = _settings.MaxSpendPercent.ToString("N0");
        SelectRestockAvailabilityMode(_settings.RestockAvailabilityMode);
        RestockAvailabilityModeBox.SelectionChanged += RestockAvailabilityModeBox_SelectionChanged;
        SelectPlannerStrategy(_plannerStrategy);
        PlannerStrategyBox.SelectionChanged += PlannerStrategyBox_SelectionChanged;
        InitializePlannerWindow();
        ApiKeyStatusText.Text = string.IsNullOrWhiteSpace(_settings.TornApiKey)
            ? "No key saved"
            : "Key saved";

        _travelRefreshService = new TravelRefreshService(
            _settings.TornApiKey,
            _settings.BazaarPrices,
            _settings.BuyCapacity,
            _settings.RestockAvailabilityMode,
            TimeSpan.FromMinutes(_settings.RefreshIntervalMinutes));
        _travelRefreshService.StateChanged += OnTravelStateChanged;
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;
        _countdownTimer.Start();
        _errorToastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(9)
        };
        _errorToastTimer.Tick += ErrorToastTimer_Tick;

        ApplyState(_travelRefreshService.GetState());
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _travelRefreshService.Start();
        _ = CheckForUpdatesAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _travelRefreshService.StateChanged -= OnTravelStateChanged;
        RestockAvailabilityModeBox.SelectionChanged -= RestockAvailabilityModeBox_SelectionChanged;
        PlannerStrategyBox.SelectionChanged -= PlannerStrategyBox_SelectionChanged;
        _countdownTimer.Stop();
        _countdownTimer.Tick -= CountdownTimer_Tick;
        _errorToastTimer.Stop();
        _errorToastTimer.Tick -= ErrorToastTimer_Tick;
        _travelRefreshService.Dispose();
        _updateCheckerService.Dispose();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await _travelRefreshService.RefreshNowAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        GitHubUpdateInfo? updateInfo;
        try
        {
            updateInfo = await _updateCheckerService.CheckForUpdateAsync(UpdateCheckerService.GetCurrentVersion());
        }
        catch
        {
            return;
        }

        if (updateInfo is null)
        {
            return;
        }

        string releaseNotes = FormatReleaseNotesPreview(updateInfo.Body);
        MessageBoxResult result = MessageBox.Show(
            this,
            $"Version {updateInfo.TagName} is available.\n\nDownload size: {updateInfo.AssetSizeText}\n\nDownload and install now?\n\n{releaseNotes}",
            "Torn Stock Travel Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            RefreshButton.IsEnabled = false;
            StatusText.Text = "Downloading update...";
            string downloadedExePath = await _updateCheckerService.DownloadUpdateAsync(updateInfo);
            MessageBox.Show(
                this,
                "The app will close for a moment, install the update, and restart automatically.",
                "Installing update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            _updateCheckerService.StartUpdater(downloadedExePath);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            RefreshButton.IsEnabled = true;
            StatusText.Text = "Ready";
            ShowErrorToastIfNeeded($"Update failed: {ex.Message}");
        }
    }

    private void SaveRefreshIntervalButton_Click(object sender, RoutedEventArgs e)
    {
        string value = RefreshIntervalBox.Text.Trim();

        if (!int.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out int minutes)
            || minutes < 1)
        {
            RefreshIntervalBox.Text = _settings.RefreshIntervalMinutes.ToString("N0");
            return;
        }

        minutes = Math.Min(minutes, AppSettings.MaximumRefreshIntervalMinutes);
        _settings = _settings with
        {
            RefreshIntervalMinutes = minutes
        };
        _settingsService.Save(_settings);
        _travelRefreshService.SetRefreshInterval(TimeSpan.FromMinutes(minutes));
        RefreshIntervalBox.Text = minutes.ToString("N0");
    }

    private void SaveMaxSpendPercentButton_Click(object sender, RoutedEventArgs e)
    {
        string value = MaxSpendPercentBox.Text.Trim();

        if (!int.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out int percent)
            || percent < AppSettings.MinimumMaxSpendPercent)
        {
            MaxSpendPercentBox.Text = _settings.MaxSpendPercent.ToString("N0");
            return;
        }

        percent = Math.Min(percent, AppSettings.MaximumMaxSpendPercent);
        _settings = _settings with
        {
            MaxSpendPercent = percent
        };
        _settingsService.Save(_settings);
        MaxSpendPercentBox.Text = percent.ToString("N0");
        UpdateRecommendation();
        UpdateTravelPlanner();
    }

    private async void RestockAvailabilityModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RestockAvailabilityMode mode = GetSelectedRestockAvailabilityMode();
        if (_settings.RestockAvailabilityMode == mode)
        {
            return;
        }

        _settings = _settings with
        {
            RestockAvailabilityMode = mode
        };
        _settingsService.Save(_settings);
        _travelRefreshService.SetRestockAvailabilityMode(mode);
        UpdateRecommendation();
        UpdateTravelPlanner();

        await _travelRefreshService.RefreshNowAsync();
    }

    private void PlannerStrategyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _plannerStrategy = GetSelectedPlannerStrategy();
        UpdateTravelPlanner();
    }

    private void PlannerWindow_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTravelPlanner();
    }

    private void PlannerFilter_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTravelPlanner();
    }

    private void PlannerWindowBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        UpdateTravelPlanner();
        Keyboard.ClearFocus();
    }

    private void PlannerUseNowButton_Click(object sender, RoutedEventArgs e)
    {
        SetPlannerWindow(DateTimeOffset.Now, DateTimeOffset.Now.Add(DefaultPlannerWindowDuration));
        UpdateTravelPlanner();
    }

    private async void SaveBuyCapacityButton_Click(object sender, RoutedEventArgs e)
    {
        string value = BuyCapacityBox.Text.Trim();
        int buyCapacity = 0;

        if (!string.IsNullOrWhiteSpace(value)
            && (!int.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out buyCapacity)
                || buyCapacity < 0))
        {
            BuyCapacityBox.Text = _settings.BuyCapacity > 0
                ? _settings.BuyCapacity.ToString("N0")
                : string.Empty;
            return;
        }

        _settings = _settings with
        {
            BuyCapacity = buyCapacity
        };
        _settingsService.Save(_settings);
        _travelRefreshService.SetBuyCapacity(buyCapacity);
        BuyCapacityBox.Text = buyCapacity > 0 ? buyCapacity.ToString("N0") : string.Empty;

        await _travelRefreshService.RefreshNowAsync();
    }

    private async void SaveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        string apiKey = ApiKeyBox.Password.Trim();
        _settings = _settings with
        {
            TornApiKey = apiKey
        };
        _settingsService.Save(_settings);
        _travelRefreshService.SetTornApiKey(apiKey);
        ApiKeyStatusText.Text = string.IsNullOrWhiteSpace(apiKey)
            ? "Key removed"
            : "Key saved";

        await _travelRefreshService.RefreshNowAsync();
    }

    private async void BazaarPriceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            await SaveBazaarPriceAsync(textBox);
        }
    }

    private async void BazaarPriceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox)
        {
            return;
        }

        e.Handled = true;
        await SaveBazaarPriceAsync(textBox);
        Keyboard.ClearFocus();
    }

    private void OnTravelStateChanged(object? sender, TravelRefreshState state)
    {
        Dispatcher.Invoke(() => ApplyState(state));
    }

    private void ApplyState(TravelRefreshState state)
    {
        StatusText.Text = GetStatusLabel(state.Status);
        _travelStatus = state.TravelStatus;
        _moneyStatus = state.MoneyStatus;
        _barsStatus = state.BarsStatus;
        _forecastSnapshot = state.ForecastSnapshot;
        UpdateTravelStatus();
        UpdateMoneyStatus();
        UpdateBarsStatus();
        UpdateForecastStatus();
        LastUpdatedText.Text = FormatDate(state.LastUpdated);
        NextRefreshText.Text = FormatDate(state.NextRefreshAt);
        _allDestinations = state.Destinations;
        EndpointText.Text = state.Endpoint.ToString();
        IntervalText.Text = $"Interval: {state.RefreshInterval.TotalMinutes:0} minutes";
        RefreshButton.IsEnabled = state.Status != TravelRefreshStatus.Loading;
        UpdateRecommendation();
        UpdateTravelPlanner();
        ApplyDashboardFilter();

        ShowErrorToastIfNeeded(state.Error);
    }

    private static string GetStatusLabel(TravelRefreshStatus status)
    {
        return status switch
        {
            TravelRefreshStatus.Idle => "Ready",
            TravelRefreshStatus.Loading => "Loading...",
            TravelRefreshStatus.Success => "Connected",
            TravelRefreshStatus.Error => "Error",
            _ => status.ToString()
        };
    }

    private static string FormatDate(DateTimeOffset? date)
    {
        return date is null
            ? "-"
            : date.Value.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyDashboardFilter();
    }

    private void CountryFilter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyDashboardFilter();
    }

    private void ApplyDashboardFilter()
    {
        string query = SearchBox.Text.Trim();
        HashSet<string> selectedCountryCodes = GetSelectedCountryCodes();
        IReadOnlyList<TravelDestination> visibleDestinations = _allDestinations;

        if (selectedCountryCodes.Count > 0)
        {
            visibleDestinations = visibleDestinations
                .Where(destination => selectedCountryCodes.Contains(destination.Code))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            visibleDestinations = visibleDestinations
                .Select(destination =>
                {
                    bool destinationMatches =
                        destination.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || destination.Code.Contains(query, StringComparison.CurrentCultureIgnoreCase);

                    IReadOnlyList<TravelItem> items = destinationMatches
                        ? destination.Items
                        : destination.Items
                            .Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                            .ToList();

                    return destination with
                    {
                        Items = items
                    };
                })
                .Where(destination => destination.ItemCount > 0)
                .ToList();
        }

        DestinationList.ItemsSource = visibleDestinations;
        RecordCountText.Text = visibleDestinations.Sum(destination => destination.ItemCount).ToString("N0");
        EmptyDashboardText.Visibility = visibleDestinations.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private HashSet<string> GetSelectedCountryCodes()
    {
        return CountryFilterPanel.Children
            .OfType<CheckBox>()
            .Where(checkBox => checkBox.IsChecked == true && checkBox.Tag is string)
            .Select(checkBox => ((string)checkBox.Tag).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        UpdateTravelStatus();
        UpdateRecommendation();
    }

    private void UpdateTravelStatus()
    {
        if (_travelStatus is null || !_travelStatus.IsTraveling)
        {
            TravelDestinationText.Text = "Not traveling";
            TravelCountdownText.Text = "-";
            TravelDepartedText.Text = "-";
            TravelArrivalText.Text = "-";
            return;
        }

        TimeSpan? remaining = _travelStatus.GetRemaining(DateTimeOffset.Now);
        TravelDestinationText.Text = _travelStatus.DestinationText;
        TravelCountdownText.Text = remaining is null ? "-" : FormatDuration(remaining.Value);
        TravelDepartedText.Text = _travelStatus.DepartedText;
        TravelArrivalText.Text = _travelStatus.ArrivalText;
    }

    private void UpdateMoneyStatus()
    {
        WalletText.Text = _moneyStatus is null ? "-" : $"Wallet ${_moneyStatus.WalletText}";
        VaultText.Text = _moneyStatus is null ? "-" : $"Vault ${_moneyStatus.VaultText} | Total ${_moneyStatus.TotalText}";
    }

    private void UpdateBarsStatus()
    {
        if (_barsStatus is null)
        {
            PlannerBarsText.Text = "Energy/nerve unavailable";
            return;
        }

        PlannerBarsText.Text = $"Energy {_barsStatus.Energy.ValueText}, full in {_barsStatus.Energy.FullInText} | Nerve {_barsStatus.Nerve.ValueText}, full in {_barsStatus.Nerve.FullInText}";
    }

    private void UpdateForecastStatus()
    {
        PlannerForecastText.Text = _forecastSnapshot is null
            ? "Forecast unavailable"
            : _forecastSnapshot.StatusText;
    }

    private void UpdateTravelPlanner()
    {
        if (!TryReadPlannerWindow(out DateTimeOffset windowStart, out DateTimeOffset windowEnd, out string? validationError))
        {
            PlannerWindowSummaryText.Text = validationError ?? "Invalid active window.";
            TravelPlannerCards.ItemsSource = Array.Empty<TravelPlanCard>();
            return;
        }

        TravelPlannerSessionPlan plan = TravelPlannerService.GetSessionPlan(
            _allDestinations,
            _travelStatus,
            _moneyStatus,
            _barsStatus,
            _forecastSnapshot,
            _settings.MaxSpendPercent,
            _plannerStrategy,
            GetPlannerFilters(),
            DateTimeOffset.Now,
            windowStart,
            windowEnd);

        PlannerWindowSummaryText.Text = plan.SummaryText;
        TravelPlannerCards.ItemsSource = plan.Trips.Count == 0
            ? new[] { new TravelPlanCard("No session route", plan.StatusText, null) }
            : plan.Trips
                .Select((trip, index) => new TravelPlanCard($"Trip {index + 1}", "No trip available.", trip))
                .ToList();
    }

    private void UpdateRecommendation()
    {
        TravelRecommendation? recommendation = TravelRecommendationService.GetRecommendation(
            _allDestinations,
            _travelStatus,
            _moneyStatus,
            _settings.MaxSpendPercent,
            _settings.RestockAvailabilityMode,
            DateTimeOffset.Now);

        if (recommendation is null)
        {
            RecommendationTitleText.Text = "No profitable trip available";
            RecommendationReasonText.Text = "Current stock and restock timing do not produce a clear next trip.";
            RecommendationDepartText.Text = "-";
            RecommendationArriveText.Text = "-";
            RecommendationProfitText.Text = "-";
            RecommendationCashText.Text = "-";
            RecommendationCashStatusText.Text = $"Budget limit: {_settings.MaxSpendPercent}% of wallet + vault";
            return;
        }

        RecommendationTitleText.Text = $"{recommendation.DestinationText} - {recommendation.ItemName}";
        RecommendationReasonText.Text = recommendation.Reason;
        RecommendationDepartText.Text = recommendation.DepartText;
        RecommendationArriveText.Text = recommendation.ArriveText;
        RecommendationProfitText.Text = $"${recommendation.ProfitPerHourText}";
        RecommendationCashText.Text = $"${recommendation.CashNeededText} / ${recommendation.SpendLimitText}";
        RecommendationCashStatusText.Text = recommendation.CashStatusText;
    }

    private void ShowErrorToastIfNeeded(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            _lastShownError = null;
            HideErrorToast();
            return;
        }

        if (string.Equals(_lastShownError, error, StringComparison.Ordinal))
        {
            return;
        }

        _lastShownError = error;
        ErrorToastText.Text = error;
        ErrorToast.Visibility = Visibility.Visible;
        _errorToastTimer.Stop();
        _errorToastTimer.Start();
    }

    private void ErrorToastTimer_Tick(object? sender, EventArgs e)
    {
        HideErrorToast();
    }

    private void DismissErrorToastButton_Click(object sender, RoutedEventArgs e)
    {
        HideErrorToast();
    }

    private void HideErrorToast()
    {
        _errorToastTimer.Stop();
        ErrorToast.Visibility = Visibility.Collapsed;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "00:00";
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private async Task SaveBazaarPriceAsync(TextBox textBox)
    {
        if (textBox.Tag is not int itemId || itemId <= 0)
        {
            return;
        }

        string value = textBox.Text.Trim();
        Dictionary<int, decimal> bazaarPrices = _settings.BazaarPrices.ToDictionary(
            entry => entry.Key,
            entry => entry.Value);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (!bazaarPrices.Remove(itemId))
            {
                return;
            }
        }
        else if (TryParseMoney(value, out decimal bazaarPrice) && bazaarPrice > 0)
        {
            if (bazaarPrices.TryGetValue(itemId, out decimal existing)
                && existing == bazaarPrice)
            {
                return;
            }

            bazaarPrices[itemId] = bazaarPrice;
        }
        else
        {
            textBox.Text = bazaarPrices.TryGetValue(itemId, out decimal existing)
                ? existing.ToString("N0")
                : string.Empty;
            return;
        }

        _settings = _settings with
        {
            BazaarPrices = bazaarPrices
        };
        _settingsService.Save(_settings);
        _travelRefreshService.SetBazaarPrices(_settings.BazaarPrices);
        await _travelRefreshService.RefreshNowAsync();
    }

    private static bool TryParseMoney(string value, out decimal parsed)
    {
        string normalized = value.Replace("$", string.Empty).Trim();

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)
            || decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed);
    }

    private static string FormatReleaseNotesPreview(string? releaseNotes)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes))
        {
            return "No release notes provided.";
        }

        string normalized = releaseNotes.Trim();
        const int maximumLength = 900;
        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..maximumLength]}...";
    }

    private void SelectRestockAvailabilityMode(RestockAvailabilityMode mode)
    {
        foreach (ComboBoxItem item in RestockAvailabilityModeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag
                && Enum.TryParse(tag, ignoreCase: true, out RestockAvailabilityMode itemMode)
                && itemMode == mode)
            {
                RestockAvailabilityModeBox.SelectedItem = item;
                return;
            }
        }

        RestockAvailabilityModeBox.SelectedIndex = 0;
    }

    private RestockAvailabilityMode GetSelectedRestockAvailabilityMode()
    {
        if (RestockAvailabilityModeBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse(tag, ignoreCase: true, out RestockAvailabilityMode mode))
        {
            return mode;
        }

        return AppSettings.DefaultRestockAvailabilityMode;
    }

    private void SelectPlannerStrategy(TravelPlannerStrategy strategy)
    {
        foreach (ComboBoxItem item in PlannerStrategyBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag
                && Enum.TryParse(tag, ignoreCase: true, out TravelPlannerStrategy itemStrategy)
                && itemStrategy == strategy)
            {
                PlannerStrategyBox.SelectedItem = item;
                return;
            }
        }

        PlannerStrategyBox.SelectedIndex = 1;
    }

    private TravelPlannerStrategy GetSelectedPlannerStrategy()
    {
        if (PlannerStrategyBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse(tag, ignoreCase: true, out TravelPlannerStrategy strategy))
        {
            return strategy;
        }

        return TravelPlannerStrategy.Balanced;
    }

    private void InitializePlannerWindow()
    {
        SetPlannerWindow(DateTimeOffset.Now, DateTimeOffset.Now.Add(DefaultPlannerWindowDuration));
    }

    private void SetPlannerWindow(DateTimeOffset start, DateTimeOffset end)
    {
        _plannerWindowStart = start;
        _plannerWindowEnd = end;
        PlannerStartTimeBox.Text = start.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        PlannerEndTimeBox.Text = end.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private bool TryReadPlannerWindow(
        out DateTimeOffset windowStart,
        out DateTimeOffset windowEnd,
        out string? validationError)
    {
        validationError = null;
        DateTime localToday = DateTime.Today;

        if (!TryParsePlannerTime(PlannerStartTimeBox.Text, out TimeSpan startTime))
        {
            windowStart = _plannerWindowStart;
            windowEnd = _plannerWindowEnd;
            validationError = "Active from must use HH:mm.";
            return false;
        }

        DateTime startLocal = localToday.Add(startTime);
        DateTime endLocal;

        if (string.IsNullOrWhiteSpace(PlannerEndTimeBox.Text))
        {
            endLocal = startLocal.Add(DefaultPlannerWindowDuration);
        }
        else if (TryParsePlannerTime(PlannerEndTimeBox.Text, out TimeSpan endTime))
        {
            endLocal = localToday.Add(endTime);
        }
        else
        {
            windowStart = _plannerWindowStart;
            windowEnd = _plannerWindowEnd;
            validationError = "Active until must use HH:mm or be empty for 12 hours.";
            return false;
        }

        if (endLocal <= startLocal)
        {
            endLocal = endLocal.AddDays(1);
        }

        windowStart = new DateTimeOffset(startLocal);
        windowEnd = new DateTimeOffset(endLocal);
        _plannerWindowStart = windowStart;
        _plannerWindowEnd = windowEnd;
        return true;
    }

    private static bool TryParsePlannerTime(string value, out TimeSpan time)
    {
        string trimmed = value.Trim();
        string[] formats = { "h\\:mm", "hh\\:mm" };
        return TimeSpan.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, out time)
            || TimeSpan.TryParse(trimmed, CultureInfo.CurrentCulture, out time);
    }

    private TravelPlannerFilters GetPlannerFilters()
    {
        string targetItem = PlannerTargetItemBox.Text.Trim();
        IReadOnlyList<string> excludedItems = SplitPlannerFilterText(PlannerExcludedItemsBox.Text);
        HashSet<string> excludedCountryCodes = PlannerExcludedCountryPanel.Children
            .OfType<CheckBox>()
            .Where(checkBox => checkBox.IsChecked == true && checkBox.Tag is string)
            .Select(checkBox => ((string)checkBox.Tag).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new TravelPlannerFilters(targetItem, excludedItems, excludedCountryCodes);
    }

    private static IReadOnlyList<string> SplitPlannerFilterText(string value)
    {
        return value
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

}
