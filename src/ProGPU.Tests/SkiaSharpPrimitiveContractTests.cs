using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkiaSharpPrimitiveContractTests
{
    [Fact]
    public void PointISupportsSkiaSharpValueOperations()
    {
        var point = new SKPointI(3, 4);

        Assert.Equal(5, point.Length);
        Assert.Equal(25, point.LengthSquared);
        Assert.Equal(new SKPointI(5, 7), point + new SKSizeI(2, 3));
        Assert.Equal(5f, SKPointI.Distance(point, new SKPointI(0, 0)));
        Assert.Equal(new Vector2(3, 4), (Vector2)point);

        point.Offset(-3, -4);
        Assert.True(point.IsEmpty);
    }

    [Fact]
    public void Point3SupportsSkiaSharpValueOperations()
    {
        var point = new SKPoint3(1f, 2f, 3f);
        var offset = new SKPoint3(4f, 5f, 6f);

        Assert.Equal(new SKPoint3(5f, 7f, 9f), point + offset);
        Assert.Equal(new Vector3(1f, 2f, 3f), (Vector3)point);
        Assert.False(point.IsEmpty);
        Assert.True(SKPoint3.Empty.IsEmpty);
    }

    [Fact]
    public void SvgEnumsPreserveSkiaSharpNumericValues()
    {
        Assert.Equal(0, (int)SKTextEncoding.Utf8);
        Assert.Equal(3, (int)SKTextEncoding.GlyphId);
        Assert.Equal(0, (int)SKColorChannel.R);
        Assert.Equal(3, (int)SKColorChannel.A);
    }

    [Theory]
    [InlineData("#123", 0xFF112233u)]
    [InlineData("8123", 0x88112233u)]
    [InlineData("#123456", 0xFF123456u)]
    [InlineData("80123456", 0x80123456u)]
    [InlineData("  #00FFFFFF  ", 0x00FFFFFFu)]
    public void ColorParsingMatchesSkiaSharpHexFormats(string text, uint expected)
    {
        Assert.True(SKColor.TryParse(text, out var color));
        Assert.Equal(expected, (uint)color);
        Assert.Equal(color, SKColor.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#GGG")]
    public void ColorParsingRejectsInvalidHexStrings(string? text)
    {
        Assert.False(SKColor.TryParse(text, out var color));
        Assert.Equal((uint)SKColor.Empty, (uint)color);
    }

    [Fact]
    public void CyanMatchesAqua()
    {
        Assert.Equal((uint)SKColors.Aqua, (uint)SKColors.Cyan);
    }

    [Fact]
    public void NamedColorPaletteMatchesNativeSkiaSharp()
    {
        var rows = typeof(SKColors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(SKColor))
            .Select(field =>
            {
                var color = (SKColor)field.GetValue(null)!;
                return $"{field.Name}\t{color.Red},{color.Green},{color.Blue},{color.Alpha}";
            })
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));

        Assert.Equal(142, rows.Length);
        Assert.Equal("8637BF92DE0388C4FC891C45A150CB1CE91F73CAA4F9BC5455643DC395EB1BF0", fingerprint);
    }

    [Fact]
    public void TypefaceFallbackPreservesRequestedStyleMetadata()
    {
        using var typeface = SKTypeface.FromFamilyName(
            "ProGPU_Missing_Test_Family",
            new SKFontStyle(SKFontStyleWeight.Bold, SKFontStyleWidth.Condensed, SKFontStyleSlant.Italic));

        Assert.Equal((int)SKFontStyleWeight.Bold, typeface.FontWeight);
        Assert.Equal((int)SKFontStyleWidth.Condensed, typeface.FontWidth);
        Assert.Equal(SKFontStyleSlant.Italic, typeface.FontSlant);
    }

    [Fact]
    public void GenericSansSerifMatchesThePlatformDefaultTypeface()
    {
        using var generic = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Normal);

        Assert.Same(SKTypeface.Default.Font, generic.Font);
        Assert.Equal(SKTypeface.Default.FamilyName, generic.FamilyName);
        Assert.NotEqual("Default", generic.FamilyName);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("Helvetica", generic.FamilyName);
        }
    }

    [Theory]
    [InlineData(SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, 400, false)]
    [InlineData(SKFontStyleWeight.Normal, SKFontStyleSlant.Italic, 400, true)]
    [InlineData(SKFontStyleWeight.Bold, SKFontStyleSlant.Upright, 700, false)]
    [InlineData(SKFontStyleWeight.Bold, SKFontStyleSlant.Italic, 700, true)]
    public void GenericSerifSelectsRequestedMacOsFace(
        SKFontStyleWeight weight,
        SKFontStyleSlant slant,
        int expectedIntrinsicWeight,
        bool expectedIntrinsicItalic)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var typeface = SKTypeface.FromFamilyName(
            "serif",
            new SKFontStyle(weight, SKFontStyleWidth.Normal, slant));

        Assert.Equal("Times", typeface.FamilyName);
        Assert.Equal(expectedIntrinsicWeight, typeface.Font.WeightClass);
        Assert.Equal(expectedIntrinsicItalic, typeface.Font.IsItalic);
    }

    [Fact]
    public void FontMetricsKeepGlobalBoundsDistinctFromLineMetrics()
    {
        using var font = new SKFont(SKTypeface.Default, 24f);

        var metrics = font.Metrics;

        Assert.True(metrics.Top <= metrics.Ascent);
        Assert.True(metrics.Bottom >= metrics.Descent);
        Assert.True(metrics.Top < metrics.Ascent || metrics.Bottom > metrics.Descent);
        Assert.True(metrics.UnderlineThickness > 0f);
        Assert.True(metrics.UnderlinePosition >= 0f);
        Assert.True(metrics.StrikeoutThickness > 0f);
        Assert.True(metrics.StrikeoutPosition <= 0f);
    }
}
