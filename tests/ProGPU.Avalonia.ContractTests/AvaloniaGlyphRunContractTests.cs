using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Xunit;
using TtfFont = ProGPU.Text.TtfFont;

namespace Avalonia.ProGpu.ContractTests;

public sealed class AvaloniaGlyphRunContractTests
{
    [Fact]
    public void ShapedGlyphsBecomeOneImmutablePositionedRun()
    {
        var manager = new FontManagerImpl();
        using var stream = File.OpenRead(
            Path.Combine(
                AppContext.BaseDirectory,
                "TestAssets",
                "Inter-Regular.ttf"));
        Assert.True(manager.TryCreateGlyphTypeface(
            stream,
            FontSimulations.None,
            out IPlatformTypeface? platformTypeface));

        var glyphTypeface = new GlyphTypeface(platformTypeface);
        try
        {
            var proGpuTypeface = Assert.IsType<ProGpuTypeface>(platformTypeface);
            ushort glyphA = proGpuTypeface.Font.GetGlyphIndex('A');
            ushort glyphV = proGpuTypeface.Font.GetGlyphIndex('V');
            GlyphInfo[] snapshot =
            [
                new GlyphInfo(
                    glyphA,
                    GlyphCluster: 0,
                    proGpuTypeface.Font.GetAdvanceWidth(glyphA, 20)),
                new GlyphInfo(
                    glyphV,
                    GlyphCluster: 1,
                    proGpuTypeface.Font.GetAdvanceWidth(glyphV, 20))
            ];

            using var run = new GlyphRunImpl(
                glyphTypeface,
                20,
                snapshot,
                new Point(7, 30));

            Assert.Equal(
                new[] { snapshot[0].GlyphIndex, snapshot[1].GlyphIndex },
                run.GlyphIndices);
            Assert.Equal(snapshot.Length, run.ProGpuGlyphPositions.Length);
            Assert.Equal(
                (float)snapshot[0].GlyphOffset.X,
                run.ProGpuGlyphPositions[0].X,
                precision: 5);
            Assert.Equal(
                (float)(snapshot[0].GlyphAdvance + snapshot[1].GlyphOffset.X),
                run.ProGpuGlyphPositions[1].X,
                precision: 5);
            Assert.True(run.Bounds.Width > 0);
            Assert.True(run.Bounds.Height > 0);

            IReadOnlyList<float> intersections = run.GetIntersections(
                (float)(run.Bounds.Top - run.BaselineOrigin.Y),
                (float)(run.Bounds.Bottom - run.BaselineOrigin.Y));
            Assert.NotEmpty(intersections);
            Assert.Equal(0, intersections.Count % 2);
        }
        finally
        {
            glyphTypeface.Dispose();
        }
    }

    [Fact]
    public void BitmapColorGlyphsContributeInkBounds()
    {
        string[] candidates =
        [
            "/System/Library/Fonts/Apple Color Emoji.ttc",
            "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf"
        ];
        string? path = Array.Find(candidates, File.Exists);
        if (path is null)
            return;

        var font = new TtfFont(path);
        ushort glyph = font.GetGlyphIndex(0x1f600);
        if (glyph == 0 ||
            !font.TryGetBitmapGlyph(glyph, 20, out _))
        {
            return;
        }

        var platformTypeface = new ProGpuTypeface(
            font,
            font.FontData,
            font.FamilyName,
            FontWeight.Normal,
            FontStyle.Normal,
            FontStretch.Normal);
        var glyphTypeface = new GlyphTypeface(platformTypeface);
        try
        {
            using var run = new GlyphRunImpl(
                glyphTypeface,
                20,
                [new GlyphInfo(
                    glyph,
                    GlyphCluster: 0,
                    font.GetAdvanceWidth(glyph, 20))],
                new Point(7, 30));

            Assert.True(run.Bounds.Width > 0);
            Assert.True(run.Bounds.Height > 0);
            Assert.NotEmpty(run.GetIntersections(
                (float)(run.Bounds.Top - run.BaselineOrigin.Y),
                (float)(run.Bounds.Bottom - run.BaselineOrigin.Y)));
        }
        finally
        {
            glyphTypeface.Dispose();
        }
    }
}
