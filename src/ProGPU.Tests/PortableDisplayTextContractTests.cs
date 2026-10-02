using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

// Contract metadata only: these controls do not qualify native/source Display.
public sealed class PortableDisplayTextContractTests
{
    [Fact]
    public void DisplayIsNotImpliedByOrdinaryOrExplicitHintedCapabilities()
    {
        Assert.False(typeof(IPortableDisplayTextFormatting).IsAssignableFrom(typeof(IPortableTextFormatting)));
        Assert.False(typeof(IPortableDisplayTextFormatting).IsAssignableFrom(typeof(IPortableHintedTextFormatting)));
        Assert.False(typeof(IPortableDisplayTextParagraph).IsAssignableFrom(typeof(IPortableHintedTextParagraph)));
        Assert.False(typeof(IPortableDisplayGlyphRunBindingFactory).IsAssignableFrom(typeof(IPortableHintedGlyphRunBindingFactory)));
        Assert.True(typeof(IPortableTextFormatting).IsAssignableFrom(typeof(IPortableDisplayTextFormatting)));
        Assert.True(typeof(IPortableTextParagraph).IsAssignableFrom(typeof(IPortableDisplayTextParagraph)));
        Assert.True(typeof(IPortableHintedTextParagraph).IsAssignableFrom(typeof(IPortableDisplayTextParagraph)));
    }

    [Fact]
    public void OriginalSourceIdentityIsNotRoundedToFloatOrPpem()
    {
        double em = Math.BitIncrement(13d), dpi = Math.BitIncrement(1.5d);
        var options = new PortableDisplayTextOptions(dpi, em, 17 / dpi, 101 / dpi, 2 / dpi);
        var style = new PortableDisplayTextStyle(em, 13 / dpi, 4 / dpi);
        Assert.Equal(dpi, options.PixelsPerDip);
        Assert.Equal(em, options.EmSize);
        Assert.Equal(em, style.EmSize);
        Assert.NotEqual((double)(float)em, options.EmSize);
        Assert.NotEqual((double)(float)dpi, options.PixelsPerDip);
        Assert.NotEqual(options, options with { PixelsPerDip = (float)dpi });
        Assert.NotEqual(style, style with { EmSize = (float)em });
    }

    [Fact]
    public void DeviceExactAdvanceMayRequireDoubleSourceGeometry()
    {
        double advance = 7 / 1.5;
        var glyph = new PortableDisplayTextGlyphMetrics(advance, 17 / 1.5, advance);
        var line = new PortableDisplayTextLineMetrics(advance, 2 / 1.5, 20 / 1.5, 15 / 1.5, 17 / 1.5);
        var widths = new PortableDisplayTextIntrinsicWidths(advance, 21 / 1.5);
        var frame = new PortableDisplayGlyphSourceFrame(2, line.BaselineY, new(advance, line.BaselineOffset));
        Assert.NotEqual(advance, (double)(float)advance);
        Assert.Equal(advance, glyph.Advance);
        Assert.Equal(advance, line.Width);
        Assert.Equal(advance, widths.Minimum);
        Assert.Equal(advance, frame.BaselineOrigin.X);
        Assert.Equal(line.BaselineY, frame.ParagraphBaselineY);
    }

    [Fact]
    public void DisplayInteractionAndReflowCannotSilentlyUseFloatSignatures()
    {
        var paragraph = typeof(IPortableDisplayTextParagraph);
        Assert.Equal(typeof(double), paragraph.GetMethod("HitTestDisplay")!.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(double), paragraph.GetMethod("GetDisplayCaretDistance")!.ReturnType);
        Assert.Equal(typeof(double), paragraph.GetMethod("ReflowDisplay")!.GetParameters()[1].ParameterType);
        Assert.Equal(paragraph, paragraph.GetMethod("ReflowDisplay")!.ReturnType);
        Assert.Equal(typeof(ReadOnlyMemory<PortableDisplayTextStyle>), paragraph.GetProperty("SourceStyles")!.PropertyType);
        Assert.Equal(typeof(double), typeof(IPortableDisplayGlyphRunBinding).GetProperty("SourcePixelsPerDip")!.PropertyType);
        Assert.True(typeof(IPortableHintedGlyphRunBinding).IsAssignableFrom(typeof(IPortableDisplayGlyphRunBinding)));
        Assert.Equal(typeof(IPortableDisplayGlyphRunBinding),
            typeof(IPortableDisplayGlyphRunBindingFactory).GetMethod("BindDisplayGlyphRun")!.ReturnType);
    }
}
