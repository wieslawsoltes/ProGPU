using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;

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
    public PathGeometry CachedPath;
}

public class MotionMarkShowcaseVisual : FrameworkElement, IAnimatedElement
{
    private readonly List<Element> _elements = new();
    private readonly List<PathGeometry> _groupPaths = new();
    private readonly List<PathFigure> _groupFigures = new();
    private readonly List<int> _groupEndIndices = new();
    private readonly Random _rand = new();
    private float _gridScale;
    private float _gridOffsetX;
    private float _gridOffsetY;
    private ElementTheme _cachedTheme = (ElementTheme)(-1);
    private VisualThemeFamily _cachedThemeFamily = (VisualThemeFamily)(-1);
    private Brush? _backgroundBrush;
    private Brush? _accentBrush;
    private Pen? _borderPen;
    private readonly SolidColorBrush _hudBackgroundBrush = new(new Vector4(0f, 0f, 0f, 0.6f));
    private readonly SolidColorBrush _hudBorderBrush = new(0xFFFFFF30);
    private readonly SolidColorBrush _hudPrimaryTextBrush = new(Vector4.One);
    private readonly SolidColorBrush _hudSecondaryTextBrush = new(new Vector4(0.8f, 0.8f, 0.8f, 1f));
    private readonly Pen _hudBorderPen;
    private string _activeShapesText = "Active Shapes: 0";
    private string _modesText = "Modes Mix: L Q C ";
    private string _pipelineText = "Pipeline: Individual retained paths";
    private int _cachedHudElementCount = -1;
    private int _cachedHudModeMask = -1;
    private bool _cachedHudIndividualPathMode;
    private float _splitToggleBudget;

    // Exposed settings
    public int ElementCount = 1000;
    public float StrokeThicknessMultiplier = 1.0f;
    public float SplitProbability = 0.5f;
    public int ColorMode = 0;
    public bool FillShapes = false;
    public bool EnableLines = true;
    public bool EnableQuadBeziers = true;
    public bool EnableCubicBeziers = true;
    public bool UseIndividualPaths = false;

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
        _hudBorderPen = new Pen(_hudBorderBrush, 1f);
        HeightConstraint = 620f;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    public void Update(float delta)
    {
        if (_elements.Count == 0 || delta <= 0f)
        {
            return;
        }

        // Preserve the original 0.5% per-60-Hz-frame split rate without scanning every element.
        // Work is O(k), where k ~= elementCount * 0.005 * delta * 60, instead of O(elementCount).
        _splitToggleBudget += _elements.Count * 0.3f * MathF.Min(delta, 0.1f);
        var toggleCount = Math.Min(_elements.Count, (int)_splitToggleBudget);
        if (toggleCount == 0)
        {
            return;
        }

        _splitToggleBudget -= toggleCount;
        for (var toggle = 0; toggle < toggleCount; toggle++)
        {
            var index = _rand.Next(_elements.Count);
            var element = _elements[index];
            element.Split ^= true;
            _elements[index] = element;
        }
        RebuildGroupedPaths();
        Invalidate();
    }

    public void AdvanceAnimation() => Update(1f / 60f);

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
        RebuildGroupedPaths();
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
        _splitToggleBudget = 0f;
        Resize(ElementCount);
    }

    private Element CreateElement(GridPoint last, ref GridPoint current)
    {
        var activeTypeCount = (EnableLines ? 1 : 0) + (EnableQuadBeziers ? 1 : 0) + (EnableCubicBeziers ? 1 : 0);
        var selectedType = activeTypeCount > 0 ? _rand.Next(activeTypeCount) : 0;
        var segType = EnableLines && selectedType-- == 0
            ? SegmentKind.Line
            : EnableQuadBeziers && selectedType-- == 0
                ? SegmentKind.Quad
                : EnableCubicBeziers
                    ? SegmentKind.Cubic
                    : SegmentKind.Line;

        Element element = new Element();
        element.Start = last;

        if (segType == SegmentKind.Line)
        {
            var next = GetRandomPoint(current);
            element.Kind = SegmentKind.Line;
            element.End = next;
            current = next;
        }
        else if (segType == SegmentKind.Quad)
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
        element.CachedPath = CreateElementPath(element);

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
            UpdateGridMapping(w, h);
            RegenerateSegments();
        }
    }

    private Vector2 MapGridPoint(GridPoint point)
    {
        float px = _gridOffsetX + (point.X + 0.5f) * _gridScale;
        float py = _gridOffsetY + (point.Y + 0.5f) * _gridScale;
        return new Vector2(px, py);
    }

    private void UpdateGridMapping(float width, float height)
    {
        var scaleX = width / 81f;
        var scaleY = height / 41f;
        _gridScale = MathF.Max(0f, MathF.Min(scaleX, scaleY));
        _gridOffsetX = (width - _gridScale * 81f) * 0.5f;
        _gridOffsetY = (height - _gridScale * 41f) * 0.5f;
    }

    private PathGeometry CreateElementPath(Element element)
    {
        var figure = new PathFigure(MapGridPoint(element.Start));
        var end = MapGridPoint(element.End);
        switch (element.Kind)
        {
            case SegmentKind.Line:
                figure.Segments.Add(new LineSegment(end));
                break;
            case SegmentKind.Quad:
                figure.Segments.Add(new QuadraticBezierSegment(MapGridPoint(element.Control1), end));
                break;
            case SegmentKind.Cubic:
                figure.Segments.Add(new CubicBezierSegment(
                    MapGridPoint(element.Control1),
                    MapGridPoint(element.Control2),
                    end));
                break;
        }

        var path = new PathGeometry();
        path.Figures.Add(figure);
        return path;
    }

    private PathFigure GetGroupFigure(int index)
    {
        while (_groupFigures.Count <= index)
        {
            var figure = new PathFigure();
            var path = new PathGeometry();
            path.Figures.Add(figure);
            _groupFigures.Add(figure);
            _groupPaths.Add(path);
        }

        var result = _groupFigures[index];
        result.Segments.Clear();
        result.IsClosed = false;
        result.IsFilled = true;
        return result;
    }

    private void RebuildGroupedPaths()
    {
        _groupEndIndices.Clear();
        _groupEndIndices.EnsureCapacity(_elements.Count);

        var groupIndex = 0;
        var elementIndex = 0;
        while (elementIndex < _elements.Count)
        {
            var groupEnd = elementIndex;
            while (groupEnd < _elements.Count - 1 && !_elements[groupEnd].Split)
            {
                groupEnd++;
            }

            var figure = GetGroupFigure(groupIndex);
            figure.StartPoint = _elements[elementIndex].CachedPath.Figures[0].StartPoint;
            for (var index = elementIndex; index <= groupEnd; index++)
            {
                figure.Segments.Add(_elements[index].CachedPath.Figures[0].Segments[0]);
            }

            _groupEndIndices.Add(groupEnd);
            groupIndex++;
            elementIndex = groupEnd + 1;
        }
    }

    private void EnsureThemeResources()
    {
        if (_cachedTheme == ActualTheme && _cachedThemeFamily == ActualThemeFamily)
        {
            return;
        }

        _cachedTheme = ActualTheme;
        _cachedThemeFamily = ActualThemeFamily;
        _backgroundBrush = ThemeManager.GetBrush("ControlBackground", _cachedTheme, _cachedThemeFamily);
        _accentBrush = ThemeManager.GetBrush("SystemAccentColor", _cachedTheme, _cachedThemeFamily);
        _borderPen = new Pen(ThemeManager.GetBrush("ControlBorder", _cachedTheme, _cachedThemeFamily), 1f);
    }

    private void EnsureHudText()
    {
        var modeMask = (EnableLines ? 1 : 0) | (EnableQuadBeziers ? 2 : 0) | (EnableCubicBeziers ? 4 : 0);
        if (_cachedHudElementCount == _elements.Count &&
            _cachedHudModeMask == modeMask &&
            _cachedHudIndividualPathMode == UseIndividualPaths)
        {
            return;
        }

        _cachedHudElementCount = _elements.Count;
        _cachedHudModeMask = modeMask;
        _cachedHudIndividualPathMode = UseIndividualPaths;
        _activeShapesText = $"Active Shapes: {_elements.Count:N0}";
        _modesText = $"Modes Mix: {(EnableLines ? "L " : string.Empty)}{(EnableQuadBeziers ? "Q " : string.Empty)}{(EnableCubicBeziers ? "C " : string.Empty)}";
        _pipelineText = $"Pipeline: {(UseIndividualPaths ? "Individual retained paths" : "Grouped retained paths")}";
    }

    public override void OnRender(DrawingContext context)
    {
        EnsureThemeResources();
        context.DrawRoundedRectangle(_backgroundBrush, _borderPen, new Rect(Vector2.Zero, Size), 8f);

        if (_elements.Count == 0) return;
        if (_gridScale <= 0f) return;

        if (UseIndividualPaths)
        {
            // Geometry and segments are retained; each frame only emits public DrawPath commands.
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

                // Retained paths keep the official path API without rebuilding segment objects each frame.
                for (int k = i; k <= groupEnd; k++)
                {
                    var elem = _elements[k];
                    if (FillShapes)
                    {
                        context.DrawPath(groupStyleElement.CachedBrush, null, elem.CachedPath);
                    }
                    else
                    {
                        context.DrawPath(null, pen, elem.CachedPath);
                    }
                }

                i = groupEnd + 1;
            }
        }
        else
        {
            for (var groupIndex = 0; groupIndex < _groupEndIndices.Count; groupIndex++)
            {
                var style = _elements[_groupEndIndices[groupIndex]];
                if (FillShapes)
                {
                    context.DrawPath(style.CachedBrush, null, _groupPaths[groupIndex]);
                }
                else
                {
                    context.DrawPath(null, style.CachedPen, _groupPaths[groupIndex]);
                }
            }
        }

        // 4. Draw HUD Benchmarking panel (FPS, item count, pipeline)
        var font = AppState.GetFont();
        if (font != null)
        {
            EnsureHudText();
            var hudRect = new Rect(15f, 15f, 260f, 85f);
            context.DrawRoundedRectangle(_hudBackgroundBrush, _hudBorderPen, hudRect, 6f);
            context.DrawText(_activeShapesText, font, 11.5f, _hudPrimaryTextBrush, new Vector2(25f, 25f));
            context.DrawText(_modesText, font, 11f, _hudSecondaryTextBrush, new Vector2(25f, 43f));
            context.DrawText(_pipelineText, font, 11f, _accentBrush!, new Vector2(25f, 61f));
        }
        base.OnRender(context);
    }
}
