using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;

namespace Avalonia.ProGpu;

/// <summary>
/// Immutable transport from Avalonia shaping results to ProGPU's retained
/// glyph-run command.
/// </summary>
internal sealed class GlyphRunImpl : IGlyphRunImpl
{
    public GlyphRunImpl(
#if AVALONIA11
        IGlyphTypeface glyphTypeface,
#else
        GlyphTypeface glyphTypeface,
#endif
        double fontRenderingEmSize,
        IReadOnlyList<GlyphInfo> glyphInfos,
        Point baselineOrigin)
    {
        ArgumentNullException.ThrowIfNull(glyphTypeface);
        ArgumentNullException.ThrowIfNull(glyphInfos);

#if AVALONIA11
        Typeface = glyphTypeface as ProGpuTypeface
            ?? throw new ArgumentException(
                "The glyph typeface is not owned by ProGPU.",
                nameof(glyphTypeface));
#else
        Typeface = glyphTypeface.PlatformTypeface as ProGpuTypeface
            ?? throw new ArgumentException(
                "The glyph typeface is not owned by ProGPU.",
                nameof(glyphTypeface));
#endif
        FontRenderingEmSize = fontRenderingEmSize;
        BaselineOrigin = baselineOrigin;

        int count = glyphInfos.Count;
        GlyphIndices = new ushort[count];
        ProGpuGlyphPositions = new Vector2[count];

        double scale = Typeface.Font.UnitsPerEm > 0
            ? fontRenderingEmSize / Typeface.Font.UnitsPerEm
            : 0;
        double penX = 0;
        Rect bounds = default;
        bool hasBounds = false;

        for (int index = 0; index < count; index++)
        {
            GlyphInfo info = glyphInfos[index];
            double x = penX + info.GlyphOffset.X;
            double y = info.GlyphOffset.Y;

            GlyphIndices[index] = info.GlyphIndex;
            ProGpuGlyphPositions[index] =
                new Vector2((float)x, (float)y);

            if (TryGetGlyphInkBounds(
                    info.GlyphIndex,
                    x,
                    y,
                    scale,
                    out Rect local))
            {
                bounds = hasBounds ? bounds.Union(local) : local;
                hasBounds = true;
            }

            penX += info.GlyphAdvance;
        }

        Bounds = hasBounds
            ? bounds.Translate(new Vector(baselineOrigin.X, baselineOrigin.Y))
            : new Rect(baselineOrigin, new Size());
    }

    public ProGpuTypeface Typeface { get; }

#if AVALONIA11
    public IGlyphTypeface GlyphTypeface => Typeface;
#endif

    public ushort[] GlyphIndices { get; }

    public Vector2[] ProGpuGlyphPositions { get; }

    public double FontRenderingEmSize { get; }

    public Point BaselineOrigin { get; }

    public Rect Bounds { get; }

    /// <summary>
    /// Returns merged x intervals for glyph ink whose conservative outline
    /// bounds overlap the requested baseline-relative strip.
    /// </summary>
    public IReadOnlyList<float> GetIntersections(
        float lowerLimit,
        float upperLimit)
    {
        if (GlyphIndices.Length == 0)
            return Array.Empty<float>();

        if (upperLimit < lowerLimit)
            (lowerLimit, upperLimit) = (upperLimit, lowerLimit);

        double scale = Typeface.Font.UnitsPerEm > 0
            ? FontRenderingEmSize / Typeface.Font.UnitsPerEm
            : 0;
        var intervals = new List<Interval>();
        for (int index = 0; index < GlyphIndices.Length; index++)
        {
            Vector2 position = ProGpuGlyphPositions[index];
            if (!TryGetGlyphInkBounds(
                    GlyphIndices[index],
                    position.X,
                    position.Y,
                    scale,
                    out Rect bounds))
            {
                continue;
            }

            if (bounds.Bottom < lowerLimit || bounds.Top > upperLimit)
                continue;

            intervals.Add(new Interval(
                (float)bounds.Left,
                (float)bounds.Right));
        }

        if (intervals.Count == 0)
            return Array.Empty<float>();

        intervals.Sort(static (left, right) =>
            left.Start.CompareTo(right.Start));

        var result = new List<float>(checked(intervals.Count * 2));
        float start = intervals[0].Start;
        float end = intervals[0].End;
        for (int index = 1; index < intervals.Count; index++)
        {
            Interval next = intervals[index];
            if (next.Start <= end)
            {
                end = Math.Max(end, next.End);
                continue;
            }

            result.Add(start);
            result.Add(end);
            start = next.Start;
            end = next.End;
        }

        result.Add(start);
        result.Add(end);
        return result;
    }

    public void Dispose()
    {
    }

    private bool TryGetGlyphInkBounds(
        ushort glyphIndex,
        double x,
        double y,
        double outlineScale,
        out Rect bounds)
    {
        if (outlineScale > 0 &&
            Typeface.Font.TryGetGlyphBounds(
                glyphIndex,
                out short xMin,
                out short yMin,
                out short xMax,
                out short yMax))
        {
            bounds = new Rect(
                x + xMin * outlineScale,
                y - yMax * outlineScale,
                Math.Max(0, (xMax - xMin) * outlineScale),
                Math.Max(0, (yMax - yMin) * outlineScale));
            return true;
        }

        if (BoundedColorGlyphMetrics.TryGetMetrics(
                Typeface.Font,
                glyphIndex,
                FontRenderingEmSize,
                out ColorGlyphMetrics metrics))
        {
            bounds = metrics.GetBounds(
                new Point(x, y),
                FontRenderingEmSize);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        bounds = default;
        return false;
    }

    private readonly record struct Interval(float Start, float End);
}
