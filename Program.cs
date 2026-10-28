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

await transport.StartScanAsync();
Console.WriteLine("Scanning… commands: <number> connect | r rescan list | q quit\n");

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
    if (int.TryParse(line, out var idx) && idx >= 0 && idx < discovered.Count)
    {
        Console.WriteLine($"[connect] attempting {discovered[idx].AddressString}…");
        var ok = await transport.ConnectAsync(discovered[idx]);
        if (!ok) Console.WriteLine("[connect] failed");
        continue;
    }
    Console.WriteLine("?? unknown command");
}

Console.WriteLine("bye");
