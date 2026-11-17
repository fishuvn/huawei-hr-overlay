using HuaweiHROverlay.Core;

namespace HuaweiHROverlay;

/// <summary>
/// Optional terminal headless mode (`--headless`) for Linux tiling WMs, SSH sessions, or scripts.
/// </summary>
internal static class HeadlessRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        bool scanOnly = args.Contains("--scan");
        bool simulate = args.Contains("--simulate");
        string? targetDevice = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--device")
                targetDevice = args[i + 1].Replace(":", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await using var ble = new BleHeartRateService();
        ble.StatusChanged += (_, msg) => Console.WriteLine($"[BLE] {msg}");

        if (scanOnly)
        {
            Console.WriteLine($"Scanning for 12s via {ble.PlatformBackendName} (Ctrl+C to stop)...");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ble.DeviceDiscovered += (_, info) =>
            {
                if (seen.Add(info.Id))
                    Console.WriteLine($"  {info.Id,-16}  {info.RssiDisplay,8}  [{info.BadgeText,-9}]  {info.DisplayName}");
            };
            await ble.StartScanningAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(12), cts.Token); } catch (OperationCanceledException) { }
            return 0;
        }

        await using var ws = new WebSocketServer();
        await using var http = new HttpOverlayServer();
        ws.Start();
        http.Start();
        Console.WriteLine($"OBS Overlay HTTP : {http.OverlayUrl}");
        Console.WriteLine($"WebSocket Stream : {ws.Uri}");

        ble.HeartRateChanged += (_, r) =>
        {
            Console.WriteLine($"[{r.DeviceName}] {r.Bpm} BPM");
            http.UpdateBpm(r.DeviceId, r.Bpm, true, r.DeviceName);
            _ = ws.BroadcastAsync(r.DeviceId, r.ToJson());
        };

        if (simulate)
        {
            Console.WriteLine("Simulating heart rate (Ctrl+C to exit)...");
            int bpm = 76, dir = 1;
            while (!cts.IsCancellationRequested)
            {
                bpm = Math.Clamp(bpm + dir * Random.Shared.Next(1, 5), 56, 178);
                if (bpm >= 165 || Random.Shared.Next(0, 8) == 0) dir = -1;
                if (bpm <= 60 || Random.Shared.Next(0, 8) == 0) dir = 1;
                http.UpdateBpm("SIM", bpm, true, "Simulator");
                await ws.BroadcastAsync("SIM", new HeartRateReading { Bpm = bpm, Connected = true, DeviceId = "SIM", DeviceName = "Simulator" }.ToJson());
                try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
            }
            return 0;
        }

        if (!string.IsNullOrEmpty(targetDevice))
        {
            await ble.StartScanningAsync();
            try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { return 0; }
            await ble.ConnectAsync(targetDevice, targetDevice);
        }

        Console.WriteLine("Running — press Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
        return 0;
    }
}
