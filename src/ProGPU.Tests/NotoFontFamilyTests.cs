using System.Security.Cryptography;
using ProGPU.Fonts.Noto;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

public sealed class NotoFontFamilyTests
{
    [Fact]
    public void FacesAreUnmodifiedOfficialReleaseAssets()
    {
        string japaneseHash = Convert.ToHexString(SHA256.HashData(NotoFontFamily.Japanese.FontData.Span)).ToLowerInvariant();
        string symbolsHash = Convert.ToHexString(SHA256.HashData(NotoFontFamily.Symbols.FontData.Span)).ToLowerInvariant();

        Assert.Equal("2.004", NotoFontFamily.CjkVersion);
        Assert.Equal("2.006", NotoFontFamily.SymbolsVersion);
        Assert.Equal("68a3fc98800b2a27b371f2fb79991daf3633bd89309d4ffaa6946fd587f375b5", japaneseHash);
        Assert.Equal("2af28573fcdb6c72ec195908c2f95a5d82fb65c8e289a11aa7088663f4cab99b", symbolsHash);
    }

    [Theory]
    [InlineData('プ')]
    [InlineData('描')]
    [InlineData('画')]
    [InlineData('速')]
    public void JapaneseFaceCoversSampleCjk(char character)
    {
        Assert.NotEqual((ushort)0, NotoFontFamily.Japanese.GetGlyphIndex(character));
    }

    [Theory]
    [InlineData('♠')]
    [InlineData('♦')]
    [InlineData('♣')]
    [InlineData('★')]
    public void SymbolsFaceCoversSampleSymbols(char character)
    {
        Assert.NotEqual((ushort)0, NotoFontFamily.Symbols.GetGlyphIndex(character));
    }

    [Fact]
    public void RegisteredFallbacksMatchByScriptAndCharacter()
    {
        var manager = new FontManager();
        NotoFontFamily.RegisterFallbacks(manager);

        Assert.True(manager.TryMatchCharacter(
            null,
            FontStyleRequest.Normal,
            ["ja-JP"],
            '描',
            out TtfFont? japanese,
            out ushort japaneseGlyph));
        Assert.NotNull(japanese);
        Assert.NotEqual((ushort)0, japanese!.GetGlyphIndex('描'));
        Assert.NotEqual((ushort)0, japaneseGlyph);

        Assert.True(manager.TryMatchCharacter(
            null,
            FontStyleRequest.Normal,
            null,
            '♠',
            out TtfFont? symbols,
            out ushort symbolGlyph));
        Assert.Same(NotoFontFamily.Symbols, symbols);
        Assert.NotEqual((ushort)0, symbolGlyph);
    }
}
