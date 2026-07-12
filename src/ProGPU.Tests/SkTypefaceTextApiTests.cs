#pragma warning disable CS0618

using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkTypefaceTextApiTests
{
    [Fact]
    public void EmptyTypefaceKeepsAllFontDataSurfacesEmpty()
    {
        var typeface = SKTypeface.Empty;
        using var font = typeface.ToFont(40f);

        Assert.True(typeface.IsEmpty);
        Assert.Equal(string.Empty, typeface.FamilyName);
        Assert.Equal(string.Empty, typeface.PostScriptName);
        Assert.Equal(0, typeface.GlyphCount);
        Assert.Equal(0, typeface.TableCount);
        Assert.Equal(0, typeface.UnitsPerEm);
        Assert.False(typeface.IsFixedPitch);
        Assert.False(typeface.TryGetTableTags(out var tags));
        Assert.Empty(tags);
        Assert.Equal(new ushort[] { 0 }, typeface.GetGlyphs("A"));
        Assert.False(typeface.ContainsGlyphs("A"));
        Assert.Equal(0f, font.MeasureText("A", out var bounds));
        Assert.True(bounds.IsEmpty);
        Assert.Equal(0f, font.Spacing);
        Assert.Null(typeface.OpenStream());
        Assert.Null(typeface.OpenStream(out var ttcIndex));
        Assert.Equal(0, ttcIndex);
    }

    [Fact]
    public void GlyphCompatibilityMethodsDelegateToSkFontSemantics()
    {
        var typeface = SKTypeface.Default;
        using var font = typeface.ToFont();
        const string text = "A\U0001F600B";
        var utf8 = Encoding.UTF8.GetBytes(text);

        Assert.Equal(font.GetGlyphs(text), typeface.GetGlyphs(text));
        Assert.Equal(font.GetGlyphs(utf8, SKTextEncoding.Utf8),
            typeface.GetGlyphs(utf8, SKTextEncoding.Utf8));
        Assert.Equal(font.CountGlyphs(text), typeface.CountGlyphs(text));
        Assert.Equal(font.CountGlyphs(utf8, SKTextEncoding.Utf8),
            typeface.CountGlyphs(utf8, SKTextEncoding.Utf8));
        Assert.Equal(font.ContainsGlyphs(text), typeface.ContainsGlyphs(text));
        Assert.Equal(font.GetGlyph('A'), typeface.GetGlyph('A'));

        var malformed = new string(new[] { 'A', '\ud800', 'B' });
        Assert.Empty(typeface.GetGlyphs(malformed));
        Assert.Equal(0, typeface.CountGlyphs(malformed));
        Assert.True(typeface.ContainsGlyphs(malformed));
    }

    [Fact]
    public void TypefaceMetadataComesFromParsedSfntTables()
    {
        var typeface = SKTypeface.Default;

        Assert.Equal(typeface.Font.NumGlyphs, typeface.GlyphCount);
        Assert.Equal(typeface.Font.PostScriptName, typeface.PostScriptName);
        Assert.Equal(typeface.Font.IsFixedPitch, typeface.IsFixedPitch);
        Assert.Equal(typeface.Font.TableTags.Count, typeface.TableCount);
        Assert.False(typeface.IsEmpty);
    }

    [Fact]
    public void TableTagsAndDataExposeExactSfntBytes()
    {
        var typeface = SKTypeface.Default;
        var headTag = MakeTag("head");
        var missingTag = MakeTag("ZZZZ");

        Assert.True(typeface.TryGetTableTags(out var tags));
        Assert.Equal(typeface.TableCount, tags.Length);
        Assert.Contains(headTag, tags);
        Assert.True(typeface.Font.TryGetTable("head", out var expected));

        var data = typeface.GetTableData(headTag);
        Assert.Equal(expected.Length, typeface.GetTableSize(headTag));
        Assert.Equal(expected.ToArray(), data);
        Assert.Equal(0, typeface.GetTableSize(missingTag));
        Assert.Throws<Exception>(() => typeface.GetTableData(missingTag));
    }

    [Fact]
    public void TableSliceCopiesOnlyRequestedBytes()
    {
        var typeface = SKTypeface.Default;
        var headTag = MakeTag("head");
        Assert.True(typeface.Font.TryGetTable("head", out var head));
        var destination = Marshal.AllocHGlobal(12);
        try
        {
            Marshal.Copy(Enumerable.Repeat((byte)0xcc, 12).ToArray(), 0, destination, 12);

            Assert.True(typeface.TryGetTableData(headTag, 2, 8, destination));

            var actual = new byte[12];
            Marshal.Copy(destination, actual, 0, actual.Length);
            Assert.Equal(head.Span.Slice(2, 8).ToArray(), actual.AsSpan(0, 8).ToArray());
            Assert.All(actual.AsSpan(8).ToArray(), value => Assert.Equal((byte)0xcc, value));
            Assert.False(typeface.TryGetTableData(headTag, -1, 8, destination));
            Assert.False(typeface.TryGetTableData(headTag, 0, 0, destination));
            Assert.False(typeface.TryGetTableData(headTag, head.Length - 2, 8, destination));
            Assert.False(typeface.TryGetTableData(headTag, 0, 8, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void LegacyKerningApiClearsOnlyPairSlots()
    {
        var typeface = SKTypeface.Default;
        var glyphs = typeface.GetGlyphs("AVTo");
        var destination = Enumerable.Repeat(999, glyphs.Length + 2).ToArray();

        Assert.False(typeface.HasGetKerningPairAdjustments);
        Assert.False(typeface.GetKerningPairAdjustments(glyphs, destination));
        Assert.All(destination.AsSpan(0, glyphs.Length - 1).ToArray(), value => Assert.Equal(0, value));
        Assert.All(destination.AsSpan(glyphs.Length - 1).ToArray(), value => Assert.Equal(999, value));
        Assert.Throws<ArgumentException>(() =>
            typeface.GetKerningPairAdjustments(glyphs, new int[glyphs.Length - 2]));
    }

    [Fact]
    public void TypefaceFactoriesAndFontsPreserveNativeDefaults()
    {
        var defaultTypeface = SKTypeface.CreateDefault();
        using var familyTypeface = SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName);
        using var fallbackTypeface = SKTypeface.FromFamilyName(null);
        using var defaultFont = defaultTypeface.ToFont();
        using var customFont = familyTypeface.ToFont(24f, 1.25f, 0.2f);

        Assert.Same(SKTypeface.Default, defaultTypeface);
        Assert.Equal(SKTypeface.Default.FamilyName, familyTypeface.FamilyName);
        Assert.Equal(SKTypeface.Default.FamilyName, fallbackTypeface.FamilyName);
        Assert.Equal(12f, defaultFont.Size);
        Assert.Equal(1f, defaultFont.ScaleX);
        Assert.Equal(0f, defaultFont.SkewX);
        Assert.Equal(24f, customFont.Size);
        Assert.Equal(1.25f, customFont.ScaleX);
        Assert.Equal(0.2f, customFont.SkewX);
    }

    private static uint MakeTag(string tag) =>
        ((uint)tag[0] << 24) |
        ((uint)tag[1] << 16) |
        ((uint)tag[2] << 8) |
        tag[3];
}

#pragma warning restore CS0618
