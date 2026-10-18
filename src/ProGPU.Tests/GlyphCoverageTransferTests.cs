using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
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

public sealed class GlyphCoverageTransferTests
{
    [Theory]
    [InlineData(-1)] [InlineData(13)] [InlineData(int.MinValue)] [InlineData(int.MaxValue)]
    public void UndefinedContrastsReject(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GlyphCoverageTransfer.GdiPlusGrayscale(value));

    [Fact]
    public void EveryContrastSurvivesSnapshotCloneArchiveAndNativeStyleIdentity()
    {
        var context = new DrawingContext();
        for (int contrast = 0; contrast <= 12; contrast++)
            context.Commands.Add(Command(contrast));
        using var picture = context.CreatePictureSnapshot();
        using var clone = picture.Clone();
        byte[] archive = PictureArchive.Serialize(clone, new SKRect(0, 0, 64, 48));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(8, 4)));
        using var restored = SKPicture.Deserialize(archive);
        Assert.NotNull(restored);
        Assert.Throws<NotSupportedException>(() => PictureArchive.Serialize(clone, new SKRect(0, 0, 64, 48), 9));
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(restored.Picture, 919, 1, out var compiled, out var error), error.ToString());
        using var compiledUse = compiled;
        Assert.Equal(13, compiled!.TextStyleCount);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resources = MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(compiled.Stream.Slice(
            (int)header.ResourceOffset, (int)header.ResourceCount * Marshal.SizeOf<NativeMethods.SceneResource>()));
        var resource = resources.ToArray().Single(r => r.Kind == NativeSceneResourceKind.TextStyleTable);
        var styles = MemoryMarshal.Cast<byte, NativeSceneTextStyle>(compiled.Stream.Slice((int)resource.PayloadOffset, (int)resource.PayloadSize));
        for (int i = 0; i <= 12; i++)
        {
            Assert.Equal((uint)i + 1, styles[i].CoverageTransfer.Encoding);
            Assert.Equal(context.Commands[i].GlyphPositions, restored.Picture.Commands[i].GlyphPositions);
            Assert.Equal(context.Commands[i].GlyphCoverageTransfer, restored.Picture.Commands[i].GlyphCoverageTransfer);
        }
        Assert.Equal(32, Marshal.SizeOf<NativeSceneTextStyle>());
        Assert.Equal(96, Marshal.SizeOf<NativeSceneGlyphPaint>());
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(12)]
    public void NativeVectorFallbackRetainsPaintTransferAndOriginalSampleGrid(int contrast)
    {
        var context = new DrawingContext();
        var command = Command(contrast);
        command.UseVectorGlyphRendering = true;
        context.Commands.Add(command);
        using var picture = context.CreatePictureSnapshot();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 922, 1, out var compiled, out var error), error.ToString());
        using var compiledUse = compiled;
        Assert.True(compiled!.PathCount > 0);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resources = MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(compiled.Stream.Slice(
            (int)header.ResourceOffset, (int)header.ResourceCount * Marshal.SizeOf<NativeMethods.SceneResource>()));
        var paths = resources.ToArray().Where(r => r.Kind == NativeSceneResourceKind.PathBatch).ToArray();
        Assert.NotEmpty(paths);
        foreach (var resource in paths)
            foreach (var path in MemoryMarshal.Cast<byte, NativeScenePathFill>(compiled.Stream.Slice((int)resource.PayloadOffset, (int)resource.PayloadSize)))
            {
                Assert.Equal(8U, path.SampleGrid & 255U);
                Assert.Equal((uint)contrast + 1, path.SampleGrid >> 8);
            }
    }

    [Theory]
    [InlineData(TextRenderingMode.Aliased)] [InlineData(TextRenderingMode.ClearType)]
    public void IncompatibleModesRejectBeforeRecordingOrNativePublication(TextRenderingMode mode)
    {
        var context = new DrawingContext();
        var command = Command(4);
        Assert.Throws<ArgumentException>(() => context.DrawGlyphRun(command.GlyphIndices!, command.GlyphPositions!,
            command.Font!, command.FontSize, command.Brush!, Vector2.Zero,
            textRenderingMode: mode, glyphCoverageTransfer: command.GlyphCoverageTransfer));
        Assert.Empty(context.Commands);
        command.TextRenderingMode = mode;
        context.Commands.Add(command);
        using var picture = context.CreatePictureSnapshot();
        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(picture, 920, 1, out var compiled, out _));
        Assert.Null(compiled);
        Assert.Throws<NotSupportedException>(() => PictureArchive.Serialize(picture, new SKRect(0, 0, 64, 48)));
    }

    [Fact]
    public void InvalidRawTransferDoesNotAlterNativeDestination()
    {
        byte[] destination = new byte[2048];
        var builder = new NativeSceneStreamBuilder(destination, 921, 1, 0, 1);
        byte[] original = (byte[])destination.Clone();
        var raw = new NativeSceneTextStyle(Vector4.One);
        Span<NativeSceneTextStyle> styles = stackalloc NativeSceneTextStyle[1];
        styles[0] = raw;
        BinaryPrimitives.WriteUInt32LittleEndian(MemoryMarshal.AsBytes(styles).Slice(20), 14);
        Assert.False(builder.TryAddTextStyleResource(1, 1, styles, out _));
        Assert.Equal(original, destination);
        styles[0] = new NativeSceneTextStyle(Vector4.One, coverageTransfer: GlyphCoverageTransfer.GdiPlusGrayscale(0));
        Assert.True(builder.TryAddTextStyleResource(1, 1, styles, out _));
    }

    [Theory]
    [InlineData(false, 1f)] [InlineData(false, 1.5f)]
    [InlineData(true, 1f)] [InlineData(true, 1.5f)]
    public void AtlasAndVectorReplayKeepTransferAcrossColdAndWarmFrames(bool vector, float dpi)
    {
        var window = HeadlessWindow.Shared;
        try
        {
            byte[][] frames = new byte[2][];
            for (int i = 0; i < 2; i++)
            {
                var command = Command(i * 12);
                command.UseVectorGlyphRendering = vector;
                window.Content = new GlyphVisual(command);
                window.RenderAtDpi(64, 48, dpi);
                frames[i] = window.ReadPixels();
                window.RenderAtDpi(64, 48, dpi);
                Assert.Equal(frames[i], window.ReadPixels());
            }
            int changed = 0;
            for (int i = 0; i < frames[0].Length; i += 4)
            {
                Assert.True(frames[1][i] <= frames[0][i], $"Coverage increased at byte {i}.");
                if (frames[1][i] != frames[0][i]) changed++;
            }
            Assert.True(changed > 10);
        }
        finally { window.Content = null; }
    }

    private sealed class GlyphVisual(RenderCommand command) : FrameworkElement
    {
        public override void OnRender(DrawingContext context) => context.Commands.Add(command);
    }

    private static RenderCommand Command(int contrast) => new()
    {
        Type = RenderCommandType.DrawGlyphRun, Font = InterFontFamily.Regular, FontSize = 21,
        GlyphIndices = [InterFontFamily.Regular.GetGlyphIndex('A'), InterFontFamily.Regular.GetGlyphIndex('g')],
        GlyphPositions = [new(4.25f, 30), new(24.25f, 30)], Brush = new SolidColorBrush(Vector4.One),
        Transform = Matrix4x4.Identity, TextRenderingMode = TextRenderingMode.Grayscale,
        GlyphCoverageTransfer = GlyphCoverageTransfer.GdiPlusGrayscale(contrast)
    };
}
