using Xunit;
using YuhuAdminBle;

namespace YuhuAdminBle.Tests;

public class BleFramingTests
{
    [Fact]
    public void GoldenVector_AB_SingleChunk()
    {
        // Interop contract (TRANSPORT-BLE.md §5.2 / BleFraming.kt):
        // message "AB", msgId=0x1234, single chunk
        //   -> 01 00 12 34 00 00 00 01 02 41 42
        var msg = "AB"u8.ToArray();
        var chunks = BleFraming.Split(msg, 0x1234, 180);

        Assert.Single(chunks);
        var expected = new byte[]
        {
            0x01, 0x00, 0x12, 0x34, 0x00, 0x00, 0x00, 0x01, 0x02, 0x41, 0x42,
        };
        Assert.Equal(expected, chunks[0]);
    }

    [Fact]
    public void Split_MultiChunk_HasCorrectCountAndIndices()
    {
        var msg = new byte[500];
        for (int i = 0; i < msg.Length; i++) msg[i] = (byte)i;

        var chunks = BleFraming.Split(msg, 1, 180); // 180*3 = 540 >= 500 -> 3 chunks
        Assert.Equal(3, chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            var h = BleFraming.ParseHeader(chunks[i]);
            Assert.Equal(Protocol.Version, h.Version);
            Assert.Equal(Protocol.FrameTypeData, h.FrameType);
            Assert.Equal((ushort)1, h.MsgId);
            Assert.Equal((ushort)i, h.ChunkIndex);
            Assert.Equal((ushort)3, h.ChunkCount);
        }
    }

    [Fact]
    public void RoundTrip_Reassembles_Original()
    {
        var msg = new byte[1000];
        new Random(42).NextBytes(msg);

        var chunks = BleFraming.Split(msg, 7, 180);
        var reasm = new BleFraming.Reassembler();

        byte[]? result = null;
        foreach (var c in chunks)
        {
            var (_, m) = reasm.Accept(c);
            if (m != null) result = m;
        }

        Assert.NotNull(result);
        Assert.Equal(msg, result);
    }

    [Fact]
    public void Reassembler_IgnoresDuplicateChunks()
    {
        var msg = new byte[400];
        new Random(1).NextBytes(msg);
        var chunks = BleFraming.Split(msg, 9, 180);
        var reasm = new BleFraming.Reassembler();

        byte[]? result = null;
        // feed first chunk twice
        reasm.Accept(chunks[0]);
        reasm.Accept(chunks[0]);
        foreach (var c in chunks)
        {
            var (_, m) = reasm.Accept(c);
            if (m != null) result = m;
        }
        Assert.NotNull(result);
        Assert.Equal(msg, result);
    }

    [Fact]
    public void PingFrame_IsRecognized()
    {
        var reasm = new BleFraming.Reassembler();
        var (frameType, message) = reasm.Accept(Protocol.Ping());
        Assert.Equal(Protocol.FrameTypePing, frameType);
        Assert.Null(message);
    }
}
