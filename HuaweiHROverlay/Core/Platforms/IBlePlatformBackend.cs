namespace HuaweiHROverlay.Core.Platforms;

public sealed record BleAdvertisementPacket(
    string DeviceId,
    string? LocalName,
    int Rssi,
    bool HasHrServiceUuid,
    string? DisplayAddress = null);

public interface IBleGattSession : IAsyncDisposable
{
    string DeviceId { get; }
    string DeviceName { get; }
    event EventHandler<int>? BpmReceived;
    event EventHandler? DisconnectedUnexpectedly;
}

public interface IBlePlatformBackend : IAsyncDisposable
{
    string PlatformName { get; }
    bool IsScanning { get; }

    event EventHandler<BleAdvertisementPacket>? AdvertisementReceived;
    event EventHandler<string>? StatusMessage;
    event EventHandler? ScanStoppedUnexpectedly;

    Task StartScanningAsync();
    Task StopScanningAsync();
    Task<IBleGattSession?> ConnectAsync(string deviceId, string deviceName, CancellationToken ct = default);
}
