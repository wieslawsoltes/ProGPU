using System;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public class NativeLayerBackgroundTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void RetainsDistinctInitializationIdentity(float opacity)
    {
        Assert.Equal(2048U, (uint)NativeSceneLayerFlags.InitializeFromBackground);
        byte[] bytes = new byte[2048];
        var builder = new NativeSceneStreamBuilder(bytes, 0xBA00, 1, commandCapacity: 2, resourceCapacity: 0);
        var layer = new NativeSceneLayer(opacity, flags: NativeSceneLayerFlags.InitializeFromBackground);
        Assert.True(builder.TryPushLayer(1, in layer));
        Assert.True(builder.TryPopLayer(2));
        Assert.True(builder.TryBuild(out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var command = MemoryMarshal.Read<NativeMethods.SceneCommand>(stream[(int)header.CommandOffset..]);
        var retained = MemoryMarshal.Read<NativeSceneLayer>(stream[(int)command.PayloadOffset..]);
        Assert.Equal(NativeSceneLayerFlags.InitializeFromBackground, retained.Flags);
        Assert.Equal(opacity, retained.Opacity);
        Assert.Equal(GpuBlendMode.SrcOver, retained.BlendMode);
    }

    [Theory]
    [InlineData(NativeSceneLayerFlags.Backdrop)]
    [InlineData(NativeSceneLayerFlags.CacheContent)]
    [InlineData(NativeSceneLayerFlags.CacheLocalSpace)]
    [InlineData(NativeSceneLayerFlags.CacheShared)]
    [InlineData(NativeSceneLayerFlags.CompositeState)]
    [InlineData(NativeSceneLayerFlags.AliasedCompositeBounds)]
    [InlineData((NativeSceneLayerFlags)0x80000000U)]
    public void UnsupportedCombinationsPreserveAllCallerBytes(NativeSceneLayerFlags extra)
    {
        byte[] bytes = new byte[2048];
        var builder = new NativeSceneStreamBuilder(bytes, 0xBA01, 1, commandCapacity: 2, resourceCapacity: 0);
        var invalid = new NativeSceneLayer(flags: NativeSceneLayerFlags.InitializeFromBackground | extra);
        byte[] before = (byte[])bytes.Clone();
        Assert.False(builder.TryPushLayer(1, in invalid));
        Assert.Equal(before, bytes);
        var valid = new NativeSceneLayer(flags: NativeSceneLayerFlags.InitializeFromBackground);
        Assert.True(builder.TryPushLayer(1, in valid));
        Assert.True(builder.TryPopLayer(2));
        Assert.True(builder.TryBuild(out _));
    }
}
