namespace YuhuAdminBle;

/// <summary>
/// A discovered YUHU peer (pre- or post-connect).
/// </summary>
public sealed class BlePeer
{
    public ulong BluetoothAddress { get; init; }
    public string AddressString => FormatAddress(BluetoothAddress);
    public string? AdvertisedName { get; set; }
    public string? LogicalId { get; set; }       // 16-hex id from advert (if present)
    public short Rssi { get; set; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    private static string FormatAddress(ulong addr)
    {
        var bytes = BitConverter.GetBytes(addr);
        // Bluetooth addresses are 48-bit; take the low 6 bytes, big-endian display.
        return string.Join(":", new[] { bytes[5], bytes[4], bytes[3], bytes[2], bytes[1], bytes[0] }
            .Select(b => b.ToString("X2")));
    }
}

/// <summary>
/// Role-agnostic transport abstraction. A.3.0 ships only a CENTRAL
/// implementation (laptop scans + connects out). The interface is deliberately
/// role-neutral so a future PERIPHERAL implementation (laptop advertises so
/// phones can find it) — and a multi-laptop/admin-mesh mode — drop in without
/// changing callers.
/// </summary>
public interface IBleTransport : IAsyncDisposable
{
    /// <summary>Fired for every sighting of a YUHU device while scanning.</summary>
    event Action<BlePeer>? PeerDiscovered;

    /// <summary>Fired when a GATT link to a peer is up and characteristics are resolved.</summary>
    event Action<BlePeer>? PeerConnected;

    /// <summary>Fired when a peer link drops (GATT disconnect or keepalive timeout).</summary>
    event Action<BlePeer>? PeerDisconnected;

    /// <summary>Fired with a fully reassembled inbound message (DATA frames).</summary>
    event Action<BlePeer, byte[]>? MessageReceived;

    /// <summary>Fired with a reassembled inbound ADMIN envelope (0x03 frames), UTF-8 JSON.</summary>
    event Action<BlePeer, string>? AdminReceived;

    /// <summary>Begin scanning for YUHU devices (by frozen Service UUID).</summary>
    Task StartScanAsync();

    /// <summary>Stop scanning.</summary>
    void StopScan();

    /// <summary>Connect to a discovered peer and resolve its GATT characteristics.</summary>
    Task<bool> ConnectAsync(BlePeer peer);

    /// <summary>Send raw bytes to a connected peer (framed + chunked).</summary>
    Task SendAsync(BlePeer peer, byte[] data);

    /// <summary>Send an admin JSON envelope to a connected peer (0x03 frames).</summary>
    Task SendAdminAsync(BlePeer peer, string json);
}
