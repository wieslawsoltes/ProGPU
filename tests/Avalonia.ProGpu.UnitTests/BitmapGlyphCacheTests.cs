using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ProGPU.Scene;
using ProGPU.Text;
using Xunit;
using AvaloniaGlyphInfo = Avalonia.Media.TextFormatting.GlyphInfo;

namespace Avalonia.ProGpu.UnitTests
{
    public class BitmapGlyphCacheTests
    {
        [Fact]
        public void SbixOriginOffsetsPlaceBitmapRelativeToBaseline()
        {
            var metrics = new BitmapGlyphMetrics(
                pixelsPerEm: 20,
                pixelsPerInch: 72,
                originOffsetX: 2,
                originOffsetY: 5,
                pixelWidth: 20,
                pixelHeight: 18);

            var bounds = metrics.GetBounds(new Point(100, 50), emSize: 40);

            Assert.Equal(new Rect(96, 24, 40, 36), bounds);
        }

        [Fact]
        public void BitmapMetricsCacheRetainsNoDecodedPixelArrays()
        {
            const string fontPath =
                "/System/Library/Fonts/Apple Color Emoji.ttc";
            if (!OperatingSystem.IsMacOS() || !File.Exists(fontPath))
                return;

            var font = new TtfFont(fontPath, 0);
            ushort glyph = font.GetGlyphIndex(0x1F600);

            Assert.NotEqual(0, glyph);
            Assert.True(BitmapGlyphCache.TryGetMetrics(
                font,
                glyph,
                64,
                out var metrics));
            Assert.True(metrics.PixelWidth > 0);
            Assert.True(metrics.PixelHeight > 0);
            Assert.Equal(0, BitmapGlyphCache.CachedDecodedPixelBytes);
            Assert.InRange(
                BitmapGlyphCache.CachedMetricCount,
                1,
                BitmapGlyphCache.MaximumCachedMetricCount);
        }

        [Fact]
        public void SolidBitmapGlyphRunUsesBoundedCompositorAtlasCommand()
        {
            const string fontPath =
                "/System/Library/Fonts/Apple Color Emoji.ttc";
            if (!OperatingSystem.IsMacOS() || !File.Exists(fontPath))
                return;

            var font = new TtfFont(fontPath, 0);
            ushort glyph = font.GetGlyphIndex(0x1F600);
            Assert.NotEqual(0, glyph);
            Assert.True(font.TryGetBitmapGlyph(glyph, 64, out _));

            var platformTypeface = new ProGpuTypeface(
                font,
                font.FontData,
                font.FamilyName,
                FontWeight.Normal,
                FontStyle.Normal,
                FontStretch.Normal);
            var glyphTypeface = new GlyphTypeface(platformTypeface);
            using var glyphRun = new GlyphRunImpl(
                glyphTypeface,
                64,
                new[]
                {
                    new AvaloniaGlyphInfo(
                        glyph,
                        0,
                        font.GetAdvanceWidth(glyph, 64))
                },
                new Point(5, 70));
            using var target = new DrawingContextImpl(
                new DrawingContextImpl.CreateInfo
                {
                    Dpi = new Vector(96, 96)
                });

            target.DrawGlyphRun(Brushes.White, glyphRun);

            RenderCommand command =
                Assert.Single(target.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.DrawGlyphRun, command.Type);
            Assert.True(command.PreferGlyphAtlas);
            Assert.Same(glyphRun.GlyphIndices, command.GlyphIndices);
            Assert.Same(
                glyphRun.ProGpuGlyphPositions,
                command.GlyphPositions);
            Assert.DoesNotContain(
                target.DrawingContext.Commands,
                static item => item.Type == RenderCommandType.DrawTexture);
        }
    }
}
