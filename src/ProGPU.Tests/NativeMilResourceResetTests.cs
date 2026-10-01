using System.Buffers.Binary;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeMilResourceResetTests
{
    [Theory]
    [InlineData(0U)]
    [InlineData(17U)]
    [InlineData(uint.MaxValue)]
    public void ResetUsesTheCanonicalChannelFieldWithoutClearingTheAuthoredBatch(uint channelHandle)
    {
        var writer = new NativeMilBatchBuilder();
        writer.CreateResource(17, NativeMilResourceType.Visual);
        byte[] original = writer.ToArray();
        writer.DestroyResourcesOnChannel(channelHandle);
        Assert.Equal(original.Length + 12, writer.Length);
        Assert.True(writer.WrittenSpan[..original.Length].SequenceEqual(original));
        ReadOnlySpan<byte> reset = writer.WrittenSpan[original.Length..];
        Assert.Equal(12U, BinaryPrimitives.ReadUInt32LittleEndian(reset));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(reset[4..]));
        Assert.Equal(channelHandle, BinaryPrimitives.ReadUInt32LittleEndian(reset[8..]));
        writer.DestroyResourcesOnChannel(channelHandle);
        Assert.True(writer.WrittenSpan[^12..].SequenceEqual(reset));
        writer.Clear();
        Assert.Equal(0, writer.Length);
    }
}
