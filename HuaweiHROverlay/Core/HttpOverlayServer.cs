using System.Net;
using System.Reflection;
using System.Text;

namespace HuaweiHROverlay.Core;

/// <summary>
/// Simple HTTP server that serves the OBS overlay HTML page.
/// The overlay.html is embedded as a resource in the assembly.
///
/// Endpoints:
///   GET /          → overlay.html (OBS Browser Source URL)
///   GET /status    → {"bpm": N, "connected": true/false}  (health check)
/// </summary>
public class HttpOverlayServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _serveLoop;
    private string _cachedHtml = string.Empty;
    private int _currentBpm;
    private bool _isConnected;

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

    public void UpdateBpm(int bpm, bool connected)
    {
        _currentBpm = bpm;
        _isConnected = connected;
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
                var json = $"{{\"bpm\":{_currentBpm},\"connected\":{_isConnected.ToString().ToLower()}}}";
                WriteResponse(ctx, 200, "application/json", json);
            }
            else
            {
                // Serve overlay for any other path
                WriteResponse(ctx, 200, "text/html; charset=utf-8", _cachedHtml);
            }
        }
        catch { /* ignore */ }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
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
        if (_serveLoop != null)
        {
            try { await _serveLoop; } catch { }
        }
        _listener.Stop();
    }
}
