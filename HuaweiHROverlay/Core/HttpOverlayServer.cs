using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace HuaweiHROverlay.Core;

/// <summary>
/// Simple HTTP server that serves the OBS overlay HTML page and telemetry status JSON.
///
/// Endpoints:
///   GET /                → overlay.html showing the most recent active device
///   GET /overlay/&lt;ID&gt;   → overlay.html locked to one device
///   GET /status          → {"bpm":N,"connected":bool,"device":"ID","name":"...","min":N,"max":N,"version":"..."}
///   GET /status?device=ID→ same, per device
/// </summary>
public class HttpOverlayServer : IAsyncDisposable
{
    private sealed record DeviceSnapshot(int Bpm, bool Connected, string Name, int MinBpm, int MaxBpm, DateTime UpdatedAtUtc);

    private readonly HttpListener _listener = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<string, DeviceSnapshot> _devices = [];
    private CancellationTokenSource? _cts;
    private Task? _serveLoop;
    private string _cachedHtml = string.Empty;
    private readonly string _version = GitHubUpdateService.GetCurrentVersionString();

    public const int Port = 8764;
    public string OverlayUrl => $"http://localhost:{Port}/";

    public void Start()
    {
        _cachedHtml = LoadEmbeddedOverlay();
        _cts = new CancellationTokenSource();
        _listener.Prefixes.Add(OverlayUrl);
        _listener.Start();
        _serveLoop = ServeLoopAsync(_cts.Token);
    }

    public void UpdateBpm(string deviceId, int bpm, bool connected, string? deviceName = null)
    {
        string key = deviceId.ToUpperInvariant();
        lock (_stateLock)
        {
            bool existed = _devices.TryGetValue(key, out var prev);
            string resolvedName = !string.IsNullOrWhiteSpace(deviceName)
                ? deviceName
                : (existed ? prev!.Name : key);

            int min = existed ? prev!.MinBpm : 0;
            int max = existed ? prev!.MaxBpm : 0;
            if (bpm > 0)
            {
                min = min == 0 ? bpm : Math.Min(min, bpm);
                max = Math.Max(max, bpm);
            }

            _devices[key] = new DeviceSnapshot(bpm, connected, resolvedName, min, max, DateTime.UtcNow);
        }
    }

    private async Task ServeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            _ = Task.Run(() => HandleRequest(ctx), ct);
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            ctx.Response.Headers.Add("Cache-Control", "no-store");

            if (path == "/status")
            {
                WriteResponse(ctx, 200, "application/json; charset=utf-8", StatusJson(GetQueryValue(ctx, "device")));
            }
            else
            {
                WriteResponse(ctx, 200, "text/html; charset=utf-8", _cachedHtml);
            }
        }
        catch { /* ignore */ }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private string StatusJson(string? deviceId)
    {
        lock (_stateLock)
        {
            if (deviceId != null)
            {
                if (_devices.TryGetValue(deviceId, out var d))
                {
                    return BuildJson(d.Bpm, d.Connected, deviceId, d.Name, d.MinBpm, d.MaxBpm);
                }
                return BuildJson(0, false, deviceId, "", 0, 0);
            }

            // Pick the most recently updated connected device first
            var active = _devices
                .Where(kv => kv.Value.Connected && kv.Value.Bpm > 0)
                .OrderByDescending(kv => kv.Value.UpdatedAtUtc)
                .FirstOrDefault();

            if (active.Key != null)
            {
                var d = active.Value;
                return BuildJson(d.Bpm, true, active.Key, d.Name, d.MinBpm, d.MaxBpm);
            }

            bool anyConnected = _devices.Values.Any(v => v.Connected);
            return BuildJson(0, anyConnected, "", "", 0, 0);
        }
    }

    private string BuildJson(int bpm, bool connected, string id, string name, int min, int max) =>
        $"{{\"bpm\":{bpm},\"connected\":{connected.ToString().ToLowerInvariant()},\"device\":\"{Escape(id)}\",\"name\":\"{Escape(name)}\",\"min\":{min},\"max\":{max},\"version\":\"{Escape(_version)}\"}}";

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? GetQueryValue(HttpListenerContext ctx, string key)
    {
        var query = ctx.Request.Url?.Query;
        if (string.IsNullOrEmpty(query)) return null;
        var m = Regex.Match(query, $@"(?:^|[?&]){key}=([^&]+)", RegexOptions.IgnoreCase);
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value).ToUpperInvariant() : null;
    }

    private static void WriteResponse(HttpListenerContext ctx, int statusCode, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static string LoadEmbeddedOverlay()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("overlay.html", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            return "<html><body><h1>overlay.html not found in resources</h1></body></html>";

        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        if (_serveLoop != null)
        {
            try { await _serveLoop; } catch { }
        }
    }
}
