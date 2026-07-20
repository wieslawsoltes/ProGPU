using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Microsoft.UI.Xaml;
using ProGPU.Fonts.Inter;
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
        Assert.Equal(30f * (5f / 7f), result.Item2, 5);
        Assert.Equal(7f / 5f, result.Item3, 5);
        Assert.Equal(30f, result.Item2 * result.Item3, 5);
    }

    [Theory]
    [InlineData(11f, 3f, 33f)]
    [InlineData(11.5f, 3f, 34.5f)]
    [InlineData(14f, 2.625f, 36.75f)]
    [InlineData(26f, 3f, 78f)]
    public void UiTextRasterizationPreservesItsPhysicalFontSize(
        float fontSize,
        float dpiScale,
        float expectedRasterSize)
    {
        var method = typeof(Compositor).GetMethod(
            "ResolveTextRasterization",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = Assert.IsType<ValueTuple<float, float, float>>(method.Invoke(
            null,
            new object[]
            {
                fontSize,
                Matrix4x4.Identity,
                dpiScale,
                1f
            }));

        Assert.Equal(expectedRasterSize, result.Item2, 5);
        Assert.Equal(1f, result.Item3, 5);
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

    [Fact]
    public void OddPixelHighDpiRasterSizeRendersRegularAndBoldFaces()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(660, 270);
        window.Content = CreateHighDpiRichTextFaces();

        try
        {
            window.RenderAtDpi(220, 90, 3f);
            var pixels = window.ReadPixels();
            var regularPixels = CountVisiblePixels(pixels, 660, 0, 135);
            var boldPixels = CountVisiblePixels(pixels, 660, 135, 270);

            Assert.True(regularPixels > 20, $"Expected regular text pixels, found {regularPixels}; vertices={window.Compositor.TextVertexCount}.");
            Assert.True(boldPixels > 20, $"Expected bold text pixels, found {boldPixels}; vertices={window.Compositor.TextVertexCount}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void HighDpiVectorFallbackRetainsOpaqueThinStemCoverage()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(900, 240);
        window.Content = new ForcedVectorTextVisual();

        try
        {
            window.RenderAtDpi(300, 80, 3f);
            var pixels = window.ReadPixels();
            var maximumCoverage = 0;
            var opaqueCoveragePixels = 0;
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                maximumCoverage = Math.Max(maximumCoverage, pixels[offset]);
                if (pixels[offset] >= 240)
                {
                    opaqueCoveragePixels++;
                }
            }

            Assert.True(
                maximumCoverage >= 240,
                $"Expected device-scale vector text to retain opaque stem coverage, found {maximumCoverage}.");
            Assert.True(
                opaqueCoveragePixels >= 100,
                $"Expected substantial opaque thin-stem coverage, found {opaqueCoveragePixels} pixels.");
        }
        finally
        {
            window.Content = null;
        }
    }

    private static int CountVisiblePixels(byte[] pixels, int width, int startY, int endY)
    {
        var count = 0;
        for (var y = startY; y < endY; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                if (pixels[offset] > 12 || pixels[offset + 1] > 12 || pixels[offset + 2] > 12)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static Microsoft.UI.Xaml.Controls.Grid CreateHighDpiRichTextFaces()
    {
        var root = new Microsoft.UI.Xaml.Controls.Grid();
        root.RowDefinitions.Add(new GridLength(45f, GridUnitType.Absolute));
        root.RowDefinitions.Add(new GridLength(45f, GridUnitType.Absolute));

        var regular = new Microsoft.UI.Xaml.Controls.RichTextBlock
        {
            Font = InterFontFamily.Regular,
            FontSize = 11f
        };
        regular.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run("Regular eleven"));
        root.AddChild(regular);

        var bold = new Microsoft.UI.Xaml.Controls.RichTextBlock
        {
            Font = InterFontFamily.Regular,
            FontSize = 11f
        };
        bold.Inlines.Add(new Microsoft.UI.Xaml.Documents.Bold(new Microsoft.UI.Xaml.Documents.Run("Bold eleven")));
        root.AddChild(bold);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(bold, 1);

        return root;
    }

    private sealed class ForcedVectorTextVisual : FrameworkElement
    {
        private readonly TtfFont _font = InterFontFamily.GetVariableFont(100f, 18f);

        public ForcedVectorTextVisual()
        {
            Width = 300f;
            Height = 80f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawText(
                "Designing clear interfaces 0123456789",
                _font,
                26f,
                new SolidColorBrush(Vector4.One),
                new Vector2(0f, 36f),
                useVectorGlyphRendering: true);
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
