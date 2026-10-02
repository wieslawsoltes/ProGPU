using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public class NativeRgbGlyphWireTests
{
    [Theory]
    [InlineData(0U)] [InlineData(1U)] [InlineData(2U)]
    public void ExplicitPolicyRetainsEachOriginalPhysicalOccurrence(uint geometry)
    {
        byte[] destination = new byte[4096];
        var builder = new NativeSceneStreamBuilder(destination, 0x9680, 1, 1, 1);
        AddGlyph(ref builder, out uint resource);
        var policy = Policy(geometry);
        var glyphs = Tiles();
        byte[] originalGlyphs = MemoryMarshal.AsBytes(glyphs.AsSpan()).ToArray();
        Assert.True(builder.TryDrawRgbGlyphRun(1, resource, new(0, 0, 32, 32), policy, glyphs));
        glyphs[0] = default;
        Assert.True(builder.TryBuild(out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var command = MemoryMarshal.Read<NativeMethods.SceneCommand>(stream[(int)header.CommandOffset..]);
        Assert.Equal(NativeSceneCommandKind.DrawRgbGlyphRun, command.Kind);
        Assert.Equal(152U, command.PayloadSize);
        var retained = MemoryMarshal.Read<NativeSceneRgbGlyphDraw>(stream[(int)command.PayloadOffset..]);
        Assert.Equal(policy, retained);
        Assert.True(originalGlyphs.AsSpan().SequenceEqual(stream.Slice((int)command.PayloadOffset + 40, 112)));
        Assert.Equal(40, Unsafe.SizeOf<NativeSceneRgbGlyphDraw>());
        Assert.Equal(56, Unsafe.SizeOf<NativeSceneRgbGlyphTile>());
        Assert.Equal(40, Marshal.OffsetOf<NativeSceneRgbGlyphTile>(nameof(NativeSceneRgbGlyphTile.Foreground)).ToInt32());
    }

    [Fact]
    public void InvalidPolicyOrLaterOccurrenceDoesNotTouchCallerStorage()
    {
        byte[] destination = new byte[4096];
        var builder = new NativeSceneStreamBuilder(destination, 0x9681, 1, 1, 1);
        AddGlyph(ref builder, out uint resource);
        byte[] before = (byte[])destination.Clone();
        var glyphs = Tiles();
        foreach (var policy in new[] {
            default(NativeSceneRgbGlyphDraw),
            new(2, (NativeRgbGlyphFilter)0, 1, 1, 0, 1, 1),
            new(2, NativeRgbGlyphFilter.FullPixelBox8X8, 3, 1, 0, 1, 1),
            new(2, NativeRgbGlyphFilter.FullPixelBox8X8, 1, 2.2f, 0, 1, 1),
            new(2, NativeRgbGlyphFilter.FullPixelBox8X8, 1, 1, .5f, 1, 1),
            new(2, NativeRgbGlyphFilter.FullPixelBox8X8, 1, 1, 0, .5f, 1),
            new(2, NativeRgbGlyphFilter.FullPixelBox8X8, 1, 1, 0, 1, float.NaN) })
        {
            Assert.False(builder.TryDrawRgbGlyphRun(1, resource, new(0, 0, 32, 32), policy, glyphs));
            Assert.Equal(before, destination);
        }
        var valid = Policy(1);
        foreach (var invalid in new[] {
            new NativeSceneRgbGlyphTile(1, 8, 8, -1, -5, 1, 0, 3, 5, Vector4.One),
            new(0, 0, 8, -1, -5, 1, 0, 3, 5, Vector4.One),
            new(0, 8, 8, -1, -5, float.Epsilon, 0, 3, 5, Vector4.One),
            new(0, 8, 8, -1, -5, 1, 0, -4097, 5, Vector4.One),
            new(0, 8, 8, -1, -5, 1, 0, 3, 5, new(0, 0, 0, float.NaN)) })
        {
            glyphs[1] = invalid;
            Assert.False(builder.TryDrawRgbGlyphRun(1, resource, new(0, 0, 32, 32), valid, glyphs));
            Assert.Equal(before, destination);
        }
        Assert.True(builder.TryDrawRgbGlyphRun(1, resource, new(0, 0, 32, 32), valid, Tiles()));
    }

    private static NativeSceneRgbGlyphDraw Policy(uint geometry) =>
        new(2, NativeRgbGlyphFilter.FullPixelBox8X8, geometry, 1, 0, 1, 1.25f);

    private static NativeSceneRgbGlyphTile[] Tiles() => new[] {
        new NativeSceneRgbGlyphTile(0, 8, 8, -1, -5, 1, .25f, 3, 5, new(1, 0, 0, 1)),
        new(0, 8, 8, -2, -5, 1.25f, .75f, 9, 7, new(0, 0, 1, .5f)) };

    private static void AddGlyph(ref NativeSceneStreamBuilder builder, out uint resource)
    {
        NativeSceneGlyphOutline[] outlines = { new(0, 4, Vector2.Zero, new(4, 4), 1) };
        NativePathSegment[] segments = {
            new(NativePathSegmentKind.Line, new(0, 0), new(4, 0)),
            new(NativePathSegmentKind.Line, new(4, 0), new(4, 4)),
            new(NativePathSegmentKind.Line, new(4, 4), new(0, 4)),
            new(NativePathSegmentKind.Line, new(0, 4), new(0, 0)) };
        Assert.True(builder.TryAddGlyphResource(1, 1, outlines, segments, out resource));
    }
}
