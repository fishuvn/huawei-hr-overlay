namespace HuaweiHROverlay.Core;

/// <summary>
/// Represents a single heart rate reading (or connection state event) from a BLE device.
/// </summary>
public class HeartRateReading
{
    public int Bpm { get; set; }
    public bool Connected { get; set; } = true;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;

    public string ToJson() =>
        $"{{\"bpm\":{Bpm},\"connected\":{Connected.ToString().ToLowerInvariant()},\"timestamp\":\"{Timestamp:O}\",\"id\":\"{EscapeJson(DeviceId)}\",\"device\":\"{EscapeJson(DeviceName)}\"}}";

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>
/// Parses standard Bluetooth SIG GATT Heart Rate Measurement (0x2A37) packets.
/// Shared identically across Windows WinRT, Linux BlueZ, and macOS CoreBluetooth.
/// </summary>
public static class BleHrPacketParser
{
    public static readonly Guid HeartRateServiceUuid = new("0000180d-0000-1000-8000-00805f9b34fb");
    public static readonly Guid HeartRateMeasurementUuid = new("00002a37-0000-1000-8000-00805f9b34fb");

    public static bool TryParseBpm(ReadOnlySpan<byte> payload, out int bpm)
    {
        bpm = 0;
        if (payload.Length < 2) return false;

        byte flags = payload[0];
        if ((flags & 0x01) == 0)
        {
            bpm = payload[1];
        }
        else
        {
            if (payload.Length < 3) return false;
            bpm = payload[1] | (payload[2] << 8);
        }

        return bpm is > 0 and <= 300;
    }
}
