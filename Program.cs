using YuhuAdminBle;

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
    admin.Authenticated += () => Console.WriteLine("\n>>> ADMIN AUTHENTICATED  (type 'logs' or 'live')\n");
    admin.Failed_ += r => Console.WriteLine($"[admin] handshake failed: {r}");
    admin.LogBatch += lines =>
    {
        Console.WriteLine($"--- LOG ({lines.Count} lines) ---");
        foreach (var l in lines) Console.WriteLine("  " + l);
    };
    admin.LiveLine += l => Console.WriteLine("[live] " + l);
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
Console.WriteLine("  <number>      connect to device by index");
Console.WriteLine("  r             rescan list");
Console.WriteLine("  auth          run admin handshake on the connected peer");
Console.WriteLine("  logs [tag]    request a log tail (optionally filter by tag, e.g. MeshFlow)");
Console.WriteLine("  live / stop   start / stop live log streaming");
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
    if (line.StartsWith("logs"))
    {
        if (admin?.Current != AdminClient.State.Authed) { Console.WriteLine("[admin] not authenticated (run auth)"); continue; }
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var tag = parts.Length > 1 ? parts[1] : null;
        admin.RequestLogTail(tag, 200);
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
