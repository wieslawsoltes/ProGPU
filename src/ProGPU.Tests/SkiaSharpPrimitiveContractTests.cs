using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        Assert.Equal(3, (int)SKRegionOperation.XOR);
        Assert.Equal("XOR", Enum.GetName(SKRegionOperation.XOR));
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
        Assert.False(SKColor.TryParse(text!, out var color));
        Assert.Equal((uint)SKColor.Empty, (uint)color);
    }

    [Fact]
    public void ColorSpanParsingIsAllocationFreeAfterWarmup()
    {
        ReadOnlySpan<char> text = "  #7f123456  ";
        Assert.True(SKColor.TryParse(text, out var expected));
        Assert.Equal(0x7f123456u, (uint)expected);
        Assert.Equal(expected, SKColor.Parse(text));

        _ = SKColor.TryParse(text, out _);
        uint checksum = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            if (!SKColor.TryParse(text, out var color))
            {
                throw new InvalidOperationException("The fixed test color must parse.");
            }

            checksum ^= (uint)color;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0u, checksum);
    }

    [Fact]
    public void CyanMatchesAqua()
    {
        Assert.Equal((uint)SKColors.Aqua, (uint)SKColors.Cyan);
    }

    [Fact]
    public void NamedColorPaletteMatchesNativeSkiaSharp()
    {
        var fields = typeof(SKColors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(SKColor))
            .ToArray();
        var rows = fields
            .Select(field =>
            {
                var color = (SKColor)field.GetValue(null)!;
                return $"{field.Name}\t{color.Red},{color.Green},{color.Blue},{color.Alpha}";
            })
            .Append($"{nameof(SKColors.Empty)}\t{SKColors.Empty.Red},{SKColors.Empty.Green},{SKColors.Empty.Blue},{SKColors.Empty.Alpha}")
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));

        Assert.Equal(141, fields.Length);
        Assert.DoesNotContain(fields, static field => field.Name == nameof(SKColors.Empty));
        Assert.NotNull(typeof(SKColors).GetProperty(nameof(SKColors.Empty), BindingFlags.Public | BindingFlags.Static));
        Assert.Equal(142, rows.Length);
        Assert.Equal("8637BF92DE0388C4FC891C45A150CB1CE91F73CAA4F9BC5455643DC395EB1BF0", fingerprint);
    }

    [Fact]
    public void TypefaceFallbackUsesNativePlatformStyleSemantics()
    {
        using var typeface = SKTypeface.FromFamilyName(
            "ProGPU_Missing_Test_Family",
            new SKFontStyle(SKFontStyleWeight.Bold, SKFontStyleWidth.Condensed, SKFontStyleSlant.Italic));

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(SKTypeface.Default.FontWeight, typeface.FontWeight);
            Assert.Equal(SKTypeface.Default.FontWidth, typeface.FontWidth);
            Assert.Equal(SKTypeface.Default.FontSlant, typeface.FontSlant);
        }
        else
        {
            Assert.Equal((int)SKFontStyleWeight.Bold, typeface.FontWeight);
            Assert.Equal((int)SKFontStyleWidth.Condensed, typeface.FontWidth);
            Assert.Equal(SKFontStyleSlant.Italic, typeface.FontSlant);
        }
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
    [InlineData(SKFontStyleWeight.Normal, SKFontStyleSlant.Upright)]
    [InlineData(SKFontStyleWeight.Normal, SKFontStyleSlant.Italic)]
    [InlineData(SKFontStyleWeight.Bold, SKFontStyleSlant.Upright)]
    [InlineData(SKFontStyleWeight.Bold, SKFontStyleSlant.Italic)]
    public void GenericSerifUsesMacOsDefaultTypeface(
        SKFontStyleWeight weight,
        SKFontStyleSlant slant)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var typeface = SKTypeface.FromFamilyName(
            "serif",
            new SKFontStyle(weight, SKFontStyleWidth.Normal, slant));

        Assert.Equal(SKTypeface.Default.FamilyName, typeface.FamilyName);
        Assert.Same(SKTypeface.Default.Font, typeface.Font);
        Assert.Equal(SKTypeface.Default.FontWeight, typeface.FontWeight);
        Assert.Equal(SKTypeface.Default.FontWidth, typeface.FontWidth);
        Assert.Equal(SKTypeface.Default.FontSlant, typeface.FontSlant);
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

    [Fact]
    public void FontMetricsUseTheOfficialCompactFlagsAndFloatLayout()
    {
        var fieldTypes = typeof(SKFontMetrics)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderBy(static field => field.MetadataToken)
            .Select(static field => field.FieldType)
            .ToArray();

        Assert.Equal(16, fieldTypes.Length);
        Assert.Equal(typeof(uint), fieldTypes[0]);
        Assert.All(fieldTypes[1..], static type => Assert.Equal(typeof(float), type));
        Assert.Equal(64, Unsafe.SizeOf<SKFontMetrics>());

        var empty = default(SKFontMetrics);
        Assert.Null(empty.UnderlineThickness);
        Assert.Null(empty.UnderlinePosition);
        Assert.Null(empty.StrikeoutThickness);
        Assert.Null(empty.StrikeoutPosition);
    }
}
