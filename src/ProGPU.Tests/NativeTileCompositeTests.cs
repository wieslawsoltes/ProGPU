using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public class NativeTileCompositeTests
{
    [Fact]
    public void LocalTileCaptureHasSeparateCompositeResource()
    {
        Assert.Equal(64, Unsafe.SizeOf<NativeSceneTileComposite>());
        byte[] bytes = new byte[4096];
        var builder = new NativeSceneStreamBuilder(bytes, 9510, 1, commandCapacity: 2, resourceCapacity: 2);
        var state = new NativeSceneState(Matrix3x2.Identity);
        Assert.True(builder.TryAddStateResource(1, 1, in state, out uint stateIndex));
        var tile = new NativeSceneTileComposite(new(8, 4, 32, 16),
            new(0.125f, 0, 0, 0.25f, -2, -2), 1, 2);
        Assert.True(builder.TryAddTileCompositeResource(2, 1, in tile, out uint tileIndex));
        var layer = new NativeSceneLayer(flags: NativeSceneLayerFlags.Bounds |
            NativeSceneLayerFlags.CacheContent | NativeSceneLayerFlags.CacheLocalSpace |
            NativeSceneLayerFlags.CacheTile, bounds: new(0, 0, 3, 5),
            contentRevision: 7, compositeRevision: 9,
            compositeStateResourceIndex: stateIndex, tileCompositeResourceIndex: tileIndex);
        Assert.True(builder.TryPushLayer(1, in layer));
        Assert.True(builder.TryPopLayer(2));
        Assert.True(builder.TryBuild(out _));
    }

    [Fact]
    public void UnsupportedTileResourcesAndFilteringFailClosed()
    {
        byte[] bytes = new byte[4096];
        var builder = new NativeSceneStreamBuilder(bytes, 9511, 1, commandCapacity: 2, resourceCapacity: 3);
        var state = new NativeSceneState(Matrix3x2.Identity);
        Assert.True(builder.TryAddStateResource(1, 1, in state, out uint stateIndex));
        var tile = new NativeSceneTileComposite(new(0, 0, 32, 16), Matrix3x2.Identity, 3, 0);
        Assert.False(builder.TryAddTileCompositeResource(2, 1, in tile, out _));
        tile.AddressU = 1;
        Assert.True(builder.TryAddTileCompositeResource(2, 1, in tile, out uint tileIndex));
        var layer = new NativeSceneLayer(flags: NativeSceneLayerFlags.Bounds |
            NativeSceneLayerFlags.CacheContent | NativeSceneLayerFlags.CacheLocalSpace |
            NativeSceneLayerFlags.CacheTile | NativeSceneLayerFlags.CacheFant,
            bounds: new(0, 0, 3, 5), contentRevision: 7, compositeRevision: 9,
            compositeStateResourceIndex: stateIndex, tileCompositeResourceIndex: tileIndex);
        Assert.False(builder.TryPushLayer(1, in layer));
        layer = new NativeSceneLayer(flags: NativeSceneLayerFlags.Bounds |
            NativeSceneLayerFlags.CacheContent | NativeSceneLayerFlags.CacheLocalSpace |
            NativeSceneLayerFlags.CacheTile, bounds: new(0, 0, 3, 5),
            contentRevision: 7, compositeRevision: 9,
            compositeStateResourceIndex: stateIndex, tileCompositeResourceIndex: stateIndex);
        Assert.False(builder.TryPushLayer(1, in layer));
    }
}
