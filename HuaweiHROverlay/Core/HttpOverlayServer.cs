using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace HuaweiHROverlay.Core;

/// <summary>
/// Simple HTTP server that serves the OBS overlay HTML page.
/// The overlay.html is embedded as a resource in the assembly.
///
/// Endpoints:
///   GET /                → overlay.html showing the most recent device
///   GET /overlay/&lt;ID&gt;   → overlay.html locked to one device (12-hex Bluetooth address)
///   GET /status          → {"bpm":N,"connected":bool,"device":"ID"} latest overall
///   GET /status?device=ID→ same, per device
/// </summary>
public class HttpOverlayServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<string, (int bpm, bool connected)> _devices = [];
    private CancellationTokenSource? _cts;
    private Task? _serveLoop;
    private string _cachedHtml = string.Empty;

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

    public void UpdateBpm(string deviceId, int bpm, bool connected)
    {
        lock (_stateLock)
        {
            _devices[deviceId.ToUpperInvariant()] = (bpm, connected);
        }
    }

    // ──────────────────────────────────────────────
    // Private
    // ──────────────────────────────────────────────

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

            if (path == "/status")
            {
                WriteResponse(ctx, 200, "application/json", StatusJson(GetQueryValue(ctx, "device")));
            }
            else
            {
                // Serve overlay for any other path — /overlay/<ID> is picked up by the page itself
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
                var found = _devices.TryGetValue(deviceId, out var d);
                return $"{{\"bpm\":{(found ? d.bpm : 0)},\"connected\":{(found && d.connected).ToString().ToLower()},\"device\":\"{deviceId}\"}}";
            }

            bool connected = _devices.Count > 0;
            int bpm = 0;
            string device = "";
            foreach (var (id, state) in _devices)
            {
                if (state.bpm > 0) { bpm = state.bpm; device = id; }
            }
            return $"{{\"bpm\":{bpm},\"connected\":{connected.ToString().ToLower()},\"device\":\"{device}\"}}";
        }
    }

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
        // Resource name is namespace.folder.filename
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
        // Stop first: a pending GetContextAsync only unblocks when the listener stops.
        try { _listener.Stop(); } catch { /* never started or already stopped */ }
        if (_serveLoop != null)
        {
            try { await _serveLoop; } catch { }
        }
    }
}
