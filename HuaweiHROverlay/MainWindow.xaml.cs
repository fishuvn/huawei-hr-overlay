using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HuaweiHROverlay.Core;

namespace HuaweiHROverlay;

/// <summary>ViewModel row for a discovered BLE device in the scanner list.</summary>
public sealed class DiscoveredDeviceVm : INotifyPropertyChanged
{
    private BleDeviceInfo _info;
    private bool _isConnected;

    public DiscoveredDeviceVm(BleDeviceInfo info, bool isConnected)
    {
        _info = info;
        _isConnected = isConnected;
    }

    public string Id => _info.Id;
    public string DisplayName => _info.DisplayName;
    public bool HasHrService => _info.HasHrService;
    public bool IsLikelyHrWatch => _info.IsLikelyHrWatch;
    public int Rssi => _info.Rssi;
    public string RssiDisplay => _info.RssiDisplay;
    public string SignalBarsGlyph => _info.SignalBarsGlyph;
    public string BadgeText => _info.BadgeText;
    public string AddressSubtitle => $"{_info.FormattedAddress}  ·  ID {_info.Id}";

    public Brush BadgeBgBrush => _info.HasHrService
        ? new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0x3B, 0x5C))
        : new SolidColorBrush(Color.FromRgb(0x1D, 0x22, 0x34));

    public Brush BadgeFgBrush => _info.HasHrService
        ? new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x93))
        : new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xB0));

    public Brush SignalBrush => _info.SignalBars switch
    {
        >= 3 => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
        2    => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
        _    => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
    };

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            Notify(nameof(IsConnected));
            Notify(nameof(ConnectedPillVisibility));
        }
    }

    public Visibility ConnectedPillVisibility => _isConnected ? Visibility.Visible : Visibility.Collapsed;

    public void UpdateFrom(BleDeviceInfo updated, bool isConnected)
    {
        _info = updated;
        _isConnected = isConnected;
        Notify(nameof(DisplayName));
        Notify(nameof(RssiDisplay));
        Notify(nameof(SignalBarsGlyph));
        Notify(nameof(SignalBrush));
        Notify(nameof(BadgeText));
        Notify(nameof(BadgeBgBrush));
        Notify(nameof(BadgeFgBrush));
        Notify(nameof(ConnectedPillVisibility));
    }

    private void Notify(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Live row for one connected watch/simulator with per-device telemetry history.</summary>
public sealed class ConnectedDeviceVm : INotifyPropertyChanged
{
    private int _bpm;
    private bool _isReconnecting;
    private bool _isHero;
    private string _copyButtonLabel = "Copy URL";
    private long _sumBpm;
    private int _sampleCount;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsSimulated { get; init; }
    public List<int> History { get; } = new(60);

    public int MinBpm { get; private set; }
    public int MaxBpm { get; private set; }
    public int AvgBpm => _sampleCount > 0 ? (int)Math.Round((double)_sumBpm / _sampleCount) : 0;

    public int Bpm
    {
        get => _bpm;
        set
        {
            _bpm = value;
            if (value > 0)
            {
                MinBpm = MinBpm == 0 ? value : Math.Min(MinBpm, value);
                MaxBpm = Math.Max(MaxBpm, value);
                _sumBpm += value;
                _sampleCount++;
                if (History.Count >= 45) History.RemoveAt(0);
                History.Add(value);
            }
            Notify(nameof(BpmDisplay));
            Notify(nameof(BpmBrush));
        }
    }

    public bool IsReconnecting
    {
        get => _isReconnecting;
        set
        {
            _isReconnecting = value;
            Notify(nameof(SubtitleText));
            Notify(nameof(StateDotBrush));
        }
    }

    public bool IsHero
    {
        get => _isHero;
        set
        {
            _isHero = value;
            Notify(nameof(HeroTagVisibility));
            Notify(nameof(RowBorderBrush));
        }
    }

    public string CopyButtonLabel
    {
        get => _copyButtonLabel;
        set { _copyButtonLabel = value; Notify(nameof(CopyButtonLabel)); }
    }

    public string BpmDisplay => _bpm > 0 ? _bpm.ToString() : "--";

    public string SubtitleText => _isReconnecting
        ? $"ID {Id}  ·  Signal lost — reconnecting…"
        : (IsSimulated ? "Simulated HR stream  ·  ID SIM" : $"ID {Id}  ·  Streaming live");

    public Visibility HeroTagVisibility => _isHero ? Visibility.Visible : Visibility.Collapsed;

    public Brush RowBorderBrush => _isHero
        ? new SolidColorBrush(Color.FromArgb(0x66, 0x38, 0xBD, 0xF8))
        : new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x3B));

    public Brush StateDotBrush => _isReconnecting
        ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
        : new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));

    public Brush BpmBrush => MainWindow.GetZoneBrush(_bpm);

    public void ResetStats()
    {
        MinBpm = _bpm > 0 ? _bpm : 0;
        MaxBpm = _bpm > 0 ? _bpm : 0;
        _sumBpm = _bpm > 0 ? _bpm : 0;
        _sampleCount = _bpm > 0 ? 1 : 0;
        History.Clear();
        if (_bpm > 0) History.Add(_bpm);
    }

    private void Notify(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private BleHeartRateService? _ble;
    private WebSocketServer? _wsServer;
    private HttpOverlayServer? _httpServer;
    private readonly GitHubUpdateService _updater = new();

    private readonly List<DiscoveredDeviceVm> _allDiscovered = [];
    private readonly ObservableCollection<DiscoveredDeviceVm> _visibleDevices = [];
    private readonly ObservableCollection<ConnectedDeviceVm> _connected = [];
    private ConnectedDeviceVm? _heroDevice;
    private string? _obsTargetDeviceId; // null = global URL, non-null = locked to one device

    // GitHub Update State
    private UpdateCheckResult? _latestUpdate;
    private CancellationTokenSource? _downloadCts;
    private bool _isApplyingUpdate;

    // Simulator State
    private const string SimDeviceId = "SIM";
    private DispatcherTimer? _simTimer;
    private readonly Random _rng = new();
    private int _simBpm = 76;
    private int _simDir = 1;

    public MainWindow()
    {
        InitializeComponent();
        VersionBadgeText.Text = $"v{GitHubUpdateService.GetCurrentVersionString()}";
        DeviceList.ItemsSource = _visibleDevices;
        ConnectedList.ItemsSource = _connected;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ── Startup ───────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var startupErrors = new List<string>();

        try
        {
            _wsServer = new WebSocketServer();
            _wsServer.ClientCountChanged += (_, count) =>
                Dispatcher.Invoke(() => WsClientCount.Text = count.ToString());
            _wsServer.Start();
        }
        catch (Exception ex)
        {
            _wsServer = null;
            startupErrors.Add($"WS :{WebSocketServer.Port} ({ex.Message})");
        }

        try
        {
            _httpServer = new HttpOverlayServer();
            _httpServer.Start();
        }
        catch (Exception ex)
        {
            _httpServer = null;
            startupErrors.Add($"HTTP :{HttpOverlayServer.Port} ({ex.Message})");
        }

        if (startupErrors.Count == 0)
        {
            ServerHealthDot.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            SetStatus($"Ready — HTTP :8764 & WebSocket :8765 active.");
        }
        else
        {
            ServerHealthDot.Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            SetStatus("Port conflict: " + string.Join("; ", startupErrors));
        }

        _ble = new BleHeartRateService
        {
            AutoReconnect = AutoReconnectCheck.IsChecked == true
        };
        _ble.HeartRateChanged += OnHeartRateChanged;
        _ble.DeviceDiscovered += OnDeviceDiscovered;
        _ble.StatusChanged += (_, msg) => Dispatcher.Invoke(() => SetStatus(msg));
        _ble.SessionAdded += (_, s) => Dispatcher.Invoke(() => OnSessionAdded(s));
        _ble.SessionRemoved += (_, info) => Dispatcher.Invoke(() => OnSessionRemoved(info.DeviceId, info.DeviceName));
        _ble.SessionReconnectStateChanged += (_, state) => Dispatcher.Invoke(() => OnSessionReconnectChanged(state));
        _ble.ScanningStoppedUnexpectedly += (_, _) => Dispatcher.Invoke(OnScanStoppedUnexpectedly);

        RefreshObsUrlBox();

        // Silent non-blocking update check shortly after startup
        await Task.Delay(1500);
        await CheckForUpdatesInternalAsync(userInitiated: false);
    }

    // ── GitHub Update Workflow ────────────────────────────────────────────

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesInternalAsync(userInitiated: true);
    }

    private async Task CheckForUpdatesInternalAsync(bool userInitiated)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Checking…";

        try
        {
            var result = await _updater.CheckForUpdatesAsync();
            _latestUpdate = result;

            if (result.IsUpdateAvailable)
            {
                ShowUpdateBanner(result);
                CheckUpdateButton.Content = $"Update v{result.LatestVersion}";
                SetStatus($"New release v{result.LatestVersion} available on GitHub.");
            }
            else
            {
                CheckUpdateButton.Content = "✓ Up to date";
                if (userInitiated)
                    SetStatus($"You are running the latest version (v{result.CurrentVersion}).");

                _ = ResetUpdateButtonLabelLaterAsync();
            }
        }
        catch (Exception ex)
        {
            CheckUpdateButton.Content = "↻  Check Updates";
            if (userInitiated)
                SetStatus($"Update check failed: {ex.Message}");
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task ResetUpdateButtonLabelLaterAsync()
    {
        await Task.Delay(3500);
        if (_latestUpdate is not { IsUpdateAvailable: true })
            CheckUpdateButton.Content = "↻  Check Updates";
    }

    private void ShowUpdateBanner(UpdateCheckResult result)
    {
        UpdateTitleText.Text = $"v{result.LatestVersion}";
        if (result.SelectedAsset != null)
        {
            string variantLabel = result.DetectedVariant == AppBuildVariant.Lite ? "Lite build" : "Self-contained";
            string verifyTag = !string.IsNullOrEmpty(result.SelectedAsset.Sha256Hex) ? " · SHA-256 verified" : "";
            UpdateMetaText.Text = $"{variantLabel} · {result.SelectedAsset.SizeDisplay}{verifyTag}";
            InstallUpdateButton.Content = $"⬇  Update to v{result.LatestVersion} & Restart";
            InstallUpdateButton.IsEnabled = true;
        }
        else
        {
            UpdateMetaText.Text = "Manual download on GitHub";
            InstallUpdateButton.Content = "Open GitHub Releases ↗";
            InstallUpdateButton.IsEnabled = true;
        }

        UpdateNotesText.Text = result.ReleaseNotes;
        UpdateBannerCard.Visibility = Visibility.Visible;
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUpdate == null) return;
        if (_latestUpdate.SelectedAsset == null)
        {
            OpenUrl(_latestUpdate.HtmlUrl);
            return;
        }

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();

        InstallUpdateButton.IsEnabled = false;
        DismissUpdateButton.IsEnabled = false;
        CancelUpdateButton.Visibility = Visibility.Visible;
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        UpdateProgressText.Text = $"Connecting to GitHub ({_latestUpdate.SelectedAsset.Name})…";

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            if (p.Percentage.HasValue)
                UpdateProgressBar.Value = p.Percentage.Value;
            UpdateProgressText.Text = $"Downloading update: {p.Summary}";
        });

        try
        {
            string stagedPath = await _updater.DownloadUpdateAsync(
                _latestUpdate.SelectedAsset, progress, _downloadCts.Token);

            UpdateProgressBar.Value = 100;
            UpdateProgressText.Text = "Checksum verified — restarting into new version…";

            var (launched, msg) = GitHubUpdateService.LaunchSelfUpdate(stagedPath);
            SetStatus(msg);

            if (launched)
            {
                _isApplyingUpdate = true;
                Application.Current.Shutdown();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateProgressText.Text = "Update download cancelled.";
            SetStatus("Update download cancelled.");
        }
        catch (ComponentModelWin32CanceledException)
        {
            UpdateProgressText.Text = "Administrator permission was declined.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            UpdateProgressText.Text = "Administrator permission was cancelled.";
            SetStatus("Update cancelled at elevation prompt.");
        }
        catch (Exception ex)
        {
            UpdateProgressText.Text = $"Update failed: {ex.Message}";
            SetStatus($"Update failed: {ex.Message}");
        }
        finally
        {
            if (!_isApplyingUpdate)
            {
                InstallUpdateButton.IsEnabled = true;
                DismissUpdateButton.IsEnabled = true;
                CancelUpdateButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CancelUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateBannerCard.Visibility = Visibility.Collapsed;
    }

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(_latestUpdate?.HtmlUrl ?? GitHubUpdateService.ReleasesPageUrl);
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl($"https://github.com/{GitHubUpdateService.RepoOwner}/{GitHubUpdateService.RepoName}");
    }

    private sealed class ComponentModelWin32CanceledException : Exception;

    // ── BLE & Telemetry Events ────────────────────────────────────────────

    private void OnHeartRateChanged(object? sender, HeartRateReading reading)
    {
        var vm = _connected.FirstOrDefault(c => c.Id == reading.DeviceId);
        if (vm != null)
        {
            vm.IsReconnecting = false;
            vm.Bpm = reading.Bpm;
        }
        if (vm != null && vm == _heroDevice)
            RefreshHeroTelemetry();

        _httpServer?.UpdateBpm(reading.DeviceId, reading.Bpm, true, reading.DeviceName);
        _ = _wsServer?.BroadcastAsync(reading.DeviceId, reading.ToJson());
    }

    private void OnDeviceDiscovered(object? sender, BleDeviceInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            bool connected = _ble?.IsConnectedOrReconnecting(info.Id) ?? false;
            var existing = _allDiscovered.FirstOrDefault(d => d.Id == info.Id);
            if (existing != null)
            {
                existing.UpdateFrom(info, connected);
            }
            else
            {
                var vm = new DiscoveredDeviceVm(info, connected);
                // Insert HR/Watch candidates near top while keeping existing selection stable
                if (vm.IsLikelyHrWatch)
                {
                    int insertAt = _allDiscovered.TakeWhile(d => d.IsLikelyHrWatch).Count();
                    _allDiscovered.Insert(insertAt, vm);
                }
                else
                {
                    _allDiscovered.Add(vm);
                }
            }

            RebuildVisibleDeviceList();
        });
    }

    private void RebuildVisibleDeviceList()
    {
        string? selectedId = (DeviceList.SelectedItem as DiscoveredDeviceVm)?.Id;
        bool hrOnly = HrOnlyFilterCheck.IsChecked == true;

        var filtered = hrOnly
            ? _allDiscovered.Where(d => d.IsLikelyHrWatch).ToList()
            : _allDiscovered;

        _visibleDevices.Clear();
        foreach (var item in filtered)
            _visibleDevices.Add(item);

        if (selectedId != null)
        {
            var match = _visibleDevices.FirstOrDefault(d => d.Id == selectedId);
            if (match != null) DeviceList.SelectedItem = match;
        }

        UpdateEmptyScanState();
        if (_ble?.IsScanning == true && _visibleDevices.Count > 0)
            ScanStatus.Text = $"Found {_visibleDevices.Count} device(s) — select or double-click a watch to connect.";
    }

    private void UpdateEmptyScanState()
    {
        if (_visibleDevices.Count > 0)
        {
            EmptyScanOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyScanOverlay.Visibility = Visibility.Visible;
        if (_ble?.IsScanning == true)
        {
            EmptyScanTitle.Text = HrOnlyFilterCheck.IsChecked == true
                ? "Scanning for Heart Rate wearables…"
                : "Scanning nearby Bluetooth LE devices…";
            EmptyScanSubtitle.Text = "Keep your watch awake with Settings → HR Data Broadcasts enabled.";
        }
        else if (_allDiscovered.Count > 0 && HrOnlyFilterCheck.IsChecked == true)
        {
            EmptyScanTitle.Text = "No HR wearables matched filter";
            EmptyScanSubtitle.Text = "Uncheck 'HR Only' to inspect all nearby Bluetooth LE devices.";
        }
        else
        {
            EmptyScanTitle.Text = "Ready to discover watches";
            EmptyScanSubtitle.Text = "On your Huawei Watch/Band open Settings → HR Data Broadcasts → ON, then press Scan.";
        }
    }

    private void OnSessionAdded(BleDeviceSession session)
    {
        var vm = _connected.FirstOrDefault(c => c.Id == session.DeviceId);
        if (vm == null)
        {
            vm = new ConnectedDeviceVm { Id = session.DeviceId, Name = session.DeviceName };
            _connected.Add(vm);
        }

        if (_heroDevice == null)
            SetHeroDevice(vm);
        else
            UpdateHeaderConnectionBadge();

        SyncDiscoveredConnectionStates();
        RefreshConnectedCardVisibility();
        UpdateConnectButtonState();
    }

    private void OnSessionRemoved(string deviceId, string deviceName)
    {
        _httpServer?.UpdateBpm(deviceId, 0, false, deviceName);
        _ = _wsServer?.BroadcastAsync(deviceId,
            new HeartRateReading { Bpm = 0, Connected = false, DeviceId = deviceId, DeviceName = deviceName }.ToJson());

        var vm = _connected.FirstOrDefault(c => c.Id == deviceId);
        if (vm != null) _connected.Remove(vm);

        if (_obsTargetDeviceId == deviceId)
        {
            _obsTargetDeviceId = null;
            RefreshObsUrlBox();
        }

        if (_heroDevice == vm)
            SetHeroDevice(_connected.FirstOrDefault());
        else
            UpdateHeaderConnectionBadge();

        SyncDiscoveredConnectionStates();
        RefreshConnectedCardVisibility();
        UpdateConnectButtonState();
    }

    private void OnSessionReconnectChanged(SessionReconnectStateEventArgs state)
    {
        var vm = _connected.FirstOrDefault(c => c.Id == state.DeviceId);
        if (vm != null)
        {
            vm.IsReconnecting = state.IsReconnecting;
            if (state.IsReconnecting)
            {
                _httpServer?.UpdateBpm(state.DeviceId, 0, false, state.DeviceName);
                _ = _wsServer?.BroadcastAsync(state.DeviceId,
                    new HeartRateReading { Bpm = 0, Connected = false, DeviceId = state.DeviceId, DeviceName = state.DeviceName }.ToJson());
            }
        }
        UpdateHeaderConnectionBadge();
        if (vm == _heroDevice) RefreshHeroTelemetry();
    }

    private void SyncDiscoveredConnectionStates()
    {
        foreach (var d in _allDiscovered)
            d.IsConnected = _ble?.IsConnectedOrReconnecting(d.Id) ?? false;
    }

    private void RefreshConnectedCardVisibility()
    {
        ConnectedCountText.Text = _connected.Count.ToString();
        ConnectedCard.Visibility = _connected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnScanStoppedUnexpectedly()
    {
        ScanButton.IsEnabled = true;
        StopScanButton.IsEnabled = false;
        ScanningPulseDot.Visibility = Visibility.Collapsed;
        ScanStatus.Text = "Bluetooth scan stopped unexpectedly. Click Scan to retry.";
        UpdateEmptyScanState();
    }

    // ── Scanner & Connection Actions ──────────────────────────────────────

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        _allDiscovered.Clear();
        _visibleDevices.Clear();
        _ble?.StartScanning();
        ScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = true;
        ScanningPulseDot.Visibility = Visibility.Visible;
        ScanStatus.Text = "Scanning… (ensure HR Data Broadcast is enabled on watch)";
        UpdateEmptyScanState();
    }

    private void StopScanButton_Click(object sender, RoutedEventArgs e)
    {
        _ble?.StopScanning();
        ScanButton.IsEnabled = true;
        StopScanButton.IsEnabled = false;
        ScanningPulseDot.Visibility = Visibility.Collapsed;
        ScanStatus.Text = $"Scan paused. {_visibleDevices.Count} device(s) listed.";
        UpdateEmptyScanState();
    }

    private void HrOnlyFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RebuildVisibleDeviceList();
    }

    private void AutoReconnect_Changed(object sender, RoutedEventArgs e)
    {
        if (_ble != null)
            _ble.AutoReconnect = AutoReconnectCheck.IsChecked == true;
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateConnectButtonState();
    }

    private async void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceList.SelectedItem is DiscoveredDeviceVm vm && !vm.IsConnected)
            await ConnectSelectedDeviceAsync(vm);
    }

    private void UpdateConnectButtonState()
    {
        if (DeviceList.SelectedItem is not DiscoveredDeviceVm selected)
        {
            ConnectButton.IsEnabled = false;
            ConnectButton.Content = "⚡  Connect Watch";
            return;
        }

        bool already = _ble?.IsConnectedOrReconnecting(selected.Id) ?? false;
        ConnectButton.IsEnabled = !already;
        ConnectButton.Content = already ? "✓  Connected" : "⚡  Connect Watch";
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is DiscoveredDeviceVm device)
            await ConnectSelectedDeviceAsync(device);
    }

    private async Task ConnectSelectedDeviceAsync(DiscoveredDeviceVm device)
    {
        if (_ble == null || _ble.IsConnectedOrReconnecting(device.Id)) return;

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Connecting…";
        SetStatus($"Connecting to {device.DisplayName}…");

        bool ok = await _ble.ConnectAsync(device.Id, device.DisplayName);

        SetStatus(ok
            ? $"✓ {device.DisplayName} streaming live ({_connected.Count} active)."
            : $"Could not connect to {device.DisplayName}. Verify HR Data Broadcast is ON.");
        SyncDiscoveredConnectionStates();
        UpdateConnectButtonState();
    }

    private void ConnectedRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConnectedDeviceVm vm) return;
        SetHeroDevice(vm);
        _obsTargetDeviceId = _connected.Count > 1 ? vm.Id : _obsTargetDeviceId;
        RefreshObsUrlBox();
    }

    private async void CopyDeviceUrl_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not ConnectedDeviceVm vm) return;

        _obsTargetDeviceId = vm.Id;
        RefreshObsUrlBox();
        string url = BuildOverlayUrl(vm.Id);
        Clipboard.SetText(url);
        SetStatus($"Copied OBS URL for {vm.Name}: {url}");

        vm.CopyButtonLabel = "✓ Copied";
        await Task.Delay(1600);
        vm.CopyButtonLabel = "Copy URL";
    }

    private void PreviewDeviceUrl_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not ConnectedDeviceVm vm) return;
        OpenUrl(BuildOverlayUrl(vm.Id));
    }

    private async void DisconnectConnected_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not ConnectedDeviceVm vm) return;

        if (vm.Id == SimDeviceId)
        {
            SimMode.IsChecked = false;
            return;
        }

        if (_ble != null)
            await _ble.DisconnectAsync(vm.Id);
    }

    // ── OBS Studio Overlay URL Builder ────────────────────────────────────

    private void ObsOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshObsUrlBox();
    }

    private void UseGlobalUrl_Click(object sender, RoutedEventArgs e)
    {
        _obsTargetDeviceId = null;
        RefreshObsUrlBox();
    }

    private void RefreshObsUrlBox()
    {
        if (OverlayUrlBox == null) return;
        OverlayUrlBox.Text = BuildOverlayUrl(_obsTargetDeviceId);

        if (_obsTargetDeviceId != null)
        {
            var vm = _connected.FirstOrDefault(c => c.Id == _obsTargetDeviceId);
            ObsTargetBadge.Text = $"Target: {vm?.Name ?? _obsTargetDeviceId}";
            UseGlobalUrlButton.Visibility = Visibility.Visible;
        }
        else
        {
            ObsTargetBadge.Text = "Target: Any Active Watch";
            UseGlobalUrlButton.Visibility = Visibility.Collapsed;
        }
    }

    private string BuildOverlayUrl(string? deviceId)
    {
        string basePath = string.IsNullOrWhiteSpace(deviceId)
            ? $"http://localhost:{HttpOverlayServer.Port}/"
            : $"http://localhost:{HttpOverlayServer.Port}/overlay/{deviceId}";

        var query = new List<string>();

        if (ThemeMinimalRadio.IsChecked == true) query.Add("theme=minimal");
        else if (ThemeNeonRadio.IsChecked == true) query.Add("theme=neon");
        else if (ThemePillRadio.IsChecked == true) query.Add("theme=pill");

        if (OptEcgCheck.IsChecked == false) query.Add("ecg=0");
        if (OptZoneCheck.IsChecked == false) query.Add("zone=0");
        if (OptStatsCheck.IsChecked == true) query.Add("stats=1");
        if (OptNameCheck.IsChecked == true) query.Add("name=1");
        if (OptAutoHideCheck.IsChecked == true) query.Add("autohide=1");

        return query.Count == 0 ? basePath : $"{basePath}?{string.Join("&", query)}";
    }

    private async void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(OverlayUrlBox.Text);
        SetStatus("OBS Browser Source URL copied to clipboard.");

        CopyUrlButton.Content = "✓  Copied!";
        await Task.Delay(1700);
        CopyUrlButton.Content = "Copy URL";
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(OverlayUrlBox.Text);
    }

    // ── Simulator ─────────────────────────────────────────────────────────

    private void SimMode_Checked(object sender, RoutedEventArgs e)
    {
        _simTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
        _simTimer.Tick += SimTick;
        _simTimer.Start();

        var vm = _connected.FirstOrDefault(c => c.Id == SimDeviceId);
        if (vm == null)
        {
            vm = new ConnectedDeviceVm { Id = SimDeviceId, Name = "Simulator", IsSimulated = true };
            _connected.Add(vm);
        }

        if (_heroDevice == null)
            SetHeroDevice(vm);
        else
            UpdateHeaderConnectionBadge();

        RefreshConnectedCardVisibility();
        SimTick(this, EventArgs.Empty);
        SetStatus("Simulator active — streaming synthetic BPM telemetry to OBS.");
    }

    private void SimMode_Unchecked(object sender, RoutedEventArgs e)
    {
        _simTimer?.Stop();
        _simTimer = null;
        OnSessionRemoved(SimDeviceId, "Simulator");
        SetStatus("Simulator stopped.");
    }

    private void SimTick(object? sender, EventArgs e)
    {
        _simBpm += _simDir * _rng.Next(1, 5);
        if (_simBpm >= 166 || _rng.Next(0, 9) == 0) _simDir = -1;
        if (_simBpm <= 58  || _rng.Next(0, 9) == 0) _simDir = 1;
        _simBpm = Math.Clamp(_simBpm, 54, 182);

        var vm = _connected.FirstOrDefault(c => c.Id == SimDeviceId);
        if (vm != null) vm.Bpm = _simBpm;
        if (_heroDevice?.Id == SimDeviceId) RefreshHeroTelemetry();

        _httpServer?.UpdateBpm(SimDeviceId, _simBpm, true, "Simulator");
        _ = _wsServer?.BroadcastAsync(SimDeviceId,
            new HeartRateReading { Bpm = _simBpm, Connected = true, DeviceId = SimDeviceId, DeviceName = "Simulator" }.ToJson());
    }

    // ── Hero Telemetry & Sparkline Rendering ──────────────────────────────

    private void SetHeroDevice(ConnectedDeviceVm? vm)
    {
        foreach (var item in _connected)
            item.IsHero = item == vm;

        _heroDevice = vm;
        UpdateHeaderConnectionBadge();
        RefreshHeroTelemetry();
    }

    private void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        _heroDevice?.ResetStats();
        RefreshHeroTelemetry();
    }

    private void RefreshHeroTelemetry()
    {
        if (_heroDevice == null)
        {
            HeroDeviceLabel.Text = "NO ACTIVE WATCH";
            LiveBpm.Text = "--";
            LiveBpm.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            ZoneLabel.Text = "Waiting for signal";
            ZoneLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xB0));
            ZoneDot.Fill = new SolidColorBrush(Color.FromRgb(0x5B, 0x62, 0x7E));
            ZonePillBorder.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x30));
            StatMinText.Text = "--";
            StatAvgText.Text = "--";
            StatMaxText.Text = "--";
            RenderSparkline(null, Color.FromRgb(0xFF, 0x3B, 0x5C));
            return;
        }

        HeroDeviceLabel.Text = _heroDevice.IsReconnecting
            ? $"RECONNECTING  ·  {_heroDevice.Name.ToUpperInvariant()}"
            : $"LIVE TELEMETRY  ·  {_heroDevice.Name.ToUpperInvariant()}";

        int bpm = _heroDevice.Bpm;
        StatMinText.Text = _heroDevice.MinBpm > 0 ? _heroDevice.MinBpm.ToString() : "--";
        StatAvgText.Text = _heroDevice.AvgBpm > 0 ? _heroDevice.AvgBpm.ToString() : "--";
        StatMaxText.Text = _heroDevice.MaxBpm > 0 ? _heroDevice.MaxBpm.ToString() : "--";

        if (bpm <= 0 || _heroDevice.IsReconnecting)
        {
            LiveBpm.Text = "--";
            LiveBpm.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            ZoneLabel.Text = _heroDevice.IsReconnecting ? "Reconnecting…" : "Awaiting pulse…";
            ZoneDot.Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            RenderSparkline(_heroDevice.History, Color.FromRgb(0x94, 0xA3, 0xB8));
            return;
        }

        LiveBpm.Text = bpm.ToString();
        (string zoneName, Color zoneColor) = GetZoneDetails(bpm);

        var zoneBrush = new SolidColorBrush(zoneColor);
        LiveBpm.Foreground = zoneBrush;
        ZoneLabel.Text = zoneName;
        ZoneLabel.Foreground = zoneBrush;
        ZoneDot.Fill = zoneBrush;
        ZonePillBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, zoneColor.R, zoneColor.G, zoneColor.B));

        RenderSparkline(_heroDevice.History, zoneColor);
    }

    private void RenderSparkline(IReadOnlyList<int>? values, Color strokeColor)
    {
        if (values == null || values.Count < 2)
        {
            SparklineStroke.Points.Clear();
            SparklineFill.Points.Clear();
            SparklinePlaceholder.Visibility = Visibility.Visible;
            return;
        }

        SparklinePlaceholder.Visibility = Visibility.Collapsed;

        const double width = 176.0;
        const double height = 54.0;
        const double padY = 7.0;

        int min = Math.Max(40, values.Min() - 6);
        int max = Math.Max(min + 12, values.Max() + 6);

        var strokePts = new PointCollection(values.Count);
        var fillPts = new PointCollection(values.Count + 2)
        {
            new Point(0, height)
        };

        for (int i = 0; i < values.Count; i++)
        {
            double x = (double)i / (values.Count - 1) * width;
            double norm = (double)(values[i] - min) / (max - min);
            double y = height - padY - norm * (height - padY * 2);
            var pt = new Point(x, y);
            strokePts.Add(pt);
            fillPts.Add(pt);
        }

        fillPts.Add(new Point(width, height));

        var brush = new SolidColorBrush(strokeColor);
        SparklineStroke.Stroke = brush;
        SparklineStroke.Points = strokePts;
        SparklineFill.Fill = brush;
        SparklineFill.Points = fillPts;
    }

    internal static (string Name, Color Color) GetZoneDetails(int bpm) => bpm switch
    {
        <= 0  => ("No signal",     Color.FromRgb(0x8B, 0x93, 0xB0)),
        < 100 => ("Rest Zone",     Color.FromRgb(0x38, 0xBD, 0xF8)),
        < 130 => ("Fat Burn Zone", Color.FromRgb(0x34, 0xD3, 0x99)),
        < 160 => ("Cardio Zone",   Color.FromRgb(0xFB, 0xBF, 0x24)),
        _     => ("Peak Zone",     Color.FromRgb(0xFF, 0x3B, 0x5C))
    };

    internal static Brush GetZoneBrush(int bpm) =>
        new SolidColorBrush(GetZoneDetails(bpm).Color);

    private void UpdateHeaderConnectionBadge()
    {
        if (_connected.Count == 0)
        {
            SetBadge(Color.FromRgb(0x88, 0x92, 0xAA), "Disconnected");
            return;
        }

        if (_connected.Any(c => c.IsReconnecting) && _connected.All(c => c.IsReconnecting))
        {
            SetBadge(Color.FromRgb(0xF5, 0x9E, 0x0B), "Reconnecting…");
            return;
        }

        bool allSim = _connected.All(c => c.IsSimulated);
        if (allSim)
        {
            SetBadge(Color.FromRgb(0xFB, 0xBF, 0x24), "Simulating");
            return;
        }

        string label = _connected.Count == 1 ? "Connected" : $"{_connected.Count} Connected";
        SetBadge(Color.FromRgb(0x22, 0xC5, 0x5E), label);
    }

    private void SetBadge(Color c, string text)
    {
        var fg = new SolidColorBrush(c);
        ConnDot.Fill = fg;
        ConnText.Text = text;
        ConnText.Foreground = fg;
        ConnBadge.Background = new SolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B));
    }

    private void SetStatus(string msg) => StatusBar.Text = msg;

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* ignore if default browser fails */ }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _downloadCts?.Cancel();
        _simTimer?.Stop();
        _updater.Dispose();
        if (_ble != null) await _ble.DisposeAsync();
        if (_wsServer != null) await _wsServer.DisposeAsync();
        if (_httpServer != null) await _httpServer.DisposeAsync();
    }
}
