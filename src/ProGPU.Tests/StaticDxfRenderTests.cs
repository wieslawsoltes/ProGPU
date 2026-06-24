using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class StaticDxfRenderTests
{
    [Fact]
    public void DrawStaticDxfHonorsActiveOpacityMask()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(190, 90);

        using var visibleBuffer = CreateStaticRect(window.Compositor, new Rect(20, 25, 40, 40));
        using var extensionMaskedBuffer = CreateStaticRect(window.Compositor, new Rect(75, 25, 40, 40));
        using var commandMaskedBuffer = CreateStaticRect(window.Compositor, new Rect(130, 25, 40, 40));

        window.Content = new MaskedStaticDxfVisual(visibleBuffer, extensionMaskedBuffer, commandMaskedBuffer);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var background = ReadPixel(pixels, window.Width, x: 10, y: 10);
            var visible = ReadPixel(pixels, window.Width, x: 40, y: 45);
            var extensionMasked = ReadPixel(pixels, window.Width, x: 95, y: 45);
            var commandMasked = ReadPixel(pixels, window.Width, x: 150, y: 45);

            Assert.True(visible.R >= 220, $"Expected unmasked static DXF draw to render red, found {visible}.");
            Assert.True(visible.G <= 35, $"Expected unmasked static DXF draw to keep green low, found {visible}.");
            Assert.True(visible.B <= 35, $"Expected unmasked static DXF draw to keep blue low, found {visible}.");
            Assert.Equal(255, visible.A);

            AssertColorNear(background, extensionMasked, tolerance: 12);
            AssertColorNear(background, commandMasked, tolerance: 12);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawStaticDxfSkipsCollapsedNestedClip()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var buffer = CreateStaticRect(window.Compositor, new Rect(0, 0, 32, 32));
        window.Content = new CollapsedNestedClipStaticDxfVisual(buffer);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var clippedEdge = ReadPixel(pixels, window.Width, x: 16, y: 8);

            Assert.True(clippedEdge.R <= 35, $"Expected collapsed clip edge to keep background red low, found {clippedEdge}.");
            Assert.True(clippedEdge.G <= 35, $"Expected collapsed clip edge to keep background green low, found {clippedEdge}.");
            Assert.True(clippedEdge.B >= 220, $"Expected collapsed clip edge to remain blue background, found {clippedEdge}.");
            Assert.Equal(255, clippedEdge.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawStaticDxfHonorsActiveBlendMode()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(72, 32);

        using var extensionBuffer = CreateStaticRect(window.Compositor, new Rect(0, 0, 32, 32));
        using var commandBuffer = CreateStaticRect(window.Compositor, new Rect(40, 0, 32, 32));

        window.Content = new ClearBlendStaticDxfVisual(extensionBuffer, commandBuffer);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var extensionCleared = ReadPixel(pixels, window.Width, x: 16, y: 16);
            var commandCleared = ReadPixel(pixels, window.Width, x: 56, y: 16);

            AssertTransparent(extensionCleared);
            AssertTransparent(commandCleared);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawStaticDxfSplineHonorsActiveBlendMode()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(48, 48);

        using var buffer = CreateStaticSpline(window.Compositor);

        window.Content = new ClearBlendStaticDxfSplineVisual(buffer);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var cleared = ReadPixel(pixels, window.Width, x: 24, y: 24);

            AssertTransparent(cleared);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CompileStaticDxfIncludesGlyphRunCommands()
    {
        var font = TryLoadTestFont();
        if (font == null)
        {
            return;
        }

        var window = HeadlessWindow.Shared;
        var context = CreateGlyphRunContext(font);

        using var listBuffer = window.Compositor.CompileStaticDxf(context.Commands);
        AssertStaticGlyphRunCompiled(listBuffer);

        using var contextBuffer = window.Compositor.CompileStaticDxf(context);
        AssertStaticGlyphRunCompiled(contextBuffer);
    }

    private static DxfStaticBuffer CreateStaticRect(Compositor compositor, Rect rect)
    {
        var context = new DrawingContext();
        context.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            null,
            rect);

        return compositor.CompileStaticDxf(context);
    }

    private static DxfStaticBuffer CreateStaticSpline(Compositor compositor)
    {
        var context = new DrawingContext();
        var pen = new Pen(new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)), 12f);
        var controlPoints = new[]
        {
            new Vector2(6f, 24f),
            new Vector2(24f, 24f),
            new Vector2(42f, 24f)
        };
        var knots = new double[] { 0, 0, 0, 1, 1, 1 };

        context.DrawSpline(pen, controlPoints, knots, degree: 2);

        return compositor.CompileStaticDxf(context);
    }

    private static DrawingContext CreateGlyphRunContext(TtfFont font)
    {
        var glyphIndex = font.GetGlyphIndex('A');
        var context = new DrawingContext();
        context.DrawGlyphRun(
            new[] { glyphIndex },
            new[] { new Vector2(20f, 55f) },
            font,
            24f,
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Vector2.Zero);

        return context;
    }

    private static void AssertStaticGlyphRunCompiled(DxfStaticBuffer buffer)
    {
        Assert.NotEmpty(buffer.TextVertices);
        Assert.Contains(
            buffer.DrawCalls,
            drawCall => drawCall.Type == Compositor.DrawCallType.Text && drawCall.IndexCount > 0);
        Assert.NotEmpty(buffer.TextRecords);
    }

    private static TtfFont? TryLoadTestFont()
    {
        string[] candidates =
        {
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/System/Library/Fonts/Supplemental/Helvetica.ttf",
            "/Library/Fonts/Arial.ttf",
            "C:\\Windows\\Fonts\\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return new TtfFont(candidate);
            }
        }

        var fontInfo = FontApi.GetSystemFonts().FirstOrDefault(font => File.Exists(font.FilePath));
        return fontInfo != null ? new TtfFont(fontInfo.FilePath) : null;
    }

    private static RgbaPixel ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new RgbaPixel(
            pixels[index + 0],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private static void AssertColorNear(RgbaPixel expected, RgbaPixel actual, int tolerance)
    {
        Assert.InRange(Math.Abs(expected.R - actual.R), 0, tolerance);
        Assert.InRange(Math.Abs(expected.G - actual.G), 0, tolerance);
        Assert.InRange(Math.Abs(expected.B - actual.B), 0, tolerance);
        Assert.InRange(Math.Abs(expected.A - actual.A), 0, tolerance);
    }

    private static void AssertTransparent(RgbaPixel actual)
    {
        Assert.InRange(actual.R, 0, 8);
        Assert.InRange(actual.G, 0, 8);
        Assert.InRange(actual.B, 0, 8);
        Assert.InRange(actual.A, 0, 8);
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class MaskedStaticDxfVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _visibleBuffer;
        private readonly DxfStaticBuffer _extensionMaskedBuffer;
        private readonly DxfStaticBuffer _commandMaskedBuffer;

        public MaskedStaticDxfVisual(
            DxfStaticBuffer visibleBuffer,
            DxfStaticBuffer extensionMaskedBuffer,
            DxfStaticBuffer commandMaskedBuffer)
        {
            _visibleBuffer = visibleBuffer;
            _extensionMaskedBuffer = extensionMaskedBuffer;
            _commandMaskedBuffer = commandMaskedBuffer;
            Width = 190f;
            Height = 90f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawStaticDxf(_visibleBuffer);

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f)),
                new Rect(75f, 25f, 40f, 40f));
            context.DrawStaticDxf(_extensionMaskedBuffer);
            context.PopOpacityMask();

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f)),
                new Rect(130f, 25f, 40f, 40f));
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawStaticDxf,
                StaticBuffer = _commandMaskedBuffer
            });
            context.PopOpacityMask();
        }
    }

    private sealed class ClearBlendStaticDxfVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _extensionBuffer;
        private readonly DxfStaticBuffer _commandBuffer;

        public ClearBlendStaticDxfVisual(DxfStaticBuffer extensionBuffer, DxfStaticBuffer commandBuffer)
        {
            _extensionBuffer = extensionBuffer;
            _commandBuffer = commandBuffer;
            Width = 72f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                null,
                new Rect(0f, 0f, 72f, 32f));

            context.PushBlendMode(GpuBlendMode.Clear);
            context.DrawStaticDxf(_extensionBuffer);
            context.PopBlendMode();

            context.PushBlendMode(GpuBlendMode.Clear);
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawStaticDxf,
                StaticBuffer = _commandBuffer
            });
            context.PopBlendMode();
        }
    }

    private sealed class CollapsedNestedClipStaticDxfVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _buffer;
        private readonly SolidColorBrush _background = new(new Vector4(0f, 0f, 1f, 1f));

        public CollapsedNestedClipStaticDxfVisual(DxfStaticBuffer buffer)
        {
            _buffer = buffer;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 32f, 32f));

            context.PushClip(new Rect(0f, 0f, 16f, 16f));
            context.PushClip(new Rect(16f, 0f, 16f, 16f));
            context.DrawStaticDxf(_buffer);
            context.PopClip();
            context.PopClip();
        }
    }

    private sealed class ClearBlendStaticDxfSplineVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _buffer;

        public ClearBlendStaticDxfSplineVisual(DxfStaticBuffer buffer)
        {
            _buffer = buffer;
            Width = 48f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                null,
                new Rect(0f, 0f, 48f, 48f));

            context.PushBlendMode(GpuBlendMode.Clear);
            context.DrawStaticDxf(_buffer);
            context.PopBlendMode();
        }
    }
}
