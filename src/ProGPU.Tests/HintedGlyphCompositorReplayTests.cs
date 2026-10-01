using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

// Authored synthetic canonical records, not a native-font or Display oracle.
public sealed class HintedGlyphCompositorReplayTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void PhysicalCoverageIsDividedByTargetDpiExactlyOnce(float dpi)
    {
        using var geometry = CreateBox(dpi);
        using var window = CreateWindow(checked((uint)(64 * dpi)));
        window.Content = new HintedVisual(geometry, spatial: false, clip: false);
        window.RenderAtDpi(64, 64, dpi);
        byte[] cold = window.ReadPixels();
        Assert.True(window.Compositor.Atlas.CachedGlyphCount > 0);
        ulong uploads = window.Compositor.Atlas.OutlineUploadWriteCount;
        ulong rasterSubmissions = window.Compositor.Atlas.RasterBatchSubmissionCount;
        int left = (int)(8 * dpi), top = (int)(24 * dpi - 12);
        for (int y = top + 2; y < top + 10; y++)
        for (int x = left + 2; x < left + 10; x++)
            Assert.Equal(new Vector4(255, 0, 0, 255), ReadPixel(cold, window.Width, x, y));
        Assert.Equal(new Vector4(255), ReadPixel(cold, window.Width, left + 14, top + 6));
        Assert.Equal(new Vector4(255), ReadPixel(cold, window.Width, left + 6, top - 2));
        window.RenderAtDpi(64, 64, dpi);
        Assert.Equal(cold, window.ReadPixels());
        Assert.Equal(uploads, window.Compositor.Atlas.OutlineUploadWriteCount);
        Assert.Equal(rasterSubmissions, window.Compositor.Atlas.RasterBatchSubmissionCount);
    }

    [Fact]
    public void SpatialPaintUsesOriginalSourceDomainAndAppliesOpacityAndParentMaskOnce()
    {
        using var geometry = CreateBox(1f);
        using var window = CreateWindow();
        window.Content = new HintedVisual(geometry, spatial: true, clip: true);
        window.Render();
        byte[] cold = window.ReadPixels();
        Vector4 center = ReadPixel(cold, window.Width, 14, 18);
        // Full interior coverage: brush 1/2 times parent 1/2 over white.
        // Absolute-local red->blue endpoints span the original [0,64] frame,
        // not the padded glyph quad [4,24] or the physical ink [8,20].
        Assert.InRange(center.Y, 188f, 194f);
        Assert.True(center.X - center.Z > 20f, $"Original domain lost: {center}");
        Assert.Equal(255f, center.W);
        Assert.Equal(new Vector4(255), ReadPixel(cold, window.Width, 9, 13));
        Assert.Equal(new Vector4(255), ReadPixel(cold, window.Width, 25, 18));
        Assert.True(window.Compositor.Metrics.MaskRenderPassCount > 0);
        ulong rasterSubmissions = window.Compositor.Atlas.RasterBatchSubmissionCount;
        window.Render();
        Assert.Equal(cold, window.ReadPixels());
        Assert.Equal(rasterSubmissions, window.Compositor.Atlas.RasterBatchSubmissionCount);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void TightFractionalSourceDomainKeepsEveryOriginalCoveragePixel(float dpi)
    {
        using var geometry = CreateFractionalBox(dpi);
        using var solidWindow = CreateWindow(checked((uint)(64 * dpi)));
        using var gradientWindow = CreateWindow(checked((uint)(64 * dpi)));
        var color = new Vector4(0.25f, 0.625f, 0.875f, 1f);
        const float opacity = 0.625f;
        var solid = new SolidColorBrush(color) { Opacity = opacity };
        var gradient = new LinearGradientBrush(Vector2.Zero, Vector2.UnitX,
            [new GradientStop(color, 0), new GradientStop(color, 1)]) { Opacity = opacity };
        // The same original geometry, source position, tight source ink domain,
        // parent opacity and target frame differ only in paint representation.
        // Full equality includes every fractional coverage edge, not an interior
        // region or widened tolerance. This is a remaining qualification gate.
        GpuGlyphRecord record = geometry.RenderOutlines[0];
        Vector2 position = geometry.RenderOccurrences[0].Position;
        float inverseDpi = 1f / dpi;
        var tight = new Rect(position + new Vector2(record.MinX, -record.MaxY) * inverseDpi,
            new Vector2(record.MaxX - record.MinX, record.MaxY - record.MinY) * inverseDpi);
        solidWindow.Content = new TightFractionalVisual(geometry, tight, solid);
        gradientWindow.Content = new TightFractionalVisual(geometry, tight, gradient);
        for (int frame = 0; frame < 2; frame++)
        {
            solidWindow.RenderAtDpi(64, 64, dpi);
            gradientWindow.RenderAtDpi(64, 64, dpi);
            byte[] original = solidWindow.ReadPixels();
            Assert.Contains(original, value => value is > 0 and < 255);
            Assert.Equal(0, gradientWindow.Compositor.Metrics.MaskRenderPassCount);
            Assert.Contains(gradientWindow.Compositor.DrawCalls, draw => draw.Type == Compositor.DrawCallType.HintedGlyphPaint);
            Assert.Equal(original, gradientWindow.ReadPixels());
        }
    }

    [Fact]
    public void OverlappingOriginalOccurrencesKeepEveryOriginalTranslucentPaint()
    {
        using var geometry = CreateBox(1f, overlappingOccurrences: true);
        using var solidWindow = CreateWindow();
        using var gradientWindow = CreateWindow();
        var color = new Vector4(0.25f, 0.625f, 0.875f, 1f);
        var solid = new SolidColorBrush(color) { Opacity = 0.625f };
        var gradient = new LinearGradientBrush(Vector2.Zero, new Vector2(64, 0),
            [new GradientStop(color, 0), new GradientStop(color, 1)]) { Opacity = 0.625f };
        var sourceDomain = new Rect(0, 0, 64, 64);
        solidWindow.Content = new TightFractionalVisual(geometry, sourceDomain, solid);
        gradientWindow.Content = new TightFractionalVisual(geometry, sourceDomain, gradient);
        // The original two positioned draws share coverage identity, never paint
        // ownership. A unioned white mask followed by one translucent paint is
        // not equivalent, even at full interior coverage and infinite precision.
        for (int frame = 0; frame < 2; frame++)
        {
            solidWindow.Render();
            gradientWindow.Render();
            Assert.Equal(0, gradientWindow.Compositor.Metrics.MaskRenderPassCount);
            Assert.Equal(2u, Assert.Single(gradientWindow.Compositor.DrawCalls,
                draw => draw.Type == Compositor.DrawCallType.HintedGlyphPaint).IndexCount);
            Assert.Equal(solidWindow.ReadPixels(), gradientWindow.ReadPixels());
        }
    }

    [Theory]
    [InlineData(1f, 4.375f, 7.625f, 21f)]
    [InlineData(2f, 6.375f, 16.125f, 10.5f)]
    public void PrivateStorageUsesOriginalPaddedPhysicalBoundsWithoutChangingSourceRect(
        float dpi, float left, float top, float size)
    {
        using var geometry = CreateFractionalBox(dpi);
        Vector2 origin = new(1.25f, 2.5f);
        GpuGlyphRecord record = geometry.RenderOutlines[0];
        Vector2 position = geometry.RenderOccurrences[0].Position + origin;
        var originalDomain = new Rect(position + new Vector2(record.MinX, -record.MaxY) / dpi,
            new Vector2(record.MaxX - record.MinX, record.MaxY - record.MinY) / dpi);
        var command = new RenderCommand
        {
            Type = RenderCommandType.DrawHintedGlyphs,
            HintedGlyphGeometry = geometry,
            GlyphRangeCount = 1,
            Position = origin,
            Rect = originalDomain
        };
        Rect storage = Compositor.GetHintedPaintStorageBounds(command, geometry, 0, 1);
        Assert.Equal(new Rect(left + origin.X, top + origin.Y, size, size), storage);
        Assert.True(storage.X < originalDomain.X && storage.Y < originalDomain.Y);
        Assert.True(storage.Right > originalDomain.Right && storage.Bottom > originalDomain.Bottom);
        Assert.Equal(originalDomain, command.Rect);
        Assert.Equal(new Vector2(8.375f, 24.625f), geometry.RenderOccurrences[0].Position);
        Assert.Equal(0.375f, geometry.RenderOutlines[0].MinX);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TextureBrushStorageKeepsTheOriginalExplicitSampleMapping(bool extend)
    {
        using var texture = new GpuTexture(HeadlessWindow.Shared.Context, 16, 16,
            TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Hinted texture mapping test");
        var brush = new GpuTextureBrush
        {
            Texture = texture,
            SourceRect = new Rect(2, 4, 8, 8),
            DestinationRect = new Rect(4, 8, 16, 16),
            Transform = Matrix4x4.CreateScale(2, 2, 1) * Matrix4x4.CreateTranslation(6, 10, 0),
            SamplingMode = TextureSamplingMode.Nearest,
            AddressModeU = TextureAddressMode.Repeat,
            AddressModeV = TextureAddressMode.MirrorRepeat,
            ExtendToFillBounds = extend,
            Opacity = 0.625f
        };
        var sourceDomain = new Rect(8, 16, 16, 16);
        var storage = new Rect(4, 12, 32, 32);
        Assert.True(brush.TryCreateTextureCommand(sourceDomain, out RenderCommand original));
        Assert.True(brush.TryCreateTextureCommand(storage, out RenderCommand padded));
        Assert.Same(texture, padded.Texture);
        Assert.Equal(original.TextureSamplingMode, padded.TextureSamplingMode);
        Assert.Equal(original.TextureAddressModeU, padded.TextureAddressModeU);
        Assert.Equal(original.TextureAddressModeV, padded.TextureAddressModeV);
        Assert.Equal(original.TextureOpacity, padded.TextureOpacity);
        Assert.Equal(original.SnapTextureToPixels, padded.SnapTextureToPixels);
        if (extend)
        {
            foreach (Vector2 point in new[] { new Vector2(8, 16), new Vector2(16, 24), new Vector2(24, 32) })
                Assert.Equal(SampleTextureSource(original, point), SampleTextureSource(padded, point));
            Assert.Equal(storage, padded.Rect);
            Assert.True(padded.AllowExtendedTextureSourceRect);
        }
        else
        {
            Assert.Equal(original.Rect, padded.Rect);
            Assert.Equal(original.SrcRect, padded.SrcRect);
            Assert.Equal(brush.Transform, padded.Transform);
        }
        Assert.Equal(new Rect(2, 4, 8, 8), brush.SourceRect);
        Assert.Equal(new Rect(4, 8, 16, 16), brush.DestinationRect);
    }

    [Theory]
    [InlineData(1f, 9f, 12f, 12f)]
    [InlineData(2f, 8.5f, 12f, 12.5f)]
    public void TextureStorageExtensionBakesOnlyTheOriginalSourceDomainSnap(
        float dpi, float snappedLeft, float snappedTop, float snappedSize)
    {
        var sourceDomain = new Rect(8.375f, 11.875f, 12.25f, 12.25f);
        var sourceSamples = new Rect(2f, 4f, snappedSize, snappedSize);
        var storage = new Rect(4.375f, 7.625f, 21f, 21f);
        var transform = Matrix4x4.CreateTranslation(0.25f, 0.25f, 0);
        var original = new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Rect = sourceDomain,
            SrcRect = sourceSamples,
            TextureSamplingMode = TextureSamplingMode.Nearest,
            TextureAddressModeU = TextureAddressMode.Repeat,
            TextureAddressModeV = TextureAddressMode.MirrorRepeat,
            SnapTextureToPixels = true,
            AllowExtendedTextureSourceRect = true,
            TextureOpacity = 0.625f,
            HasTextureOpacity = true
        };
        Assert.True(TexturePaintMapping.TryExtendSnappedTextureCommand(original, storage,
            transform, dpi, out RenderCommand padded));
        Assert.Equal(storage, padded.Rect);
        Assert.False(padded.SnapTextureToPixels); // Original snap is already in its sample map.
        Assert.True(original.SnapTextureToPixels);
        Assert.Equal(sourceDomain, original.Rect);
        Assert.Equal(sourceSamples, original.SrcRect);
        Assert.Equal(original.TextureSamplingMode, padded.TextureSamplingMode);
        Assert.Equal(original.TextureAddressModeU, padded.TextureAddressModeU);
        Assert.Equal(original.TextureAddressModeV, padded.TextureAddressModeV);
        Assert.Equal(original.TextureOpacity, padded.TextureOpacity);
        foreach (Vector2 fraction in new[] { Vector2.Zero, new Vector2(0.5f), Vector2.One })
        {
            Vector2 world = new Vector2(snappedLeft, snappedTop) + fraction * snappedSize;
            Vector2 local = world - new Vector2(transform.M41, transform.M42);
            Assert.Equal(new Vector2(2, 4) + fraction * snappedSize,
                SampleTextureSource(padded, local));
        }
    }

    [Fact]
    public void StorageDoesNotInventPaintForAnOriginallyCollapsedSnappedTextureDomain()
    {
        var original = new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Rect = new Rect(8.125f, 12.125f, 0.125f, 0.125f),
            SrcRect = new Rect(0, 0, 16, 16),
            SnapTextureToPixels = true
        };
        Assert.False(TexturePaintMapping.TryExtendSnappedTextureCommand(original,
            new Rect(4, 8, 12, 12), Matrix4x4.Identity, 1f, out _));
        Assert.True(original.SnapTextureToPixels);
    }

    [Theory]
    [InlineData(TextureSamplingMode.Nearest, GpuTextureAlphaMode.Straight, true, false)]
    [InlineData(TextureSamplingMode.Linear, GpuTextureAlphaMode.Premultiplied, true, true)]
    [InlineData(TextureSamplingMode.Cubic, GpuTextureAlphaMode.Straight, true, true)]
    [InlineData(TextureSamplingMode.Nearest, GpuTextureAlphaMode.Straight, false, true)]
    [InlineData(TextureSamplingMode.Linear, GpuTextureAlphaMode.Straight, false, true)]
    [InlineData(TextureSamplingMode.Cubic, GpuTextureAlphaMode.Premultiplied, false, true)]
    public void DirectTexturePaintRetainsOriginalCornersSamplesAndRepresentation(
        TextureSamplingMode sampling, GpuTextureAlphaMode alphaMode, bool extend, bool snap)
    {
        using var geometry = CreateFractionalBox(2f);
        using var window = CreateWindow(128);
        using var texture = new GpuTexture(window.Context, 16, 16, TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst, "Original hinted paint representation", alphaMode: alphaMode);
        byte[] pixels = new byte[16 * 16 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 64; pixels[index + 1] = 128; pixels[index + 2] = 192; pixels[index + 3] = 255;
        }
        texture.WritePixels(pixels);
        Matrix4x4 brushTransform = extend
            ? Matrix4x4.CreateScale(0.75f, 0.5f, 1f) * Matrix4x4.CreateTranslation(1.125f, 2.375f, 0)
            : new Matrix4x4(1f, 0.25f, 0f, 0f, -0.125f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f, 1.125f, 2.375f, 0f, 1f);
        var brush = new GpuTextureBrush
        {
            Texture = texture, SourceRect = new Rect(2, 4, 8, 8), DestinationRect = new Rect(4, 8, 16, 16),
            Transform = brushTransform, SamplingMode = sampling, AddressModeU = TextureAddressMode.Repeat,
            AddressModeV = TextureAddressMode.MirrorRepeat, ExtendToFillBounds = extend,
            SnapToPixels = snap, Opacity = 0.625f
        };
        var sourceDomain = new Rect(0, 0, 64, 64);
        var effective = Matrix4x4.CreateTranslation(0.25f, 0.125f, 0);
        window.Content = new DirectPaintVisual(geometry, sourceDomain, brush, effective);
        window.RenderAtDpi(64, 64, 2f);
        Assert.Equal(0, window.Compositor.Metrics.MaskRenderPassCount);
        Assert.Contains(window.Compositor.DrawCalls, draw => draw.Type == Compositor.DrawCallType.HintedGlyphPaint);
        GpuHintedGlyphPaint record = Assert.Single(window.Compositor.HintedGlyphPaintRecords.ToArray());
        Assert.Equal(GpuHintedGlyphPaint.TextureMaterial, record.Kind);
        Assert.Equal(0u, record.BrushIndex);
        Assert.Equal(0u, record.Reserved);
        Assert.Equal(new Vector4(0.25f, 0.125f, 0.625f * 0.75f, 0f), record.SourceOffsetOpacity);
        Assert.Equal((uint)sampling, record.Flags >> GpuHintedGlyphPaint.SamplingModeShift);
        Assert.Equal(alphaMode == GpuTextureAlphaMode.Premultiplied,
            (record.Flags & GpuHintedGlyphPaint.PremultipliedTexture) != 0);
        Assert.Equal(0u, record.Flags & 8u);
        Assert.Equal(!extend, (record.Flags & GpuHintedGlyphPaint.BoundedTexture) != 0);
        Assert.Equal(sampling == TextureSamplingMode.Cubic, (record.Flags & GpuHintedGlyphPaint.CubicTexture) != 0);
        Assert.Equal((float)TextureAddressMode.Repeat, record.Sampling.Z);
        Assert.Equal((float)TextureAddressMode.MirrorRepeat, record.Sampling.W);
        Assert.True(brush.TryCreateTextureCommand(sourceDomain, out RenderCommand original));
        Assert.Equal(new Vector4(original.SrcRect.X / 16f, original.SrcRect.Y / 16f,
            original.SrcRect.Right / 16f, original.SrcRect.Bottom / 16f), record.UVBounds);
        Matrix4x4 originalTransform = extend ? effective : original.Transform * effective;
        Vector2[] originalCorners =
        [new(original.Rect.X, original.Rect.Y), new(original.Rect.Right, original.Rect.Y),
         new(original.Rect.Right, original.Rect.Bottom), new(original.Rect.X, original.Rect.Bottom)];
        for (int index = 0; index < originalCorners.Length; index++)
        {
            originalCorners[index] = Vector2.Transform(originalCorners[index], originalTransform);
            if (snap) originalCorners[index] = new Vector2(MathF.Round(originalCorners[index].X * 2f) / 2f,
                MathF.Round(originalCorners[index].Y * 2f) / 2f);
        }
        Assert.Equal(new Vector4(originalCorners[0], originalCorners[1].X, originalCorners[1].Y), record.TextureQuad01);
        Assert.Equal(new Vector4(originalCorners[2], originalCorners[3].X, originalCorners[3].Y), record.TextureQuad23);
        byte[] cold = window.ReadPixels();
        ulong rasterSubmissions = window.Compositor.Atlas.RasterBatchSubmissionCount;
        window.RenderAtDpi(64, 64, 2f);
        Assert.Equal(cold, window.ReadPixels());
        Assert.Equal(rasterSubmissions, window.Compositor.Atlas.RasterBatchSubmissionCount);
        Assert.Equal(brushTransform, brush.Transform);
    }

    [Fact]
    public void DirectPaintWireKeepsOriginalInstanceAndLazyFailureOwnership()
    {
        Assert.Equal(96, Marshal.SizeOf<GpuHintedGlyphPaint>());
        Assert.Equal(16, Marshal.OffsetOf<GpuHintedGlyphPaint>(nameof(GpuHintedGlyphPaint.SourceOffsetOpacity)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<GpuHintedGlyphPaint>(nameof(GpuHintedGlyphPaint.UVBounds)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<GpuHintedGlyphPaint>(nameof(GpuHintedGlyphPaint.TextureQuad01)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<GpuHintedGlyphPaint>(nameof(GpuHintedGlyphPaint.TextureQuad23)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<GpuHintedGlyphPaint>(nameof(GpuHintedGlyphPaint.Sampling)).ToInt32());
        Assert.Equal(96, Marshal.SizeOf<GlyphInstance>());
        Assert.Equal(92, Marshal.OffsetOf<GlyphInstance>(nameof(GlyphInstance.Padding)).ToInt32());
        uint exactIndex = 0x01000001;
        Assert.Equal(exactIndex, BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(exactIndex)));
        string direct = ReadSource("ProGPU.Scene", "Compositor.HintedGlyphPaint.cs");
        Assert.Contains("Offset = 92, ShaderLocation = 8", direct);
        Assert.Contains("Format = VertexFormat.Uint32", direct);
        Assert.Contains("if (_hintedGlyphPaints is not { Count: > 0 }) return", direct);
        Assert.Contains("_hintedGlyphPaintRetiringBuffer = _hintedGlyphPaintBuffer", direct);
        Assert.Contains("_hintedGlyphPaintBuffer = replacement", direct);
        Assert.Contains("try { _context.Api.BindGroupRelease(replacement); }", direct);
        Assert.Contains("HintedGlyphPaintSetupCleanupFailure", direct);
        Assert.Contains("alphaMode == GpuTextureAlphaMode.Premultiplied || BlendModeRequiresPremultipliedSource", direct);
        Assert.DoesNotContain("RentMaskTexture", direct);
    }

    [Fact]
    public void AllOriginalNoInkOccurrencesDoNotAllocateCoverageOrSpatialMask()
    {
        var owner = new CountingOwner();
        using var geometry = new HintedGlyphGeometry(1f, [], [],
            [Occurrence(uint.MaxValue)], owner);
        using var window = CreateWindow();
        window.Content = new HintedVisual(geometry, spatial: true, clip: false);
        window.Render();
        Assert.Equal(0, window.Compositor.Atlas.CachedGlyphCount);
        Assert.Equal(0, window.Compositor.Metrics.MaskRenderPassCount);
        byte[] pixels = window.ReadPixels();
        Assert.All(pixels, value => Assert.Equal((byte)255, value));
    }

    [Fact]
    public void StaticDxfRejectsHintedAtlasUvPublication()
    {
        using var geometry = CreateBox(1f);
        using var window = CreateWindow();
        var context = new DrawingContext();
        try
        {
            context.DrawHintedGlyphs(geometry, Vector2.Zero, new Rect(0, 0, 64, 64),
                new SolidColorBrush(new Vector4(1, 0, 0, 1)));
            Assert.Throws<NotSupportedException>(() => window.Compositor.CompileStaticDxf(context));
            Assert.Equal(0, window.Compositor.Atlas.CachedGlyphCount);
            // Exercise the separate legacy List overload as well as DrawingContext.
            var commands = new List<RenderCommand>(context.Commands);
            Assert.Throws<NotSupportedException>(() => window.Compositor.CompileStaticDxf(commands));
            Assert.Equal(0, window.Compositor.Atlas.CachedGlyphCount);
        }
        finally { context.Clear(); }
    }

    [Fact]
    public void ManagedAdmissionAndDirectPaintFailureOwnershipRemainBeforePublication()
    {
        string replay = ReadSource("ProGPU.Scene", "Compositor.HintedGlyphs.cs");
        int validation = replay.IndexOf("HintedGlyphCommandGeometry.TryValidate(", StringComparison.Ordinal);
        int noInk = replay.IndexOf("!hasInk", StringComparison.Ordinal);
        int retain = replay.IndexOf("RetainHintedGeometryForCompiledReplay(geometry);", StringComparison.Ordinal);
        int record = replay.IndexOf("_compiledTextRecords.Add", StringComparison.Ordinal);
        Assert.True(validation >= 0 && validation < noInk && noInk < retain && retain < record);
        Assert.Contains("geometry.RetainForReplay()", replay);
        Assert.Contains("ReferenceEquals(retained.Identity, geometry)", replay);
        Assert.Contains("ActiveCompilationContext?.RetainedGlyphBuilder != null", replay);
        Assert.Contains("command.Brush is not SolidColorBrush", replay);
        Assert.Contains("CreateHintedTexturePaint(textureBrush, command.Rect, transform)", replay);
        Assert.Contains("brush.TryCreateTextureCommand(originalSourceDomain", replay);
        Assert.Contains("Type = DrawCallType.HintedGlyphPaint", replay);
        Assert.Contains("BitConverter.UInt32BitsToSingle(paintIndex.Value)", replay);
        Assert.DoesNotContain("PushHintedGlyphCoverageMask", replay);
        Assert.DoesNotContain("RentMaskTexture", replay);
        Assert.DoesNotContain("CompileRectCommand(", replay);
        Assert.Contains("new Vector4(1f, 0f, 0f", replay);
        Assert.Contains("BearSize = new Vector4(info.BearX, info.BearY, info.Width, info.Height)", replay);
        Assert.Contains("_textVerticesList.RemoveRange(textStart", replay);
        Assert.Contains("_hintedGlyphPaints.RemoveAt(checked((int)paintIndex))", replay);
        Assert.Contains("_pendingTextStart = checked((uint)_textVerticesList.Count)", replay);
        Assert.Contains("separateDestinationComposites", replay);
        Assert.DoesNotContain("TtfFont", replay);
        Assert.DoesNotContain("GetGlyphIndex", replay);
        int instanceStart = replay.IndexOf("private void CompileHintedGlyphAtlasInstances", StringComparison.Ordinal);
        Assert.DoesNotContain("Snap", replay[instanceStart..].Replace("SnappedLogicalPos", "", StringComparison.Ordinal));
    }

    [Fact]
    public void AbandonedRasterHasReusableTicketsAndScopedNativeHandles()
    {
        string hinted = ReadSource("ProGPU.Text", "GlyphAtlas.HintedGeometry.cs");
        string atlas = ReadSource("ProGPU.Text", "GlyphAtlas.cs");
        string gpu = ReadSource("ProGPU.Text", "GlyphAtlas.GpuRasterizer.cs");
        Assert.Contains("internal GlyphInfo GetOrCreateHintedGlyph", hinted);
        Assert.Contains("ReserveRasterFailureTickets();", hinted);
        Assert.Contains("TryReuseFailedRasterRegion", hinted);
        Assert.Contains("ReturnFailedRasterRegion(cached.Info)", hinted);
        Assert.Contains("ReturnFailedRasterRegion(reservation)", hinted);
        Assert.Contains("AbandonPendingRaster(key, reservation)", hinted);
        Assert.Contains("if (removed) Generation++", hinted);
        Assert.Contains("_hintedGpuSlots.Clear()", hinted);
        Assert.Contains("_fontGpuData.Clear()", hinted);
        Assert.DoesNotContain("ComputePassEncoderEnd", hinted);
        Assert.DoesNotContain("_context.Submit", hinted);
        Assert.DoesNotContain("ClearRenderTarget", hinted);
        Assert.Contains("_hintedGpuSlots.Remove(candidateKey)", atlas);
        Assert.Contains("finally { _context.Api.CommandBufferRelease(cmdBuffer); }", atlas);
        Assert.Contains("if (commandBuffer != null) _context.Api.CommandBufferRelease(commandBuffer)", gpu);
        Assert.Contains("if (encoder != null) _context.Api.CommandEncoderRelease(encoder)", gpu);
        Assert.Contains("if (bindGroup != null) _context.Api.BindGroupRelease(bindGroup)", gpu);
        Assert.DoesNotContain("WaitIdle", gpu);
    }

    [Fact]
    public void FrameAndCompiledSceneRetirementDrainAllOwnersAndKeepTheFirstFailure()
    {
        var firstFailure = new InvalidOperationException("original retained release failure");
        var first = new FaultOwner(firstFailure);
        var second = new FaultOwner(new InvalidOperationException("later retained release failure"));
        var last = new CountingOwner();
        List<RetainedResourceLease> leases =
        [RetainedResourceLease.Create(first), RetainedResourceLease.Create(second), RetainedResourceLease.Create(last)];
        Exception actual = Assert.Throws<InvalidOperationException>(() => RetainedResourceLease.DisposeAll(leases));
        Assert.Same(firstFailure, actual);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, last.DisposeCount);
        RetainedResourceLease.DisposeAll(leases); // Reference lease end is idempotent.
        Assert.Equal(1, last.DisposeCount);

        string compositor = ReadSource("ProGPU.Scene", "Compositor.cs");
        Assert.Contains("RetainedResourceLease.DisposeAll(_frameRetainedResources)", compositor);
        Assert.Contains("finally { _frameRetainedResources.Clear(); }", compositor);
        Assert.Contains("RetainedResourceLease.DisposeAll(_compiledSceneRetainedResources)", compositor);
        Assert.Contains("finally { _compiledSceneRetainedResources.Clear(); }", compositor);
        Assert.Contains("ReleaseFrameRetainedResourcesPreserving(frameFailure)", compositor);
        Assert.Contains("ReleaseFrameRetainedResourcesPreserving(offscreenFailure)", compositor);
        Assert.Contains("RetainedFrameResourceCleanupFailure", compositor);
        Assert.Contains("ReleaseAllRetainedResources();", compositor);
    }

    private static HeadlessWindow CreateWindow(uint physicalSize = 64) => new(physicalSize, physicalSize,
        CompositorOptions.Default with
        {
            EnableGpuHitTesting = false,
            EnableCompiledSceneCache = true,
            EnableIncrementalScenePages = false,
            EnableRetainedCompositionPictures = false
        });

    private sealed class HintedVisual(HintedGlyphGeometry geometry, bool spatial, bool clip) : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 128, 128));
            if (clip) context.PushGeometryClip(PrimitivePathGeometry.CreateEllipse(new Vector2(14, 18), 5, 4));
            Brush brush;
            if (spatial)
            {
                context.PushOpacity(0.5f);
                brush = new LinearGradientBrush(Vector2.Zero, new Vector2(64, 0),
                    [new GradientStop(new Vector4(1, 0, 0, 1), 0),
                     new GradientStop(new Vector4(0, 0, 1, 1), 1)]) { Opacity = 0.5f };
            }
            else brush = new SolidColorBrush(new Vector4(1, 0, 0, 1));
            context.DrawHintedGlyphs(geometry, Vector2.Zero, new Rect(0, 0, 64, 64), brush);
            if (spatial) context.PopOpacity();
            if (clip) context.PopGeometryClip();
        }
    }

    private sealed class TightFractionalVisual(HintedGlyphGeometry geometry, Rect inkBounds, Brush brush) : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 64, 64));
            context.PushOpacity(0.75f);
            context.DrawHintedGlyphs(geometry, Vector2.Zero, inkBounds, brush);
            context.PopOpacity();
        }
    }

    private sealed class DirectPaintVisual(HintedGlyphGeometry geometry, Rect inkBounds,
        Brush brush, Matrix4x4 transform) : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 64, 64));
            context.PushOpacity(0.75f);
            context.DrawHintedGlyphs(geometry, Vector2.Zero, inkBounds, brush, transform);
            context.PopOpacity();
        }
    }

    private static HintedGlyphGeometry CreateFractionalBox(float dpi)
    {
        Vector2 low = new(0.375f, 0.125f), high = new(12.625f, 12.875f);
        return new HintedGlyphGeometry(dpi,
            [new GpuGlyphRecord { SegmentCount = 4, MinX = low.X, MinY = low.Y, MaxX = high.X, MaxY = high.Y }],
            [Line(low, new(high.X, low.Y)), Line(new(high.X, low.Y), high),
             Line(high, new(low.X, high.Y)), Line(new(low.X, high.Y), low)],
            [new HintedGlyphOccurrence(0, 0, 7, 0, 0, 0, 0, 0, 0, 1, 0,
                new Vector2(8.375f, 24.625f), new Vector2(12.25f, 0))], new CountingOwner());
    }

    private static HintedGlyphGeometry CreateBox(float dpi, bool overlappingOccurrences = false) => new(dpi,
        [new GpuGlyphRecord { SegmentCount = 4, MinX = 0, MinY = 0, MaxX = 12, MaxY = 12 }],
        [Line(new(0, 0), new(12, 0)), Line(new(12, 0), new(12, 12)),
         Line(new(12, 12), new(0, 12)), Line(new(0, 12), new(0, 0))],
        overlappingOccurrences
            ? [Occurrence(0), new HintedGlyphOccurrence(1, 1, 7, 0, 0, 1, 0, 0, 1, 2, 0,
                new Vector2(8, 24), new Vector2(12, 0))]
            : [Occurrence(0)], new CountingOwner());

    private static HintedGlyphOccurrence Occurrence(uint outline) => new(
        0, 0, 7, 0, 0, 0, 0, outline, 0, 1, 0, new Vector2(8, 24), new Vector2(12, 0));

    private static GpuSegment Line(Vector2 from, Vector2 to) => new()
    {
        P0 = from, P1 = to, SegmentType = 0
    };

    private static Vector2 SampleTextureSource(RenderCommand command, Vector2 point) =>
        new Vector2(command.SrcRect.X, command.SrcRect.Y) +
        (point - new Vector2(command.Rect.X, command.Rect.Y)) *
        new Vector2(command.SrcRect.Width / command.Rect.Width, command.SrcRect.Height / command.Rect.Height);

    private sealed class CountingOwner : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class FaultOwner(Exception failure) : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() { DisposeCount++; throw failure; }
    }

    private static Vector4 ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        int offset = checked((y * (int)width + x) * 4);
        return new Vector4(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private static string ReadSource(string project, string file)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", project, file);
            if (File.Exists(candidate)) return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        throw new FileNotFoundException($"Could not locate {project}/{file}.");
    }
}
