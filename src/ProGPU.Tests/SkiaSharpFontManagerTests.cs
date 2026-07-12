using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public class SkiaSharpFontManagerTests
{
    private const int ArabicCodepoint = 0x0622;
    private const int HanCodepoint = 0x5203;

    [Fact]
    public void ArabicLanguagePrioritizesNativeCompatibleArabicFamilies()
    {
        IReadOnlyList<string> families = SKFontManager.GetFallbackFamilyPreferences(
            new[] { "ar-SA" },
            ArabicCodepoint);

        Assert.Equal("Geeza Pro", families[0]);
        Assert.True(IndexOf(families, "Noto Naskh Arabic") < IndexOf(families, "DejaVu Sans"));
    }

    [Fact]
    public void ArabicCodepointUsesArabicFallbackWithoutLanguage()
    {
        IReadOnlyList<string> families = SKFontManager.GetFallbackFamilyPreferences(
            Array.Empty<string>(),
            ArabicCodepoint);

        Assert.Equal("Geeza Pro", families[0]);
        Assert.DoesNotContain(".DecoType Nastaleeq Urdu UI", families);
    }

    [Theory]
    [InlineData(0x0050)]
    [InlineData(0x0119)]
    [InlineData(0x039C)]
    [InlineData(0x042F)]
    public void EuropeanScriptsPrioritizeBrowserCompatibleSansFamilies(int codepoint)
    {
        IReadOnlyList<string> families = SKFontManager.GetFallbackFamilyPreferences(
            Array.Empty<string>(),
            codepoint);

        Assert.Equal("Helvetica", families[0]);
        Assert.True(IndexOf(families, "Arial") < IndexOf(families, "DejaVu Sans"));
    }

    [Fact]
    public void HebrewScriptPrioritizesNativeCompatibleHebrewFamilies()
    {
        IReadOnlyList<string> families = SKFontManager.GetFallbackFamilyPreferences(
            Array.Empty<string>(),
            0x05D0);

        Assert.Equal("Arial Hebrew", families[0]);
        Assert.True(IndexOf(families, "Lucida Grande") < IndexOf(families, "DejaVu Sans"));
    }

    [Fact]
    public void JapaneseLanguagePrioritizesJapaneseSansFamilies()
    {
        IReadOnlyList<string> families = SKFontManager.GetFallbackFamilyPreferences(
            new[] { "ja-JP" },
            HanCodepoint);

        Assert.Equal("Hiragino Sans", families[0]);
        Assert.True(IndexOf(families, "Noto Sans CJK JP") < IndexOf(families, "PingFang SC"));
    }

    [Fact]
    public void TraditionalChineseLanguagePrecedesDefaultHanFallback()
    {
        IReadOnlyList<string> families = SKFontManager.GetFallbackFamilyPreferences(
            new[] { "zh_HANT" },
            HanCodepoint);

        Assert.Equal("PingFang TC", families[0]);
        Assert.True(IndexOf(families, "Heiti TC") < IndexOf(families, "Heiti SC"));
    }

    [Fact]
    public void LanguageListPreservesCallerPreferenceOrder()
    {
        IReadOnlyList<string> families = SKFontManager.GetFallbackFamilyPreferences(
            new[] { "ko", "ja" },
            HanCodepoint);

        Assert.True(IndexOf(families, "Apple SD Gothic Neo") < IndexOf(families, "Hiragino Sans"));
    }

    [Fact]
    public void HanCodepointUsesSimplifiedChineseDefaultWithoutLanguage()
    {
        IReadOnlyList<string> families = SKFontManager.GetFallbackFamilyPreferences(
            Array.Empty<string>(),
            HanCodepoint);

        Assert.Equal("PingFang SC", families[0]);
        Assert.True(IndexOf(families, "Hiragino Sans GB") < IndexOf(families, "Heiti SC"));
        Assert.Contains("Noto Sans CJK SC", families);
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
