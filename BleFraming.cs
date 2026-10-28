namespace YuhuAdminBle;

/// <summary>
/// Pure framing/chunking mirroring BleFraming.kt (TRANSPORT-BLE.md §5.2),
/// big-endian 9-byte header. Kept free of any BLE/WinRT dependency so it is
/// unit-testable off-device and provably interoperable with Android.
///
/// Golden vector (interop contract): message "AB", msgId=0x1234, single chunk
///   -> 01 00 12 34 00 00 00 01 02 41 42
/// </summary>
public static class BleFraming
{
    /// <summary>
    /// Split a whole message into DATA chunks sized to chunkPayload bytes each.
    /// </summary>
    public static List<byte[]> Split(byte[] message, ushort msgId, int chunkPayload)
    {
        if (chunkPayload < 1 || chunkPayload > Protocol.MaxChunkPayload)
            throw new ArgumentOutOfRangeException(nameof(chunkPayload));

        var chunks = new List<byte[]>();
        int total = message.Length == 0
            ? 1
            : (message.Length + chunkPayload - 1) / chunkPayload;

        for (int i = 0; i < total; i++)
        {
            int start = i * chunkPayload;
            int len = Math.Min(chunkPayload, message.Length - start);
            if (len < 0) len = 0;

            var frame = new byte[Protocol.HeaderSize + len];
            frame[0] = Protocol.Version;
            frame[1] = Protocol.FrameTypeData;
            frame[2] = (byte)(msgId >> 8);
            frame[3] = (byte)(msgId & 0xFF);
            frame[4] = (byte)(i >> 8);
            frame[5] = (byte)(i & 0xFF);
            frame[6] = (byte)(total >> 8);
            frame[7] = (byte)(total & 0xFF);
            frame[8] = (byte)len;
            if (len > 0) Array.Copy(message, start, frame, Protocol.HeaderSize, len);
            chunks.Add(frame);
        }
        return chunks;
    }

    /// <summary>Parsed header view of a single frame.</summary>
    public readonly record struct Header(
        byte Version, byte FrameType, ushort MsgId,
        ushort ChunkIndex, ushort ChunkCount, byte PayloadLen);

    public static Header ParseHeader(byte[] frame)
    {
        if (frame.Length < Protocol.HeaderSize)
            throw new ArgumentException("frame shorter than header");
        return new Header(
            frame[0], frame[1],
            (ushort)((frame[2] << 8) | frame[3]),
            (ushort)((frame[4] << 8) | frame[5]),
            (ushort)((frame[6] << 8) | frame[7]),
            frame[8]);
    }

    /// <summary>
    /// Reassembles inbound chunks per msgId. Returns the full message when the
    /// last missing chunk for an id arrives; otherwise null. Duplicate indices
    /// are ignored (idempotent).
    /// </summary>
    public sealed class Reassembler
    {
        private sealed class Partial
        {
            public ushort Count;
            public readonly Dictionary<int, byte[]> Chunks = new();
            public DateTimeOffset Started = DateTimeOffset.UtcNow;
        }

        private readonly Dictionary<ushort, Partial> _buffers = new();
        private readonly int _reassemblyTimeoutMs;

        public Reassembler(int reassemblyTimeoutMs = 5000) => _reassemblyTimeoutMs = reassemblyTimeoutMs;

        /// <summary>Returns a frame type to let the caller route PING/IDENTITY/DATA.</summary>
        public (byte frameType, byte[]? message) Accept(byte[] frame)
        {
            PruneStale();
            var h = ParseHeader(frame);

            if (h.FrameType == Protocol.FrameTypePing)
                return (Protocol.FrameTypePing, null);

            var payload = new byte[h.PayloadLen];
            Array.Copy(frame, Protocol.HeaderSize, payload, 0,
                Math.Min(h.PayloadLen, frame.Length - Protocol.HeaderSize));

            if (!_buffers.TryGetValue(h.MsgId, out var p))
            {
                p = new Partial { Count = h.ChunkCount };
                _buffers[h.MsgId] = p;
            }
            p.Chunks[h.ChunkIndex] = payload; // dup indices overwrite harmlessly

            if (p.Chunks.Count < p.Count)
                return (h.FrameType, null);

            // all chunks in — concatenate in index order
            using var ms = new MemoryStream();
            for (int i = 0; i < p.Count; i++)
                if (p.Chunks.TryGetValue(i, out var c)) ms.Write(c, 0, c.Length);
            _buffers.Remove(h.MsgId);
            return (h.FrameType, ms.ToArray());
        }

        private void PruneStale()
        {
            var now = DateTimeOffset.UtcNow;
            var dead = _buffers
                .Where(kv => (now - kv.Value.Started).TotalMilliseconds > _reassemblyTimeoutMs)
                .Select(kv => kv.Key).ToList();
            foreach (var k in dead) _buffers.Remove(k);
        }
    }
}
