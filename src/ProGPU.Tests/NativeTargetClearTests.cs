using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public class NativeTargetClearTests
{
    [Fact]
    public void RetainsOriginalStraightColorWithoutGeometryOrStackMutation()
    {
        Assert.Equal(5U, (uint)NativeSceneCommandKind.ClearTarget);
        byte[] bytes = new byte[2048];
        var builder = new NativeSceneStreamBuilder(bytes, 0x96D3, 1, 3, 0);
        var color = new Vector4(2f, -.5f, -0f, .25f);
        Assert.True(builder.TrySave(1));
        Assert.True(builder.TryClearTarget(2, color));
        Assert.True(builder.TryRestore(3));
        Assert.True(builder.TryBuild(out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var first = MemoryMarshal.Read<NativeMethods.SceneCommand>(stream[(int)header.CommandOffset..]);
        var command = MemoryMarshal.Read<NativeMethods.SceneCommand>(stream[(int)(header.CommandOffset + first.StructSize)..]);
        Assert.Equal(NativeSceneCommandKind.ClearTarget, command.Kind);
        Assert.Equal(NativeMethods.SceneNoIndex, command.ResourceIndex);
        Assert.Equal(16U, command.PayloadSize);
        Assert.Equal(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref color, 1)).ToArray(),
            stream.Slice((int)command.PayloadOffset, 16).ToArray());
        Assert.Equal(0U, header.ResourceCount);
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f, 1f)]
    [InlineData(0f, float.PositiveInfinity, 0f, 1f)]
    [InlineData(0f, 0f, float.NegativeInfinity, 1f)]
    [InlineData(0f, 0f, 0f, float.NaN)]
    [InlineData(0f, 0f, 0f, -.25f)]
    [InlineData(0f, 0f, 0f, 1.25f)]
    public void InvalidColorPreservesAllBytesAndCommandIdentity(float r, float g, float b, float a)
    {
        byte[] bytes = new byte[2048];
        var builder = new NativeSceneStreamBuilder(bytes, 0x96D4, 1, 1, 0);
        byte[] before = (byte[])bytes.Clone();
        Assert.False(builder.TryClearTarget(1, new Vector4(r, g, b, a)));
        Assert.Equal(before, bytes);
        Assert.True(builder.TryClearTarget(1, Vector4.Zero));
        Assert.True(builder.TryBuild(out _));
    }

    [Fact]
    public void DuplicateIdentityAndUnavailableStateDoNotWritePayload()
    {
        byte[] bytes = new byte[2048];
        var builder = new NativeSceneStreamBuilder(bytes, 0x96D5, 1, 2, 0);
        Assert.True(builder.TryClearTarget(7, Vector4.One));
        byte[] before = (byte[])bytes.Clone();
        Assert.False(builder.TryClearTarget(7, Vector4.Zero));
        Assert.False(builder.TryClearTarget(8, Vector4.Zero, stateIndex: 0));
        Assert.Equal(before, bytes);
        Assert.True(builder.TryClearTarget(8, Vector4.Zero));
        Assert.True(builder.TryBuild(out _));
    }
}
