using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableInlineTextContractTests
{
    [Fact]
    public void OrdinaryGlyphConstructorAndDeconstructionKeepTheirShape()
    {
        var glyph = new PortableTextGlyph(7, 2, 4, 1, 3, 5, 0);
        var (id, start, end, x, y, advance, bidi, font, tab, symbol) = glyph;
        Assert.Equal((7u, 2, 4, 1f, 3f, 5f, (sbyte)0, 0u, false, false),
            (id, start, end, x, y, advance, bidi, font, tab, symbol));
        Assert.False(glyph.IsInlineObject);
        Assert.True((glyph with { IsInlineObject = true }).IsInlineObject);
    }

    [Fact]
    public void TextOnlyProviderDoesNotAdvertiseInlineAdmission()
    {
        IPortableTextFormatting provider = new TextOnlyProvider();
        Assert.False(provider is IPortableInlineTextFormatting);
    }

    private sealed class TextOnlyProvider : IPortableTextFormatting
    {
        public IPortableTextParagraph Format(in PortableTextParagraphRequest request)
            => throw new NotSupportedException();
    }
}
