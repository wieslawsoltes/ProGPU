using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

internal static partial class Program
{
    // Additional Microsoft API observations only. The original indexed-run
    // receipts remain intact, and this diagnostic performs no input or drawing
    // into a window. Preserve nested transform history instead of flattening it.
    private static object CaptureDrawingFrame(TextLine line)
    {
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open())
            line.Draw(context, new Point(0, 0), InvertAxes.None);
        int count = 0;
        return CaptureDrawingNode(drawing, 0, ref count);
    }

    private static object CaptureDrawingNode(Drawing drawing, int depth, ref int count)
    {
        if (depth > 16 || ++count > 128)
            throw new InvalidOperationException("Supplemental drawing exceeded its observation bound.");
        if (drawing is DrawingGroup group)
        {
            if (group.ClipGeometry is not null || group.OpacityMask is not null)
                throw new InvalidOperationException("Supplemental drawing has an unobserved clip or mask.");
            var children = new List<object>();
            foreach (Drawing child in group.Children)
                children.Add(CaptureDrawingNode(child, depth + 1, ref count));
            Matrix transform = group.Transform?.Value ?? Matrix.Identity;
            return new
            {
                Kind = "Group", group.Opacity,
                Transform = new[] { transform.M11, transform.M12, transform.M21, transform.M22, transform.OffsetX, transform.OffsetY },
                GuidelinesX = group.GuidelineSet?.GuidelinesX.ToArray(),
                GuidelinesY = group.GuidelineSet?.GuidelinesY.ToArray(), Children = children
            };
        }
        if (drawing is GlyphRunDrawing glyphDrawing)
        {
            GlyphRun run = glyphDrawing.GlyphRun;
            if (run.GlyphTypeface.FontUri.IsFile is false || glyphDrawing.ForegroundBrush is not SolidColorBrush brush)
                throw new InvalidOperationException("Unexpected drawing font or brush.");
            foreach (double value in run.AdvanceWidths) Finite(value);
            return new
            {
                Kind = "GlyphRun", run.BidiLevel, run.IsSideways,
                run.FontRenderingEmSize, run.PixelsPerDip,
                Baseline = Point(run.BaselineOrigin), GlyphIds = run.GlyphIndices.ToArray(),
                Advances = run.AdvanceWidths.ToArray(), Offsets = run.GlyphOffsets?.Select(Point).ToArray(),
                NominalDesignAdvances = run.GlyphIndices.Select(index => run.GlyphTypeface.AdvanceWidths[index]).ToArray(),
                Utf16 = run.Characters?.Select(character => (int)character).ToArray(),
                Clusters = run.ClusterMap?.ToArray(), CaretStops = run.CaretStops?.ToArray(),
                FontUri = run.GlyphTypeface.FontUri.AbsoluteUri,
                StyleSimulations = run.GlyphTypeface.StyleSimulations.ToString(),
                Foreground = brush.Color.ToString(), brush.Opacity
            };
        }
        throw new InvalidOperationException("Unexpected supplemental drawing kind: " + drawing.GetType().FullName);
    }
}
