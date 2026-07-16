using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextRenderingModeRenderTests
{
    [Fact]
    public void TransformedTextRasterizationUsesDeviceScaleWithoutChangingGeometry()
    {
        var method = typeof(Compositor).GetMethod(
            "ResolveTextRasterization",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = Assert.IsType<ValueTuple<float, float, float>>(method.Invoke(
            null,
            new object[]
            {
                30f,
                Matrix4x4.CreateScale(5f / 7f),
                1f,
                1f
            }));

        Assert.Equal(1f, result.Item1);
        Assert.Equal(21.5f, result.Item2);
        Assert.Equal(30f / 21.5f, result.Item3, 5);
        Assert.Equal(30f, result.Item2 * result.Item3, 5);
    }

    [Fact]
    public void IsTextAliasedCompatibilityPropertyMapsTextRenderingMode()
    {
        var command = new RenderCommand();

        command.IsTextAliased = true;
        Assert.Equal(TextRenderingMode.Aliased, command.TextRenderingMode);
        Assert.True(command.IsTextAliased);

        command.IsTextAliased = false;
        Assert.Equal(TextRenderingMode.Grayscale, command.TextRenderingMode);
        Assert.False(command.IsTextAliased);

        command.TextRenderingMode = TextRenderingMode.ClearType;
        Assert.False(command.IsTextAliased);
    }

    [Fact]
    public void DrawingContextRecordsNativeClearTypeTextRenderingMode()
    {
        var font = TryLoadTestFont();
        if (font == null)
        {
            return;
        }

        var context = new DrawingContext();
        context.DrawText(
            "ProGPU",
            font,
            18f,
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            new Vector2(4f, 24f),
            textRenderingMode: TextRenderingMode.ClearType);
        context.DrawGlyphRun(
            new[] { font.GetGlyphIndex('A') },
            new[] { new Vector2(4f, 24f) },
            font,
            18f,
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Vector2.Zero,
            textRenderingMode: TextRenderingMode.ClearType);

        Assert.Collection(
            context.Commands,
            first => Assert.Equal(TextRenderingMode.ClearType, first.TextRenderingMode),
            second => Assert.Equal(TextRenderingMode.ClearType, second.TextRenderingMode));
    }

    [Fact]
    public void DrawingContextRecordsNativeAnimatedTextHintingMode()
    {
        var font = TryLoadTestFont();
        if (font == null)
        {
            return;
        }

        var context = new DrawingContext();
        context.DrawText(
            "ProGPU",
            font,
            18f,
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            new Vector2(4.25f, 24.5f),
            textHintingMode: TextHintingMode.Animated);
        context.DrawGlyphRun(
            new[] { font.GetGlyphIndex('A') },
            new[] { new Vector2(4.25f, 24.5f) },
            font,
            18f,
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Vector2.Zero,
            textHintingMode: TextHintingMode.Animated);

        Assert.Collection(
            context.Commands,
            first => Assert.Equal(TextHintingMode.Animated, first.TextHintingMode),
            second => Assert.Equal(TextHintingMode.Animated, second.TextHintingMode));
    }

    [Fact]
    public void CompileStaticDxfUsesHighPrecisionRetainedOutlinesForClearTypeText()
    {
        var font = TryLoadTestFont();
        if (font == null)
        {
            return;
        }

        var window = HeadlessWindow.Shared;
        var context = new DrawingContext();
        context.DrawText(
            "ProGPU",
            font,
            24f,
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            new Vector2(10f, 36f),
            textRenderingMode: TextRenderingMode.ClearType);

        using var buffer = window.Compositor.CompileStaticDxf(context);

        Assert.True(buffer.RetainedGlyphRecordCount > 0);
        Assert.True(buffer.RetainedGlyphSegmentCount > 0);
        Assert.True(buffer.RetainedGlyphInstanceCount > 0);
        Assert.Empty(buffer.TextRecords);
    }

    [Fact]
    public void CompileStaticDxfRetainsAnimatedTextWithoutAtlasRecompilation()
    {
        var font = TryLoadTestFont();
        if (font == null)
        {
            return;
        }

        var window = HeadlessWindow.Shared;
        var context = new DrawingContext();
        context.DrawText(
            "ProGPU",
            font,
            24f,
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            new Vector2(10.25f, 36.5f),
            textHintingMode: TextHintingMode.Animated);

        using var buffer = window.Compositor.CompileStaticDxf(context);

        Assert.True(buffer.RetainedGlyphRecordCount > 0);
        Assert.True(buffer.RetainedGlyphSegmentCount > 0);
        Assert.True(buffer.RetainedGlyphInstanceCount > 0);
        Assert.Empty(buffer.TextRecords);
    }

    [Fact]
    public void ClearTypeTextRenderingModeRendersSubpixelCoverage()
    {
        var font = TryLoadTestFont();
        if (font == null)
        {
            return;
        }

        var window = HeadlessWindow.Shared;
        window.Resize(220, 80);
        window.Content = new ClearTypeTextVisual(font);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            Assert.True(
                ContainsSubpixelCoverage(pixels),
                "Expected ClearType text rendering to produce channel-separated subpixel coverage.");
        }
        finally
        {
            window.Content = null;
        }
    }

    private static bool ContainsSubpixelCoverage(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var r = pixels[i + 0];
            var g = pixels[i + 1];
            var b = pixels[i + 2];
            var max = Math.Max(r, Math.Max(g, b));

            if (max > 8
                && (Math.Abs(r - g) > 4
                    || Math.Abs(g - b) > 4
                    || Math.Abs(r - b) > 4))
            {
                return true;
            }
        }

        return false;
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

    private sealed class ClearTypeTextVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public ClearTypeTextVisual(TtfFont font)
        {
            _font = font;
            Width = 220f;
            Height = 80f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 220f, 80f));
            context.DrawText(
                "IIIIIIIIII",
                _font,
                42f,
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                new Vector2(8f, 56f),
                textRenderingMode: TextRenderingMode.ClearType);
        }
    }
}
