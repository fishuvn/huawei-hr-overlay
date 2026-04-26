using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace HuaweiHROverlay.Core;

/// <summary>
/// Lightweight WebSocket broadcast server.
/// All connected clients (OBS browser sources) receive every BPM update as JSON.
/// Runs on ws://localhost:8765/
/// </summary>
public class WebSocketServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<WebSocket> _clients = [];
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
    /// Broadcast a JSON message to all connected WebSocket clients.
    /// </summary>
    public async Task BroadcastAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        await _lock.WaitAsync();
        try
        {
            var dead = new List<WebSocket>();
            foreach (var ws in _clients)
            {
                if (ws.State == WebSocketState.Open)
                {
                    try { await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None); }
                    catch { dead.Add(ws); }
                }
                else
                {
                    dead.Add(ws);
                }
            }
            foreach (var d in dead) _clients.Remove(d);
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

        await _lock.WaitAsync(ct);
        _clients.Add(ws);
        ClientCount = _clients.Count;
        _lock.Release();
        ClientCountChanged?.Invoke(this, ClientCount);

        // Keep alive — read and discard any incoming frames
        var buf = new byte[256];
        try
        {
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
        catch { /* client disconnected */ }
        finally
        {
            await _lock.WaitAsync(CancellationToken.None);
            _clients.Remove(ws);
            ClientCount = _clients.Count;
            _lock.Release();
            ClientCountChanged?.Invoke(this, ClientCount);
            ws.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_acceptLoop != null)
        {
            try { await _acceptLoop; } catch { }
        }
        _listener.Stop();
        await _lock.WaitAsync();
        foreach (var ws in _clients)
        {
            try { ws.Dispose(); } catch { }
        }
        _clients.Clear();
        _lock.Release();
    }
}
