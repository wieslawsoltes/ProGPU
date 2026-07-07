using System.Globalization;
using ProGPU.Scene;
using Xunit;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfFormattedText = System.Windows.Media.FormattedText;
using WpfPoint = System.Windows.Point;
using WpfTypeface = System.Windows.Media.Typeface;

namespace ProGPU.Tests;

public sealed class WpfFormattedTextTests
{
    [Fact]
    public void DrawTextPropagatesTypefaceWeightAndStyleFlags()
    {
        var nativeContext = new DrawingContext();
        using var context = new WpfDrawingContext(nativeContext);
        var typeface = new WpfTypeface(
            new WpfFontFamily("Arial"),
            System.Windows.FontStyles.Italic,
            System.Windows.FontWeights.Bold,
            System.Windows.FontStretches.Normal);
        var formattedText = new WpfFormattedText(
            "Styled",
            CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            16,
            WpfBrushes.Black);

        Assert.True(formattedText.IsBold);
        Assert.True(formattedText.IsItalic);

        context.DrawText(formattedText, new WpfPoint(4, 8));

        var command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawText, command.Type);
        Assert.True(command.IsBold);
        Assert.True(command.IsItalic);
    }

    [Fact]
    public void FontWeightAndStyleExposeWpfCompatibleValueSemantics()
    {
        Assert.Equal(400, System.Windows.FontWeights.Normal.ToOpenTypeWeight());
        Assert.Equal(700, System.Windows.FontWeights.Bold.ToOpenTypeWeight());
        Assert.True(System.Windows.FontWeights.Bold > System.Windows.FontWeights.Normal);
        Assert.Equal(System.Windows.FontWeights.Normal, System.Windows.FontWeight.Normal);
        Assert.Equal(System.Windows.FontStyles.Italic, System.Windows.FontStyle.Italic);
        Assert.Equal("Italic", System.Windows.FontStyles.Italic.ToString());
    }

    [Theory]
    [InlineData("Calibri")]
    [InlineData("Segoe UI")]
    [InlineData("Segoe UI, Arial")]
    [InlineData("Consolas")]
    public void FontFamilyResolvesCommonWpfFontsThroughPortableFallbacks(string familyName)
    {
        var family = new WpfFontFamily(familyName);

        Assert.NotNull(family.NativeFont);
    }

    [Fact]
    public void DrawTextUsesPortableFallbackForWindowsOnlyFontFamily()
    {
        var nativeContext = new DrawingContext();
        using var context = new WpfDrawingContext(nativeContext);
        var formattedText = new WpfFormattedText(
            "Family.Show",
            CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            new WpfTypeface(new WpfFontFamily("Calibri")),
            16,
            WpfBrushes.Black);

        Assert.NotNull(formattedText.Font);

        context.DrawText(formattedText, new WpfPoint(4, 8));

        var command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawText, command.Type);
        Assert.Same(formattedText.Font, command.Font);
    }
}
