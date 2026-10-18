using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using ProGPU.Tests.Headless;
using Microsoft.UI.Xaml;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class IntegerGlyphRasterOriginTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void CompleteColdAndWarmFramesMatchIndependentIntegerOriginDraw(float dpi)
    {
        var window = HeadlessWindow.Shared;
        var command = Command();
        command.GlyphPositions = [new(8.5f / dpi, 24.5f / dpi), new(28.499f / dpi, 24.5f / dpi)];
        command.GlyphIndices = [InterFontFamily.Regular.GetGlyphIndex('A'), InterFontFamily.Regular.GetGlyphIndex('W')];
        try
        {
            window.Content = new GlyphVisual(command);
            window.RenderAtDpi(64, 40, dpi);
            var cold = window.ReadPixels();
            window.RenderAtDpi(64, 40, dpi);
            Assert.Equal(cold, window.ReadPixels());
            var reference = command;
            reference.TextHintingMode = TextHintingMode.Auto;
            reference.GlyphPositions = [new(9f / dpi, 25f / dpi), new(28f / dpi, 25f / dpi)];
            window.Content = new GlyphVisual(reference);
            window.RenderAtDpi(64, 40, dpi);
            Assert.Equal(window.ReadPixels(), cold);
            Assert.Contains(Enumerable.Range(0, cold.Length / 4), i => cold[i * 4] != 0);
            Assert.Equal(new Vector2(8.5f / dpi, 24.5f / dpi), command.GlyphPositions[0]);
        }
        finally { window.Content = null; }
    }

    [Fact]
    public void CapacityFallbackRetainsResolvedOriginsAndEveryGlyph()
    {
        var options = CompositorOptions.Default with { GlyphAtlasSize = 32, PathAtlasSize = 256 };
        using var actualWindow = new HeadlessWindow(160, 64, options);
        using var referenceWindow = new HeadlessWindow(160, 64, options);
        var command = Command();
        command.FontSize = 28;
        command.GlyphIndices = "WMGB".Select(InterFontFamily.Regular.GetGlyphIndex).ToArray();
        command.GlyphPositions = [new(8.5f, 40.5f), new(44.5f, 40.5f), new(80.5f, 40.5f), new(116.5f, 40.5f)];
        var reference = command;
        reference.TextHintingMode = TextHintingMode.Auto;
        reference.GlyphPositions = [new(9, 41), new(45, 41), new(81, 41), new(117, 41)];
        actualWindow.Content = new GlyphVisual(command);
        referenceWindow.Content = new GlyphVisual(reference);
        for (int frame = 0; frame < 2; frame++)
        {
            actualWindow.Render();
            referenceWindow.Render();
            Assert.True(actualWindow.Compositor.Atlas.CapacityExceeded);
            Assert.True(referenceWindow.Compositor.Atlas.CapacityExceeded);
            byte[] pixels = actualWindow.ReadPixels();
            Assert.Equal(referenceWindow.ReadPixels(), pixels);
            for (int glyph = 0; glyph < 4; glyph++)
            {
                int left = 8 + glyph * 36;
                Assert.Contains(Enumerable.Range(0, 32 * 48), i =>
                    pixels[((i / 32) * 160 + left + i % 32) * 4] != 0);
            }
        }
    }

    [Fact]
    public void RecordingBorrowsRangeAndDefersPerGlyphValidationToCompilation()
    {
        var command = Command();
        command.GlyphPositions![1] = new(float.NaN, 0);
        var context = new DrawingContext();
        context.DrawGlyphRun(command.GlyphIndices!, command.GlyphPositions, command.Font!, command.FontSize,
            command.Brush!, Vector2.Zero, textRenderingMode: TextRenderingMode.Aliased,
            textHintingMode: TextHintingMode.IntegerPixel, preferGlyphAtlas: true);
        Assert.Same(command.GlyphPositions, Assert.Single(context.Commands).GlyphPositions);
        using var picture = context.CreatePictureSnapshot();
        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(picture, 917, 1, out var compiled, out _));
        Assert.Null(compiled);
    }

    private sealed class GlyphVisual(RenderCommand command) : FrameworkElement
    {
        public override void OnRender(DrawingContext context) => context.Commands.Add(command);
    }

    [Fact]
    public void WriterRejectsUnsupportedModeBeforeAppendingAndKeepsOriginalArrays()
    {
        var command = Command();
        var context = new DrawingContext();
        context.DrawGlyphRun(command.GlyphIndices!, command.GlyphPositions!, command.Font!, command.FontSize,
            command.Brush!, Vector2.Zero, textRenderingMode: TextRenderingMode.Aliased,
            textHintingMode: TextHintingMode.IntegerPixel, preferGlyphAtlas: true);
        Assert.Same(command.GlyphPositions, Assert.Single(context.Commands).GlyphPositions);
        Assert.Throws<NotSupportedException>(() => context.DrawGlyphRun(command.GlyphIndices!,
            command.GlyphPositions!, command.Font!, command.FontSize, command.Brush!, Vector2.Zero,
            textHintingMode: TextHintingMode.IntegerPixel, preferGlyphAtlas: true));
        Assert.Throws<NotSupportedException>(() => context.DrawText("A", command.Font!, 12,
            command.Brush!, Vector2.Zero, textHintingMode: TextHintingMode.IntegerPixel));
        Assert.Single(context.Commands);
        var retained = new RetainedTextCommandData(context.Commands[0]);
        RenderCommand restored = default;
        retained.Apply(ref restored);
        Assert.Equal(TextHintingMode.IntegerPixel, restored.TextHintingMode);
        Assert.True(restored.PreferGlyphAtlas);
        Assert.Same(command.GlyphPositions, restored.GlyphPositions);
    }

    [Theory]
    [InlineData(8.499f, 8f)]
    [InlineData(8.5f, 9f)]
    [InlineData(8.501f, 9f)]
    [InlineData(-8.5f, -8f)]
    [InlineData(-8.501f, -9f)]
    [InlineData(8388609f, 8388609f)]
    [InlineData(-8388609f, -8388609f)]
    public void PhysicalHalfTiesDoNotChangeLogicalPositionsOrLargeIntegers(float input, float expected)
    {
        Vector2 original = new(input, input);
        Assert.True(IntegerGlyphRasterOrigin.TryResolve(original, 1f, out var actual));
        Assert.Equal(new Vector2(expected), actual);
        Assert.Equal(new Vector2(input), original);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidPhysicalScaleDoesNotPublishAnOrigin(float dpi)
    {
        Assert.False(IntegerGlyphRasterOrigin.TryResolve(new Vector2(2, 3), dpi, out var value));
        Assert.Equal(default, value);
    }

    [Fact]
    public void LaterRangeOverflowRejectsBeforeNativePublication()
    {
        var command = Command();
        command.GlyphPositions![1] = new Vector2(float.MaxValue);
        using var picture = Picture(command);
        Assert.False(IntegerGlyphRasterOrigin.IsSupported(command, Matrix4x4.Identity, 2f));
        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(picture, 917, 1,
            new NativePictureCompileOptions(2f), out var compiled, out _));
        Assert.Null(compiled);
        Assert.Equal(new Vector2(float.MaxValue), command.GlyphPositions[1]);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void NativeCompilerUsesActualTargetDpiAndZeroAtlasPhase(float dpi)
    {
        var command = Command();
        Vector2[] original = (Vector2[])command.GlyphPositions!.Clone();
        command.Transform = Matrix4x4.CreateTranslation(.25f, .5f, 0);
        using var picture = Picture(command);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 917, 1,
            new NativePictureCompileOptions(dpi), out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resources = MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(compiled.Stream.Slice(
            checked((int)header.ResourceOffset), checked((int)header.ResourceCount * Marshal.SizeOf<NativeMethods.SceneResource>())));
        var outline = MemoryMarshal.Read<NativeSceneGlyphOutline>(compiled.Stream.Slice(checked((int)resources[0].PayloadOffset)));
        Assert.Equal(0f, outline.SubpixelX);
        var record = MemoryMarshal.Read<NativeMethods.SceneCommand>(compiled.Stream.Slice(checked((int)header.CommandOffset)));
        var glyphs = MemoryMarshal.Cast<byte, NativePositionedGlyph>(compiled.Stream.Slice(
            checked((int)record.PayloadOffset + 24), 2 * Marshal.SizeOf<NativePositionedGlyph>()));
        // Independently evaluated expected physical origins for each exact DPI.
        Vector2 first = dpi == 1 ? new(9, 13) : dpi == 1.5f ? new(13f/1.5f, 19f/1.5f) : new(8.5f, 12.5f);
        Vector2 second = dpi == 1 ? new(19, 13) : dpi == 1.5f ? new(28f/1.5f, 19f/1.5f) : new(18.5f, 12.5f);
        Assert.Equal(first, glyphs[0].Position);
        Assert.Equal(second, glyphs[1].Position);
        Assert.Equal(original, command.GlyphPositions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void UnsupportedFramesAndRasterRoutesDoNotSilentlyUseLegacyPlacement(int scenario)
    {
        var command = Command();
        switch (scenario)
        {
            case 0: command.Transform.M12 = float.Epsilon; break;
            case 1: command.Transform.M11 = -1; break;
            case 2: command.Rotation = float.Epsilon; break;
            case 3: command.UseVectorGlyphRendering = true; break;
            case 4: command.TextRenderingMode = TextRenderingMode.Grayscale; break;
            case 5: command.HasFontTransform = true; command.FontTransform = new(1, float.Epsilon); break;
        }
        using var picture = Picture(command);
        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(picture, 917, 1,
            out var compiled, out _));
        Assert.Null(compiled);
    }

    [Fact]
    public void SnapshotCloneAndVersionNineRetainRawPlacementAndAtlasPolicy()
    {
        var command = Command();
        var context = new DrawingContext();
        context.Commands.Add(command);
        using var picture = context.CreatePictureSnapshot();
        using var clone = picture.Clone();
        byte[] archive = PictureArchive.Serialize(clone, new SKRect(0, 0, 64, 64), 9);
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(8, 4)));
        using var restored = SKPicture.Deserialize(archive);
        Assert.NotNull(restored);
        var actual = Assert.Single(restored.Picture.Commands);
        Assert.Equal(TextHintingMode.IntegerPixel, actual.TextHintingMode);
        Assert.True(actual.PreferGlyphAtlas);
        Assert.Equal(command.GlyphPositions, actual.GlyphPositions);
        Assert.True(IntegerGlyphRasterOrigin.IsSupported(actual, Matrix4x4.Identity, 1f));
        Assert.Throws<NotSupportedException>(() => PictureArchive.Serialize(clone, new SKRect(0, 0, 64, 64), 8));
        BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(8, 4), 8);
        Assert.Null(SKPicture.Deserialize(archive));
    }

    private static RenderCommand Command() => new()
    {
        Type = RenderCommandType.DrawGlyphRun,
        Font = InterFontFamily.Regular,
        FontSize = 12,
        GlyphIndices = [InterFontFamily.Regular.GetGlyphIndex('i'), InterFontFamily.Regular.GetGlyphIndex('i')],
        GlyphPositions = [new(8.25f, 12f), new(18.25f, 12f)],
        Brush = new SolidColorBrush(Vector4.One),
        Transform = Matrix4x4.Identity,
        TextRenderingMode = TextRenderingMode.Aliased,
        TextHintingMode = TextHintingMode.IntegerPixel,
        PreferGlyphAtlas = true
    };

    private static GpuPicture Picture(RenderCommand command) => new([command], [], [], [], []);
}
