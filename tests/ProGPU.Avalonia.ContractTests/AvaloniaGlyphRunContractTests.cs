using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Xunit;

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
}
