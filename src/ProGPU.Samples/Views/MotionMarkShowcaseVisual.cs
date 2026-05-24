using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.WinUI;

namespace ProGPU.Samples;

public class MotionMarkShowcaseVisual : FrameworkElement
{
    public class PathSegmentElement
    {
        public PathSegment? OriginalSeg;
        public PathSegment? WobbledSeg;
        public Vector2 OriginalStartPoint;
        public Vector2 WobbledStartPoint;
        public Vector4 Color;
        public float Width;
        public bool IsSplit;
        public GridPoint GP;
        public int GridIndex;
        public SolidColorBrush? CachedBrush;
        public Pen? CachedPen;
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

        public Vector2 ToCoordinate(float width, float height)
        {
            float w = float.IsInfinity(width) || float.IsNaN(width) || width <= 0f ? 800f : width;
            float h = float.IsInfinity(height) || float.IsNaN(height) || height <= 0f ? 520f : height;
            float scaleX = w / 81f;
            float scaleY = (h - 60f) / 41f;
            return new Vector2((X + 0.5f) * scaleX, 30f + (Y + 0.5f) * scaleY);
        }
    }

    private readonly List<PathSegmentElement> _elements = new();
    private readonly Random _rand = new();

    // Exposed settings
    public int ElementCount = 1000;
    public float StrokeThicknessMultiplier = 1.0f;
    public float SplitProbability = 0.5f;
    public float AnimationSpeed = 1.0f;
    public int ColorMode = 0;
    public bool FillShapes = false;
    public bool EnableLines = true;
    public bool EnableQuadBeziers = true;
    public bool EnableCubicBeziers = true;

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
        HeightConstraint = 520f;
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
            var lastGP = _elements.Count > 0 ? _elements[^1].GP : new GridPoint(40, 20);
            for (int i = oldN; i < n; i++)
            {
                var elem = CreateElement(lastGP, ref lastGP, i);
                _elements.Add(elem);
            }
        }
        Invalidate();
    }

    public void RegenerateColors()
    {
        foreach (var elem in _elements)
        {
            elem.Color = GetColorForScheme();
            elem.CachedBrush = new SolidColorBrush(elem.Color);
            elem.CachedPen = new Pen(elem.CachedBrush, elem.Width * StrokeThicknessMultiplier);
        }
        Invalidate();
    }

    public void UpdateCachedPens()
    {
        foreach (var elem in _elements)
        {
            if (elem.CachedBrush != null)
            {
                elem.CachedPen = new Pen(elem.CachedBrush, elem.Width * StrokeThicknessMultiplier);
            }
        }
        Invalidate();
    }

    public void RegenerateSegments()
    {
        _elements.Clear();
        Resize(ElementCount);
    }

    private PathSegment DuplicateSegment(PathSegment seg)
    {
        if (seg is LineSegment line) return new LineSegment(line.Point);
        if (seg is QuadraticBezierSegment quad) return new QuadraticBezierSegment(quad.ControlPoint, quad.Point);
        if (seg is CubicBezierSegment cubic) return new CubicBezierSegment(cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point);
        return seg;
    }

    private PathSegmentElement CreateElement(GridPoint last, ref GridPoint current, int gridIndex)
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

        var startPt = current.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f);
        var next = GetRandomPoint(current);
        PathSegment seg;

        if (segType == 0) // Line
        {
            seg = new LineSegment(next.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f));
            current = next;
        }
        else if (segType == 1) // Quad Bezier
        {
            var p2 = GetRandomPoint(next);
            seg = new QuadraticBezierSegment(next.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f), p2.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f));
            current = p2;
        }
        else // Cubic Bezier
        {
            var p2 = GetRandomPoint(next);
            var p3 = GetRandomPoint(next);
            seg = new CubicBezierSegment(next.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f), p2.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f), p3.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f));
            current = p3;
        }

        var color = GetColorForScheme();
        float width = (float)Math.Pow(_rand.NextDouble(), 5) * 20f + 1f;
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, width * StrokeThicknessMultiplier);

        return new PathSegmentElement
        {
            OriginalSeg = seg,
            WobbledSeg = DuplicateSegment(seg),
            OriginalStartPoint = startPt,
            WobbledStartPoint = startPt,
            Color = color,
            Width = width,
            IsSplit = _rand.NextDouble() < SplitProbability,
            GP = current,
            GridIndex = gridIndex,
            CachedBrush = brush,
            CachedPen = pen
        };
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = availableSize.X;
        float h = HeightConstraint ?? 520f;
        if (float.IsInfinity(w)) w = 800f;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float w = arrangeRect.Width;
        float h = arrangeRect.Height;
        if (float.IsInfinity(w) || float.IsNaN(w) || w <= 0f) w = 800f;
        if (float.IsInfinity(h) || float.IsNaN(h) || h <= 0f) h = 520f;

        bool sizeChanged = Size.X != w || Size.Y != h;
        Size = new Vector2(w, h);
        if (sizeChanged || _elements.Count == 0)
        {
            RegenerateSegments();
        }
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
                _elements[i].IsSplit ^= true;
            }
        }

        // 2. Batch path rendering based on splits (matches Vello's exact stroke/color animation grouping)
        var path = new PathGeometry();
        PathFigure? fig = null;

        for (int i = 0; i < _elements.Count; i++)
        {
            var element = _elements[i];

            if (fig == null)
            {
                fig = new PathFigure(element.OriginalStartPoint);
            }

            if (element.OriginalSeg != null)
            {
                fig.Segments.Add(element.OriginalSeg);
            }

            if (element.IsSplit || i == _elements.Count - 1)
            {
                path.Figures.Add(fig);
                
                if (FillShapes)
                {
                    var brush = element.CachedBrush ?? new SolidColorBrush(element.Color);
                    context.DrawPath(brush, null, path);
                }
                else
                {
                    var pen = element.CachedPen ?? new Pen(element.CachedBrush ?? new SolidColorBrush(element.Color), element.Width * StrokeThicknessMultiplier);
                    context.DrawPath(null, pen, path);
                }

                path = new PathGeometry();
                fig = null;
            }
        }

        // 4. Draw HUD Benchmarking panel (FPS, item count, pipeline)
        if (AppState.GetFont() != null)
        {
            var hudBrush = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.6f));
            var hudRect = new Rect(15f, 15f, 250f, 85f);
            context.DrawRoundedRectangle(hudBrush, new Pen(new SolidColorBrush(0xFFFFFF30), 1f), hudRect, 6f);

            string typText = (EnableLines ? "L " : "") + (EnableQuadBeziers ? "Q " : "") + (EnableCubicBeziers ? "C " : "");
            context.DrawText($"Active Shapes: {_elements.Count:N0}", AppState.GetFont()!, 11.5f, new SolidColorBrush(Vector4.One), new Vector2(25f, 25f));
            context.DrawText($"Modes Mix: {typText}", AppState.GetFont()!, 11f, new SolidColorBrush(new Vector4(0.8f, 0.8f, 0.8f, 1.0f)), new Vector2(25f, 43f));
            context.DrawText("Pipeline: ProGPU 100% GPU-Bound", AppState.GetFont()!, 11f, ThemeManager.GetBrush("SystemAccentColor"), new Vector2(25f, 61f));
        }

        // Re-invalidate to animate smoothly at max monitor refresh rate
        Invalidate();
        base.OnRender(context);
    }
}
