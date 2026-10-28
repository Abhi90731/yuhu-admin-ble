using YuhuAdminBle;

// Two modes:
//   (default)   interactive console spike/driver
//   --bridge    headless sidecar: exposes the BLE admin over a local WebSocket
//               for the dashboard UI (no console typing).

if (args.Contains("--bridge"))
{
    Console.WriteLine("YUHU Admin BLE — dashboard bridge (sidecar) mode");
    AdminIdentity? bridgeIdentity = null;
    try { bridgeIdentity = AdminIdentity.Load(Path.Combine(AppContext.BaseDirectory, "admin-identity")); Console.WriteLine("[admin] identity loaded; cert present."); }
    catch (FileNotFoundException ex) { Console.WriteLine("[admin] " + ex.Message); }

    await using var bridgeTransport = new WinRtCentralTransport();
    var bridge = new DashboardBridge(bridgeTransport, bridgeIdentity, port: 4318);
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await bridge.RunAsync(cts.Token);
    return;
}

// A.3.0 spike driver.
//
// Scans for YUHU devices (frozen Service UUID), lists them with RSSI/name, then
// lets you connect to one by index. On connect it resolves the GATT
// characteristics, subscribes to TX, reads identity, and runs keepalive.
//
// This proves Windows ↔ phone BLE connectivity BEFORE we add admin auth and the
// log protocol. No auth, no Noise, no dashboard dependency.

Console.WriteLine("YUHU Admin BLE — A.3.0 central spike");
Console.WriteLine("=====================================\n");

await using var transport = new WinRtCentralTransport();

var discovered = new List<BlePeer>();
var seenAddrs = new HashSet<ulong>();

transport.PeerDiscovered += peer =>
{
    if (seenAddrs.Add(peer.BluetoothAddress))
    {
        discovered.Add(peer);
        Console.WriteLine(
            $"  [{discovered.Count - 1}] {peer.AdvertisedName ?? "(no name)"} " +
            $"id={peer.LogicalId ?? "?"} addr={peer.AddressString} rssi={peer.Rssi}");
    }
};

transport.PeerConnected += peer =>
    Console.WriteLine($"\n>>> CONNECTED: {peer.AdvertisedName ?? peer.AddressString}\n");

transport.PeerDisconnected += peer =>
    Console.WriteLine($"\n<<< DISCONNECTED: {peer.AddressString}\n");

transport.MessageReceived += (peer, data) =>
    Console.WriteLine($"[msg] {peer.AddressString} ({data.Length} bytes): " +
                      System.Text.Encoding.UTF8.GetString(data));

// ---- A.3 admin wiring ----
// Load the admin identity (DPAPI key + root-signed cert) from ./admin-identity.
AdminIdentity? identity = null;
try
{
    identity = AdminIdentity.Load(Path.Combine(AppContext.BaseDirectory, "admin-identity"));
    Console.WriteLine("[admin] identity loaded; cert present.");
}
catch (FileNotFoundException ex)
{
    Console.WriteLine("[admin] " + ex.Message);
    Console.WriteLine("[admin] admin commands disabled until a cert is placed.");
}

AdminClient? admin = null;
BlePeer? adminPeer = null;

transport.AdminReceived += (peer, json) => admin?.OnEnvelope(json);

// Build an AdminClient for a connected peer and start the handshake.
// Returns false if prerequisites (identity/cert, phone id) are missing.
bool StartAuth(BlePeer peer)
{
    if (identity == null) { Console.WriteLine("[admin] no identity/cert loaded"); return false; }
    var phoneId = peer.LogicalId;
    if (string.IsNullOrEmpty(phoneId)) { Console.WriteLine("[admin] unknown phone logical id; cannot bind proof"); return false; }

    admin = new AdminClient(identity, phoneId, json => _ = transport.SendAdminAsync(peer, json));
    admin.Authenticated += () => Console.WriteLine("\n>>> ADMIN AUTHENTICATED  (state | detail [min] | exc | live | sys)\n");
    admin.Failed_ += r => Console.WriteLine($"[admin] handshake failed: {r}");
    admin.LogBatch += lines =>
    {
        Console.WriteLine($"--- LOG ({lines.Count} lines) ---");
        foreach (var l in lines) Console.WriteLine("  " + l);
    };
    admin.LiveLine += l => Console.WriteLine("[live] " + l);
    admin.SystemLine += l => Console.WriteLine("[sys] " + l);
    admin.Start();
    Console.WriteLine("[admin] ADMIN_HELLO sent (auto)…");
    return true;
}

// AUTO-AUTH: the keepalive window is only ~6s — too tight to type `auth` by
// hand. So kick off the handshake automatically the moment a peer connects
// (its identity, hence logical id, is already resolved by then). The handshake
// traffic itself keeps the link alive.
transport.PeerConnected += peer =>
{
    adminPeer = peer;
    if (identity != null && !string.IsNullOrEmpty(peer.LogicalId))
        StartAuth(peer);
};

await transport.StartScanAsync();
Console.WriteLine("Scanning… commands:");
Console.WriteLine("  <number>      connect to device by index (auto-authenticates)");
Console.WriteLine("  r             rescan list");
Console.WriteLine("  state         pull STATE logs (major issues / failures)");
Console.WriteLine("  detail [min]  pull DETAIL logs for last min minutes (1/5/20, default 5)");
Console.WriteLine("  exc           pull EXCEPTIONS (crashes + stack traces)");
Console.WriteLine("  live / stop   start / stop continuous DETAIL streaming");
Console.WriteLine("  sys / sysstop start / stop full-logcat SYSTEM streaming (with timestamps)");
Console.WriteLine("  q             quit\n");

while (true)
{
    var line = Console.ReadLine()?.Trim();
    if (line == null) continue;
    if (line is "q" or "quit") break;
    if (line is "r")
    {
        Console.WriteLine($"-- {discovered.Count} device(s) seen --");
        for (int i = 0; i < discovered.Count; i++)
        {
            var p = discovered[i];
            Console.WriteLine($"  [{i}] {p.AdvertisedName ?? "(no name)"} id={p.LogicalId ?? "?"} " +
                              $"addr={p.AddressString} rssi={p.Rssi}");
        }
        continue;
    }
    if (line.StartsWith("auth"))
    {
        if (adminPeer == null) { Console.WriteLine("[admin] connect to a peer first"); continue; }
        StartAuth(adminPeer);
        continue;
    }
    if (line is "state")
    {
        if (admin?.Current != AdminClient.State.Authed) { Console.WriteLine("[admin] not authenticated"); continue; }
        admin.RequestState();
        continue;
    }
    if (line.StartsWith("detail"))
    {
        if (admin?.Current != AdminClient.State.Authed) { Console.WriteLine("[admin] not authenticated"); continue; }
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        int win = (parts.Length > 1 && int.TryParse(parts[1], out var m)) ? m : 5;
        admin.RequestDetail(win);
        continue;
    }
    if (line is "live")
    {
        if (admin?.Current != AdminClient.State.Authed) { Console.WriteLine("[admin] not authenticated"); continue; }
        admin.SetLiveLogs(true);
        Console.WriteLine("[admin] live streaming ON");
        continue;
    }
    if (line is "stop")
    {
        admin?.SetLiveLogs(false);
        Console.WriteLine("[admin] live streaming OFF");
        continue;
    }
    if (line is "exc")
    {
        if (admin?.Current != AdminClient.State.Authed) { Console.WriteLine("[admin] not authenticated"); continue; }
        admin.RequestExceptions();
        continue;
    }
    if (line is "sys")
    {
        if (admin?.Current != AdminClient.State.Authed) { Console.WriteLine("[admin] not authenticated"); continue; }
        admin.SetSystemLogs(true);
        Console.WriteLine("[admin] SYSTEM (full logcat) streaming ON");
        continue;
    }
    if (line is "sysstop")
    {
        admin?.SetSystemLogs(false);
        Console.WriteLine("[admin] SYSTEM streaming OFF");
        continue;
    }
    if (int.TryParse(line, out var idx) && idx >= 0 && idx < discovered.Count)
    {
        Console.WriteLine($"[connect] attempting {discovered[idx].AddressString}…");
        var ok = await transport.ConnectAsync(discovered[idx]);
        if (ok) adminPeer = discovered[idx];
        else Console.WriteLine("[connect] failed");
        continue;
    }
    Console.WriteLine("?? unknown command");
}

Console.WriteLine("bye");
