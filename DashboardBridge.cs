using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace YuhuAdminBle;

/// <summary>
/// Local WebSocket bridge (A.3 Step 7): exposes the BLE admin engine to the
/// dashboard UI so all actions are buttons, not console typing.
///
/// The C# app runs headless as a "sidecar"; the Node dashboard connects to
/// ws://127.0.0.1:{port}/ and exchanges JSON:
///
///   dashboard → sidecar (commands):
///     {"cmd":"scan"}                      refresh discovered list
///     {"cmd":"connect","addr":"<hex>"}    connect + auto-auth
///     {"cmd":"state"} {"cmd":"detail","window":5} {"cmd":"exc"}
///     {"cmd":"live","on":true} {"cmd":"system","on":true}
///
///   sidecar → dashboard (events):
///     {"ev":"peers","peers":[{addr,name,id,rssi}]}
///     {"ev":"connected","name":..,"id":..}
///     {"ev":"auth"}  {"ev":"disconnected"}
///     {"ev":"log","tier":..,"lines":[..]}
///     {"ev":"live","line":..}  {"ev":"sys","line":..}
///     {"ev":"status","text":..}
///
/// Binds to loopback only (127.0.0.1) — not exposed off the machine.
/// </summary>
public sealed class DashboardBridge
{
    private readonly IBleTransport _transport;
    private readonly AdminIdentity? _identity;
    private readonly int _port;
    private readonly ConcurrentDictionary<ulong, BlePeer> _peers = new();
    private readonly List<WebSocket> _clients = new();
    private readonly object _clientsLock = new();

    private AdminClient? _admin;
    private BlePeer? _adminPeer;

    public DashboardBridge(IBleTransport transport, AdminIdentity? identity, int port = 4318)
    {
        _transport = transport;
        _identity = identity;
        _port = port;

        _transport.PeerDiscovered += p => { _peers[p.BluetoothAddress] = p; BroadcastPeers(); };
        _transport.PeerConnected += OnPeerConnected;
        _transport.PeerDisconnected += _ => Broadcast(new { ev = "disconnected" });
        _transport.AdminReceived += (_, json) => _admin?.OnEnvelope(json);

        // Periodically re-broadcast so stale (rotated-away) addresses expire from
        // the UI even when no new discovery events arrive.
        _peerRefresh = new Timer(_ => BroadcastPeers(), null, 5000, 5000);
    }

    private readonly Timer _peerRefresh;

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        listener.Start();
        Console.WriteLine($"[bridge] dashboard WebSocket on ws://127.0.0.1:{_port}/");
        Console.WriteLine("[bridge] press Ctrl+C to stop.");
        await _transport.StartScanAsync();

        // Stopping the listener unblocks the pending GetContextAsync so Ctrl+C
        // (which cancels ct) actually shuts us down.
        using var reg = ct.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // listener stopped (shutdown) or error

            if (ctx.Request.IsWebSocketRequest)
                _ = HandleClientAsync(ctx);
            else { ctx.Response.StatusCode = 400; ctx.Response.Close(); }
        }
        try { listener.Close(); } catch { }
        _peerRefresh.Dispose();
        Console.WriteLine("[bridge] stopped.");
    }

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;
        lock (_clientsLock) _clients.Add(ws);
        BroadcastPeers();

        var buf = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) break;
                var msg = Encoding.UTF8.GetString(buf, 0, res.Count);
                HandleCommand(msg);
            }
        }
        catch { /* client gone */ }
        finally
        {
            lock (_clientsLock) _clients.Remove(ws);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    private void HandleCommand(string json)
    {
        JsonElement o;
        try { o = JsonSerializer.Deserialize<JsonElement>(json); } catch { return; }
        if (!o.TryGetProperty("cmd", out var cmdEl)) return;
        var cmd = cmdEl.GetString();

        switch (cmd)
        {
            case "scan":
                BroadcastPeers();
                break;
            case "connect":
                _ = ConnectAsync(o.GetProperty("addr").GetString() ?? "");
                break;
            case "state":
                _admin?.RequestState();
                break;
            case "detail":
                _admin?.RequestDetail(o.TryGetProperty("window", out var w) ? w.GetInt32() : 5);
                break;
            case "exc":
                _admin?.RequestExceptions();
                break;
            case "live":
                _admin?.SetLiveLogs(o.TryGetProperty("on", out var l) && l.GetBoolean());
                break;
            case "system":
                _admin?.SetSystemLogs(o.TryGetProperty("on", out var s) && s.GetBoolean());
                break;
        }
    }

    private async Task ConnectAsync(string addrHex)
    {
        var peer = _peers.Values.FirstOrDefault(p => p.AddressString == addrHex);
        if (peer == null) { Broadcast(new { ev = "status", text = "peer not in list; rescan" }); return; }
        Broadcast(new { ev = "status", text = $"connecting {addrHex}…" });
        var ok = await _transport.ConnectAsync(peer);
        if (!ok) Broadcast(new { ev = "status", text = "connect failed (retry)" });
    }

    private void OnPeerConnected(BlePeer peer)
    {
        _adminPeer = peer;
        Broadcast(new { ev = "connected", name = peer.AdvertisedName, id = peer.LogicalId });

        if (_identity == null || string.IsNullOrEmpty(peer.LogicalId))
        {
            Broadcast(new { ev = "status", text = "cannot auth (no identity/cert or no phone id)" });
            return;
        }

        _admin = new AdminClient(_identity, peer.LogicalId!, j => _ = _transport.SendAdminAsync(peer, j));
        _admin.Authenticated += () =>
        {
            Broadcast(new { ev = "auth" });
            // Auto-pull on connect so the UI shows content immediately instead of
            // a blank panel: recent state (failures) + the last 5 min of detail.
            // Staggered slightly so the two chunked requests don't interleave.
            _ = Task.Run(async () =>
            {
                try { _admin!.RequestState(); await Task.Delay(400); _admin!.RequestDetail(5); }
                catch { /* link may have dropped */ }
            });
        };
        _admin.Failed_ += r => Broadcast(new { ev = "status", text = $"auth failed: {r}" });
        _admin.LogBatch += lines => Broadcast(new { ev = "log", tier = "batch", lines });
        _admin.LiveLine += line => Broadcast(new { ev = "live", line });
        _admin.SystemLine += line => Broadcast(new { ev = "sys", line });
        _admin.Start();
        Broadcast(new { ev = "status", text = "authenticating…" });
    }

    private void BroadcastPeers()
    {
        var now = DateTimeOffset.UtcNow;
        const int staleMs = 12000; // drop devices not seen recently (addresses rotate)

        // Expire stale entries so rotated-away addresses don't pile up.
        foreach (var kv in _peers)
        {
            if ((now - kv.Value.LastSeen).TotalMilliseconds > staleMs)
                _peers.TryRemove(kv.Key, out _);
        }

        // Dedupe: prefer the stable logical id when known; else fall back to the
        // (rotating) address. A phone that has revealed its id collapses to one
        // entry even across address rotations.
        var deduped = new Dictionary<string, BlePeer>();
        foreach (var p in _peers.Values)
        {
            var key = !string.IsNullOrEmpty(p.LogicalId) ? "id:" + p.LogicalId : "addr:" + p.AddressString;
            if (!deduped.TryGetValue(key, out var existing) || p.LastSeen > existing.LastSeen)
                deduped[key] = p;
        }

        var peers = deduped.Values
            .OrderByDescending(p => p.Rssi)
            .Select(p => new { addr = p.AddressString, name = p.AdvertisedName, id = p.LogicalId, rssi = (int)p.Rssi })
            .ToList();
        Broadcast(new { ev = "peers", peers });
    }

    private void Broadcast(object payload)
    {
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        List<WebSocket> clients;
        lock (_clientsLock) clients = _clients.Where(c => c.State == WebSocketState.Open).ToList();
        foreach (var c in clients)
        {
            try { _ = c.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { /* drop */ }
        }
    }
}
