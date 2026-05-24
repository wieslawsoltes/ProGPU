using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.WinUI;

namespace ProGPU.Samples;

public enum SegmentKind : byte
{
    Line,
    Quad,
    Cubic
}

public struct GridPoint
{
    public int X;
    public int Y;

    public GridPoint(int x, int y)
    {
        X = x;
        Y = y;
    }
}

public struct Element
{
    public SegmentKind Kind;
    public GridPoint Start;
    public GridPoint Control1;
    public GridPoint Control2;
    public GridPoint End;
    public Vector4 Color;
    public float Width;
    public bool Split;
    public SolidColorBrush CachedBrush;
    public Pen CachedPen;
}

public class MotionMarkShowcaseVisual : FrameworkElement
{
    private readonly List<Element> _elements = new();
    private readonly Random _rand = new();

    // Exposed settings
    public int ElementCount = 1000;
    public float StrokeThicknessMultiplier = 1.0f;
    public float SplitProbability = 0.5f;
    public int ColorMode = 0;
    public bool FillShapes = false;
    public bool EnableLines = true;
    public bool EnableQuadBeziers = true;
    public bool EnableCubicBeziers = true;
    public bool DirectGpuMode = true; // High-performance Direct GPU primitives mode

    private static readonly (int, int)[] Offsets = { (-4, 0), (2, 0), (1, -2), (1, 2) };

    private static readonly Vector4[] VelloColors = {
        new Vector4(0.06f, 0.06f, 0.06f, 1.0f),
        new Vector4(0.50f, 0.50f, 0.50f, 1.0f),
        new Vector4(0.75f, 0.75f, 0.75f, 1.0f),
        new Vector4(0.06f, 0.06f, 0.06f, 1.0f),
        new Vector4(0.50f, 0.50f, 0.50f, 1.0f),
        new Vector4(0.75f, 0.75f, 0.75f, 1.0f),
        new Vector4(0.88f, 0.06f, 0.25f, 1.0f) // Crimson red accent
    };

    private static readonly Vector4[] FluentColors = {
        new Vector4(0f, 0.47f, 0.83f, 1f),    // Segoe Blue
        new Vector4(0.52f, 0.15f, 0.79f, 1f),  // Purple
        new Vector4(0.91f, 0.11f, 0.38f, 1f),  // Pink
        new Vector4(1f, 0.73f, 0f, 1f),        // Amber Yellow
        new Vector4(0.06f, 0.69f, 0.32f, 1f)   // Green
    };

    private static readonly Vector4[] MonochromeColors = {
        new Vector4(0.12f, 0.12f, 0.12f, 1f),
        new Vector4(0.24f, 0.24f, 0.24f, 1f),
        new Vector4(0.6f, 0.6f, 0.6f, 1f),
        new Vector4(0.9f, 0.9f, 0.9f, 1f)
    };

    public MotionMarkShowcaseVisual()
    {
        HeightConstraint = 620f;
        HorizontalAlignment = ProGPU.Layout.HorizontalAlignment.Stretch;
    }

    private GridPoint GetRandomPoint(GridPoint last)
    {
        var offset = Offsets[_rand.Next(Offsets.Length)];
        int x = last.X + offset.Item1;
        if (x < 0 || x > 80)
        {
            x -= offset.Item1 * 2;
        }
        int y = last.Y + offset.Item2;
        if (y < 0 || y > 40)
        {
            y -= offset.Item2 * 2;
        }
        return new GridPoint(Math.Clamp(x, 0, 80), Math.Clamp(y, 0, 40));
    }

    private Vector4 GetColorForScheme()
    {
        return ColorMode switch
        {
            0 => VelloColors[_rand.Next(VelloColors.Length)],
            1 => FluentColors[_rand.Next(FluentColors.Length)],
            2 => HsvToRgb((float)_rand.NextDouble() * 360f, 0.85f, 0.95f),
            3 => MonochromeColors[_rand.Next(MonochromeColors.Length)],
            _ => VelloColors[_rand.Next(VelloColors.Length)]
        };
    }

    private Vector4 HsvToRgb(float h, float s, float v)
    {
        float c = v * s;
        float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        float m = v - c;
        float r = 0, g = 0, b = 0;
        if (h >= 0 && h < 60) { r = c; g = x; b = 0; }
        else if (h >= 60 && h < 120) { r = x; g = c; b = 0; }
        else if (h >= 120 && h < 180) { r = 0; g = c; b = x; }
        else if (h >= 180 && h < 240) { r = 0; g = x; b = c; }
        else if (h >= 240 && h < 300) { r = x; g = 0; b = c; }
        else if (h >= 300 && h <= 360) { r = c; g = 0; b = x; }
        return new Vector4(r + m, g + m, b + m, 1.0f);
    }

    public void SetComplexity(int count)
    {
        ElementCount = count;
        Resize(count);
    }

    public void Resize(int n)
    {
        int oldN = _elements.Count;
        if (n < oldN)
        {
            _elements.RemoveRange(n, oldN - n);
        }
        else if (n > oldN)
        {
            var lastGP = _elements.Count > 0 ? _elements[^1].End : new GridPoint(40, 20);
            for (int i = oldN; i < n; i++)
            {
                var elem = CreateElement(lastGP, ref lastGP);
                _elements.Add(elem);
            }
        }
        Invalidate();
    }

    public void RegenerateColors()
    {
        for (int i = 0; i < _elements.Count; i++)
        {
            var elem = _elements[i];
            elem.Color = GetColorForScheme();
            elem.CachedBrush = new SolidColorBrush(elem.Color);
            elem.CachedPen = new Pen(elem.CachedBrush, elem.Width * StrokeThicknessMultiplier);
            _elements[i] = elem;
        }
        Invalidate();
    }

    public void UpdateCachedPens()
    {
        for (int i = 0; i < _elements.Count; i++)
        {
            var elem = _elements[i];
            if (elem.CachedBrush != null)
            {
                elem.CachedPen = new Pen(elem.CachedBrush, elem.Width * StrokeThicknessMultiplier);
                _elements[i] = elem;
            }
        }
        Invalidate();
    }

    public void RegenerateSegments()
    {
        _elements.Clear();
        Resize(ElementCount);
    }

    private Element CreateElement(GridPoint last, ref GridPoint current)
    {
        var activeTypes = new List<int>();
        if (EnableLines) activeTypes.Add(0);
        if (EnableQuadBeziers) activeTypes.Add(1);
        if (EnableCubicBeziers) activeTypes.Add(2);

        int segType = 0;
        if (activeTypes.Count > 0)
        {
            segType = activeTypes[_rand.Next(activeTypes.Count)];
        }

        Element element = new Element();
        element.Start = last;

        if (segType == 0) // Line
        {
            var next = GetRandomPoint(current);
            element.Kind = SegmentKind.Line;
            element.End = next;
            current = next;
        }
        else if (segType == 1) // Quad Bezier
        {
            var next = GetRandomPoint(current);
            var p2 = GetRandomPoint(next);
            element.Kind = SegmentKind.Quad;
            element.Control1 = next;
            element.End = p2;
            current = p2;
        }
        else // Cubic Bezier
        {
            var next = GetRandomPoint(current);
            var p2 = GetRandomPoint(next);
            var p3 = GetRandomPoint(next);
            element.Kind = SegmentKind.Cubic;
            element.Control1 = next;
            element.Control2 = p2;
            element.End = p3;
            current = p3;
        }

        element.Color = GetColorForScheme();
        element.Width = (float)Math.Pow(_rand.NextDouble(), 5) * 20f + 1f;
        element.Split = _rand.NextDouble() < SplitProbability;

        element.CachedBrush = new SolidColorBrush(element.Color);
        element.CachedPen = new Pen(element.CachedBrush, element.Width * StrokeThicknessMultiplier);

        return element;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = availableSize.X;
        float h = HeightConstraint ?? 620f;
        if (float.IsInfinity(w)) w = 800f;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float w = arrangeRect.Width;
        float h = arrangeRect.Height;
        if (float.IsInfinity(w) || float.IsNaN(w) || w <= 0f) w = 800f;
        if (float.IsInfinity(h) || float.IsNaN(h) || h <= 0f) h = 620f;

        bool sizeChanged = Size.X != w || Size.Y != h;
        Size = new Vector2(w, h);
        if (sizeChanged || _elements.Count == 0)
        {
            RegenerateSegments();
        }
    }

    private static Vector2 MapGridPoint(GridPoint pt, float scale, float offsetX, float offsetY)
    {
        float px = offsetX + (pt.X + 0.5f) * scale;
        float py = offsetY + (pt.Y + 0.5f) * scale;
        return new Vector2(px, py);
    }

    public override void OnRender(DrawingContext context)
    {
        // 1. Draw card background outline
        var borderPen = new Pen(ThemeManager.GetBrush("ControlBorder"), 1.0f);
        var bg = ThemeManager.GetBrush("ControlBackground");
        context.DrawRoundedRectangle(bg, borderPen, new Rect(Vector2.Zero, Size), 8f);

        if (_elements.Count == 0) return;

        // Vello MotionMark animation: randomly toggle split state (0.5% chance per frame) on CPU so it matches behavior
        for (int i = 0; i < _elements.Count; i++)
        {
            if (_rand.NextDouble() > 0.995)
            {
                var elem = _elements[i];
                elem.Split ^= true;
                _elements[i] = elem;
            }
        }

        // Calculate aspect ratio scale and offsets (centers the 80x40 grid)
        float scaleX = Size.X / (80f + 1f);
        float scaleY = Size.Y / (40f + 1f);
        float uniformScale = Math.Max(0.0f, Math.Min(scaleX, scaleY));

        if (uniformScale <= 0f) return;

        float offsetX = (Size.X - uniformScale * (80f + 1f)) * 0.5f;
        float offsetY = (Size.Y - uniformScale * (40f + 1f)) * 0.5f;

        if (DirectGpuMode)
        {
            // Direct GPU Mode: highly optimized, zero path rasterizer allocations
            int i = 0;
            while (i < _elements.Count)
            {
                // Find the end of the current connected path group
                int groupEnd = i;
                while (groupEnd < _elements.Count - 1 && !_elements[groupEnd].Split)
                {
                    groupEnd++;
                }

                // The style (pen) for all elements in this group is determined by the groupEnd element
                var groupStyleElement = _elements[groupEnd];
                var pen = groupStyleElement.CachedPen;

                // Draw all segments in the group with this style
                for (int k = i; k <= groupEnd; k++)
                {
                    var elem = _elements[k];
                    var startPt = MapGridPoint(elem.Start, uniformScale, offsetX, offsetY);
                    var endPt = MapGridPoint(elem.End, uniformScale, offsetX, offsetY);

                    switch (elem.Kind)
                    {
                        case SegmentKind.Line:
                            context.DrawLine(pen, startPt, endPt);
                            break;
                        case SegmentKind.Quad:
                            var c1 = MapGridPoint(elem.Control1, uniformScale, offsetX, offsetY);
                            context.DrawQuadraticBezier(pen, startPt, c1, endPt);
                            break;
                        case SegmentKind.Cubic:
                            var cc1 = MapGridPoint(elem.Control1, uniformScale, offsetX, offsetY);
                            var cc2 = MapGridPoint(elem.Control2, uniformScale, offsetX, offsetY);
                            context.DrawCubicBezier(pen, startPt, cc1, cc2, endPt);
                            break;
                    }
                }

                i = groupEnd + 1;
            }
        }
        else
        {
            // Path Geometry Mode: Optimized path batching based on splits
            var path = new PathGeometry();
            PathFigure? fig = null;

            for (int i = 0; i < _elements.Count; i++)
            {
                var elem = _elements[i];
                var startPt = MapGridPoint(elem.Start, uniformScale, offsetX, offsetY);
                var endPt = MapGridPoint(elem.End, uniformScale, offsetX, offsetY);

                if (fig == null)
                {
                    fig = new PathFigure(startPt);
                }

                switch (elem.Kind)
                {
                    case SegmentKind.Line:
                        fig.Segments.Add(new LineSegment(endPt));
                        break;
                    case SegmentKind.Quad:
                        var c1 = MapGridPoint(elem.Control1, uniformScale, offsetX, offsetY);
                        fig.Segments.Add(new QuadraticBezierSegment(c1, endPt));
                        break;
                    case SegmentKind.Cubic:
                        var cc1 = MapGridPoint(elem.Control1, uniformScale, offsetX, offsetY);
                        var cc2 = MapGridPoint(elem.Control2, uniformScale, offsetX, offsetY);
                        fig.Segments.Add(new CubicBezierSegment(cc1, cc2, endPt));
                        break;
                }

                if (elem.Split || i == _elements.Count - 1)
                {
                    path.Figures.Add(fig);

                    if (FillShapes)
                    {
                        var brush = elem.CachedBrush;
                        context.DrawPath(brush, null, path);
                    }
                    else
                    {
                        var pen = elem.CachedPen;
                        context.DrawPath(null, pen, path);
                    }

                    path = new PathGeometry();
                    fig = null;
                }
            }
        }

        // 4. Draw HUD Benchmarking panel (FPS, item count, pipeline)
        if (AppState.GetFont() != null)
        {
            var hudBrush = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.6f));
            var hudRect = new Rect(15f, 15f, 260f, 85f);
            context.DrawRoundedRectangle(hudBrush, new Pen(new SolidColorBrush(0xFFFFFF30), 1f), hudRect, 6f);

            string typText = (EnableLines ? "L " : "") + (EnableQuadBeziers ? "Q " : "") + (EnableCubicBeziers ? "C " : "");
            string pipelineText = DirectGpuMode ? "Direct GPU Shader Pipeline" : "Path Compute-Rasterizer";

            context.DrawText($"Active Shapes: {_elements.Count:N0}", AppState.GetFont()!, 11.5f, new SolidColorBrush(Vector4.One), new Vector2(25f, 25f));
            context.DrawText($"Modes Mix: {typText}", AppState.GetFont()!, 11f, new SolidColorBrush(new Vector4(0.8f, 0.8f, 0.8f, 1.0f)), new Vector2(25f, 43f));
            context.DrawText($"Pipeline: {pipelineText}", AppState.GetFont()!, 11f, ThemeManager.GetBrush("SystemAccentColor"), new Vector2(25f, 61f));
        }

        // Re-invalidate to animate smoothly at max monitor refresh rate
        Invalidate();
        base.OnRender(context);
    }
}
