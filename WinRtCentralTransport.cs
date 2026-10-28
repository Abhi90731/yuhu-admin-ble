using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace YuhuAdminBle;

/// <summary>
/// CENTRAL-ONLY WinRT BLE transport (A.3.0 spike).
///
/// Scans for the frozen YUHU Service UUID, connects outward to a peer,
/// resolves the TX/RX/Identity/Capability characteristics, subscribes to TX
/// notifications, reassembles inbound frames, and runs the keepalive PING.
///
/// It does NOT advertise (no peripheral role) — that's a future extension
/// behind this same IBleTransport interface. It performs NO admin auth and NO
/// Noise handshake; this spike only proves Windows ↔ phone BLE connectivity.
/// </summary>
public sealed class WinRtCentralTransport : IBleTransport
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new();
    private readonly Dictionary<ulong, BlePeer> _seen = new();
    private readonly Dictionary<ulong, Connection> _connections = new();
    private readonly object _lock = new();

    public event Action<BlePeer>? PeerDiscovered;
    public event Action<BlePeer>? PeerConnected;
    public event Action<BlePeer>? PeerDisconnected;
    public event Action<BlePeer, byte[]>? MessageReceived;
    public event Action<BlePeer, string>? AdminReceived;

    private sealed class Connection
    {
        public required BlePeer Peer;
        public required BluetoothLEDevice Device;
        public GattSession? GattSession;
        public GattCharacteristic? Tx;
        public GattCharacteristic? Rx;
        public GattCharacteristic? Identity;
        public GattCharacteristic? Capability;
        public readonly BleFraming.Reassembler Reassembler = new();
        public DateTimeOffset LastInbound = DateTimeOffset.UtcNow;
        public Timer? Keepalive;
        public ushort NextMsgId;
    }

    public Task StartScanAsync()
    {
        _watcher.ScanningMode = BluetoothLEScanningMode.Active; // active -> get scan response (name)
        // Match only devices advertising the frozen Service UUID (primary PDU).
        _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(Protocol.ServiceUuid);
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += (_, args) =>
            Console.WriteLine($"[scan] stopped: {args.Error}");
        _watcher.Start();
        Console.WriteLine($"[scan] watching for Service UUID {Protocol.ServiceUuid}");
        return Task.CompletedTask;
    }

    public void StopScan() => _watcher.Stop();

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        BlePeer peer;
        lock (_lock)
        {
            if (!_seen.TryGetValue(args.BluetoothAddress, out peer!))
            {
                peer = new BlePeer { BluetoothAddress = args.BluetoothAddress };
                _seen[args.BluetoothAddress] = peer;
            }
        }
        peer.Rssi = args.RawSignalStrengthInDBm;
        peer.LastSeen = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(args.Advertisement.LocalName))
            peer.AdvertisedName = args.Advertisement.LocalName;

        // Manufacturer data (company 0xFFFF) carries 8-byte logical id + name (§3.2.1).
        foreach (var md in args.Advertisement.ManufacturerData)
        {
            if (md.CompanyId == 0xFFFF)
            {
                var bytes = ReadBuffer(md.Data);
                if (bytes.Length >= 8)
                    peer.LogicalId = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
                if (bytes.Length > 8)
                    peer.AdvertisedName = System.Text.Encoding.UTF8.GetString(bytes, 8, bytes.Length - 8);
            }
        }

        PeerDiscovered?.Invoke(peer);
    }

    public async Task<bool> ConnectAsync(BlePeer peer)
    {
        // Clean up any prior connection object for this address so we don't reuse
        // a stale BluetoothLEDevice / GATT cache (root cause of "missing TX/RX"
        // on reconnect).
        lock (_lock)
        {
            if (_connections.TryGetValue(peer.BluetoothAddress, out var old))
            {
                _connections.Remove(peer.BluetoothAddress);
                old.Keepalive?.Dispose();
                try { old.Device.Dispose(); } catch { }
            }
        }
        // Give Windows a moment to release the old handles.
        await Task.Delay(200);

        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(peer.BluetoothAddress);
        if (device == null)
        {
            Console.WriteLine($"[connect] could not open device {peer.AddressString}");
            return false;
        }

        // Hold the GATT session open with MaintainConnection so the link is
        // actually established before we enumerate services/characteristics.
        GattSession? gattSession = null;
        try
        {
            gattSession = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            gattSession.MaintainConnection = true;
        }
        catch { /* older stacks: proceed without explicit session */ }

        // Resolve the service + TX/RX with retries, UNCACHED, allowing the GATT
        // connection time to come up.
        GattDeviceService? service = null;
        GattCharacteristic? tx = null, rx = null, identity = null, capability = null;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var servicesResult = await device.GetGattServicesForUuidAsync(
                Protocol.ServiceUuid, BluetoothCacheMode.Uncached);
            if (servicesResult.Status == GattCommunicationStatus.Success &&
                servicesResult.Services.Count > 0)
            {
                service = servicesResult.Services[0];
                tx = await ResolveChar(service, Protocol.TxCharUuid);
                rx = await ResolveChar(service, Protocol.RxCharUuid);
                identity = await ResolveChar(service, Protocol.IdentityCharUuid);
                capability = await ResolveChar(service, Protocol.CapabilityCharUuid);
                if (tx != null && rx != null) break; // success
            }
            Console.WriteLine($"[connect] attempt {attempt + 1}: service/char not ready, retrying…");
            await Task.Delay(500);
        }

        if (service == null || tx == null || rx == null)
        {
            Console.WriteLine("[connect] missing TX/RX characteristic after retries — aborting");
            gattSession?.Dispose();
            device.Dispose();
            return false;
        }

        var conn = new Connection { Peer = peer, Device = device };
        conn.Tx = tx; conn.Rx = rx; conn.Identity = identity; conn.Capability = capability;
        conn.GattSession = gattSession;

        if (conn.Tx == null || conn.Rx == null)
        {
            Console.WriteLine("[connect] missing TX/RX characteristic — aborting");
            device.Dispose();
            return false;
        }

        // Subscribe to TX notifications (peer -> us).
        conn.Tx.ValueChanged += (_, args) => OnTxNotification(conn, ReadBuffer(args.CharacteristicValue));
        var cccd = await conn.Tx.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        Console.WriteLine($"[connect] TX subscribe: {cccd}");

        // Read identity (optional for the spike, informative).
        if (conn.Identity != null)
        {
            var idRead = await conn.Identity.ReadValueAsync();
            if (idRead.Status == GattCommunicationStatus.Success)
            {
                var idStr = System.Text.Encoding.UTF8.GetString(ReadBuffer(idRead.Value));
                Console.WriteLine($"[identity] {idStr}");
                // Derive the phone's logical id (TRANSPORT-BLE §3.3) from its
                // "<base64 pubkey>|<name>" so the admin proof can bind to it.
                peer.LogicalId = Protocol.LogicalIdFromAdvertisement(idStr) ?? peer.LogicalId;
                var name = idStr.Contains('|') ? idStr[(idStr.IndexOf('|') + 1)..] : null;
                if (!string.IsNullOrEmpty(name)) peer.AdvertisedName = name;
            }
        }

        device.ConnectionStatusChanged += (d, _) =>
        {
            if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                HandleDisconnect(conn, "gatt-disconnect");
        };

        lock (_lock) _connections[peer.BluetoothAddress] = conn;

        // Start keepalive PING loop.
        conn.Keepalive = new Timer(_ => _ = KeepaliveTick(conn), null,
            Protocol.KeepaliveIntervalMs, Protocol.KeepaliveIntervalMs);

        PeerConnected?.Invoke(peer);
        Console.WriteLine($"[connect] linked to {peer.AdvertisedName ?? peer.AddressString}");
        return true;
    }

    private static async Task<GattCharacteristic?> ResolveChar(GattDeviceService service, Guid uuid)
    {
        var res = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        return res.Status == GattCommunicationStatus.Success && res.Characteristics.Count > 0
            ? res.Characteristics[0]
            : null;
    }

    private void OnTxNotification(Connection conn, byte[] frame)
    {
        conn.LastInbound = DateTimeOffset.UtcNow;
        try
        {
            var (frameType, message) = conn.Reassembler.Accept(frame);
            if (frameType == Protocol.FrameTypePing) return; // liveness only
            if (message == null) return;
            if (frameType == Protocol.FrameTypeAdmin)
                AdminReceived?.Invoke(conn.Peer, System.Text.Encoding.UTF8.GetString(message));
            else
                MessageReceived?.Invoke(conn.Peer, message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[rx] frame parse error: {ex.Message}");
        }
    }

    public async Task SendAsync(BlePeer peer, byte[] data)
        => await SendFramedAsync(peer, data, Protocol.FrameTypeData);

    public async Task SendAdminAsync(BlePeer peer, string json)
        => await SendFramedAsync(peer, System.Text.Encoding.UTF8.GetBytes(json), Protocol.FrameTypeAdmin);

    private async Task SendFramedAsync(BlePeer peer, byte[] data, byte frameType)
    {
        Connection? conn;
        lock (_lock) _connections.TryGetValue(peer.BluetoothAddress, out conn);
        if (conn?.Rx == null) throw new InvalidOperationException("peer not connected");

        ushort msgId = conn.NextMsgId++;
        // Conservative chunk payload for the 20-byte ATT fallback is 11; use a
        // safe 180 here (negotiated MTU is typically higher). Keep <= 244.
        var chunks = BleFraming.Split(data, msgId, 180, frameType);
        foreach (var chunk in chunks)
        {
            using var writer = new DataWriter();
            writer.WriteBytes(chunk);
            var status = await conn.Rx.WriteValueAsync(writer.DetachBuffer(),
                GattWriteOption.WriteWithoutResponse);
            if (status != GattCommunicationStatus.Success)
                Console.WriteLine($"[tx] write failed: {status}");
        }
    }

    private async Task KeepaliveTick(Connection conn)
    {
        // Declare link dead if no inbound traffic within the timeout.
        if ((DateTimeOffset.UtcNow - conn.LastInbound).TotalMilliseconds > Protocol.LinkTimeoutMs)
        {
            HandleDisconnect(conn, "keepalive-timeout");
            return;
        }
        try
        {
            if (conn.Rx != null)
            {
                using var writer = new DataWriter();
                writer.WriteBytes(Protocol.Ping());
                await conn.Rx.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[keepalive] ping failed: {ex.Message}");
        }
    }

    private void HandleDisconnect(Connection conn, string reason)
    {
        lock (_lock)
        {
            if (!_connections.Remove(conn.Peer.BluetoothAddress)) return;
        }
        conn.Keepalive?.Dispose();
        try { conn.GattSession?.Dispose(); } catch { /* noop */ }
        try { conn.Device.Dispose(); } catch { /* noop */ }
        Console.WriteLine($"[disconnect] {conn.Peer.AddressString} ({reason})");
        PeerDisconnected?.Invoke(conn.Peer);
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        try { _watcher.Stop(); } catch { /* noop */ }
        List<Connection> conns;
        lock (_lock) { conns = _connections.Values.ToList(); _connections.Clear(); }
        foreach (var c in conns)
        {
            c.Keepalive?.Dispose();
            try { c.Device.Dispose(); } catch { /* noop */ }
        }
        return ValueTask.CompletedTask;
    }
}
