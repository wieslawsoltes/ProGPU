using System.Buffers.Binary;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeMilResourceResetTests
{
    [Fact]
    public void ResetUsesTheCanonicalEightByteFramingWithoutClearingTheAuthoredBatch()
    {
        var writer = new NativeMilBatchBuilder();
        writer.CreateResource(17, NativeMilResourceType.Visual);
        byte[] original = writer.ToArray();
        writer.DestroyResourcesOnChannel();
        Assert.Equal(original.Length + 8, writer.Length);
        Assert.True(writer.WrittenSpan[..original.Length].SequenceEqual(original));
        ReadOnlySpan<byte> reset = writer.WrittenSpan[original.Length..];
        Assert.Equal(8U, BinaryPrimitives.ReadUInt32LittleEndian(reset));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(reset[4..]));
        writer.DestroyResourcesOnChannel();
        Assert.True(writer.WrittenSpan[^8..].SequenceEqual(reset));
        writer.Clear();
        Assert.Equal(0, writer.Length);
    }
}
