namespace YuhuAdminBle;

/// <summary>
/// Frozen YUHU BLE protocol constants. These MUST match the Android
/// implementation byte-for-byte (see TRANSPORT-BLE.md §2.1 and BleFraming.kt).
/// Do NOT regenerate the UUIDs.
/// </summary>
public static class Protocol
{
    // ---- Service + characteristic UUIDs (FROZEN, TRANSPORT-BLE.md §2.1) ----
    public static readonly Guid ServiceUuid = Guid.Parse("0da35ffe-f05d-4824-a6ad-0f6a1277c82b");

    /// <summary>Peer → us. We subscribe to notifications on this.</summary>
    public static readonly Guid TxCharUuid = Guid.Parse("440098df-4167-421e-b52b-800c21dd718b");

    /// <summary>Us → peer. We write to this.</summary>
    public static readonly Guid RxCharUuid = Guid.Parse("26f3501f-259a-40a5-bd15-d243b5701853");

    /// <summary>Peer's "&lt;base64 pubkey&gt;|&lt;name&gt;" (Read + Notify).</summary>
    public static readonly Guid IdentityCharUuid = Guid.Parse("79a65656-5047-4a34-b4d8-864c8ed4ccde");

    /// <summary>Capability bitfield (Read).</summary>
    public static readonly Guid CapabilityCharUuid = Guid.Parse("e7dc8ba9-ae89-40fe-9151-329227a859fd");

    // ---- Frame header (TRANSPORT-BLE.md §5.2 / BleFraming.kt), 9 bytes, big-endian ----
    public const byte Version = 0x01;
    public const byte FrameTypeData = 0x00;
    public const byte FrameTypePing = 0x01;
    public const byte FrameTypeIdentity = 0x02;
    public const byte FrameTypeAdmin = 0x03; // A.3: admin control envelope (JSON), chunked like DATA
    public const int HeaderSize = 9;
    public const int MaxChunkPayload = 255;

    /// <summary>A single pre-built PING control frame (header only, no payload).</summary>
    public static byte[] Ping() => new byte[]
    {
        Version, FrameTypePing,
        0, 0, // msgId (unused for PING)
        0, 0, // chunkIndex
        0, 0, // chunkCount
        0,    // payloadLen
    };

    // ---- Keepalive timing (TRANSPORT-BLE.md §7) ----
    public const int KeepaliveIntervalMs = 2000;
    public const int LinkTimeoutMs = 6000;

    /// <summary>
    /// Derive the 16-hex logical id from an advertisement "&lt;base64 pubkey&gt;|&lt;name&gt;"
    /// per TRANSPORT-BLE.md §3.3: hex(SHA-256(raw_32_byte_pubkey))[0..16). Returns
    /// null if the advert is malformed or the key is not 32 bytes.
    /// </summary>
    public static string? LogicalIdFromAdvertisement(string advertisement)
    {
        if (string.IsNullOrEmpty(advertisement)) return null;
        var pubB64 = advertisement.Contains('|')
            ? advertisement[..advertisement.IndexOf('|')]
            : advertisement;
        byte[] pub;
        try { pub = Convert.FromBase64String(pubB64.Trim()); }
        catch { return null; }
        if (pub.Length != 32) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(pub);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
