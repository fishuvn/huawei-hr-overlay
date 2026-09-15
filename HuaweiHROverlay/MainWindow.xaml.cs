using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HuaweiHROverlay.Core;

namespace HuaweiHROverlay;

/// <summary>Live row for one connected device.</summary>
public class ConnectedDeviceVm : INotifyPropertyChanged
{
    private int _bpm;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    public int Bpm
    {
        get => _bpm;
        set
        {
            _bpm = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BpmDisplay)));
        }
    }

    public string BpmDisplay => _bpm > 0 ? _bpm.ToString() : "--";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    // ── Services ──────────────────────────────────────────────────────────
    private BleHeartRateService? _ble;
    private WebSocketServer? _wsServer;
    private HttpOverlayServer? _httpServer;

    // ── State ─────────────────────────────────────────────────────────────
    private readonly ObservableCollection<BleDeviceInfo> _devices = [];
    private readonly ObservableCollection<ConnectedDeviceVm> _connected = [];
    private ConnectedDeviceVm? _heroDevice;   // which connected device the big BPM shows

    // ── Simulator ─────────────────────────────────────────────────────────
    private const string SimDeviceId = "SIM";
    private DispatcherTimer? _simTimer;
    private readonly Random _rng = new();
    private int _simBpm = 72;
    private int _simDir = 1;

    // ──────────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        ConnectedList.ItemsSource = _connected;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ── Startup ───────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Start servers — a busy port must not take BLE down with it
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
            startupErrors.Add($"WebSocket (port {WebSocketServer.Port}): {ex.Message}");
        }

        try
        {
            _httpServer = new HttpOverlayServer();
            _httpServer.Start();
        }
        catch (Exception ex)
        {
            _httpServer = null;
            startupErrors.Add($"HTTP (port {HttpOverlayServer.Port}): {ex.Message}");
        }

        SetStatus(startupErrors.Count == 0
            ? $"Servers ready. WS: ws://localhost:{WebSocketServer.Port}/   HTTP: http://localhost:{HttpOverlayServer.Port}/"
            : "Server startup failed — " + string.Join("; ", startupErrors));

        // Init BLE service
        _ble = new BleHeartRateService();
        _ble.HeartRateChanged += OnHeartRateChanged;
        _ble.DeviceDiscovered += OnDeviceDiscovered;
        _ble.StatusChanged += (_, msg) => Dispatcher.Invoke(() => SetStatus(msg));
        _ble.SessionAdded   += (_, s) => Dispatcher.Invoke(() => OnSessionAdded(s));
        _ble.SessionRemoved += (_, s) => Dispatcher.Invoke(() => OnSessionRemoved(s));
        _ble.ScanningStoppedUnexpectedly += (_, _) => Dispatcher.Invoke(OnScanStoppedUnexpectedly);

        await Task.CompletedTask;
    }

    // ── BLE Events ────────────────────────────────────────────────────────

    private void OnHeartRateChanged(object? sender, HeartRateReading reading)
    {
        var vm = _connected.FirstOrDefault(c => c.Id == reading.DeviceId);
        if (vm != null) vm.Bpm = reading.Bpm;
        if (vm != null && vm == _heroDevice) UpdateBpmDisplay(reading.Bpm);

        _httpServer?.UpdateBpm(reading.DeviceId, reading.Bpm, true);
        _ = _wsServer?.BroadcastAsync(reading.DeviceId, reading.ToJson());
    }

    private void OnDeviceDiscovered(object? sender, BleDeviceInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            // Update existing or add new; keep the selected row stable across refreshes
            var selectedId = (DeviceList.SelectedItem as BleDeviceInfo)?.Id;
            var existing = _devices.FirstOrDefault(d => d.Id == info.Id);
            if (existing != null)
            {
                var idx = _devices.IndexOf(existing);
                _devices[idx] = info;
                if (selectedId == info.Id) DeviceList.SelectedIndex = idx;
            }
            else
            {
                _devices.Add(info);
                ScanStatus.Text = $"Found {_devices.Count} device(s). Select one and click Connect.";
            }
        });
    }

    private void OnSessionAdded(BleDeviceSession session)
    {
        var vm = new ConnectedDeviceVm { Id = session.DeviceId, Name = session.DeviceName };
        _connected.Add(vm);

        if (_heroDevice == null)
        {
            _heroDevice = vm;
            UpdateConnectionBadge(true);
            UpdateBpmDisplay(vm.Bpm);
        }
        ConnectedCard.Visibility = Visibility.Visible;
        UpdateConnectButtonState();
    }

    private void OnSessionRemoved(BleDeviceSession session)
    {
        var vm = _connected.FirstOrDefault(c => c.Id == session.DeviceId);
        if (vm != null) _connected.Remove(vm);

        if (_heroDevice == vm)
        {
            _heroDevice = _connected.FirstOrDefault();
            UpdateConnectionBadge(_heroDevice != null);
            UpdateBpmDisplay(_heroDevice?.Bpm ?? 0);
        }
        if (_connected.Count == 0) ConnectedCard.Visibility = Visibility.Collapsed;
        UpdateConnectButtonState();
    }

    private void OnScanStoppedUnexpectedly()
    {
        ScanButton.IsEnabled = true;
        StopScanButton.IsEnabled = false;
        ScanStatus.Text = "Scan stopped unexpectedly. Click Scan to retry.";
    }

    // ── Button Handlers ───────────────────────────────────────────────────

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        _devices.Clear();
        _ble?.StartScanning();
        ScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = true;
        ScanStatus.Text = "Scanning… (ensure HR Data Broadcast is enabled on watch)";
    }

    private void StopScanButton_Click(object sender, RoutedEventArgs e)
    {
        _ble?.StopScanning();
        ScanButton.IsEnabled = true;
        StopScanButton.IsEnabled = false;
        ScanStatus.Text = $"Scan stopped. {_devices.Count} device(s) found.";
    }

    private void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateConnectButtonState();
    }

    private void UpdateConnectButtonState()
    {
        if (DeviceList.SelectedItem is not BleDeviceInfo selected)
        {
            ConnectButton.IsEnabled = false;
            return;
        }
        ConnectButton.IsEnabled = !(_ble?.IsConnectedTo(selected.Id) ?? false);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not BleDeviceInfo device) return;
        if (_ble == null || _ble.IsConnectedTo(device.Id)) return;

        ConnectButton.IsEnabled = false;
        SetStatus($"Connecting to {device.DisplayName}…");

        // Scanning stays on so more devices can be connected right after
        bool ok = await _ble.ConnectAsync(device.Id, device.DisplayName);

        SetStatus(ok
            ? $"{device.DisplayName} connected. {_connected.Count} device(s) live."
            : $"Could not connect to {device.DisplayName}.");
        UpdateConnectButtonState();
    }

    private async void DisconnectConnected_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConnectedDeviceVm vm || _ble == null) return;
        await _ble.DisconnectAsync(vm.Id);
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(OverlayUrlBox.Text);
        SetStatus("URL copied to clipboard! Paste it into OBS Browser Source.");
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(OverlayUrlBox.Text) { UseShellExecute = true });
    }

    // ── Simulator ─────────────────────────────────────────────────────────

    private void SimMode_Checked(object sender, RoutedEventArgs e)
    {
        _simTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _simTimer.Tick += SimTick;
        _simTimer.Start();

        var vm = _connected.FirstOrDefault(c => c.Id == SimDeviceId);
        if (vm == null)
        {
            vm = new ConnectedDeviceVm { Id = SimDeviceId, Name = "Simulator" };
            _connected.Add(vm);
        }
        if (_heroDevice == null)
        {
            _heroDevice = vm;
            UpdateConnectionBadge(true, simulated: true);
            UpdateBpmDisplay(vm.Bpm);
        }
        ConnectedCard.Visibility = Visibility.Visible;
        SetStatus("Simulator active — generating fake BPM values (device id SIM).");
    }

    private void SimMode_Unchecked(object sender, RoutedEventArgs e)
    {
        _simTimer?.Stop();
        _simTimer = null;
        _httpServer?.UpdateBpm(SimDeviceId, 0, false);
        _ = _wsServer?.BroadcastAsync(SimDeviceId, "{\"bpm\":0,\"id\":\"SIM\",\"simulated\":false}");

        var vm = _connected.FirstOrDefault(c => c.Id == SimDeviceId);
        if (vm != null) _connected.Remove(vm);
        if (_heroDevice?.Id == SimDeviceId)
        {
            _heroDevice = _connected.FirstOrDefault();
            UpdateConnectionBadge(_heroDevice != null);
            UpdateBpmDisplay(_heroDevice?.Bpm ?? 0);
        }
        if (_connected.Count == 0) ConnectedCard.Visibility = Visibility.Collapsed;
        SetStatus("Simulator stopped.");
    }

    private void SimTick(object? sender, EventArgs e)
    {
        // Smooth random walk around a target BPM
        _simBpm += _simDir * _rng.Next(1, 4);
        if (_simBpm >= 160 || _rng.Next(0, 10) == 0) _simDir = -1;
        if (_simBpm <= 55  || _rng.Next(0, 10) == 0) _simDir = 1;
        _simBpm = Math.Clamp(_simBpm, 50, 180);

        var vm = _connected.FirstOrDefault(c => c.Id == SimDeviceId);
        if (vm != null) vm.Bpm = _simBpm;
        if (_heroDevice?.Id == SimDeviceId) UpdateBpmDisplay(_simBpm);

        _httpServer?.UpdateBpm(SimDeviceId, _simBpm, true);
        _ = _wsServer?.BroadcastAsync(SimDeviceId,
            new HeartRateReading { Bpm = _simBpm, DeviceId = SimDeviceId, DeviceName = "Simulator" }.ToJson());
    }

    // ── UI Helpers ────────────────────────────────────────────────────────

    private void UpdateBpmDisplay(int bpm)
    {
        if (bpm <= 0)
        {
            LiveBpm.Text = "--";
            ZoneLabel.Text = "No signal";
            return;
        }

        LiveBpm.Text = bpm.ToString();

        (string zoneName, Color zoneColor) = bpm switch
        {
            < 100 => ("Rest Zone", Color.FromRgb(0x40, 0xA0, 0xFF)),
            < 130 => ("Fat Burn Zone", Color.FromRgb(0x30, 0xCC, 0x80)),
            < 160 => ("Cardio Zone", Color.FromRgb(0xFF, 0xB0, 0x20)),
            _     => ("Peak Zone",   Color.FromRgb(0xFF, 0x40, 0x40))
        };

        ZoneLabel.Text = zoneName;
        LiveBpm.Foreground = new SolidColorBrush(zoneColor);
    }

    private void UpdateConnectionBadge(bool connected, bool simulated = false)
    {
        if (connected)
        {
            var color = simulated ? Color.FromRgb(0xFF, 0xB0, 0x20) : Color.FromRgb(0x22, 0xDD, 0x66);
            ConnDot.Fill = new SolidColorBrush(color);
            ConnText.Text = simulated ? "Simulating" : "Connected";
            ConnText.Foreground = new SolidColorBrush(color);
            ConnBadge.Background = new SolidColorBrush(Color.FromArgb(0x28, color.R, color.G, color.B));
        }
        else
        {
            ConnDot.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            ConnText.Text = "Disconnected";
            ConnText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            ConnBadge.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x88, 0x88, 0x88));
        }
    }

    private void SetStatus(string msg) => StatusBar.Text = msg;

    // ── Cleanup ───────────────────────────────────────────────────────────

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _simTimer?.Stop();
        if (_ble != null) await _ble.DisposeAsync();
        if (_wsServer != null) await _wsServer.DisposeAsync();
        if (_httpServer != null) await _httpServer.DisposeAsync();
    }
}
