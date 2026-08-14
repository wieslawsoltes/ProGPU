using System.Security.Cryptography;
using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

public sealed class InterFontFamilyTests
{
    [Fact]
    public void NativeSfntCheckpointMatchesManagedMediumFaceMetrics()
    {
        var assembly = typeof(InterFontFamily).Assembly;
        string resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith("Inter-Medium.ttf", StringComparison.Ordinal));
        using Stream resource = assembly.GetManifestResourceStream(resourceName)!;
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        byte[] data = memory.ToArray();
        SfntFontFace face = SfntFontFace.Load(data);
        var font = new TtfFont(data);

        Assert.Equal((ushort)2048, font.UnitsPerEm);
        Assert.Equal((short)-1546, font.XMin);
        Assert.Equal((short)-668, font.YMin);
        Assert.Equal((short)5290, font.XMax);
        Assert.Equal((short)2272, font.YMax);
        Assert.Equal((short)1984, font.Ascender);
        Assert.Equal((short)-494, font.Descender);
        Assert.Equal((short)0, font.LineGap);
        Assert.Equal((ushort)2937, font.NumGlyphs);
        Assert.True(face.TryGetGlyphIndex(0x53, out ushort glyphIndex));
        Assert.Equal((ushort)397, glyphIndex);
        Assert.True(face.TryGetHorizontalGlyphMetrics(glyphIndex, out var metrics));
        Assert.Equal((ushort)1323, metrics.AdvanceWidth);
        Assert.Equal((short)106, metrics.LeftSideBearing);
        Assert.True(face.TryGetGlyphBounds(glyphIndex, out var bounds));
        Assert.Equal((short)106, bounds.XMin);
        Assert.Equal((short)-25, bounds.YMin);
        Assert.Equal((short)1217, bounds.XMax);
        Assert.Equal((short)1510, bounds.YMax);
        var outline = Assert.IsType<ProGPU.Vector.PathGeometry>(
            font.GetGlyphOutline(glyphIndex));
        var figure = Assert.Single(outline.Figures);
        Assert.Equal(new Vector2(665f, -25f), figure.StartPoint);
        Assert.Equal(34, figure.Segments.Count);
        Assert.Equal(13245664145576799719UL, HashOutline(outline));

        Assert.True(face.TryGetGlyphIndex(0x00E9, out ushort compositeIndex));
        Assert.Equal((ushort)618, compositeIndex);
        var composite = Assert.IsType<ProGPU.Vector.PathGeometry>(
            font.GetGlyphOutline(compositeIndex));
        Assert.Equal(3, composite.Figures.Count);
        Assert.Equal(27, composite.Figures.Sum(static item => item.Segments.Count));
        Assert.Equal(new Vector2(630f, -23f), composite.Figures[0].StartPoint);
        Assert.Equal(5543379682355176128UL, HashOutline(composite));
    }

    [Fact]
    public void NativeFvarCheckpointMatchesManagedVariableAxes()
    {
        var assembly = typeof(InterFontFamily).Assembly;
        string resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith("InterVariable.ttf", StringComparison.Ordinal));
        using Stream resource = assembly.GetManifestResourceStream(resourceName)!;
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var font = new TtfFont(memory.ToArray());

        Assert.Collection(
            font.VariationAxes,
            axis =>
            {
                Assert.Equal("opsz", axis.Tag);
                Assert.Equal(14f, axis.Minimum);
                Assert.Equal(14f, axis.Default);
                Assert.Equal(32f, axis.Maximum);
                Assert.False(axis.IsHidden);
            },
            axis =>
            {
                Assert.Equal("wght", axis.Tag);
                Assert.Equal(100f, axis.Minimum);
                Assert.Equal(400f, axis.Default);
                Assert.Equal(900f, axis.Maximum);
                Assert.False(axis.IsHidden);
            });
    }

    private static ulong HashOutline(ProGPU.Vector.PathGeometry outline)
    {
        ulong hash = 1469598103934665603UL;
        foreach (ProGPU.Vector.PathFigure figure in outline.Figures)
        {
            Vector2 current = figure.StartPoint;
            foreach (ProGPU.Vector.PathSegment segment in figure.Segments)
            {
                Vector2 p0 = current;
                uint kind;
                Vector2 p1;
                Vector2 p2;
                switch (segment)
                {
                    case ProGPU.Vector.LineSegment line:
                        kind = 0;
                        p1 = line.Point;
                        p2 = default;
                        current = line.Point;
                        break;
                    case ProGPU.Vector.QuadraticBezierSegment quadratic:
                        kind = 1;
                        p1 = quadratic.ControlPoint;
                        p2 = quadratic.Point;
                        current = quadratic.Point;
                        break;
                    default:
                        throw new InvalidOperationException(segment.GetType().Name);
                }
                hash = Append(hash, kind);
                hash = Append(hash, BitConverter.SingleToUInt32Bits(p0.X));
                hash = Append(hash, BitConverter.SingleToUInt32Bits(p0.Y));
                hash = Append(hash, BitConverter.SingleToUInt32Bits(p1.X));
                hash = Append(hash, BitConverter.SingleToUInt32Bits(p1.Y));
                hash = Append(hash, BitConverter.SingleToUInt32Bits(p2.X));
                hash = Append(hash, BitConverter.SingleToUInt32Bits(p2.Y));
            }
        }
        return hash;

        static ulong Append(ulong value, uint field) =>
            unchecked((value ^ field) * 1099511628211UL);
    }

    [Fact]
    public void RegularFaceIsUnmodifiedOfficialInter41Asset()
    {
        var font = InterFontFamily.Regular;
        Assert.True(font.UsesExternalStorage);
        var hash = Convert.ToHexString(SHA256.HashData(font.FontData.Span)).ToLowerInvariant();

        Assert.Equal("4.1", InterFontFamily.Version);
        Assert.Equal("40d692fce188e4471e2b3cba937be967878f631ad3ebbbdcd587687c7ebe0c82", hash);
        Assert.Equal("Inter", font.FamilyName);
        Assert.Equal("Regular", font.SubfamilyName);
        Assert.Equal((ushort)400, font.WeightClass);
    }

    [Fact]
    public void EmbeddedResourceSliceBorrowsAssemblyImageWithoutManagedPayloadCopy()
    {
        var assembly = typeof(InterFontFamily).Assembly;
        string resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith("Inter-Regular.ttf", StringComparison.Ordinal));
        using Stream resource = assembly.GetManifestResourceStream(resourceName)!;

        var font = TtfFont.LoadEmbeddedResourceSlice(
            assembly,
            resourceName,
            0,
            checked((int)resource.Length));

        Assert.True(font.UsesExternalStorage);
        Assert.Null(font.OwnedFontData);
        Assert.Equal(
            SHA256.HashData(InterFontFamily.Regular.FontData.Span),
            SHA256.HashData(font.FontData.Span));
        Assert.Equal("Inter", font.FamilyName);
    }

    [Fact]
    public void OpenResourceStreamSliceTransfersZeroCopyStorageWithoutAssemblyContract()
    {
        var assembly = typeof(InterFontFamily).Assembly;
        string resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith("Inter-Regular.ttf", StringComparison.Ordinal));
        Stream resource = assembly.GetManifestResourceStream(resourceName)!;
        int resourceLength = checked((int)resource.Length);

        var font = TtfFont.LoadEmbeddedResourceSlice(
            resource,
            0,
            resourceLength);

        Assert.True(font.UsesExternalStorage);
        Assert.Null(font.OwnedFontData);
        Assert.Equal(
            SHA256.HashData(InterFontFamily.Regular.FontData.Span),
            SHA256.HashData(font.FontData.Span));
        Assert.Equal("Inter", font.FamilyName);
    }

    [Fact]
    public void EmbeddedResourceSliceRejectsRangesOutsideResource()
    {
        var assembly = typeof(InterFontFamily).Assembly;
        string resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            static name => name.EndsWith("Inter-Regular.ttf", StringComparison.Ordinal));
        using Stream resource = assembly.GetManifestResourceStream(resourceName)!;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TtfFont.LoadEmbeddedResourceSlice(
                assembly,
                resourceName,
                checked((int)resource.Length),
                1));
    }

    [Fact]
    public void CompleteStaticTextAndDisplayFamiliesAreRegisteredLazily()
    {
        var manager = new FontManager();
        InterFontFamily.RegisterFonts(manager);

        Assert.Equal(18, manager.GetFontStyles(InterFontFamily.TextFamilyName).Count);
        Assert.Equal(18, manager.GetFontStyles(InterFontFamily.DisplayFamilyName).Count);
        Assert.Equal(2, manager.GetFontStyles(InterFontFamily.VariableFamilyName).Count);

        TtfFont boldItalic = Assert.IsType<TtfFont>(manager.MatchFamily(
            InterFontFamily.TextFamilyName,
            new FontStyleRequest(700, 5, FontSlant.Italic)));
        Assert.Equal((ushort)700, boldItalic.WeightClass);
        Assert.True(boldItalic.IsItalic);
        Assert.Equal("Inter", boldItalic.FamilyName);

        TtfFont display = Assert.IsType<TtfFont>(manager.MatchFamily(
            InterFontFamily.DisplayFamilyName,
            FontStyleRequest.Normal));
        Assert.Equal("Inter Display", display.FamilyName);
        Assert.Equal((ushort)400, display.WeightClass);

        TtfFont variable = Assert.IsType<TtfFont>(manager.MatchFamily(
            InterFontFamily.VariableFamilyName,
            new FontStyleRequest(637, 5, FontSlant.Italic)));
        Assert.Equal((ushort)637, variable.WeightClass);
        Assert.True(variable.IsItalic);
    }

    [Fact]
    public void RepresentativeStaticFacesMatchOfficialReleaseHashes()
    {
        static string Hash(TtfFont font) =>
            Convert.ToHexString(SHA256.HashData(font.FontData.Span)).ToLowerInvariant();

        Assert.Equal("288316099b1e0a47a4716d159098005eef7c0066921f34e3200393dbdb01947f", Hash(InterFontFamily.Bold));
        Assert.Equal("bbc051dd204b5019a1aa0bc0ae2aa8a05ab13e7a3f979fa357631dc7feb6833a", Hash(InterFontFamily.Italic));
        Assert.Equal("99614bda7ff423aaf470990692dd93613a5971ab4446e4a6d5a83b3d74865074", Hash(InterFontFamily.Display));
    }

    [Fact]
    public void PackageEmbedsAllOfficialStaticAndVariableFaces()
    {
        string[] resources = typeof(InterFontFamily).Assembly.GetManifestResourceNames();
        Assert.Equal(38, resources.Count(static name =>
            name.StartsWith("ProGPU.Fonts.Inter.Fonts.Inter", StringComparison.Ordinal) &&
            name.EndsWith(".ttf", StringComparison.Ordinal)));
    }

    [Fact]
    public void VariableFacesAreUnmodifiedAndExposeStandardAxes()
    {
        static string Hash(TtfFont font) =>
            Convert.ToHexString(SHA256.HashData(font.FontData.Span)).ToLowerInvariant();

        Assert.Equal("4989b125924991b90d05b2d16e0e388c48f7d5bb8b30539bbf9c755278d0ccaf", Hash(InterFontFamily.Variable));
        Assert.Equal("d6f1f6a172d9e588438db9f986fd5cfad7b30f644374080a8a9d4d91e344586f", Hash(InterFontFamily.VariableItalic));

        FontVariationAxis weight = Assert.Single(
            InterFontFamily.Variable.VariationAxes,
            static axis => axis.Tag == "wght");
        FontVariationAxis opticalSize = Assert.Single(
            InterFontFamily.Variable.VariationAxes,
            static axis => axis.Tag == "opsz");
        Assert.Equal((100f, 400f, 900f), (weight.Minimum, weight.Default, weight.Maximum));
        Assert.Equal((14f, 14f, 32f), (opticalSize.Minimum, opticalSize.Default, opticalSize.Maximum));
    }

    [Fact]
    public void TypefaceMatchingUsesSeparateVariableItalicFaceWhenSlantIsNotAnAxis()
    {
        var manager = new FontManager();
        InterFontFamily.RegisterFonts(manager);
        TtfFont upright = Assert.IsType<TtfFont>(manager.MatchFamily(
            InterFontFamily.VariableFamilyName,
            new FontStyleRequest(400, 5, FontSlant.Upright)));

        TtfFont italic = manager.MatchTypeface(
            upright,
            new FontStyleRequest(637, 5, FontSlant.Italic));

        Assert.NotSame(upright, italic);
        Assert.True(italic.IsItalic);
        Assert.Equal((ushort)637, italic.WeightClass);
        Assert.DoesNotContain(italic.VariationAxes, static axis => axis.Tag is "ital" or "slnt");
    }

    [Theory]
    [InlineData(100f, 14f, 100, false)]
    [InlineData(537f, 23f, 537, false)]
    [InlineData(900f, 32f, 900, false)]
    [InlineData(650f, 18f, 650, true)]
    public void VariableInstancesAreImmutableCachedAndCarryStyleMetadata(
        float weight,
        float opticalSize,
        int expectedWeight,
        bool italic)
    {
        FontSlant slant = italic ? FontSlant.Italic : FontSlant.Upright;
        TtfFont first = InterFontFamily.GetVariableFont(weight, opticalSize, slant);
        TtfFont second = InterFontFamily.GetVariableFont(weight, opticalSize, slant);

        Assert.Same(first, second);
        Assert.Equal((ushort)expectedWeight, first.WeightClass);
        Assert.Equal(italic, first.IsItalic);
        Assert.Equal(weight, Assert.Single(first.VariationSettings, static value => value.Tag == "wght").Value);
        Assert.Equal(opticalSize, Assert.Single(first.VariationSettings, static value => value.Tag == "opsz").Value);
    }

    [Fact]
    public void RotatedVariableInstancesKeepCanonicalDictionaryIdentity()
    {
        TtfFont first = InterFontFamily.GetVariableFont(250f, 18f);
        for (var weight = 300; weight < 350; weight++)
        {
            _ = InterFontFamily.GetVariableFont(weight, 18f);
        }

        TtfFont recreated = InterFontFamily.GetVariableFont(250f, 18f);
        Assert.NotSame(first, recreated);
        Assert.Equal(first, recreated);
        Assert.Equal(first.GetHashCode(), recreated.GetHashCode());

        var retainedCache = new Dictionary<TtfFont, string> { [first] = "retained" };
        Assert.Equal("retained", retainedCache[recreated]);

        byte[] staticData = InterFontFamily.Regular.FontData.ToArray();
        Assert.NotEqual(new TtfFont(staticData), new TtfFont(staticData));
    }

    [Theory]
    [InlineData('A')]
    [InlineData('g')]
    [InlineData('0')]
    public void VariableAxisExtremesMatchOfficialStaticMasterMetrics(char character)
    {
        AssertMaster(InterFontFamily.GetVariableFont(100, 14), InterFontFamily.GetFont(100), character);
        AssertMaster(InterFontFamily.GetVariableFont(900, 32), InterFontFamily.GetFont(900, display: true), character);

        static void AssertMaster(TtfFont variable, TtfFont expected, char character)
        {
            ushort variableGlyph = variable.GetGlyphIndex(character);
            ushort expectedGlyph = expected.GetGlyphIndex(character);
            Assert.Equal(
                expected.GetAdvanceWidth(expectedGlyph, expected.UnitsPerEm),
                variable.GetAdvanceWidth(variableGlyph, variable.UnitsPerEm),
                0.51f);

            Assert.True(variable.GetGlyphOutline(variableGlyph)!.TryGetBounds(out Vector2 variableMin, out Vector2 variableMax));
            Assert.True(expected.GetGlyphOutline(expectedGlyph)!.TryGetBounds(out Vector2 expectedMin, out Vector2 expectedMax));
            Assert.InRange(Vector2.Distance(variableMin, expectedMin), 0f, 1.5f);
            Assert.InRange(Vector2.Distance(variableMax, expectedMax), 0f, 1.5f);
        }
    }

    [Theory]
    [InlineData('A', 1430f, 33, 0, 1397, 1490)]
    [InlineData('g', 1231f, 80, -437, 1101, 1104)]
    [InlineData('0', 1311f, 93, -22, 1218, 1512)]
    [InlineData('é', 1164f, 80, -24, 1088, 1529)]
    public void MidAxisOutlinesAndMetricsMatchOpenTypeReferenceInstantiation(
        char character,
        float expectedAdvance,
        short expectedMinX,
        short expectedMinY,
        short expectedMaxX,
        short expectedMaxY)
    {
        TtfFont font = InterFontFamily.GetVariableFont(537, 23);
        ushort glyph = font.GetGlyphIndex(character);

        Assert.Equal(expectedAdvance, font.GetAdvanceWidth(glyph, font.UnitsPerEm), 0.51f);
        Assert.True(font.TryGetGlyphBounds(glyph, out short minX, out short minY, out short maxX, out short maxY));
        Assert.InRange(minX, expectedMinX - 1, expectedMinX + 1);
        Assert.InRange(minY, expectedMinY - 1, expectedMinY + 1);
        Assert.InRange(maxX, expectedMaxX - 1, expectedMaxX + 1);
        Assert.InRange(maxY, expectedMaxY - 1, expectedMaxY + 1);
    }

    [Fact]
    public void RichTextDesiredWidthUsesResolvedBoldFaceMetrics()
    {
        InterFontFamily.RegisterFonts();
        var text = new RichTextBlock
        {
            Font = InterFontFamily.Regular,
            FontSize = 11f
        };
        text.Inlines.Add(new Bold(new Run("WinUI")));

        text.Measure(new Vector2(float.PositiveInfinity, float.PositiveInfinity));

        TtfFont bold = InterFontFamily.Bold;
        float expectedWidth = 0f;
        foreach (char character in "WinUI")
        {
            expectedWidth += bold.GetAdvanceWidth(bold.GetGlyphIndex(character), 11f);
        }

        Assert.InRange(text.DesiredSize.X, expectedWidth - 0.01f, expectedWidth + 0.01f);
        Assert.All(text.PositionedChars, positioned => Assert.Same(bold, positioned.Info.Font));
    }

    [Theory]
    [InlineData('A')]
    [InlineData('\u25cf')]
    [InlineData('\u03a6')]
    [InlineData('\u042f')]
    [InlineData('\u2665')]
    public void RegularFaceContainsRequiredUiAndSampleGlyphs(char character)
    {
        Assert.NotEqual(0, InterFontFamily.Regular.GetGlyphIndex(character));
    }
}
