using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HuaweiHROverlay.Core;

namespace HuaweiHROverlay;

public partial class MainWindow : Window
{
    // ── Services ──────────────────────────────────────────────────────────
    private BleHeartRateService? _ble;
    private WebSocketServer? _wsServer;
    private HttpOverlayServer? _httpServer;

    // ── State ─────────────────────────────────────────────────────────────
    private readonly ObservableCollection<BleDeviceInfo> _devices = [];
    private bool _isConnected;
    private int _currentBpm;

    // ── Simulator ─────────────────────────────────────────────────────────
    private DispatcherTimer? _simTimer;
    private readonly Random _rng = new();
    private int _simBpm = 72;
    private int _simDir = 1;

    // ──────────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ── Startup ───────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Start WebSocket server
        _wsServer = new WebSocketServer();
        _wsServer.ClientCountChanged += (_, count) =>
            Dispatcher.Invoke(() => WsClientCount.Text = count.ToString());
        _wsServer.Start();

        // Start HTTP overlay server
        _httpServer = new HttpOverlayServer();
        _httpServer.Start();

        SetStatus($"Servers ready. WS: ws://localhost:{WebSocketServer.Port}/   HTTP: http://localhost:{HttpOverlayServer.Port}/");

        // Init BLE service
        _ble = new BleHeartRateService();
        _ble.HeartRateChanged += OnHeartRateChanged;
        _ble.DeviceDiscovered += OnDeviceDiscovered;
        _ble.DeviceRemoved += OnDeviceRemoved;
        _ble.StatusChanged += (_, msg) => Dispatcher.Invoke(() => SetStatus(msg));
        _ble.Disconnected += (_, _) => Dispatcher.Invoke(OnBleDisconnected);

        await Task.CompletedTask;
    }

    // ── BLE Events ────────────────────────────────────────────────────────

    private void OnHeartRateChanged(object? sender, HeartRateReading reading)
    {
        _currentBpm = reading.Bpm;
        Dispatcher.Invoke(() => UpdateBpmDisplay(reading.Bpm));

        _httpServer?.UpdateBpm(reading.Bpm, true);
        _ = _wsServer?.BroadcastAsync(reading.ToJson());
    }

    private void OnDeviceDiscovered(object? sender, BleDeviceInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            // Update existing or add new
            var existing = _devices.FirstOrDefault(d => d.Id == info.Id);
            if (existing != null)
            {
                var idx = _devices.IndexOf(existing);
                _devices[idx] = info;
            }
            else
            {
                _devices.Add(info);
                ScanStatus.Text = $"Found {_devices.Count} device(s). Select one and click Connect.";
            }
        });
    }

    private void OnDeviceRemoved(object? sender, BleDeviceInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _devices.FirstOrDefault(d => d.Id == info.Id);
            if (existing != null) _devices.Remove(existing);
        });
    }

    private void OnBleDisconnected()
    {
        _isConnected = false;
        UpdateConnectionBadge(false);
        ConnectButton.IsEnabled = DeviceList.SelectedItem != null;
        DisconnectButton.IsEnabled = false;
        UpdateBpmDisplay(0);
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
        ConnectButton.IsEnabled = DeviceList.SelectedItem != null && !_isConnected;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not BleDeviceInfo device) return;

        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = false;
        SetStatus($"Connecting to {device.DisplayName}…");

        bool ok = await _ble!.ConnectAsync(device.Id, device.DisplayName);

        if (ok)
        {
            _isConnected = true;
            UpdateConnectionBadge(true);
            DisconnectButton.IsEnabled = true;
        }
        else
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ble != null) await _ble.DisconnectAsync();
        OnBleDisconnected();
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
        // Disconnect real BLE if active
        if (_isConnected) DisconnectButton_Click(sender, e);

        _simTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _simTimer.Tick += SimTick;
        _simTimer.Start();

        UpdateConnectionBadge(true, simulated: true);
        SetStatus("Simulator active — generating fake BPM values.");
    }

    private void SimMode_Unchecked(object sender, RoutedEventArgs e)
    {
        _simTimer?.Stop();
        _simTimer = null;
        UpdateConnectionBadge(false);
        UpdateBpmDisplay(0);
        SetStatus("Simulator stopped.");
        _httpServer?.UpdateBpm(0, false);
        _ = _wsServer?.BroadcastAsync("{\"bpm\":0,\"simulated\":false}");
    }

    private void SimTick(object? sender, EventArgs e)
    {
        // Smooth random walk around a target BPM
        _simBpm += _simDir * _rng.Next(1, 4);
        if (_simBpm >= 160 || _rng.Next(0, 10) == 0) _simDir = -1;
        if (_simBpm <= 55  || _rng.Next(0, 10) == 0) _simDir = 1;
        _simBpm = Math.Clamp(_simBpm, 50, 180);

        var reading = new HeartRateReading
        {
            Bpm = _simBpm,
            DeviceName = "Simulator"
        };

        UpdateBpmDisplay(_simBpm);
        _httpServer?.UpdateBpm(_simBpm, true);
        _ = _wsServer?.BroadcastAsync(reading.ToJson());
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
