using System.Buffers.Binary;
using System.Text;
using ProGPU.Browser;
using Xunit;

namespace ProGPU.Tests;

public sealed class BrowserGpuProtocolTests
{
    [Fact]
    public void EmptyPacketHasVersionedHeader()
    {
        using var encoder = new BrowserGpuCommandEncoder(16);
        var packet = encoder.WrittenSpan;

        Assert.Equal(BrowserGpuProtocol.PacketHeaderSize, packet.Length);
        Assert.Equal(BrowserGpuProtocol.Magic, BinaryPrimitives.ReadUInt32LittleEndian(packet));
        Assert.Equal(BrowserGpuProtocol.Version, BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]));
        Assert.Equal((uint)packet.Length, BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]));
    }

    [Fact]
    public void FrameCommandsAreAlignedAndCounted()
    {
        using var encoder = new BrowserGpuCommandEncoder(16);
        encoder.Reset(BrowserGpuPacketFlags.Validation);
        encoder.BeginFrame(0.1f, 0.2f, 0.3f, 1f, 1920, 1080);
        encoder.EndFrame();

        var packet = encoder.WrittenSpan;
        Assert.True(encoder.ContainsFrame);
        Assert.Equal(2, encoder.CommandCount);
        Assert.Equal(56, packet.Length);
        Assert.Equal((ushort)BrowserGpuPacketFlags.Validation, BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]));
        Assert.Equal((ushort)BrowserGpuOpcode.BeginFrame, BinaryPrimitives.ReadUInt16LittleEndian(packet[16..]));
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(packet[20..]));
        Assert.Equal(1920u, BinaryPrimitives.ReadUInt32LittleEndian(packet[40..]));
        Assert.Equal(1080u, BinaryPrimitives.ReadUInt32LittleEndian(packet[44..]));
        Assert.Equal((ushort)BrowserGpuOpcode.EndFrame, BinaryPrimitives.ReadUInt16LittleEndian(packet[48..]));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(packet[52..]));
    }

    [Fact]
    public void ResetClearsFrameMarker()
    {
        using var encoder = new BrowserGpuCommandEncoder();
        encoder.BeginFrame(0, 0, 0, 1, 1, 1);

        encoder.Reset();

        Assert.False(encoder.ContainsFrame);
        Assert.Equal(0, encoder.CommandCount);
    }

    [Fact]
    public void ShaderCommandPreservesExactUtf8Wgsl()
    {
        const string wgsl = "// π\n@compute @workgroup_size(1) fn main() {}";
        using var encoder = new BrowserGpuCommandEncoder(16);
        var handle = BrowserGpuHandle.Create(7, 3);
        encoder.CreateShaderModule(handle, wgsl);

        var packet = encoder.WrittenSpan;
        Assert.Equal((ushort)BrowserGpuOpcode.CreateShaderModule, BinaryPrimitives.ReadUInt16LittleEndian(packet[16..]));
        Assert.Equal(handle.Value, BinaryPrimitives.ReadUInt32LittleEndian(packet[24..]));
        var byteCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet[28..]));
        Assert.Equal(Encoding.UTF8.GetByteCount(wgsl), byteCount);
        Assert.Equal(wgsl, Encoding.UTF8.GetString(packet.Slice(32, byteCount)));
        Assert.Equal(BrowserGpuProtocol.Align(32 + byteCount), packet.Length);
    }

    [Fact]
    public void EncoderGrowthPreservesEarlierCommands()
    {
        using var encoder = new BrowserGpuCommandEncoder(16);
        encoder.BeginFrame(0, 0, 0, 1, 1, 1);
        encoder.CreateShaderModule(BrowserGpuHandle.Create(1, 1), new string('x', 128 * 1024));
        encoder.EndFrame();

        var packet = encoder.WrittenSpan;
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]));
        Assert.Equal((ushort)BrowserGpuOpcode.BeginFrame, BinaryPrimitives.ReadUInt16LittleEndian(packet[16..]));
        Assert.Equal((ushort)BrowserGpuOpcode.CreateShaderModule, BinaryPrimitives.ReadUInt16LittleEndian(packet[48..]));
    }

    [Fact]
    public void RecycledHandlesAdvanceGenerationAndRejectStaleRelease()
    {
        var pool = new BrowserGpuHandlePool();
        var first = pool.Allocate();
        pool.Release(first);
        var second = pool.Allocate();

        Assert.Equal(first.Index, second.Index);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.False(pool.IsCurrent(first));
        Assert.True(pool.IsCurrent(second));
        Assert.Throws<InvalidOperationException>(() => pool.Release(first));
    }

    [Fact]
    public void HandleRejectsReservedOrOutOfRangeParts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserGpuHandle.Create(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserGpuHandle.Create(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserGpuHandle.Create(0x10_0000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserGpuHandle.Create(1, 0x1000));
    }

    [Fact]
    public void PacketReaderRejectsLengthMismatchAndUnknownVersion()
    {
        using var encoder = new BrowserGpuCommandEncoder();
        encoder.EndFrame();
        var packet = encoder.WrittenSpan.ToArray();

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)(packet.Length + 8));
        Assert.Throws<InvalidDataException>(() => ReadAll(packet));

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), BrowserGpuProtocol.Version + 1);
        Assert.Throws<NotSupportedException>(() => ReadAll(packet));
    }

    [Fact]
    public void PacketReaderEnumeratesAlignedPayloads()
    {
        using var encoder = new BrowserGpuCommandEncoder();
        encoder.WriteRaw(BrowserGpuOpcode.ClearBuffer, [1, 2, 3]);
        encoder.EndFrame();
        var reader = new BrowserGpuPacketReader(encoder.WrittenSpan);

        Assert.True(reader.TryRead(out var first));
        Assert.Equal(BrowserGpuOpcode.ClearBuffer, first.Opcode);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Payload.ToArray());
        Assert.True(reader.TryRead(out var second));
        Assert.Equal(BrowserGpuOpcode.EndFrame, second.Opcode);
        Assert.False(reader.TryRead(out _));
    }

    private static void ReadAll(byte[] packet)
    {
        var reader = new BrowserGpuPacketReader(packet);
        while (reader.TryRead(out _))
        {
        }
    }
}
