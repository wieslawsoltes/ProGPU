using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

// These metadata controls do not create a native generation, execute a font,
// or qualify source Display, drawing, or interaction behavior.
public sealed class PortableHintedTextContractTests
{
    [Fact]
    public void SourceGlyphTransportIsTypedOwnedAndSeparateFromDesignFontExports()
    {
        Assert.False(typeof(IPortableNativeGlyphRunSource).IsAssignableFrom(typeof(IPortableHintedGlyphRunSource)));
        Assert.False(typeof(IPortableHintedGlyphRunBindingFactory).IsAssignableFrom(typeof(IPortableHintedTextGlyphRun)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IPortableHintedGlyphRunBinding)));
        Assert.Equal(typeof(IPortableHintedGlyphRunBinding), typeof(IPortableHintedGlyphRunBinding).GetMethod("Retain")!.ReturnType);
        Assert.Equal(typeof(IPortableHintedTextGlyphRun), typeof(IPortableHintedGlyphRunBinding).GetMethod("AcquireGlyphRun")!.ReturnType);
        Assert.Equal(typeof(ReadOnlyMemory<ushort>), typeof(IPortableHintedGlyphRunBinding).GetProperty("GlyphIndices")!.PropertyType);
        Assert.Equal(typeof(PortableRect), typeof(IPortableHintedGlyphRunBinding).GetProperty("BaselineRelativeInkBounds")!.PropertyType);
        Assert.Null(typeof(IPortableHintedGlyphRunBinding).GetProperty("NativeFont"));
        Assert.DoesNotContain(typeof(IPortableHintedGlyphRunBinding).GetProperties(), property => property.PropertyType == typeof(object));
    }

    [Fact]
    public void OrdinaryProviderDoesNotAdvertiseHintedFormatting()
    {
        IPortableTextFormatting provider = new OrdinaryProvider();

        Assert.False(provider is IPortableHintedTextFormatting);
        Assert.False(typeof(IPortableTextFormatting).IsAssignableFrom(typeof(IPortableHintedTextFormatting)));
        Assert.False(typeof(IPortableTextParagraph).IsAssignableFrom(typeof(IPortableHintedTextParagraph)));
        Assert.Null(typeof(IPortableHintedTextParagraph).GetProperty("NativeFont"));
        Assert.Null(typeof(IPortableHintedTextParagraph).GetMethod("GetNativeFont"));
        Assert.Null(typeof(IPortableHintedTextParagraph).GetMethod("Collapse"));
        Assert.Null(typeof(IPortableHintedTextParagraph).GetMethod("Reflow"));
    }

    [Fact]
    public void OccurrenceIdentityRetainsLogicalSourceAndStyleIndependentlyOfGlyphId()
    {
        var first = new PortableHintedTextGlyph(5, 8, 42, 1, 2, 3, 4, 7,
            6, 8, 1, 17.25f, 23.5f, 8.125f, 0);
        var repeated = first with
        {
            PositionedIndex = 6,
            LogicalIndex = 2,
            StyleIndex = 1,
            RunIndex = 1,
            RunGlyphIndex = 0,
            DescriptorIndex = 2,
            Cluster = 0,
            ClusterEnd = 2,
            X = 9.125f
        };

        Assert.Equal(first.GlyphId, repeated.GlyphId);
        Assert.Equal(first.FontIndex, repeated.FontIndex);
        Assert.NotEqual(first, repeated);
        Assert.Equal((5, 8u, 2u, 3u, 4u, 7u, 6, 8),
            (first.PositionedIndex, first.LogicalIndex, first.StyleIndex, first.RunIndex,
                first.RunGlyphIndex, first.DescriptorIndex, first.Cluster, first.ClusterEnd));
        Assert.Equal((6, 2u, 1u, 1u, 0u, 2u, 0, 2),
            (repeated.PositionedIndex, repeated.LogicalIndex, repeated.StyleIndex, repeated.RunIndex,
                repeated.RunGlyphIndex, repeated.DescriptorIndex, repeated.Cluster, repeated.ClusterEnd));
    }

    [Fact]
    public void MeasuredBaselinePenOriginAndInteractionTopHaveSeparateFields()
    {
        var line = new PortableHintedTextLine(2, 3, 4, 8, 31.5f, 40.25f, 17.5f, 52.75f, false);
        var box = new PortableHintedTextClusterBox(4, 6, 1, 52.75f, 26.5f, 8.125f, 17.5f, 1);
        var caret = new PortableHintedTextCaret(6, true, 1, 52.75f, 26.5f, 17.5f, 1);
        var emptyLine = new PortableHintedTextLine(5, 0, 8, 9, 0, 57.75f, 17.5f, 84.25f, false);

        Assert.Equal(40.25f, line.BaselineY);
        Assert.Equal(52.75f, line.PenOriginX);
        Assert.Equal(26.5f, box.Y);
        Assert.Equal(box.Y, caret.Y);
        Assert.NotEqual(line.BaselineY, box.Y);
        Assert.Equal(0, emptyLine.GlyphCount);
        Assert.Null(typeof(PortableHintedTextLine).GetProperty("Y"));
        Assert.Null(typeof(PortableHintedTextLine).GetProperty("Top"));
    }

    private sealed class OrdinaryProvider : IPortableTextFormatting
    {
        public IPortableTextParagraph Format(in PortableTextParagraphRequest request)
            => throw new NotSupportedException();
    }
}
