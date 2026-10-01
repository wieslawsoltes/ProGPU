using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

// Synthetic neutral geometry tests only the compiler's pointer-free wire,
// source range, paint and scope contracts. It is NOT a fabricated paragraph,
// a font/hinting positive, GPU pixel evidence or source Display admission.
public sealed class RetainedHintedGlyphNativeCompilerTests
{
    private static readonly Rect SourceDomain = new(10, 15, 30, 20);
    private static readonly Vector2 Origin = new(5, 6);

    [Fact]
    public void SolidReplayKeepsOriginalPhysicalRecordsAndEverySelectedInkOccurrence()
    {
        var owner = new Owner();
        using var geometry = Geometry(owner);
        var recorder = new GpuPictureRecorder();
        var drawing = recorder.BeginRecording(new Rect(0, 0, 80, 80));
        drawing.DrawHintedGlyphs(geometry, Origin, SourceDomain,
            new SolidColorBrush(new Vector4(.2f, .4f, .8f, .5f)) { Opacity = .75f },
            Matrix4x4.CreateTranslation(7, 11, 0));
        using var picture = recorder.EndRecording();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 991, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        Assert.Equal(NativePictureCommandCapability.DirectDraw,
            GpuPictureNativeSceneCompiler.GetCommandCapability(RenderCommandType.DrawHintedGlyphs));
        Assert.Equal(1, compiled.GlyphOutlineCount);
        Assert.Equal(4, compiled.GlyphSegmentCount);
        Assert.Equal(2, compiled.PositionedGlyphCount); // Repeated ink slots never collapse owners.
        var resources = Resources(compiled.Stream);
        var glyphResource = resources.Single(x => x.Kind == NativeSceneResourceKind.GlyphRun);
        var outline = MemoryMarshal.Read<NativeSceneGlyphOutline>(compiled.Stream.Slice((int)glyphResource.PayloadOffset));
        Assert.Equal(new Vector2(-2, 3), outline.Minimum);
        Assert.Equal(new Vector2(4, 9), outline.Maximum); // Still original Y-up, not re-flipped.
        Assert.Equal(1f, outline.RasterScale);
        Assert.Equal(0f, outline.SubpixelX);
        var wireSegments = MemoryMarshal.Cast<byte, NativePathSegment>(compiled.Stream.Slice(
            (int)glyphResource.AuxiliaryOffset, (int)glyphResource.AuxiliarySize));
        Assert.Equal(new Vector2(-2, 3), wireSegments[0].P0);
        Assert.Equal(new Vector2(4, 3), wireSegments[0].P1);
        var draw = Commands(compiled.Stream).Single(x => x.Kind == NativeSceneCommandKind.DrawGlyphRun);
        var positioned = MemoryMarshal.Cast<byte, NativePositionedGlyph>(compiled.Stream.Slice(
            (int)draw.PayloadOffset + 24, 2 * Unsafe.SizeOf<NativePositionedGlyph>()));
        Assert.Equal(new Vector2(24, 36), positioned[0].Position);
        Assert.Equal(new Vector2(34, 36), positioned[1].Position);
        Assert.All(positioned.ToArray(), g =>
        {
            Assert.Equal(0U, g.OutlineIndex);
            Assert.Equal(Vector2.UnitX, g.BasisX);
            Assert.Equal(Vector2.UnitY, g.BasisY);
            Assert.Equal(1f, g.AtlasToLogicalScale);
            Assert.Equal(0f, g.BoldOffset);
            Assert.Equal(0f, g.ItalicSkew);
        });
        var styleResource = resources.Single(x => x.Kind == NativeSceneResourceKind.TextStyleTable);
        var style = MemoryMarshal.Read<NativeSceneTextStyle>(compiled.Stream.Slice((int)styleResource.PayloadOffset));
        Assert.Equal(new Vector4(.2f, .4f, .8f, .375f), style.Color);
        Assert.Equal(3, geometry.OccurrenceCount); // The no-ink source owner is still retained.
        Assert.Equal(uint.MaxValue, geometry.Occurrences[1].OutlineIndex);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 1, 1)]
    public void ExplicitRangeDoesNotUseGlyphIdLookupOrTreatZeroCountAsWholeParagraph(int start, int count, int draws)
    {
        using var geometry = Geometry(new Owner());
        var command = Command(geometry, new SolidColorBrush(Vector4.One));
        command.GlyphRangeStart = start; command.GlyphRangeCount = count;
        using var picture = Picture(command);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 992, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        Assert.Equal(draws, compiled.NativeDrawCount);
        Assert.Equal(draws, compiled.PositionedGlyphCount);
        if (draws != 0)
        {
            var draw = Commands(compiled.Stream).Single(x => x.Kind == NativeSceneCommandKind.DrawGlyphRun);
            var glyph = MemoryMarshal.Read<NativePositionedGlyph>(compiled.Stream.Slice((int)draw.PayloadOffset + 24));
            Assert.Equal(new Vector2(27, 25), glyph.Position);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpatialPaintKeepsEachDestinationCompositeAndOriginalCallerDomainWithOuterState(bool rotatedMask)
    {
        using var geometry = Geometry(new Owner());
        var gradient = new LinearGradientBrush(new Vector2(0, 0), new Vector2(1, 0),
            [new GradientStop(Vector4.One, 0), new GradientStop(new Vector4(0, 0, 1, 1), 1)]) { Opacity = .75f };
        RenderCommand command = Command(geometry, gradient);
        command.Transform = Matrix4x4.CreateTranslation(7, 11, 0);
        using var picture = Picture(
            new RenderCommand { Type = RenderCommandType.PushOpacity, FontSize = .5f },
            new RenderCommand { Type = RenderCommandType.PushClip, Rect = new Rect(0, 0, 70, 70) },
            new RenderCommand
            {
                Type = RenderCommandType.PushOpacityMask, Rect = SourceDomain,
                Brush = new SolidColorBrush(Vector4.One),
                Transform = rotatedMask ? Matrix4x4.CreateRotationZ(.125f) : Matrix4x4.Identity
            },
            new RenderCommand { Type = RenderCommandType.PushBlendMode, IntParam = (int)GpuBlendMode.Multiply },
            command,
            new RenderCommand { Type = RenderCommandType.PopBlendMode },
            new RenderCommand { Type = RenderCommandType.PopOpacityMask },
            new RenderCommand { Type = RenderCommandType.PopClip },
            new RenderCommand { Type = RenderCommandType.PopOpacity });
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 993, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        Assert.Equal(2, compiled.NativeDrawCount);
        Assert.Equal(0, compiled.AnalyticPrimitiveCount);
        Assert.Equal(1, compiled.GlyphOutlineCount);
        Assert.Equal(2, compiled.PositionedGlyphCount);
        var resources = Resources(compiled.Stream);
        Assert.Equal(SourceDomain, picture.RetainedCommands[4].Rect);
        var draws = Commands(compiled.Stream).Where(x => x.Kind == NativeSceneCommandKind.DrawPaintedGlyphRun).ToArray();
        Assert.Equal(2, draws.Length);
        Assert.Equal(draws[0].ResourceIndex, draws[1].ResourceIndex);
        for (int index = 0; index < draws.Length; index++)
        {
            var prefix = MemoryMarshal.Read<NativeScenePaintedGlyphDraw>(compiled.Stream.Slice((int)draws[index].PayloadOffset));
            Assert.Equal(32U, prefix.StructSize); Assert.Equal(1U, prefix.GlyphCount);
            Assert.Equal((uint)NativeSceneTextRenderingMode.Grayscale, prefix.RenderingMode);
            var paint = PaintedMaterial(compiled.Stream, draws[index]);
            Assert.Equal(NativeSceneGlyphPaint.Material, paint.Kind);
            Assert.Equal(prefix.PaintIndex, paint.BrushIndex);
            Assert.Equal(new Vector4(7, 11, 0, 0), paint.SourceOffsetOpacity);
            var glyph = MemoryMarshal.Read<NativePositionedGlyph>(compiled.Stream.Slice((int)draws[index].PayloadOffset + 128));
            Assert.Equal(new Vector2(24 + index * 10, 36), glyph.Position);
        }
        Assert.DoesNotContain(resources, x => x.Kind is NativeSceneResourceKind.AnalyticBatch or NativeSceneResourceKind.TextStyleTable);
        if (rotatedMask)
        {
            var maskResource = resources.Single(x => x.Kind == NativeSceneResourceKind.LayerMask);
            Assert.Equal((uint)Unsafe.SizeOf<NativeSceneLayerBrushMask>(), maskResource.PayloadSize);
        }
        else
        {
            // The original axis-aligned solid mask folds into opacity + clip.
            Assert.DoesNotContain(resources, x => x.Kind == NativeSceneResourceKind.LayerMask);
        }
        var paintState = resources.Where(x => x.Kind == NativeSceneResourceKind.State)
            .Select(x => MemoryMarshal.Read<NativeSceneState>(compiled.Stream.Slice((int)x.PayloadOffset))).Last();
        Assert.Equal(.5f, paintState.Opacity);
        Assert.True((paintState.Flags & NativeSceneStateFlags.ClipRect) != 0);
        Assert.Equal(rotatedMask, (paintState.Flags & NativeSceneStateFlags.Mask) != 0);
        Assert.Equal(rotatedMask ? new NativeImageRect(0, 0, 70, 70) : new NativeImageRect(10, 15, 30, 20), paintState.ClipRect);
        Assert.Equal(2, Commands(compiled.Stream).Count(x => x.Kind == NativeSceneCommandKind.PushLayer));
    }

    [Fact]
    public void FractionalSpatialStorageCoversOriginalRasterWithoutMovingAbsoluteGradientCoordinates()
    {
        using var geometry = FractionalGeometry(new Owner());
        var gradient = new LinearGradientBrush(new Vector2(12.25f, 18.5f), new Vector2(40.25f, 28.5f),
            [new GradientStop(Vector4.One, 0), new GradientStop(new Vector4(0, 0, 1, 1), 1)])
        {
            CoordinateTransform = Matrix4x4.CreateScale(1.25f, .75f, 1) * Matrix4x4.CreateTranslation(3.5f, -2.25f, 0),
            Opacity = .625f
        };
        RenderCommand source = FractionalCommand(geometry, gradient);
        using var picture = Picture(source);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 999, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        Assert.Equal(source.Rect, picture.RetainedCommands[0].Rect);
        NativeMethods.SceneResource[] resources = Resources(compiled.Stream);
        var draw = Commands(compiled.Stream).Single(x => x.Kind == NativeSceneCommandKind.DrawPaintedGlyphRun);
        var paint = PaintedMaterial(compiled.Stream, draw);
        Assert.Equal(new Vector4(.125f, .375f, 0, 0), paint.SourceOffsetOpacity);
        Assert.Equal(new NativeImageRect(15, 18.875f, 7, 7.5f), draw.Bounds);
        Assert.DoesNotContain(resources, x => x.Kind is NativeSceneResourceKind.LayerMask or NativeSceneResourceKind.AnalyticBatch);
        NativeMethods.SceneResource brushes = resources.Single(x => x.Kind == NativeSceneResourceKind.BrushTable);
        var retainedBrush = MemoryMarshal.Read<NativeSceneBrush>(compiled.Stream.Slice((int)brushes.PayloadOffset));
        Assert.Equal(gradient.StartPoint, retainedBrush.StartPoint);
        Assert.Equal(gradient.EndPoint, retainedBrush.EndPoint);
        Assert.Equal(new Vector4(1.25f, 0, 3.5f, 0), retainedBrush.CoordinateTransform0);
        Assert.Equal(new Vector4(0, .75f, -2.25f, 0), retainedBrush.CoordinateTransform1);
        Assert.Equal(.625f, retainedBrush.Opacity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FractionalTextureStoragePreservesOriginalMappingInsteadOfStretchingToThePaddedDomain(bool extend)
    {
        using var texture = CreateUnbackedTexture(16, 16);
        using var geometry = FractionalGeometry(new Owner());
        var brush = new GpuTextureBrush
        {
            Texture = texture, SourceRect = new Rect(2, 3, 4, 5), DestinationRect = new Rect(10, 20, 8, 10),
            Transform = Matrix4x4.CreateScale(2, .5f, 1) * Matrix4x4.CreateTranslation(3, 7, 0),
            ExtendToFillBounds = extend, SamplingMode = TextureSamplingMode.Nearest,
            AddressModeU = extend ? TextureAddressMode.Repeat : TextureAddressMode.Clamp,
            AddressModeV = extend ? TextureAddressMode.MirrorRepeat : TextureAddressMode.Clamp,
            Opacity = .625f
        };
        RenderCommand source = FractionalCommand(geometry, brush);
        using var picture = Picture(source);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 1000, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        Assert.Equal(source.Rect, picture.RetainedCommands[0].Rect);
        Assert.Equal(new Rect(2, 3, 4, 5), brush.SourceRect);
        Assert.Equal(new Rect(10, 20, 8, 10), brush.DestinationRect);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(1, compiled.ExternalImages.Length);
        NativeMethods.SceneCommand draw = Commands(compiled.Stream).Single(x => x.Kind == NativeSceneCommandKind.DrawPaintedGlyphRun);
        var paint = PaintedMaterial(compiled.Stream, draw);
        Assert.Equal(NativeSceneGlyphPaint.Texture, paint.Kind);
        Assert.Equal(new Vector4(.125f, .375f, .625f, 0f), paint.SourceOffsetOpacity);
        Assert.Equal(new NativeImageRect(15, 18.875f, 7, 7.5f), draw.Bounds);
        if (extend)
        {
            // Original source domain, not padded storage, defines UV mapping.
            float x = 2f + (((source.Rect.X - 3f) / 2f) - 10f) * .5f;
            float y = 3f + (((source.Rect.Y - 7f) / .5f) - 20f) * .5f;
            float width = (source.Rect.Width / 2f) * .5f;
            float height = (source.Rect.Height / .5f) * .5f;
            Assert.Equal(new Vector4(x / 16f, y / 16f, (x + width) / 16f, (y + height) / 16f), paint.UVBounds);
            Assert.Equal(new Vector4(source.Rect.X + .125f, source.Rect.Y + .375f,
                source.Rect.Right + .125f, source.Rect.Y + .375f), paint.TextureQuad01);
            Assert.Equal((uint)TextureSamplingMode.Nearest << NativeSceneGlyphPaint.SamplingModeShift, paint.Flags);
            Assert.Equal(new Vector4(0, .5f, 1, 2), paint.Sampling);
        }
        else
        {
            Assert.Equal(new Vector4(.125f, .1875f, .375f, .5f), paint.UVBounds);
            Assert.Equal(new Vector4(23.125f, 17.375f, 39.125f, 17.375f), paint.TextureQuad01);
            Assert.Equal(new Vector4(39.125f, 22.375f, 23.125f, 22.375f), paint.TextureQuad23);
            Assert.Equal(NativeSceneGlyphPaint.BoundedTexture |
                (uint)TextureSamplingMode.Nearest << NativeSceneGlyphPaint.SamplingModeShift, paint.Flags);
            Assert.Equal(new Vector4(0, .5f, 0, 0), paint.Sampling);
        }
        Assert.DoesNotContain(Resources(compiled.Stream), x => x.Kind == NativeSceneResourceKind.LayerMask);
        var prefix = MemoryMarshal.Read<NativeScenePaintedGlyphDraw>(compiled.Stream.Slice((int)draw.PayloadOffset));
        Assert.Equal(1U, prefix.GlyphCount);
        Assert.Equal(NativeSceneResourceKind.Image, Resources(compiled.Stream)[(int)prefix.PaintResourceIndex].Kind);
    }

    [Theory]
    [InlineData(TextureAddressMode.Repeat, TextureAddressMode.Clamp, 2, 3)]
    [InlineData(TextureAddressMode.Clamp, TextureAddressMode.MirrorRepeat, 2, 3)]
    [InlineData(TextureAddressMode.Clamp, TextureAddressMode.Clamp, -1, 3)]
    [InlineData(TextureAddressMode.Clamp, TextureAddressMode.Clamp, 14, 3)]
    [InlineData(TextureAddressMode.Clamp, TextureAddressMode.Clamp, 2, -1)]
    [InlineData(TextureAddressMode.Clamp, TextureAddressMode.Clamp, 2, 14)]
    public void BoundedTexturePaintUsesExplicitOriginalAddressingAndSourceExtentAdmission(
        TextureAddressMode u, TextureAddressMode v, int sourceX, int sourceY)
    {
        using var texture = CreateUnbackedTexture(16, 16);
        using var geometry = FractionalGeometry(new Owner());
        var brush = new GpuTextureBrush
        {
            Texture = texture, SourceRect = new Rect(sourceX, sourceY, 4, 5),
            DestinationRect = new Rect(10, 20, 8, 10), ExtendToFillBounds = false,
            Transform = Matrix4x4.CreateScale(2, .5f, 1) * Matrix4x4.CreateTranslation(3, 7, 0),
            AddressModeU = u, AddressModeV = v, SamplingMode = TextureSamplingMode.Nearest, Opacity = .625f
        };
        RenderCommand source = FractionalCommand(geometry, brush);
        using var picture = Picture(source);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 1001, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        NativeMethods.SceneCommand draw = Commands(compiled.Stream).Single(x => x.Kind == NativeSceneCommandKind.DrawPaintedGlyphRun);
        var paint = PaintedMaterial(compiled.Stream, draw);
        Assert.Equal(new Vector4(sourceX / 16f, sourceY / 16f, (sourceX + 4) / 16f, (sourceY + 5) / 16f), paint.UVBounds);
        Assert.Equal(new Vector4(23.125f, 17.375f, 39.125f, 17.375f), paint.TextureQuad01);
        Assert.Equal(new Vector4(39.125f, 22.375f, 23.125f, 22.375f), paint.TextureQuad23);
        Assert.Equal(NativeSceneGlyphPaint.BoundedTexture |
            (uint)TextureSamplingMode.Nearest << NativeSceneGlyphPaint.SamplingModeShift, paint.Flags);
        Assert.Equal(new Vector4(0, .5f, (float)u, (float)v), paint.Sampling);
        Assert.Equal(.625f, paint.SourceOffsetOpacity.Z);
        Assert.Equal(source.Rect, picture.RetainedCommands[0].Rect);
    }

    [Fact]
    public void SnappedTextureExtensionKeepsOriginalSnappedQuadAndUvWithoutExpandingItsMapping()
    {
        using var texture = CreateUnbackedTexture(16, 16);
        using var geometry = FractionalGeometry(new Owner());
        var brush = new GpuTextureBrush
        {
            Texture = texture, SourceRect = new Rect(2, 3, 4, 5), DestinationRect = new Rect(10, 20, 8, 10),
            Transform = Matrix4x4.CreateScale(2, .5f, 1) * Matrix4x4.CreateTranslation(3, 7, 0),
            ExtendToFillBounds = true, SnapToPixels = true, SamplingMode = TextureSamplingMode.Linear,
            AddressModeU = TextureAddressMode.Repeat, AddressModeV = TextureAddressMode.MirrorRepeat,
            Opacity = .625f
        };
        RenderCommand source = FractionalCommand(geometry, brush);
        source.Rect = new Rect(17, 20.5f, 3, 3.5f);
        using var picture = Picture(source);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 1002, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        var draw = Commands(compiled.Stream).Single(x => x.Kind == NativeSceneCommandKind.DrawPaintedGlyphRun);
        var paint = PaintedMaterial(compiled.Stream, draw);
        // Original source UV rectangle is (.5,6.5,.75,3.5). Original world
        // destination endpoints (17.125,20.875)/(20.125,24.375) snap to
        // (17,21)/(20,24.5), so source/world scale is exactly (.25,1).
        // Glyph storage is independent; the shader extrapolates this map.
        Assert.Equal(new Vector4(.03125f, .40625f, .078125f, .625f), paint.UVBounds);
        Assert.Equal(new Vector4(17, 21, 20, 21), paint.TextureQuad01);
        Assert.Equal(new Vector4(20, 24.5f, 17, 24.5f), paint.TextureQuad23);
        Assert.Equal((uint)TextureSamplingMode.Linear << NativeSceneGlyphPaint.SamplingModeShift, paint.Flags);
        Assert.Equal(new Vector4(0, .5f, 1, 2), paint.Sampling);
        Assert.Equal(.625f, paint.SourceOffsetOpacity.Z);
        Assert.Equal(source.Rect, picture.RetainedCommands[0].Rect);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitSingularBoundedBrushMappingCollapsesPaintRatherThanBecomingIdentity(bool zeroMatrix)
    {
        using var texture = CreateUnbackedTexture(16, 16);
        using var geometry = FractionalGeometry(new Owner());
        var brush = new GpuTextureBrush
        {
            Texture = texture, SourceRect = new Rect(2, 3, 4, 5), DestinationRect = new Rect(10, 20, 8, 10),
            Transform = zeroMatrix ? default : Matrix4x4.CreateScale(0, 1, 1), ExtendToFillBounds = false
        };
        using var picture = Picture(FractionalCommand(geometry, brush));
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 1003, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        Assert.Equal(0, compiled.NativeDrawCount);
        Assert.Empty(compiled.ExternalImages.ToArray());
        Assert.DoesNotContain(Commands(compiled.Stream), x => x.Kind is NativeSceneCommandKind.DrawImage or NativeSceneCommandKind.DrawPaintedGlyphRun);
    }

    [Fact]
    public void TinyInvertibleBoundedBrushMappingKeepsItsExactComponentsWithoutAnEpsilonGate()
    {
        using var texture = CreateUnbackedTexture(16, 16);
        using var geometry = FractionalGeometry(new Owner());
        var brush = new GpuTextureBrush
        {
            Texture = texture, SourceRect = new Rect(2, 3, 4, 5), DestinationRect = new Rect(10, 20, 8, 10),
            Transform = Matrix4x4.CreateScale(1e-30f, 1e-30f, 1), ExtendToFillBounds = false
        };
        RenderCommand source = FractionalCommand(geometry, brush);
        source.Transform = Matrix4x4.Identity;
        using var picture = Picture(source);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 1004, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        var draw = Commands(compiled.Stream).Single(x => x.Kind == NativeSceneCommandKind.DrawPaintedGlyphRun);
        var paint = PaintedMaterial(compiled.Stream, draw);
        Assert.Equal(new Vector4(10f * 1e-30f, 20f * 1e-30f, 18f * 1e-30f, 20f * 1e-30f), paint.TextureQuad01);
        Assert.Equal(new Vector4(18f * 1e-30f, 30f * 1e-30f, 10f * 1e-30f, 30f * 1e-30f), paint.TextureQuad23);
        Assert.Equal(new Vector4(.125f, .1875f, .375f, .5f), paint.UVBounds);
        Assert.Equal(NativeSceneGlyphPaint.BoundedTexture | (uint)TextureSamplingMode.Linear << NativeSceneGlyphPaint.SamplingModeShift, paint.Flags);
    }

    [Fact]
    public void OrdinarySpatialPaintBatchesOriginalOccurrencesWithoutAWhiteCoverageResource()
    {
        var owner = new Owner();
        using var geometry = Geometry(owner);
        var brush = new LinearGradientBrush(new Vector2(4, 7), new Vector2(30, 17),
            [new GradientStop(Vector4.One, 0), new GradientStop(Vector4.Zero, 1)]) { Opacity = .625f };
        using var picture = Picture(Command(geometry, brush));
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 1006, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        var draw = Commands(compiled.Stream).Single(x => x.Kind == NativeSceneCommandKind.DrawPaintedGlyphRun);
        var prefix = MemoryMarshal.Read<NativeScenePaintedGlyphDraw>(compiled.Stream.Slice((int)draw.PayloadOffset));
        Assert.Equal(2U, prefix.GlyphCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(1, compiled.GlyphOutlineCount);
        Assert.Equal(0, compiled.TextStyleCount);
        var glyphs = MemoryMarshal.Cast<byte, NativePositionedGlyph>(compiled.Stream.Slice(
            (int)draw.PayloadOffset + 128, 2 * Unsafe.SizeOf<NativePositionedGlyph>()));
        Assert.Equal(new Vector2(17, 25), glyphs[0].Position);
        Assert.Equal(new Vector2(27, 25), glyphs[1].Position);
        Assert.DoesNotContain(Resources(compiled.Stream), x => x.Kind == NativeSceneResourceKind.LayerMask);
        geometry.Dispose(); picture.Dispose();
        Assert.Equal(0, owner.Disposals);
        compiled.Dispose();
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public void BoundedRotatedTextureKeepsAllFourIndependentlySnappedWorldCorners()
    {
        using var texture = CreateUnbackedTexture(16, 16);
        using var geometry = FractionalGeometry(new Owner());
        var mapping = new Matrix4x4(new Matrix3x2(.8f, .6f, -.6f, .8f, .2f, .35f));
        var brush = new GpuTextureBrush
        {
            Texture = texture, SourceRect = new Rect(2, 3, 4, 5), DestinationRect = new Rect(1, 2, 3, 4),
            Transform = mapping, ExtendToFillBounds = false, SnapToPixels = true, SamplingMode = TextureSamplingMode.Cubic
        };
        using var picture = Picture(FractionalCommand(geometry, brush));
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 1007, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        var draw = Commands(compiled.Stream).Single(x => x.Kind == NativeSceneCommandKind.DrawPaintedGlyphRun);
        var paint = PaintedMaterial(compiled.Stream, draw);
        Vector2 Snap(float x, float y) => new(MathF.Round(x * 2f) / 2f, MathF.Round(y * 2f) / 2f);
        Vector2 p0 = Snap(1f * .8f - 2f * .6f + (.2f + .125f), 1f * .6f + 2f * .8f + (.35f + .375f));
        Vector2 p1 = Snap(4f * .8f - 2f * .6f + (.2f + .125f), 4f * .6f + 2f * .8f + (.35f + .375f));
        Vector2 p2 = Snap(4f * .8f - 6f * .6f + (.2f + .125f), 4f * .6f + 6f * .8f + (.35f + .375f));
        Vector2 p3 = Snap(1f * .8f - 6f * .6f + (.2f + .125f), 1f * .6f + 6f * .8f + (.35f + .375f));
        Assert.Equal(new Vector4(p0, p1.X, p1.Y), paint.TextureQuad01);
        Assert.Equal(new Vector4(p2, p3.X, p3.Y), paint.TextureQuad23);
        Assert.Equal(new Vector4(.125f, .1875f, .375f, .5f), paint.UVBounds);
        Assert.Equal(NativeSceneGlyphPaint.BoundedTexture | NativeSceneGlyphPaint.CubicTexture |
            (uint)TextureSamplingMode.Cubic << NativeSceneGlyphPaint.SamplingModeShift, paint.Flags);
        Assert.Equal(new Vector4(0, .5f, 0, 0), paint.Sampling);
        var prefix = MemoryMarshal.Read<NativeScenePaintedGlyphDraw>(compiled.Stream.Slice((int)draw.PayloadOffset));
        NativeMethods.SceneResource image = Resources(compiled.Stream)[(int)prefix.PaintResourceIndex];
        Assert.Equal(NativeSceneResourceKind.Image, image.Kind);
        Assert.Equal(image.ResourceId, compiled.ExternalImages[0].ResourceId);
        Assert.Same(texture, compiled.ExternalImages[0].Texture);
    }

    [Fact]
    public void OriginallyCollapsedSnappedTextureDomainDoesNotBecomePaddedStoragePaint()
    {
        using var texture = CreateUnbackedTexture(16, 16);
        using var geometry = new HintedGlyphGeometry(2,
            [new GpuGlyphRecord { SegmentCount = 4, MinX = 0, MinY = 0, MaxX = .125f, MaxY = .125f }],
            [Line(new(0, 0), new(.125f, 0)), Line(new(.125f, 0), new(.125f, .125f)),
             Line(new(.125f, .125f), new(0, .125f)), Line(new(0, .125f), new(0, 0))],
            [Occurrence(0, 0, Vector2.Zero)], new Owner());
        var brush = new GpuTextureBrush
        {
            Texture = texture, SourceRect = new Rect(0, 0, 16, 16), DestinationRect = new Rect(0, 0, 16, 16),
            ExtendToFillBounds = true, SnapToPixels = true
        };
        using var picture = Picture(new RenderCommand
        {
            Type = RenderCommandType.DrawHintedGlyphs, HintedGlyphGeometry = geometry, GlyphRangeCount = 1,
            Rect = new Rect(0, -.0625f, .0625f, .0625f), Brush = brush
        });
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 1005, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        Assert.Equal(0, compiled.NativeDrawCount);
        Assert.Empty(compiled.ExternalImages.ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void LateUnsupportedFrameOrPayloadRollsBackTheEntireCompiledScene(int invalid)
    {
        using var geometry = Geometry(new Owner());
        RenderCommand command = Command(geometry, new SolidColorBrush(Vector4.One));
        switch (invalid)
        {
            case 0: command.Transform = Matrix4x4.CreateScale(1.01f); break;
            case 1: command.Transform = Matrix4x4.CreateRotationZ(.01f); break;
            case 2: command.Transform = Matrix4x4.CreateTranslation(float.PositiveInfinity, 0, 0); break;
            case 3: command.GlyphRangeStart = -1; break;
            case 4: command.GlyphRangeCount = 4; break;
            case 5: command.Rect = new Rect(20, 15, 1, 1); break;
            case 6: command.TextRenderingMode = TextRenderingMode.ClearType; break;
            case 7: command.UseGpuTransforms = true; break;
        }
        using var picture = Picture(new RenderCommand
        {
            Type = RenderCommandType.DrawRect, Rect = new Rect(1, 2, 3, 4), Brush = new SolidColorBrush(Vector4.One)
        }, command);
        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(picture, 994, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure));
        Assert.Null(compiled);
        Assert.Equal(1, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawHintedGlyphs, failure.CommandType);
    }

    [Fact]
    public void ExactPreparedDpiCannotBeChangedAtNativeCompilation()
    {
        using var geometry = Geometry(new Owner());
        using var picture = Picture(Command(geometry, new SolidColorBrush(Vector4.One)));
        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(picture, 995, 3,
            new NativePictureCompileOptions(1), out var compiled, out _));
        Assert.Null(compiled);
    }

    [Fact]
    public void NativeSnapshotOwnsOriginalGenerationAfterRecordedPictureAndPublicWrapperRetire()
    {
        var owner = new Owner();
        using var geometry = Geometry(owner);
        using var picture = Picture(Command(geometry, new SolidColorBrush(Vector4.One)));
        geometry.Dispose(); // Public admission is closed, but the recorded owner is live.
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 996, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        picture.Dispose();
        Assert.Equal(0, owner.Disposals);
        Assert.False(compiled.Stream.IsEmpty);
        compiled.Dispose();
        Assert.Equal(1, owner.Disposals);
        Assert.Throws<ObjectDisposedException>(() => { _ = compiled.Stream.Length; });
        compiled.Dispose();
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public void ParentMaskReacquiresOriginalSourceWithoutBorrowingChildSnapshotLifetime()
    {
        var owner = new Owner();
        using var geometry = Geometry(owner);
        using var maskPicture = Picture(Command(geometry, new SolidColorBrush(Vector4.One)));
        using var parent = Picture(
            new RenderCommand { Type = RenderCommandType.PushOpacityMask, Picture = maskPicture, Rect = SourceDomain },
            new RenderCommand { Type = RenderCommandType.DrawRect, Rect = SourceDomain, Brush = new SolidColorBrush(Vector4.One) },
            new RenderCommand { Type = RenderCommandType.PopOpacityMask });
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(parent, 997, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        geometry.Dispose(); maskPicture.Dispose(); parent.Dispose();
        Assert.Equal(0, owner.Disposals);
        Assert.False(compiled.Stream.IsEmpty);
        compiled.Dispose();
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public void SnapshotRetirementDrainsIndependentSourcesAndRetriesOnlyTheExactFailedUse()
    {
        var first = new Owner { Failures = 1 };
        var second = new Owner();
        using var firstGeometry = Geometry(first);
        using var secondGeometry = Geometry(second);
        using var picture = Picture(Command(firstGeometry, new SolidColorBrush(Vector4.One)),
            Command(secondGeometry, new SolidColorBrush(Vector4.One)));
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 998, 3,
            new NativePictureCompileOptions(2), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using var compiledUse = compiled;
        firstGeometry.Dispose(); secondGeometry.Dispose(); picture.Dispose();
        first.DuringDispose = compiled.Dispose; // Reentrancy must not reenter the drain.
        Assert.Equal("controlled original teardown fault", Assert.Throws<InvalidOperationException>(compiled.Dispose).Message);
        Assert.Equal(1, first.Disposals); Assert.Equal(1, second.Disposals);
        Assert.Throws<ObjectDisposedException>(() => { _ = compiled.Stream.Length; });
        compiled.Dispose();
        Assert.Equal(2, first.Disposals); Assert.Equal(1, second.Disposals);
    }

    private static GpuPicture Picture(params RenderCommand[] commands)
    {
        var uses = commands.Select(x => x.HintedGlyphGeometry).OfType<HintedGlyphGeometry>()
            .Distinct<HintedGlyphGeometry>(ReferenceEqualityComparer.Instance)
            .Select(x => RetainedResourceLease.Create(x.RetainForRecording(), x)).ToArray();
        return new GpuPicture(commands, [], [], [], [], [], uses);
    }
    private static RenderCommand Command(HintedGlyphGeometry geometry, Brush brush) => new()
    {
        Type = RenderCommandType.DrawHintedGlyphs, HintedGlyphGeometry = geometry,
        GlyphRangeCount = geometry.OccurrenceCount, Position = Origin, Rect = SourceDomain, Brush = brush
    };

    private static NativeMethods.SceneResource[] Resources(ReadOnlySpan<byte> stream)
    {
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        return MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(stream.Slice(
            (int)header.ResourceOffset, (int)header.ResourceCount * Unsafe.SizeOf<NativeMethods.SceneResource>())).ToArray();
    }
    private static NativeMethods.SceneCommand[] Commands(ReadOnlySpan<byte> stream)
    {
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        return MemoryMarshal.Cast<byte, NativeMethods.SceneCommand>(stream.Slice(
            (int)header.CommandOffset, (int)header.CommandCount * Unsafe.SizeOf<NativeMethods.SceneCommand>())).ToArray();
    }
    private static NativeSceneGlyphPaint PaintedMaterial(ReadOnlySpan<byte> stream, NativeMethods.SceneCommand draw) =>
        MemoryMarshal.Read<NativeSceneGlyphPaint>(stream.Slice((int)draw.PayloadOffset + 32));
    private static HintedGlyphGeometry Geometry(IDisposable owner) => new(2,
        [new GpuGlyphRecord { SegmentCount = 4, MinX = -2, MinY = 3, MaxX = 4, MaxY = 9 }],
        [Line(new(-2, 3), new(4, 3)), Line(new(4, 3), new(4, 9)),
         Line(new(4, 9), new(-2, 9)), Line(new(-2, 9), new(-2, 3))],
        [Occurrence(0, 0, new(12, 19)), Occurrence(1, uint.MaxValue, new(20, 19)), Occurrence(2, 0, new(22, 19))], owner);
    private static GpuSegment Line(Vector2 a, Vector2 b) => new() { P0 = a, P1 = b };
    private static HintedGlyphGeometry FractionalGeometry(IDisposable owner) => new(2,
        [new GpuGlyphRecord { SegmentCount = 4, MinX = -.3f, MinY = 3.25f, MaxX = 4.25f, MaxY = 9.5f }],
        [Line(new(-.3f, 3.25f), new(4.25f, 3.25f)), Line(new(4.25f, 3.25f), new(4.25f, 9.5f)),
         Line(new(4.25f, 9.5f), new(-.3f, 9.5f)), Line(new(-.3f, 9.5f), new(-.3f, 3.25f))],
        [Occurrence(0, 0, new(12.125f, 19.375f))], owner);
    private static RenderCommand FractionalCommand(HintedGlyphGeometry geometry, Brush brush) => new()
    {
        Type = RenderCommandType.DrawHintedGlyphs, HintedGlyphGeometry = geometry, GlyphRangeCount = 1,
        Position = new Vector2(5.25f, 6.125f), Rect = new Rect(17.2f, 20.7f, 2.4f, 3.2f), Brush = brush,
        Transform = Matrix4x4.CreateTranslation(.125f, .375f, 0)
    };
    private static GpuTexture CreateUnbackedTexture(uint width, uint height)
    {
        // Original compiler test seam: no device, font, native module or GPU
        // executes. The actual same-device image binding remains a hosted gate.
        var texture = (GpuTexture)RuntimeHelpers.GetUninitializedObject(typeof(GpuTexture));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(GpuTexture).GetField("<Width>k__BackingField", flags)!.SetValue(texture, width);
        typeof(GpuTexture).GetField("<Height>k__BackingField", flags)!.SetValue(texture, height);
        return texture;
    }
    private static HintedGlyphOccurrence Occurrence(uint index, uint outline, Vector2 position) =>
        new(index, index, 27, 0, 0, index, 0, outline, index, (int)index + 1, 0, position, new(10, 0));
    private sealed class Owner : IDisposable
    {
        public int Disposals;
        public int Failures;
        public Action? DuringDispose;
        public void Dispose()
        {
            Disposals++;
            DuringDispose?.Invoke();
            if (Failures-- > 0) throw new InvalidOperationException("controlled original teardown fault");
        }
    }
}
