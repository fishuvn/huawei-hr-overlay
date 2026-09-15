using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace HuaweiHROverlay.Core;

/// <summary>
/// Lightweight WebSocket broadcast server.
/// Clients connect to ws://localhost:8765/?device=&lt;DEVICE_ID&gt; to receive only that
/// device's BPM updates; clients without a device parameter receive every update.
/// </summary>
public class WebSocketServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<(WebSocket ws, string? deviceId)> _clients = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public const int Port = 8765;
    public string Uri => $"ws://localhost:{Port}/";
    public int ClientCount { get; private set; }

    public event EventHandler<int>? ClientCountChanged;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Broadcast a JSON message to all clients subscribed to deviceId
    /// (null-subscribed clients receive everything).
    /// </summary>
    public async Task BroadcastAsync(string deviceId, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        await _lock.WaitAsync();
        try
        {
            var dead = new List<WebSocket>();
            foreach (var (ws, sub) in _clients)
            {
                if (ws.State != WebSocketState.Open) { dead.Add(ws); continue; }
                if (sub != null && !string.Equals(sub, deviceId, StringComparison.OrdinalIgnoreCase)) continue;
                try { await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { dead.Add(ws); }
            }
            foreach (var d in dead) _clients.RemoveAll(c => c.ws == d);
            ClientCount = _clients.Count;
        }
        finally
        {
            _lock.Release();
        }
        ClientCountChanged?.Invoke(this, ClientCount);
    }

    // ──────────────────────────────────────────────
    // Private
    // ──────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            if (ctx.Request.IsWebSocketRequest)
            {
                _ = HandleClientAsync(ctx, ct);
            }
            else
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
            }
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
        catch { return; }

        var ws = wsCtx.WebSocket;

        // ?device=<ID> subscribes the client to a single device
        string? deviceId = null;
        var query = ctx.Request.Url?.Query;
        if (!string.IsNullOrEmpty(query))
        {
            var m = Regex.Match(query, @"(?:^|[?&])device=([^&]+)", RegexOptions.IgnoreCase);
            if (m.Success) deviceId = System.Uri.UnescapeDataString(m.Groups[1].Value).ToUpperInvariant();
        }

        try
        {
            await _lock.WaitAsync(ct);
            _clients.Add((ws, deviceId));
            ClientCount = _clients.Count;
            _lock.Release();
            ClientCountChanged?.Invoke(this, ClientCount);

            // Keep alive — read and discard any incoming frames
            var buf = new byte[256];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                    break;
                }
            }
        }
        catch { /* client disconnected or app shutting down */ }
        finally
        {
            try
            {
                await _lock.WaitAsync(CancellationToken.None);
                _clients.RemoveAll(c => c.ws == ws);
                ClientCount = _clients.Count;
                _lock.Release();
                ClientCountChanged?.Invoke(this, ClientCount);
            }
            catch { /* lock unavailable during shutdown */ }
            ws.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        // Stop first: a pending GetContextAsync only unblocks when the listener stops.
        try { _listener.Stop(); } catch { /* never started or already stopped */ }
        if (_acceptLoop != null)
        {
            try { await _acceptLoop; } catch { }
        }
        await _lock.WaitAsync();
        foreach (var (ws, _) in _clients)
        {
            try { ws.Dispose(); } catch { }
        }
        _clients.Clear();
        _lock.Release();
    }
}
