namespace HuaweiHROverlay.Core;

/// <summary>
/// Represents a single heart rate reading from the BLE device.
/// </summary>
public class HeartRateReading
{
    public int Bpm { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string DeviceName { get; set; } = string.Empty;

    public string ToJson() =>
        $"{{\"bpm\":{Bpm},\"timestamp\":\"{Timestamp:O}\",\"device\":\"{EscapeJson(DeviceName)}\"}}";

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
