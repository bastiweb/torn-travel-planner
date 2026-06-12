using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TornStockTravel.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMessageBox = System.Windows.MessageBox;

namespace TornStockTravel;

public partial class MainWindow : Window
{
    private static readonly TimeSpan DefaultPlannerWindowDuration = TimeSpan.FromHours(12);

    private readonly AppSettingsService _settingsService;
    private readonly TravelRefreshService _travelRefreshService;
    private readonly UpdateCheckerService _updateCheckerService;
    private readonly HistoryDatabaseService _historyDatabaseService;
    private readonly DesktopNotificationService _notificationService;
    private readonly DiscordWebhookService _discordWebhookService;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _errorToastTimer;
    private readonly DispatcherTimer _alertTimer;
    private AppSettings _settings;
    private TornTravelStatus? _travelStatus;
    private TornMoneyStatus? _moneyStatus;
    private TornBarsStatus? _barsStatus;
    private TornCooldownStatus? _cooldownStatus;
    private DroqsForecastSnapshot? _forecastSnapshot;
    private TravelPlannerStrategy _plannerStrategy = TravelPlannerStrategy.Balanced;
    private DateTimeOffset _plannerWindowStart;
    private DateTimeOffset _plannerWindowEnd;
    private string? _lastShownError;
    private DateTimeOffset? _lastUpdateCheckAt;
    private readonly Dictionary<string, int> _manualBoughtReservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sentAlertKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _isApplyingPlannerPreset;
    private bool _isCheckingAlerts;
    private bool _isUpdatingHistoryOverview;
    private bool _nerveNearFullAlertActive;
    private TravelPlannerSessionPlan? _currentPlannerPlan;
    private IReadOnlyList<TravelDestination> _sourceDestinations = Array.Empty<TravelDestination>();
    private IReadOnlyList<TravelDestination> _allDestinations = Array.Empty<TravelDestination>();

    public MainWindow()
    {
        InitializeComponent();
        Title = BuildWindowTitle();
        StatusHeader.RefreshRequested += StatusHeader_RefreshRequested;
        ErrorToast.DismissRequested += ErrorToast_DismissRequested;
        StockOverview.BazaarPriceCommitRequested = SaveBazaarPriceAsync;
        HistoryTrends.ItemSelectionChanged += HistoryTrends_ItemSelectionChanged;

        _settingsService = new AppSettingsService();
        _updateCheckerService = new UpdateCheckerService();
        _historyDatabaseService = new HistoryDatabaseService();
        _notificationService = new DesktopNotificationService();
        _discordWebhookService = new DiscordWebhookService();
        _settings = _settingsService.Load();
        ApiKeyBox.Password = _settings.TornApiKey;
        BuyCapacityBox.Text = _settings.BuyCapacity > 0
            ? _settings.BuyCapacity.ToString("N0")
            : string.Empty;
        RefreshIntervalBox.Text = _settings.RefreshIntervalMinutes.ToString("N0");
        MaxSpendPercentBox.Text = _settings.MaxSpendPercent.ToString("N0");
        WatchlistBox.Text = string.Join(", ", _settings.WatchlistItemNames);
        ExcludedItemsBox.Text = string.Join(", ", _settings.GlobalExcludedItemQueries);
        InitializeAlertSettings();
        SelectRestockAvailabilityMode(_settings.RestockAvailabilityMode);
        RestockAvailabilityModeBox.SelectionChanged += RestockAvailabilityModeBox_SelectionChanged;
        SelectPlannerStrategy(_plannerStrategy);
        PlannerStrategyBox.SelectionChanged += PlannerStrategyBox_SelectionChanged;
        InitializePlannerWindow();
        InitializePlannerPresets();
        InitializePlannerCapacityProfile();
        ApiKeyStatusText.Text = string.IsNullOrWhiteSpace(_settings.TornApiKey)
            ? "No key saved"
            : "Key saved";
        InitializeUpdateStatus();
        DatabasePathText.Text = $"Active database: {_historyDatabaseService.DatabasePath}";

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
        _alertTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _alertTimer.Tick += AlertTimer_Tick;
        _alertTimer.Start();

        ApplyState(_travelRefreshService.GetState());
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ShowChangelogNoticeIfNeeded();
        _travelRefreshService.Start();
        _ = CheckForUpdatesAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        StatusHeader.RefreshRequested -= StatusHeader_RefreshRequested;
        ErrorToast.DismissRequested -= ErrorToast_DismissRequested;
        StockOverview.BazaarPriceCommitRequested = null;
        HistoryTrends.ItemSelectionChanged -= HistoryTrends_ItemSelectionChanged;
        _travelRefreshService.StateChanged -= OnTravelStateChanged;
        RestockAvailabilityModeBox.SelectionChanged -= RestockAvailabilityModeBox_SelectionChanged;
        PlannerStrategyBox.SelectionChanged -= PlannerStrategyBox_SelectionChanged;
        _countdownTimer.Stop();
        _countdownTimer.Tick -= CountdownTimer_Tick;
        _errorToastTimer.Stop();
        _errorToastTimer.Tick -= ErrorToastTimer_Tick;
        _alertTimer.Stop();
        _alertTimer.Tick -= AlertTimer_Tick;
        _travelRefreshService.Dispose();
        _updateCheckerService.Dispose();
        _notificationService.Dispose();
        _discordWebhookService.Dispose();
    }

    private async void StatusHeader_RefreshRequested(object? sender, EventArgs e)
    {
        await _travelRefreshService.RefreshNowAsync();
    }

    private async void HistoryTrends_ItemSelectionChanged(object? sender, Views.HistoryItemSelectionChangedEventArgs e)
    {
        await UpdateHistoryItemDetailAsync(e.SelectedItem);
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(showNoUpdateMessage: true);
    }

    private async Task CheckForUpdatesAsync(bool showNoUpdateMessage = false)
    {
        GitHubUpdateInfo? updateInfo;
        bool installStarted = false;
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking...";

        try
        {
            updateInfo = await _updateCheckerService.CheckForUpdateAsync(UpdateCheckerService.GetCurrentVersion());
            _lastUpdateCheckAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            _lastUpdateCheckAt = DateTimeOffset.Now;
            UpdateLatestVersionText.Text = "-";
            UpdateLastCheckText.Text = FormatDate(_lastUpdateCheckAt);
            UpdateStatusText.Text = "Update check failed";
            AppLogService.Warning($"Update check failed: {ex.Message}");
            if (showNoUpdateMessage)
            {
                ShowErrorToastIfNeeded($"Update check failed: {ex.Message}");
            }

            CheckForUpdatesButton.IsEnabled = true;
            return;
        }

        if (updateInfo is null)
        {
            UpdateLatestVersionText.Text = $"v{FormatVersion(UpdateCheckerService.GetCurrentVersion())}";
            UpdateLastCheckText.Text = FormatDate(_lastUpdateCheckAt);
            UpdateStatusText.Text = "Up to date";
            if (showNoUpdateMessage)
            {
                WpfMessageBox.Show(
                    this,
                    "You are already running the latest version.",
                    "Torn Stock Travel Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            CheckForUpdatesButton.IsEnabled = true;
            return;
        }

        UpdateLatestVersionText.Text = updateInfo.TagName;
        UpdateLastCheckText.Text = FormatDate(_lastUpdateCheckAt);
        UpdateStatusText.Text = "Update available";

        string releaseNotes = FormatReleaseNotesPreview(updateInfo.Body);
        MessageBoxResult result = WpfMessageBox.Show(
            this,
            $"Version {updateInfo.TagName} is available.\n\nDownload size: {updateInfo.AssetSizeText}\n\nDownload and install now?\n\n{releaseNotes}",
            "Torn Stock Travel Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result != MessageBoxResult.Yes)
        {
            CheckForUpdatesButton.IsEnabled = true;
            return;
        }

        try
        {
            StatusHeader.IsRefreshEnabled = false;
            CheckForUpdatesButton.IsEnabled = false;
            StatusText.Text = "Downloading update...";
            UpdateStatusText.Text = "Downloading update...";
            string downloadedExePath = await _updateCheckerService.DownloadUpdateAsync(updateInfo);
            _settings = _settings with
            {
                PendingReleaseVersion = NormalizeVersionTag(updateInfo.TagName),
                PendingReleaseNotes = updateInfo.Body
            };
            _settingsService.Save(_settings);
            WpfMessageBox.Show(
                this,
                "The app will close for a moment, install the update, and restart automatically.",
                "Installing update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            installStarted = true;
            _updateCheckerService.StartUpdater(downloadedExePath);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusHeader.IsRefreshEnabled = true;
            CheckForUpdatesButton.IsEnabled = true;
            StatusText.Text = "Ready";
            UpdateStatusText.Text = "Update failed";
            AppLogService.Error("Update failed.", ex);
            ShowErrorToastIfNeeded($"Update failed: {ex.Message}");
        }
        finally
        {
            if (!installStarted)
            {
                CheckForUpdatesButton.IsEnabled = true;
            }
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

    private void SaveAlertSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadAlertSettings(out AppSettings nextSettings, out string? validationError))
        {
            AlertSettingsStatusText.Text = validationError ?? "Invalid alert settings.";
            return;
        }

        _settings = nextSettings;
        _settingsService.Save(_settings);
        _sentAlertKeys.Clear();
        AlertSettingsStatusText.Text = "Alert settings saved";
    }

    private void TestAlertButton_Click(object sender, RoutedEventArgs e)
    {
        _notificationService.Show(
            "Torn Stock Travel test",
            "Windows notifications are working. Discord webhook alerts are sent only by real alert events.");
        AlertSettingsStatusText.Text = "Windows test requested. If nothing appears, check Windows notification or focus assist settings.";
        ShowErrorToastIfNeeded("Windows notification test requested.");
    }

    private async void TestDiscordButton_Click(object sender, RoutedEventArgs e)
    {
        string webhookUrl = DiscordWebhookBox.Password.Trim();
        if (!IsValidDiscordWebhookUrl(webhookUrl))
        {
            AlertSettingsStatusText.Text = "Enter a valid https Discord webhook URL.";
            return;
        }

        try
        {
            string message = FormatDiscordMessage(
                NormalizeDiscordMessageTemplate(DiscordMessageTemplateBox.Text),
                "Torn Stock Travel test",
                "Discord webhook alerts are connected.");
            await _discordWebhookService.SendAsync(
                webhookUrl,
                message);
            AlertSettingsStatusText.Text = "Discord test message sent";
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Discord test failed: {ex.Message}");
            AlertSettingsStatusText.Text = "Discord test failed";
            ShowErrorToastIfNeeded($"Discord test failed: {ex.Message}");
        }
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
        if (_isApplyingPlannerPreset)
        {
            return;
        }

        _plannerStrategy = GetSelectedPlannerStrategy();
        UpdateTravelPlanner();
    }

    private void PlannerWindow_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplyingPlannerPreset)
        {
            return;
        }

        UpdateTravelPlanner();
    }

    private void PlannerFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplyingPlannerPreset)
        {
            return;
        }

        UpdateTravelPlanner();
    }

    private void PlannerWindowBox_KeyDown(object sender, WpfKeyEventArgs e)
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

    private void PlannerCapacityProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingPlannerPreset)
        {
            return;
        }

        ApplySelectedPlannerCapacityProfile();
        UpdateTravelPlanner();
    }

    private void ApplyPlannerPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlannerPresetBox.SelectedItem is PlannerPreset preset)
        {
            ApplyPlannerPreset(preset);
        }
    }

    private void SavePlannerPresetButton_Click(object sender, RoutedEventArgs e)
    {
        string name = PlannerPresetNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            PlannerWindowSummaryText.Text = "Enter a preset name before saving.";
            return;
        }

        if (!TryReadPlannerCapacity(out int? capacity, out string? capacityError))
        {
            PlannerWindowSummaryText.Text = capacityError ?? "Invalid capacity.";
            return;
        }

        if (!TryReadPlannerWindow(out DateTimeOffset windowStart, out DateTimeOffset windowEnd, out string? validationError))
        {
            PlannerWindowSummaryText.Text = validationError ?? "Invalid active window.";
            return;
        }

        PlannerPreset preset = new(
            name,
            PlannerTargetItemBox.Text.Trim(),
            SplitPlannerFilterText(PlannerExcludedItemsBox.Text),
            GetPlannerExcludedCountryCodes().OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToList(),
            _plannerStrategy,
            capacity ?? 0,
            Math.Clamp((int)Math.Ceiling(windowEnd.Subtract(windowStart).TotalHours), 1, 24));

        List<PlannerPreset> presets = _settings.PlannerPresets
            .Where(existing => !string.Equals(existing.Name, preset.Name, StringComparison.CurrentCultureIgnoreCase))
            .Append(preset)
            .OrderBy(existing => existing.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _settings = _settings with
        {
            PlannerPresets = presets
        };
        _settingsService.Save(_settings);
        RefreshPlannerPresetList(preset.Name);
        PlannerWindowSummaryText.Text = $"Preset \"{preset.Name}\" saved.";
    }

    private void MarkTripBoughtButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not TravelPlanCandidate candidate || candidate.Stock <= 0)
        {
            return;
        }

        string key = TravelPlannerService.BuildReservationKey(candidate.DestinationCode, candidate.ItemName);
        _manualBoughtReservations[key] = _manualBoughtReservations.TryGetValue(key, out int reserved)
            ? reserved + candidate.BuyAmount
            : candidate.BuyAmount;
        UpdateTravelPlanner();
    }

    private void ExcludeTripItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not TravelPlanCandidate candidate)
        {
            return;
        }

        AddGlobalExcludedItem(candidate.ItemName);
        ExcludedItemsStatusText.Text = $"Excluded: {candidate.ItemName}";
        PlannerWindowSummaryText.Text = $"Excluded item globally: {candidate.ItemName}";
        RefreshVisibleDataFromSettings();
    }

    private void ExcludeTripCountryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not TravelPlanCandidate candidate)
        {
            return;
        }

        CheckPlannerExcludedCountry(candidate.DestinationCode);
        PlannerWindowSummaryText.Text = $"Excluded country: {candidate.DestinationText}";
        UpdateTravelPlanner();
    }

    private void AddPlannerExcludedItem(string itemName)
    {
        List<string> excludedItems = SplitPlannerFilterText(PlannerExcludedItemsBox.Text).ToList();
        if (!excludedItems.Contains(itemName, StringComparer.CurrentCultureIgnoreCase))
        {
            excludedItems.Add(itemName);
        }

        PlannerExcludedItemsBox.Text = string.Join(", ", excludedItems);
    }

    private void AddGlobalExcludedItem(string itemName)
    {
        List<string> excludedItems = SplitPlannerFilterText(ExcludedItemsBox.Text).ToList();
        if (!excludedItems.Contains(itemName, StringComparer.CurrentCultureIgnoreCase))
        {
            excludedItems.Add(itemName);
        }

        _settings = _settings with
        {
            GlobalExcludedItemQueries = excludedItems
        };
        _settingsService.Save(_settings);
        ExcludedItemsBox.Text = string.Join(", ", _settings.GlobalExcludedItemQueries);
    }

    private void CheckPlannerExcludedCountry(string countryCode)
    {
        foreach (WpfCheckBox checkBox in PlannerExcludedCountryPanel.Children.OfType<WpfCheckBox>())
        {
            if (checkBox.Tag is string tag && string.Equals(tag, countryCode, StringComparison.OrdinalIgnoreCase))
            {
                checkBox.IsChecked = true;
                break;
            }
        }
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
        if (PlannerCapacityProfileBox.SelectedIndex == 0)
        {
            PlannerCapacityBox.Text = BuyCapacityBox.Text;
        }

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

    private async void ExportDatabaseBackupButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Export database backup",
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            FileName = $"torn-stock-backup-{DateTimeOffset.Now:yyyyMMdd-HHmm}.db",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        DatabaseStatusText.Text = "Exporting full backup...";
        try
        {
            await Task.Run(() => _historyDatabaseService.ExportFullBackup(dialog.FileName));
            DatabaseStatusText.Text = $"Backup exported: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            DatabaseStatusText.Text = "Backup export failed";
            AppLogService.Warning($"Database backup export failed: {ex.Message}");
            ShowErrorToastIfNeeded($"Database backup export failed: {ex.Message}");
        }
    }

    private async void ExportShareableHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Export shareable history",
            Filter = "Torn Stock history (*.tornhistory)|*.tornhistory|SQLite database (*.db)|*.db|All files (*.*)|*.*",
            FileName = $"torn-stock-history-{DateTimeOffset.Now:yyyyMMdd-HHmm}.tornhistory",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        DatabaseStatusText.Text = "Exporting sanitized history...";
        try
        {
            HistoryShareExportSummary summary = await Task.Run(() => _historyDatabaseService.ExportShareableHistory(dialog.FileName));
            DatabaseStatusText.Text = $"{summary.SummaryText}. No API keys or settings included.";
        }
        catch (Exception ex)
        {
            DatabaseStatusText.Text = "Shareable export failed";
            AppLogService.Warning($"Shareable history export failed: {ex.Message}");
            ShowErrorToastIfNeeded($"Shareable history export failed: {ex.Message}");
        }
    }

    private async void ImportShareableHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Import and merge history",
            Filter = "Torn Stock history or SQLite (*.tornhistory;*.db)|*.tornhistory;*.db|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            "This will merge shareable history into your local database. Existing duplicate sellout events and samples will be skipped.\n\nContinue?",
            "Import History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        DatabaseStatusText.Text = "Merging history...";
        try
        {
            HistoryImportSummary summary = await Task.Run(() => _historyDatabaseService.ImportShareableHistory(dialog.FileName));
            DatabaseStatusText.Text = summary.SummaryText;
            QueueHistoryOverviewUpdate();
            RefreshVisibleDataFromSettings();
        }
        catch (Exception ex)
        {
            DatabaseStatusText.Text = "History import failed";
            AppLogService.Warning($"Shareable history import failed: {ex.Message}");
            ShowErrorToastIfNeeded($"Shareable history import failed: {ex.Message}");
        }
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
        _cooldownStatus = state.CooldownStatus;
        _forecastSnapshot = state.ForecastSnapshot;
        UpdateTravelStatus();
        UpdateMoneyStatus();
        UpdateBarsStatus();
        UpdateForecastStatus();
        LastUpdatedText.Text = FormatDate(state.LastUpdated);
        NextRefreshText.Text = FormatDate(state.NextRefreshAt);
        _sourceDestinations = state.Destinations;
        _allDestinations = ApplyGlobalExcludedItems(_sourceDestinations);
        EndpointText.Text = state.Endpoint.ToString();
        IntervalText.Text = $"Interval: {state.RefreshInterval.TotalMinutes:0} minutes";
        StatusHeader.IsRefreshEnabled = state.Status != TravelRefreshStatus.Loading;
        UpdateRecommendation();
        UpdateTravelPlanner();
        UpdateDashboardIntelligence();
        RecordCountText.Text = _allDestinations.Sum(destination => destination.ItemCount).ToString("N0");
        StockOverview.SetDestinations(_allDestinations);
        QueueHistoryOverviewUpdate();

        ShowErrorToastIfNeeded(state.Error);
    }

    private void RefreshVisibleDataFromSettings()
    {
        _allDestinations = ApplyGlobalExcludedItems(_sourceDestinations);
        UpdateRecommendation();
        UpdateTravelPlanner();
        UpdateDashboardIntelligence();
        RecordCountText.Text = _allDestinations.Sum(destination => destination.ItemCount).ToString("N0");
        StockOverview.SetDestinations(_allDestinations);
        QueueHistoryOverviewUpdate();
    }

    private IReadOnlyList<TravelDestination> ApplyGlobalExcludedItems(IReadOnlyList<TravelDestination> destinations)
    {
        IReadOnlyList<string> excludedQueries = _settings.GlobalExcludedItemQueries;
        if (excludedQueries.Count == 0)
        {
            return destinations;
        }

        return destinations
            .Select(destination => destination with
            {
                Items = destination.Items
                    .Where(item => !IsGloballyExcludedItem(item.Name, excludedQueries))
                    .ToList()
            })
            .Where(destination => destination.ItemCount > 0)
            .ToList();
    }

    private static bool IsGloballyExcludedItem(string itemName, IReadOnlyList<string> excludedQueries)
    {
        return excludedQueries.Any(query =>
            itemName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private HistoryOverview ApplyGlobalExcludedItems(HistoryOverview overview)
    {
        IReadOnlyList<string> excludedQueries = _settings.GlobalExcludedItemQueries;
        if (excludedQueries.Count == 0)
        {
            return overview;
        }

        return overview with
        {
            TopProfitItems = overview.TopProfitItems
                .Where(item => !IsGloballyExcludedItem(item.ItemName, excludedQueries))
                .ToList()
        };
    }

    private void QueueHistoryOverviewUpdate()
    {
        if (_isUpdatingHistoryOverview)
        {
            return;
        }

        _isUpdatingHistoryOverview = true;
        _ = UpdateHistoryOverviewAsync();
    }

    private async Task UpdateHistoryOverviewAsync()
    {
        try
        {
            HistoryOverview overview = await Task.Run(() => _historyDatabaseService.BuildOverview(DateTimeOffset.Now));
            overview = ApplyGlobalExcludedItems(overview);
            HistoryTrends.SetOverview(overview);
            await UpdateHistoryItemDetailAsync(HistoryTrends.SelectedHistoryItem);
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"History overview update failed: {ex.Message}");
        }
        finally
        {
            _isUpdatingHistoryOverview = false;
        }
    }

    private async Task UpdateHistoryItemDetailAsync(HistoryItemOption? selectedItem)
    {
        if (selectedItem is null)
        {
            HistoryTrends.SetItemDetail(null);
            return;
        }

        try
        {
            HistoryItemDetail? detail = await Task.Run(() =>
                _historyDatabaseService.BuildItemDetail(
                    selectedItem.ItemId,
                    selectedItem.CountryCode,
                    DateTimeOffset.Now));
            HistoryTrends.SetItemDetail(detail);
        }
        catch (Exception ex)
        {
            HistoryTrends.SetItemDetail(null);
            AppLogService.Warning($"History item detail update failed: {ex.Message}");
        }
    }

    private static string GetStatusLabel(TravelRefreshStatus status)
    {
        return status switch
        {
            TravelRefreshStatus.Idle => "Ready",
            TravelRefreshStatus.Loading => "Loading...",
            TravelRefreshStatus.Success => "Connected",
            TravelRefreshStatus.Stale => "Stale cache",
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

    private void SaveWatchlistButton_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with
        {
            WatchlistItemNames = SplitPlannerFilterText(WatchlistBox.Text)
        };
        _settingsService.Save(_settings);
        WatchlistBox.Text = string.Join(", ", _settings.WatchlistItemNames);
        UpdateDashboardIntelligence();
    }

    private void SaveExcludedItemsButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> excludedItems = SplitPlannerFilterText(ExcludedItemsBox.Text);
        _settings = _settings with
        {
            GlobalExcludedItemQueries = excludedItems
        };
        _settingsService.Save(_settings);
        ExcludedItemsBox.Text = string.Join(", ", _settings.GlobalExcludedItemQueries);
        ExcludedItemsStatusText.Text = excludedItems.Count == 0
            ? "No global exclusions"
            : $"{excludedItems.Count:N0} excluded";
        RefreshVisibleDataFromSettings();
    }

    private void UpdateDashboardIntelligence()
    {
        InventoryValueSummary inventorySummary = BuildInventoryValueSummary();
        InventoryValueText.Text = $"${inventorySummary.TotalValueText}";
        InventoryProfitText.Text = $"${inventorySummary.ExpectedProfitText}";
        InventoryDetailsText.Text = $"{inventorySummary.RelevantOwnedItemsText} owned relevant items";
        WatchlistSummaryText.Text = $"{inventorySummary.WatchlistMatchesText} watchlist matches";
        CountrySummaryCards.ItemsSource = BuildCountrySummaryCards();
        WatchlistItemsList.ItemsSource = BuildWatchlistItems();
        UpdateForecastQualityBadge();
    }

    private InventoryValueSummary BuildInventoryValueSummary()
    {
        List<TravelItem> allItems = _allDestinations.SelectMany(destination => destination.Items).ToList();
        List<TravelItem> uniqueOwnedItems = allItems
            .Where(item => item.OwnedAmount > 0 && item.EffectiveValue is not null)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        decimal totalValue = uniqueOwnedItems.Sum(item => item.OwnedValue ?? 0);
        decimal expectedProfit = allItems.Where(item => item.Profit is > 0).Sum(item => item.Profit ?? 0);
        int watchlistMatches = BuildWatchlistItems().Count;

        return new InventoryValueSummary(totalValue, expectedProfit, uniqueOwnedItems.Count, watchlistMatches);
    }

    private IReadOnlyList<CountrySummaryCard> BuildCountrySummaryCards()
    {
        return _allDestinations
            .Select(destination =>
            {
                List<TravelItem> profitableItems = destination.Items
                    .Where(item => item.ProfitPerHour is > 0)
                    .OrderByDescending(item => item.ProfitPerHour)
                    .ToList();
                TravelItem? bestItem = profitableItems.FirstOrDefault();
                DateTimeOffset? nextRestock = destination.Items
                    .Where(item => item.RestockEstimateUtc is not null)
                    .Select(item => item.RestockEstimateUtc!.Value)
                    .Where(restock => restock >= DateTimeOffset.UtcNow.AddHours(-1))
                    .OrderBy(restock => restock)
                    .Select(restock => (DateTimeOffset?)restock)
                    .FirstOrDefault();

                return new CountrySummaryCard(
                    destination.Name,
                    destination.Code,
                    profitableItems.Count,
                    bestItem?.ProfitPerHour ?? 0,
                    bestItem?.Name ?? string.Empty,
                    nextRestock);
            })
            .OrderByDescending(card => card.BestProfitPerHour)
            .ToList();
    }

    private IReadOnlyList<WatchlistItemView> BuildWatchlistItems()
    {
        IReadOnlyList<string> watchlist = _settings.WatchlistItemNames;
        if (watchlist.Count == 0)
        {
            return Array.Empty<WatchlistItemView>();
        }

        return _allDestinations
            .SelectMany(destination => destination.Items
                .Where(item => watchlist.Any(watch =>
                    item.Name.Contains(watch, StringComparison.CurrentCultureIgnoreCase)))
                .Select(item => new WatchlistItemView(
                    item.Name,
                    destination.DisplayName,
                    item.ProfitPerHour is null ? "-" : $"${item.ProfitPerHour.Value:N0}/h",
                    item.Quantity.ToString("N0"))))
            .OrderBy(item => item.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DestinationText, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void UpdateForecastQualityBadge()
    {
        if (_forecastSnapshot is null)
        {
            ForecastQualityBadgeText.Text = "Fallback";
            ForecastQualityDetailsText.Text = "Droqs forecast unavailable";
            return;
        }

        string freshness = string.IsNullOrWhiteSpace(_forecastSnapshot.SnapshotFreshness)
            ? "unknown"
            : _forecastSnapshot.SnapshotFreshness;
        ForecastQualityBadgeText.Text = _forecastSnapshot.Stale
            ? "Stale"
            : freshness.Contains("fresh", StringComparison.OrdinalIgnoreCase)
                ? "Fresh"
                : "Live";
        ForecastQualityDetailsText.Text = $"{_forecastSnapshot.ItemCount:N0} items, {_forecastSnapshot.WindowCount:N0} windows ({freshness})";
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
        string cooldownText = _cooldownStatus is null
            ? "Drug cooldown unavailable"
            : _cooldownStatus.DrugText;

        if (_barsStatus is null)
        {
            PlannerBarsText.Text = $"Energy/nerve unavailable | {cooldownText}";
            return;
        }

        PlannerBarsText.Text = $"Energy {_barsStatus.Energy.ValueText}, full in {_barsStatus.Energy.FullInText} | Nerve {_barsStatus.Nerve.ValueText}, full in {_barsStatus.Nerve.FullInText} | {cooldownText}";
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
            _currentPlannerPlan = null;
            TravelPlannerCards.ItemsSource = Array.Empty<TravelPlanCard>();
            TravelPlannerTimeline.ItemsSource = Array.Empty<TravelPlanTimelineEntry>();
            return;
        }

        if (!TryReadPlannerCapacity(out int? plannerCapacity, out string? capacityError))
        {
            PlannerWindowSummaryText.Text = capacityError ?? "Invalid carry capacity.";
            _currentPlannerPlan = null;
            TravelPlannerCards.ItemsSource = Array.Empty<TravelPlanCard>();
            TravelPlannerTimeline.ItemsSource = Array.Empty<TravelPlanTimelineEntry>();
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
            windowEnd,
            _cooldownStatus,
            plannerCapacity,
            _manualBoughtReservations);

        _currentPlannerPlan = plan;
        PlannerWindowSummaryText.Text = plan.SummaryText;
        UpdatePlannerWalletReadiness(plan);
        TravelPlannerTimeline.ItemsSource = BuildTimelineEntries(plan);
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
        ErrorToast.ShowMessage(error);
        _errorToastTimer.Stop();
        _errorToastTimer.Start();
    }

    private void ErrorToastTimer_Tick(object? sender, EventArgs e)
    {
        HideErrorToast();
    }

    private async void AlertTimer_Tick(object? sender, EventArgs e)
    {
        if (_isCheckingAlerts)
        {
            return;
        }

        _isCheckingAlerts = true;
        try
        {
            await CheckTravelStatusAlertsAsync(DateTimeOffset.Now);
            await CheckBarsAlertsAsync();
            await CheckCooldownAlertsAsync(DateTimeOffset.Now);
            await CheckPlannerAlertsAsync(DateTimeOffset.Now);
        }
        finally
        {
            _isCheckingAlerts = false;
        }
    }

    private async Task CheckTravelStatusAlertsAsync(DateTimeOffset now)
    {
        if (!_settings.LandingReminderEnabled || _travelStatus is null || !_travelStatus.IsTraveling)
        {
            return;
        }

        TimeSpan? remaining = _travelStatus.GetRemaining(now);
        if (remaining is null || remaining.Value <= TimeSpan.Zero || remaining.Value > TimeSpan.FromMinutes(1))
        {
            return;
        }

        string arrivalKey = _travelStatus.ArrivalAt is null
            ? $"{_travelStatus.Destination}:{_travelStatus.FetchedAt.UtcTicks}:{_travelStatus.TimeLeftSeconds}"
            : $"{_travelStatus.Destination}:{_travelStatus.ArrivalAt.Value.UtcTicks}";
        string alertKey = $"landing:{arrivalKey}";
        if (!_sentAlertKeys.Add(alertKey))
        {
            return;
        }

        await SendPlannerAlertAsync(
            "Landing soon",
            $"You are about to land in {_travelStatus.DestinationText}. Time left: {FormatDuration(remaining.Value)}.");
    }

    private async Task CheckBarsAlertsAsync()
    {
        if (!_settings.NerveNearFullAlertEnabled || _barsStatus is null)
        {
            return;
        }

        int missingNerve = _barsStatus.Nerve.Maximum - _barsStatus.Nerve.Current;
        bool isNearFull = missingNerve > 0 && missingNerve <= 5;
        if (!isNearFull)
        {
            _nerveNearFullAlertActive = false;
            return;
        }

        if (_nerveNearFullAlertActive)
        {
            return;
        }

        _nerveNearFullAlertActive = true;
        await SendPlannerAlertAsync(
            "Nerve almost full",
            $"Nerve is {_barsStatus.Nerve.ValueText}. Only {missingNerve:N0} point{(missingNerve == 1 ? string.Empty : "s")} missing.");
    }

    private async Task CheckCooldownAlertsAsync(DateTimeOffset now)
    {
        if (!_settings.DrugCooldownReminderEnabled || _cooldownStatus?.DrugEndsAt is null)
        {
            return;
        }

        TimeSpan remaining = _cooldownStatus.DrugEndsAt.Value - now;
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(1))
        {
            return;
        }

        string alertKey = $"drug-cooldown:{_cooldownStatus.DrugEndsAt.Value.UtcTicks}";
        if (!_sentAlertKeys.Add(alertKey))
        {
            return;
        }

        await SendPlannerAlertAsync(
            "Drug cooldown ending",
            $"Drug cooldown ends at {_cooldownStatus.DrugEndsAt.Value.LocalDateTime:HH:mm}. Time left: {FormatDuration(remaining)}.");
    }

    private async Task CheckPlannerAlertsAsync(DateTimeOffset now)
    {
        TravelPlannerSessionPlan? plan = _currentPlannerPlan;
        if (plan is null || plan.Trips.Count == 0)
        {
            return;
        }

        bool anyAlertEnabled = _settings.DepartureRemindersEnabled || _settings.RestockAlertsEnabled;
        if (!anyAlertEnabled)
        {
            return;
        }

        TimeSpan leadTime = TimeSpan.FromMinutes(_settings.DepartureReminderMinutes);
        foreach (TravelPlanCandidate trip in plan.Trips)
        {
            TimeSpan untilDeparture = trip.DepartAt - now;
            if (untilDeparture < TimeSpan.FromMinutes(-1) || untilDeparture > leadTime)
            {
                continue;
            }

            bool isRestockTrip = trip.UsesForecast
                || trip.TripType.Contains("restock", StringComparison.OrdinalIgnoreCase)
                || trip.TripType.Contains("forecast", StringComparison.OrdinalIgnoreCase);
            bool shouldAlert = _settings.DepartureRemindersEnabled
                || (_settings.RestockAlertsEnabled && isRestockTrip);
            if (!shouldAlert)
            {
                continue;
            }

            string alertKey = $"{trip.DestinationCode}:{trip.ItemName}:{trip.DepartAt.UtcTicks}:{isRestockTrip}";
            if (!_sentAlertKeys.Add(alertKey))
            {
                continue;
            }

            string title = isRestockTrip ? "Restock trip departure" : "Trip departure";
            string message = $"Depart {trip.DepartText} for {trip.DestinationText} - {trip.ItemName}. Bring ${trip.CashNeededText}. Expected ${trip.ProfitPerHourText}/h, return {trip.ReturnText}.";
            await SendPlannerAlertAsync(title, message);
        }
    }

    private async Task SendPlannerAlertAsync(string title, string message)
    {
        try
        {
            _notificationService.Show(title, message);
            if (_settings.DiscordAlertsEnabled && !string.IsNullOrWhiteSpace(_settings.DiscordWebhookUrl))
            {
                string discordMessage = FormatDiscordMessage(_settings.DiscordMessageTemplate, title, message);
                await _discordWebhookService.SendAsync(_settings.DiscordWebhookUrl, discordMessage);
            }
        }
        catch (Exception ex)
        {
            AppLogService.Warning($"Alert delivery failed: {ex.Message}");
            ShowErrorToastIfNeeded($"Alert delivery failed: {ex.Message}");
        }
    }

    private void ErrorToast_DismissRequested(object? sender, EventArgs e)
    {
        HideErrorToast();
    }

    private void HideErrorToast()
    {
        _errorToastTimer.Stop();
        ErrorToast.Hide();
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

    private async Task<string?> SaveBazaarPriceAsync(int itemId, string value)
    {
        if (itemId <= 0)
        {
            return null;
        }

        value = value.Trim();
        Dictionary<int, decimal> bazaarPrices = _settings.BazaarPrices.ToDictionary(
            entry => entry.Key,
            entry => entry.Value);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (!bazaarPrices.Remove(itemId))
            {
                return null;
            }
        }
        else if (TryParseMoney(value, out decimal bazaarPrice) && bazaarPrice > 0)
        {
            if (bazaarPrices.TryGetValue(itemId, out decimal existing)
                && existing == bazaarPrice)
            {
                return null;
            }

            bazaarPrices[itemId] = bazaarPrice;
        }
        else
        {
            return bazaarPrices.TryGetValue(itemId, out decimal existing)
                ? existing.ToString("N0")
                : string.Empty;
        }

        _settings = _settings with
        {
            BazaarPrices = bazaarPrices
        };
        _settingsService.Save(_settings);
        _travelRefreshService.SetBazaarPrices(_settings.BazaarPrices);
        await _travelRefreshService.RefreshNowAsync();
        return null;
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

    private void InitializeUpdateStatus()
    {
        UpdateCurrentVersionText.Text = $"v{FormatVersion(UpdateCheckerService.GetCurrentVersion())}";
        UpdateLatestVersionText.Text = "-";
        UpdateLastCheckText.Text = "-";
        UpdateStatusText.Text = "Not checked yet";
        LogPathText.Text = $"Error log: {AppLogService.LogPath}";
    }

    private void InitializeAlertSettings()
    {
        DepartureReminderCheckBox.IsChecked = _settings.DepartureRemindersEnabled;
        DepartureReminderMinutesBox.Text = _settings.DepartureReminderMinutes.ToString("N0");
        RestockAlertCheckBox.IsChecked = _settings.RestockAlertsEnabled;
        LandingReminderCheckBox.IsChecked = _settings.LandingReminderEnabled;
        NerveNearFullAlertCheckBox.IsChecked = _settings.NerveNearFullAlertEnabled;
        DrugCooldownReminderCheckBox.IsChecked = _settings.DrugCooldownReminderEnabled;
        DiscordAlertCheckBox.IsChecked = _settings.DiscordAlertsEnabled;
        DiscordWebhookBox.Password = _settings.DiscordWebhookUrl;
        DiscordMessageTemplateBox.Text = _settings.DiscordMessageTemplate;
        AlertSettingsStatusText.Text = "Alerts inactive until enabled";
    }

    private bool TryReadAlertSettings(out AppSettings nextSettings, out string? validationError)
    {
        nextSettings = _settings;
        validationError = null;

        if (!int.TryParse(
                DepartureReminderMinutesBox.Text.Trim(),
                NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture,
                out int minutes))
        {
            validationError = "Lead time must be a number.";
            return false;
        }

        if (minutes < AppSettings.MinimumDepartureReminderMinutes
            || minutes > AppSettings.MaximumDepartureReminderMinutes)
        {
            validationError = $"Lead time must be between {AppSettings.MinimumDepartureReminderMinutes} and {AppSettings.MaximumDepartureReminderMinutes} minutes.";
            return false;
        }

        string webhookUrl = DiscordWebhookBox.Password.Trim();
        bool discordEnabled = DiscordAlertCheckBox.IsChecked == true;
        if (discordEnabled && !IsValidDiscordWebhookUrl(webhookUrl))
        {
            validationError = "Discord webhook must be a valid https URL.";
            return false;
        }

        nextSettings = _settings with
        {
            DepartureRemindersEnabled = DepartureReminderCheckBox.IsChecked == true,
            DepartureReminderMinutes = minutes,
            RestockAlertsEnabled = RestockAlertCheckBox.IsChecked == true,
            LandingReminderEnabled = LandingReminderCheckBox.IsChecked == true,
            NerveNearFullAlertEnabled = NerveNearFullAlertCheckBox.IsChecked == true,
            DrugCooldownReminderEnabled = DrugCooldownReminderCheckBox.IsChecked == true,
            DiscordAlertsEnabled = discordEnabled,
            DiscordWebhookUrl = webhookUrl,
            DiscordMessageTemplate = NormalizeDiscordMessageTemplate(DiscordMessageTemplateBox.Text)
        };
        return true;
    }

    private static bool IsValidDiscordWebhookUrl(string webhookUrl)
    {
        return Uri.TryCreate(webhookUrl, UriKind.Absolute, out Uri? webhookUri)
            && webhookUri.Scheme == Uri.UriSchemeHttps;
    }

    private static string NormalizeDiscordMessageTemplate(string? template)
    {
        return string.IsNullOrWhiteSpace(template)
            ? AppSettings.DefaultDiscordMessageTemplate
            : template;
    }

    private static string FormatDiscordMessage(string template, string title, string message)
    {
        return NormalizeDiscordMessageTemplate(template)
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{message}", message, StringComparison.OrdinalIgnoreCase)
            .Replace("{app}", "Torn Stock Travel", StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", DateTimeOffset.Now.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{newline}", Environment.NewLine, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowChangelogNoticeIfNeeded()
    {
        string currentVersion = FormatVersion(UpdateCheckerService.GetCurrentVersion());
        bool hasPendingReleaseNotes = string.Equals(
            NormalizeVersionTag(_settings.PendingReleaseVersion),
            currentVersion,
            StringComparison.OrdinalIgnoreCase);
        string releaseNotes = hasPendingReleaseNotes
            ? FormatReleaseNotesPreview(_settings.PendingReleaseNotes)
            : "You can check for future release notes from the Settings tab.";

        if (string.Equals(_settings.LastSeenVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool shouldShowNotice = !string.IsNullOrWhiteSpace(_settings.LastSeenVersion)
            || hasPendingReleaseNotes;
        _settings = _settings with
        {
            LastSeenVersion = currentVersion,
            PendingReleaseVersion = null,
            PendingReleaseNotes = null
        };
        _settingsService.Save(_settings);

        if (!shouldShowNotice)
        {
            return;
        }

        WpfMessageBox.Show(
            this,
            $"Updated to v{currentVersion}.\n\n{releaseNotes}",
            "Torn Stock Travel",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string BuildWindowTitle()
    {
        Version version = UpdateCheckerService.GetCurrentVersion();
        return $"Torn Stock Travel (v{FormatVersion(version)})";
    }

    private static string FormatVersion(Version version)
    {
        string displayVersion = version.Revision > 0
            ? version.ToString()
            : version.Build > 0
                ? version.ToString(3)
                : version.ToString(2);

        return displayVersion;
    }

    private static string? NormalizeVersionTag(string? version)
    {
        return string.IsNullOrWhiteSpace(version)
            ? null
            : version.Trim().TrimStart('v', 'V');
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

    private void InitializePlannerPresets()
    {
        RefreshPlannerPresetList(_settings.PlannerPresets.FirstOrDefault()?.Name);
    }

    private void RefreshPlannerPresetList(string? selectedName)
    {
        PlannerPresetBox.ItemsSource = _settings.PlannerPresets;
        PlannerPresetBox.SelectedItem = _settings.PlannerPresets.FirstOrDefault(preset =>
            string.Equals(preset.Name, selectedName, StringComparison.CurrentCultureIgnoreCase));
    }

    private void InitializePlannerCapacityProfile()
    {
        PlannerCapacityProfileBox.SelectedIndex = 0;
        ApplySelectedPlannerCapacityProfile();
    }

    private void ApplySelectedPlannerCapacityProfile()
    {
        if (PlannerCapacityProfileBox.SelectedItem is not ComboBoxItem item
            || item.Tag is not string tag
            || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int capacity))
        {
            PlannerCapacityBox.Text = _settings.BuyCapacity > 0 ? _settings.BuyCapacity.ToString("N0") : string.Empty;
            return;
        }

        PlannerCapacityBox.Text = capacity > 0
            ? capacity.ToString("N0")
            : _settings.BuyCapacity > 0 ? _settings.BuyCapacity.ToString("N0") : string.Empty;
    }

    private bool TryReadPlannerCapacity(out int? capacity, out string? validationError)
    {
        validationError = null;
        string value = PlannerCapacityBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            capacity = _settings.BuyCapacity > 0 ? _settings.BuyCapacity : null;
            return true;
        }

        if (!int.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out int parsed)
            || parsed < 0)
        {
            capacity = null;
            validationError = "Carry capacity must be a positive number or empty.";
            return false;
        }

        capacity = parsed > 0 ? parsed : null;
        return true;
    }

    private void ApplyPlannerPreset(PlannerPreset preset)
    {
        _isApplyingPlannerPreset = true;
        try
        {
            PlannerTargetItemBox.Text = preset.TargetItemQuery;
            PlannerExcludedItemsBox.Text = string.Join(", ", preset.ExcludedItemQueries);
            SetPlannerExcludedCountries(preset.ExcludedCountryCodes);
            SelectPlannerStrategy(preset.Strategy);
            _plannerStrategy = preset.Strategy;
            PlannerCapacityBox.Text = preset.CarryCapacity > 0
                ? preset.CarryCapacity.ToString("N0")
                : _settings.BuyCapacity > 0 ? _settings.BuyCapacity.ToString("N0") : string.Empty;
            SetPlannerWindow(DateTimeOffset.Now, DateTimeOffset.Now.Add(TimeSpan.FromHours(Math.Clamp(preset.ActiveWindowHours, 1, 24))));
            PlannerPresetNameBox.Text = preset.Name;
        }
        finally
        {
            _isApplyingPlannerPreset = false;
        }

        UpdateTravelPlanner();
    }

    private void SetPlannerExcludedCountries(IReadOnlyList<string> countryCodes)
    {
        HashSet<string> excluded = countryCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (WpfCheckBox checkBox in PlannerExcludedCountryPanel.Children.OfType<WpfCheckBox>())
        {
            if (checkBox.Tag is string tag)
            {
                checkBox.IsChecked = excluded.Contains(tag);
            }
        }
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
        HashSet<string> excludedCountryCodes = GetPlannerExcludedCountryCodes();

        return new TravelPlannerFilters(targetItem, excludedItems, excludedCountryCodes);
    }

    private HashSet<string> GetPlannerExcludedCountryCodes()
    {
        return PlannerExcludedCountryPanel.Children
            .OfType<WpfCheckBox>()
            .Where(checkBox => checkBox.IsChecked == true && checkBox.Tag is string)
            .Select(checkBox => ((string)checkBox.Tag).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<TravelPlanTimelineEntry> BuildTimelineEntries(TravelPlannerSessionPlan plan)
    {
        return plan.Trips
            .Select((trip, index) => new TravelPlanTimelineEntry(
                $"Trip {index + 1}",
                $"{trip.DestinationText} - {trip.ItemName}",
                $"Depart {trip.DepartText}",
                $"Arrive {trip.ArriveText}",
                $"Return {trip.ReturnText}",
                $"${trip.ProfitPerHourText}/h | ${trip.ProfitText}",
                trip.WalletReadinessText))
            .ToList();
    }

    private void UpdatePlannerWalletReadiness(TravelPlannerSessionPlan plan)
    {
        if (_moneyStatus is null)
        {
            PlannerWalletReadinessText.Text = $"Bring ${plan.TotalCashNeeded:N0} | Wallet unavailable | Budget limit {_settings.MaxSpendPercent}%";
            return;
        }

        decimal spendLimit = _moneyStatus.Total * _settings.MaxSpendPercent / 100m;
        decimal walletShortfall = Math.Max(0, plan.TotalCashNeeded - _moneyStatus.Wallet);
        string walletText = walletShortfall <= 0
            ? "Wallet ready"
            : $"Wallet short by ${walletShortfall:N0}";
        string limitText = plan.TotalCashNeeded <= spendLimit
            ? $"Within {_settings.MaxSpendPercent}% limit"
            : $"Above {_settings.MaxSpendPercent}% limit";

        PlannerWalletReadinessText.Text = $"Bring ${plan.TotalCashNeeded:N0} | {walletText} | {limitText}";
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
