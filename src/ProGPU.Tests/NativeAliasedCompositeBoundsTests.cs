using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public class NativeAliasedCompositeBoundsTests
{
    private const NativeSceneLayerFlags Required =
        NativeSceneLayerFlags.Bounds | NativeSceneLayerFlags.AliasedCompositeBounds;

    [Fact]
    public void AliasedReplacementRetainsOriginalFloatBoundsAndLayerLayout()
    {
        Assert.Equal(1024U, (uint)NativeSceneLayerFlags.AliasedCompositeBounds);
        Assert.Equal(64, Unsafe.SizeOf<NativeSceneLayer>());
        byte[] bytes = new byte[2048];
        var builder = new NativeSceneStreamBuilder(bytes, 0x94F7, 1, commandCapacity: 2, resourceCapacity: 0);
        var layer = new NativeSceneLayer(blendMode: GpuBlendMode.Src, flags: Required,
            bounds: new(12.75f, 14.75f, 11f, 11f));
        Assert.True(builder.TryPushLayer(1, in layer));
        Assert.True(builder.TryPopLayer(2));
        Assert.True(builder.TryBuild(out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var command = MemoryMarshal.Read<NativeMethods.SceneCommand>(stream[(int)header.CommandOffset..]);
        var retained = MemoryMarshal.Read<NativeSceneLayer>(stream[(int)command.PayloadOffset..]);
        Assert.Equal(layer.Flags, retained.Flags);
        Assert.Equal(layer.Bounds.X, retained.Bounds.X);
        Assert.Equal(layer.Bounds.Y, retained.Bounds.Y);
        Assert.Equal(layer.Bounds.Width, retained.Bounds.Width);
        Assert.Equal(layer.Bounds.Height, retained.Bounds.Height);
        Assert.Equal(layer.BlendMode, retained.BlendMode);
    }

    [Theory]
    [InlineData(NativeSceneLayerFlags.None)]
    [InlineData(NativeSceneLayerFlags.Backdrop)]
    [InlineData(NativeSceneLayerFlags.ForceIsolation)]
    [InlineData(NativeSceneLayerFlags.CacheContent)]
    [InlineData(NativeSceneLayerFlags.CacheLocalSpace)]
    [InlineData(NativeSceneLayerFlags.CacheNearest)]
    [InlineData(NativeSceneLayerFlags.CacheFant)]
    [InlineData(NativeSceneLayerFlags.CompositeState)]
    [InlineData(NativeSceneLayerFlags.CacheTile)]
    [InlineData(NativeSceneLayerFlags.CacheShared)]
    [InlineData((NativeSceneLayerFlags)0x80000000U)]
    public void UnsupportedFlagsRejectBeforePublishingAnyBytes(NativeSceneLayerFlags extra)
    {
        var flags = extra == NativeSceneLayerFlags.None
            ? NativeSceneLayerFlags.AliasedCompositeBounds : Required | extra;
        Reject(new NativeSceneLayer(blendMode: GpuBlendMode.Src, flags: flags, bounds: new(1, 2, 3, 4)));
    }

    [Theory]
    [InlineData(GpuBlendMode.SrcOver, 1f, uint.MaxValue, uint.MaxValue, 0UL, 0UL, 0U, 0U)]
    [InlineData(GpuBlendMode.Clear, 1f, uint.MaxValue, uint.MaxValue, 0UL, 0UL, 0U, 0U)]
    [InlineData(GpuBlendMode.Src, 0f, uint.MaxValue, uint.MaxValue, 0UL, 0UL, 0U, 0U)]
    [InlineData(GpuBlendMode.Src, 0.5f, uint.MaxValue, uint.MaxValue, 0UL, 0UL, 0U, 0U)]
    [InlineData(GpuBlendMode.Src, 1f, 0U, uint.MaxValue, 0UL, 0UL, 0U, 0U)]
    [InlineData(GpuBlendMode.Src, 1f, uint.MaxValue, 0U, 0UL, 0UL, 0U, 0U)]
    [InlineData(GpuBlendMode.Src, 1f, uint.MaxValue, uint.MaxValue, 1UL, 0UL, 0U, 0U)]
    [InlineData(GpuBlendMode.Src, 1f, uint.MaxValue, uint.MaxValue, 0UL, 1UL, 0U, 0U)]
    [InlineData(GpuBlendMode.Src, 1f, uint.MaxValue, uint.MaxValue, 0UL, 0UL, 1U, 0U)]
    [InlineData(GpuBlendMode.Src, 1f, uint.MaxValue, uint.MaxValue, 0UL, 0UL, 0U, 1U)]
    public void UnsupportedLayerFormsRejectAtomically(GpuBlendMode blend, float opacity,
        uint mask, uint effect, ulong content, ulong composite, uint state, uint tile)
    {
        Reject(new NativeSceneLayer(opacity, blend, Required, new(1, 2, 3, 4),
            mask, effect, content, composite, state, tile));
    }

    private static void Reject(NativeSceneLayer invalid)
    {
        byte[] bytes = new byte[2048];
        var builder = new NativeSceneStreamBuilder(bytes, 0x94F7, 1, commandCapacity: 4, resourceCapacity: 0);
        var valid = new NativeSceneLayer(blendMode: GpuBlendMode.Src, flags: Required, bounds: new(1, 2, 3, 4));
        Assert.True(builder.TryPushLayer(1, in valid));
        Assert.True(builder.TryPopLayer(2));
        Assert.True(builder.TryBuild(out _));
        byte[] before = (byte[])bytes.Clone();
        Assert.False(builder.TryPushLayer(3, in invalid));
        Assert.Equal(before, bytes);
        Assert.True(builder.TryBuild(out _));
        Assert.Equal(before, bytes);
    }
}
