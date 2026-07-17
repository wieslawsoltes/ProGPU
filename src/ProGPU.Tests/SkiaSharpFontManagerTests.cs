using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public class SkiaSharpFontManagerTests
{
    private const int ArabicCodepoint = 0x0622;
    private const int HanCodepoint = 0x5203;

    [Fact]
    public void FontManagerAndStyleSetMatchNativeCatalogAndLifetimeSurface()
    {
        Assert.Equal(typeof(SKObject), typeof(SKFontManager).BaseType);
        Assert.Empty(typeof(SKFontManager).GetConstructors());
        Assert.Equal(typeof(SKObject), typeof(SKFontStyleSet).BaseType);

        var manager = SKFontManager.Default;
        var defaultHandle = manager.Handle;
        var families = manager.GetFontFamilies();
        Assert.Equal(families.Length, manager.FontFamilyCount);
        Assert.Equal(families, manager.FontFamilies);
        Assert.NotEmpty(families);
        Assert.Equal(families[0], manager.GetFamilyName(0));

        using (var styles = manager.GetFontStyles(0))
        {
            Assert.True(styles.Count > 0);
            Assert.False(string.IsNullOrWhiteSpace(styles.GetStyleName(0)));
            using var typeface = styles.CreateTypeface(0);
            Assert.NotNull(typeface);
            Assert.Equal(families[0], typeface.FamilyName);
            using var matched = styles.CreateTypeface(SKFontStyle.Bold);
            Assert.NotNull(matched);
            using var matchedAlias = styles.MatchStyle(SKFontStyle.Bold);
            Assert.NotNull(matchedAlias);
        }

        using (var missing = manager.GetFontStyles("ProGPU Missing Font Family"))
        {
            Assert.Empty(missing);
            Assert.Throws<ArgumentOutOfRangeException>(() => missing.CreateTypeface(0));
        }

        using (var generic = manager.GetFontStyles("sans-serif"))
        {
            Assert.True(generic.Count > 0);
        }

        manager.Dispose();
        Assert.Equal(defaultHandle, manager.Handle);

        using var created = SKFontManager.CreateDefault();
        Assert.NotEqual(IntPtr.Zero, created.Handle);
        created.Dispose();
        Assert.Equal(IntPtr.Zero, created.Handle);
    }

    [Fact]
    public void CharacterOverloadsResolveRenderableTypeface()
    {
        using var fromCharacter = SKFontManager.Default.MatchCharacter('A');
        using var fromFamily = SKFontManager.Default.MatchCharacter(
            SKTypeface.Default.FamilyName,
            Array.Empty<string>(),
            'A');

        Assert.NotNull(fromCharacter);
        Assert.NotNull(fromFamily);
        using var characterFont = new SKFont(fromCharacter);
        using var familyFont = new SKFont(fromFamily);
        Assert.True(characterFont.ContainsGlyph('A'));
        Assert.True(familyFont.ContainsGlyph('A'));
    }

    [Fact]
    public void MatchFamilyReturnsNullForUnknownFamily()
    {
        using var typeface = SKFontManager.Default.MatchFamily(
            "ProGPU Missing Font Family",
            SKFontStyle.Normal);

        Assert.Null(typeface);
    }

    [Fact]
    public void FromFamilyNameFallsBackToDefaultForUnknownFamily()
    {
        using var typeface = SKTypeface.FromFamilyName(
            "ProGPU Missing Font Family",
            SKFontStyle.Normal);

        Assert.Equal(SKTypeface.Default.FamilyName, typeface.FamilyName);
    }

    [Fact]
    public void MatchFamilyResolvesInstalledFamilyExactly()
    {
        var familyName = SKTypeface.Default.FamilyName;
        using var typeface = SKFontManager.Default.MatchFamily(
            familyName,
            SKFontStyle.Normal);

        Assert.NotNull(typeface);
        Assert.Equal(familyName, typeface.FamilyName);
    }

    [Fact]
    public void MatchTypefaceUsesSharedFamilyStyleMatcher()
    {
        SKTypeface source = SKTypeface.Default;
        using SKTypeface? matched = SKFontManager.Default.MatchTypeface(source, SKFontStyle.BoldItalic);

        Assert.NotNull(matched);
        Assert.Equal(source.FamilyName, matched.FamilyName);
        Assert.Equal((int)SKFontStyleWeight.Bold, matched.FontWeight);
        Assert.Equal(SKFontStyleSlant.Italic, matched.FontSlant);
    }

    [Fact]
    public void CoreTextGenericFamilyUsesNativeDefaultFallback()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var managerTypeface = SKFontManager.Default.MatchFamily(
            "monospace",
            SKFontStyle.Normal);
        using var staticTypeface = SKTypeface.FromFamilyName(
            "monospace",
            SKFontStyle.Normal);

        Assert.Null(managerTypeface);
        Assert.Equal(SKTypeface.Default.FamilyName, staticTypeface.FamilyName);
    }

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
