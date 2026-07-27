using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Harfbuzz;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using ProGPU.Text;
using Xunit;

namespace Avalonia.ProGpu.ContractTests
{
    public sealed class ProGpuTextShaperContractTests
    {
        [Theory]
        [InlineData("office AV")]
        [InlineData("\u0645\u0631\u062d\u0628\u0627")]
        [InlineData("e\u0301 \U0001F600")]
        [InlineData("\uD802\uD802\uD802")]
        public void ManagedShaperMatchesHarfBuzzForRepresentativeRuns(string text)
        {
            GlyphResult[] expected = Shape(
                new HarfBuzzTextShaper(),
                text.AsMemory(),
                bidiLevel: text[0] == '\u0645' ? (sbyte)1 : (sbyte)0);
            GlyphResult[] actual = Shape(
                new ProGpuTextShaper(),
                text.AsMemory(),
                bidiLevel: text[0] == '\u0645' ? (sbyte)1 : (sbyte)0);

            AssertEquivalent(expected, actual);
        }

        [Fact]
        public void ManagedShaperPreservesSliceRelativeClustersAndContext()
        {
            const string containing = "xxoffice AVyy";
            ReadOnlyMemory<char> slice = containing.AsMemory(2, 9);

            GlyphResult[] expected = Shape(
                new HarfBuzzTextShaper(),
                slice);
            GlyphResult[] actual = Shape(
                new ProGpuTextShaper(),
                slice);

            AssertEquivalent(expected, actual);
            Assert.All(actual, glyph => Assert.InRange(glyph.Cluster, 0, slice.Length - 1));
        }

        [Fact]
        public void ManagedShaperPreservesRangedFeatureAndTabContracts()
        {
            FontFeature[] features = [FontFeature.Parse("-liga")];
            const string text = "ffi\tAV";

            GlyphResult[] expected = Shape(
                new HarfBuzzTextShaper(),
                text.AsMemory(),
                incrementalTabWidth: 37,
                features: features);
            GlyphResult[] actual = Shape(
                new ProGpuTextShaper(),
                text.AsMemory(),
                incrementalTabWidth: 37,
                features: features);

            AssertEquivalent(expected, actual);
            Assert.Contains(actual, glyph => Math.Abs(glyph.Advance - 37) < 0.001);
        }

        [Fact]
        public void ManagedShaperMatchesHarfBuzzForRightToLeftCombiningMarksWithoutFontCoverage()
        {
            const string text = "נִקּוּד";

            GlyphResult[] expected = Shape(
                new HarfBuzzTextShaper(),
                text.AsMemory(),
                bidiLevel: 1);
            GlyphResult[] actual = Shape(
                new ProGpuTextShaper(),
                text.AsMemory(),
                bidiLevel: 1);

            AssertEquivalent(expected, actual);
        }

        private static GlyphResult[] Shape(
            ITextShaperImpl shaper,
            ReadOnlyMemory<char> text,
            sbyte bidiLevel = 0,
            double incrementalTabWidth = 0,
            IReadOnlyList<FontFeature>? features = null)
        {
            using var scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ITextShaperImpl>().ToConstant(shaper);

            byte[] data = ReadInterFont();
            var manager = new FontManagerImpl(() => Array.Empty<FontInfo>());
            using var stream = new MemoryStream(data, writable: false);
            Assert.True(manager.TryCreateGlyphTypeface(
                stream,
                FontSimulations.None,
                out IPlatformTypeface? platformTypeface));
            var glyphTypeface = new GlyphTypeface(platformTypeface);
            try
            {
                ShapedBuffer shaped = shaper.ShapeText(
                    text,
                    new TextShaperOptions(
                        glyphTypeface,
                        18,
                        bidiLevel,
                        CultureInfo.GetCultureInfo("en-US"),
                        incrementalTabWidth,
                        letterSpacing: 0,
                        features));

                return shaped
                    .Select(glyph => new GlyphResult(
                        glyph.GlyphIndex,
                        glyph.GlyphCluster,
                        glyph.GlyphAdvance,
                        glyph.GlyphOffset.X,
                        glyph.GlyphOffset.Y))
                    .ToArray();
            }
            finally
            {
                glyphTypeface.Dispose();
            }
        }

        private static void AssertEquivalent(
            IReadOnlyList<GlyphResult> expected,
            IReadOnlyList<GlyphResult> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (var index = 0; index < expected.Count; index++)
            {
                Assert.Equal(expected[index].GlyphIndex, actual[index].GlyphIndex);
                Assert.Equal(expected[index].Cluster, actual[index].Cluster);
                AssertClose(expected[index].Advance, actual[index].Advance, index, nameof(GlyphResult.Advance));
                AssertClose(expected[index].OffsetX, actual[index].OffsetX, index, nameof(GlyphResult.OffsetX));
                AssertClose(expected[index].OffsetY, actual[index].OffsetY, index, nameof(GlyphResult.OffsetY));
            }
        }

        private static void AssertClose(double expected, double actual, int index, string field)
        {
            Assert.True(
                Math.Round(expected, 4) == Math.Round(actual, 4),
                $"Glyph {index} {field}: expected {expected}, actual {actual}.");
        }

        private static byte[] ReadInterFont()
        {
            return File.ReadAllBytes(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "TestAssets",
                    "Inter-Regular.ttf"));
        }

        private readonly record struct GlyphResult(
            ushort GlyphIndex,
            int Cluster,
            double Advance,
            double OffsetX,
            double OffsetY);
    }
}
