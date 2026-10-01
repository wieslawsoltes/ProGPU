using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

// Synthetic protocol controls only. Authentic producer/font/coverage and
// provider pixel comparisons remain separate hosted qualification gates.
public class NativeHintedGlyphPaintWireTests
{
    [Theory]
    [InlineData(0U)] [InlineData(1U)] [InlineData(2U)] [InlineData(3U)]
    [InlineData(4U)] [InlineData(5U)] [InlineData(6U)] [InlineData(7U)]
    [InlineData(8U)] [InlineData(9U)]
    public void OriginalTextureModesKeepExact96BytePaintAndEveryOccurrence(uint mode)
    {
        byte[] destination = new byte[4096];
        var builder = new NativeSceneStreamBuilder(destination, 9600, 1, 1, 2);
        AddGlyph(ref builder, out uint glyphResource);
        Assert.True(builder.TryAddExternalImageResource(2, 7, out uint imageResource));
        NativeSceneGlyphPaint paint = TexturePaint(mode);
        NativePositionedGlyph[] glyphs = Glyphs();
        Assert.True(builder.TryDrawPaintedGlyphRun(1, glyphResource, new(1, 2, 20, 30),
            glyphs, imageResource, 0, paint, NativeSceneTextRenderingMode.Aliased));
        Assert.True(builder.TryBuild(out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var command = MemoryMarshal.Read<NativeMethods.SceneCommand>(stream[(int)header.CommandOffset..]);
        Assert.Equal(NativeSceneCommandKind.DrawPaintedGlyphRun, command.Kind);
        Assert.Equal(128 + glyphs.Length * Unsafe.SizeOf<NativePositionedGlyph>(), (int)command.PayloadSize);
        var prefix = MemoryMarshal.Read<NativeScenePaintedGlyphDraw>(stream[(int)command.PayloadOffset..]);
        Assert.Equal(2U, prefix.GlyphCount);
        Assert.Equal((uint)NativeSceneTextRenderingMode.Aliased, prefix.RenderingMode);
        Assert.Equal(imageResource, prefix.PaintResourceIndex);
        var retained = MemoryMarshal.Read<NativeSceneGlyphPaint>(stream[((int)command.PayloadOffset + 32)..]);
        Assert.Equal(paint.Flags, retained.Flags);
        Assert.Equal(paint.SourceOffsetOpacity, retained.SourceOffsetOpacity);
        Assert.Equal(paint.UVBounds, retained.UVBounds);
        Assert.Equal(paint.TextureQuad01, retained.TextureQuad01);
        Assert.Equal(paint.TextureQuad23, retained.TextureQuad23);
        Assert.Equal(paint.Sampling, retained.Sampling);
        Assert.True(MemoryMarshal.AsBytes(glyphs.AsSpan()).SequenceEqual(
            stream.Slice((int)command.PayloadOffset + 128, glyphs.Length * Unsafe.SizeOf<NativePositionedGlyph>())));
        Assert.Equal(96, Unsafe.SizeOf<NativeSceneGlyphPaint>());
        Assert.Equal(32, Unsafe.SizeOf<NativeScenePaintedGlyphDraw>());
        Assert.Equal(80, Marshal.OffsetOf<NativeSceneGlyphPaint>(nameof(NativeSceneGlyphPaint.Sampling)).ToInt32());
    }

    [Fact]
    public void UnknownPaintPoliciesAndFailedPublicationPreserveDestinationAndFollowingDraw()
    {
        byte[] destination = new byte[4096];
        var builder = new NativeSceneStreamBuilder(destination, 9601, 1, 1, 2);
        AddGlyph(ref builder, out uint glyphResource);
        Assert.True(builder.TryAddExternalImageResource(2, 7, out uint imageResource));
        NativePositionedGlyph[] glyphs = Glyphs();
        byte[] original = (byte[])destination.Clone();
        foreach (uint flags in new[] { 0x10U, 10U << 8, NativeSceneGlyphPaint.CubicTexture,
            8U | NativeSceneGlyphPaint.PremultipliedTexture })
        {
            var invalid = new NativeSceneGlyphPaint(NativeSceneGlyphPaint.Texture, 0, flags,
                new(0, 0, .5f, 0), new(0, 0, 1, 1), sampling: new(0, .5f, 0, 0));
            Assert.False(builder.TryDrawPaintedGlyphRun(1, glyphResource, new(0, 0, 10, 10),
                glyphs, imageResource, 0, invalid));
            Assert.Equal(original, destination);
        }
        var valid = TexturePaint(0);
        Assert.False(builder.TryDrawPaintedGlyphRun(1, uint.MaxValue, new(0, 0, 10, 10), glyphs, imageResource, 0, valid));
        Assert.Equal(original, destination);
        Assert.False(builder.TryDrawPaintedGlyphRun(1, glyphResource, new(0, 0, 10, 10),
            glyphs, imageResource, 0, valid, NativeSceneTextRenderingMode.ClearType));
        Assert.Equal(original, destination);
        Assert.True(builder.TryDrawPaintedGlyphRun(1, glyphResource, new(0, 0, 10, 10), glyphs, imageResource, 0, valid));
        Assert.True(builder.TryBuild(out var stream));
        Assert.Equal(1U, MemoryMarshal.Read<NativeMethods.SceneHeader>(stream).CommandCount);
    }

    [Theory]
    [InlineData(0U, -64f, true)] [InlineData(1U, -128f, true)]
    [InlineData(1U, -64f, false)] [InlineData(0U, -128f, false)]
    [InlineData(0U, -65f, false)] [InlineData(3U, -64f, false)]
    public void ExplicitSamplingSentinelsRetainOnlyTheirOriginalModePair(uint mode, float coefficient, bool admitted)
    {
        byte[] destination = new byte[4096];
        var builder = new NativeSceneStreamBuilder(destination, 9602, 1, 1, 2);
        AddGlyph(ref builder, out uint glyphResource);
        Assert.True(builder.TryAddExternalImageResource(2, 7, out uint imageResource));
        var paint = new NativeSceneGlyphPaint(NativeSceneGlyphPaint.Texture, 0, mode << 8,
            new(0, 0, .5f, 0), new(0, 0, 1, 1), sampling: new(coefficient, .5f, 0, 0));
        Assert.Equal(admitted, builder.TryDrawPaintedGlyphRun(1, glyphResource,
            new(0, 0, 10, 10), Glyphs(), imageResource, 0, paint));
    }

    private static NativeSceneGlyphPaint TexturePaint(uint mode) => new(NativeSceneGlyphPaint.Texture, 0,
        NativeSceneGlyphPaint.BoundedTexture | (mode << 8) |
            (mode == 2 ? NativeSceneGlyphPaint.CubicTexture : 0),
        new(3.25f, -2.5f, .625f, 0), new(-.25f, .125f, 1.25f, .875f),
        new(1.25f, 2.5f, 9.75f, 3.5f), new(8.25f, 12.5f, .5f, 11.25f), new(0, .5f, 1, 2));

    private static NativePositionedGlyph[] Glyphs() => new[]
    {
        new NativePositionedGlyph(0, new(1.25f, 2.5f), Vector2.UnitX, -Vector2.UnitY, Vector4.One, .5f),
        new NativePositionedGlyph(0, new(1.25f, 2.5f), Vector2.UnitX, -Vector2.UnitY, Vector4.One, .5f)
    };

    private static void AddGlyph(ref NativeSceneStreamBuilder builder, out uint resource)
    {
        NativeSceneGlyphOutline[] outlines = { new(0, 4, Vector2.Zero, new(4, 4), 1) };
        NativePathSegment[] segments =
        {
            new(NativePathSegmentKind.Line, new(0, 0), new(4, 0)),
            new(NativePathSegmentKind.Line, new(4, 0), new(4, 4)),
            new(NativePathSegmentKind.Line, new(4, 4), new(0, 4)),
            new(NativePathSegmentKind.Line, new(0, 4), new(0, 0))
        };
        Assert.True(builder.TryAddGlyphResource(1, 7, outlines, segments, out resource));
    }
}
