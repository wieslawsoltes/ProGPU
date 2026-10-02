using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public class NativeAxisClipMaskTests
{
    [Fact]
    public void RetainsExplicitAreaPolicyInOriginalWireAndChains()
    {
        var mask = NativeSceneLayerMask.CreateAxisAlignedClip(new(2.25f, 3.75f, 10.5f, 11.25f));
        Assert.Equal(1U, mask.Flags);
        Assert.Equal(Matrix3x2.Identity, mask.Transform);
        Assert.Equal(Vector4.Zero, mask.CornerRadiiX);
        Assert.Equal(Vector4.Zero, mask.CornerRadiiY);
        Assert.Equal(1f, mask.Opacity);
        byte[] bytes = new byte[4096];
        var builder = new NativeSceneStreamBuilder(bytes, 0x9643, 1, 0, 2);
        Assert.True(builder.TryAddLayerMaskResource(1, 1, mask, out uint index));
        Assert.Equal(0U, index);
        var chain = new NativeSceneLayerMaskChain(new[] { mask, mask });
        Assert.True(builder.TryAddLayerMaskChainResource(2, 1, chain, out index));
        Assert.True(builder.TryBuild(out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(stream[(int)header.ResourceOffset..]);
        var retained = MemoryMarshal.Read<NativeSceneLayerMask>(stream[(int)resource.PayloadOffset..]);
        Assert.Equal(mask.Flags, retained.Flags);
        resource = MemoryMarshal.Read<NativeMethods.SceneResource>(stream[(int)(header.ResourceOffset + resource.StructSize)..]);
        var retainedChain = MemoryMarshal.Read<NativeSceneLayerMaskChain>(stream[(int)resource.PayloadOffset..]);
        Assert.Equal(1U, retainedChain.Mask0.Flags);
        Assert.Equal(1U, retainedChain.Mask1.Flags);
    }

    [Theory]
    [InlineData(8, 2U)] // unknown flags
    [InlineData(32, 0x40000000U)] // source scale 2
    [InlineData(36, 0x3e800000U)] // source shear .25
    [InlineData(48, 0x3f800000U)] // source translation 1
    [InlineData(56, 0x3f800000U)] // radius X
    [InlineData(84, 0x3f800000U)] // radius Y
    [InlineData(88, 0x3f000000U)] // nonunit opacity
    public void RejectsUnprovenAreaFramesBeforeWriting(int offset, uint value)
    {
        var mask = NativeSceneLayerMask.CreateAxisAlignedClip(new(2.25f, 3.75f, 10.5f, 11.25f));
        byte[] wire = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref mask, 1)).ToArray();
        MemoryMarshal.Write(wire.AsSpan(offset), in value);
        var invalid = MemoryMarshal.Read<NativeSceneLayerMask>(wire);
        byte[] bytes = new byte[2048];
        var builder = new NativeSceneStreamBuilder(bytes, 0x9644, 1, 0, 1);
        byte[] before = (byte[])bytes.Clone();
        Assert.False(builder.TryAddLayerMaskResource(1, 1, invalid, out _));
        Assert.Equal(before, bytes);
        Assert.True(builder.TryAddLayerMaskResource(1, 1, mask, out _));
        Assert.True(builder.TryBuild(out _));
    }

    [Fact]
    public void EveryMaskShaderRetainsIdenticalExplicitAreaArithmetic()
    {
        string? original = null;
        foreach (string resource in new[] { "Vector.wgsl", "Texture.wgsl", "TextMaskCommon.wgsl" })
        {
            string shader = ShaderResource.Load(typeof(Shaders), resource);
            int start = shader.IndexOf("if (sampling.options.x == 5.0)", StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = shader.IndexOf("return coverage.x * coverage.y;", start, StringComparison.Ordinal);
            Assert.True(end > start);
            string area = shader[start..(end + "return coverage.x * coverage.y;".Length)];
            if (original is null) original = area;
            else Assert.Equal(original, area);
            Assert.DoesNotContain("fwidth(", area, StringComparison.Ordinal);
            Assert.DoesNotContain("textureSample(", area, StringComparison.Ordinal);
            Assert.Contains("rounded_mask_alpha_local(", shader[(end + 1)..], StringComparison.Ordinal);
        }
    }
}
