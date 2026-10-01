using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

/// <summary>Noninteractive retained route handles in the existing shared adorner plane.</summary>
internal sealed class HmiRouteAdorner(DesignerCanvas canvas) : Control
{
    private static readonly string[] Labels = Enumerable.Range(1, HmiRouteWaypoints.MaximumCount).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    private HmiPoint[] _points = [];
    private HmiRouteSegment[] _segments = [];
    private HmiRouteSegment? _activeSegment;
    private float _segmentOffset;
    private HmiColorScheme _scheme;
    private float _zoom;
    private Pen? _outline, _fault, _grip;
    internal TtfFont? LabelFont { get; set; }
    internal int SelectedIndex { get; set; } = -1;
    internal bool Blocked { get; set; }
    internal bool Locked { get; set; }
    internal HmiColorScheme Scheme { get => _scheme; set { if (_scheme != value) { _scheme = value; _outline = null; } } }

    internal void SetPoints(IReadOnlyList<HmiPoint> points)
    {
        if (!_points.SequenceEqual(points)) _points = points.ToArray();
        Invalidate();
    }
    internal void SetSegments(IReadOnlyList<HmiRouteSegment> segments, HmiRouteSegment? active, float offset)
    {
        _segments = segments.ToArray(); _activeSegment = active; _segmentOffset = offset;
        Invalidate();
    }
    internal int HitSegment(HmiPoint point)
    {
        if (Locked || !float.IsFinite(canvas.ZoomScale) || canvas.ZoomScale <= 0) return -1;
        // Numbered pin handles retain input priority in the owner.
        foreach (var segment in _segments)
        {
            if (!ShowsGrip(segment)) continue;
            var middle = segment.Midpoint;
            float x = segment.IsHorizontal ? 12 : 8, y = segment.IsHorizontal ? 8 : 12;
            if (Math.Abs(point.X - middle.X) * canvas.ZoomScale <= x && Math.Abs(point.Y - middle.Y) * canvas.ZoomScale <= y)
                return segment.Index;
        }
        return -1;
    }
    private bool ShowsGrip(HmiRouteSegment segment) =>
        (Math.Abs(segment.End.X - segment.Start.X) + Math.Abs(segment.End.Y - segment.Start.Y)) * canvas.ZoomScale >= 32;

    internal int Hit(HmiPoint point, float tolerance)
    {
        double best = (double)tolerance * tolerance;
        int found = -1;
        for (int i = 0; i < _points.Length; i++)
        {
            double x = (double)point.X - _points[i].X, y = (double)point.Y - _points[i].Y;
            double distance = x * x + y * y;
            if (distance <= best) { best = distance; found = i; }
        }
        return found;
    }
    protected override Vector2 MeasureOverride(Vector2 availableSize) => new(canvas.DesignSurface.Width, canvas.DesignSurface.Height);
    public override void OnRender(DrawingContext context)
    {
        float zoom = canvas.ZoomScale;
        if (_points.Length == 0 && _segments.Length == 0 && _activeSegment == null || !float.IsFinite(zoom) || zoom <= 0) return;
        if (_outline == null || _zoom != zoom)
        {
            _zoom = zoom;
            _outline = new(HmiThemeResources.GetBrush(_scheme, HmiBrushRole.Accent), 1.5f / zoom);
            _fault = new(HmiThemeResources.GetBrush(_scheme, HmiBrushRole.Warning), 1.5f / zoom);
            _grip = new(HmiThemeResources.GetBrush(_scheme, HmiBrushRole.Surface), 1 / zoom);
        }
        var surface = HmiThemeResources.GetBrush(_scheme, HmiBrushRole.Surface);
        var text = HmiThemeResources.GetBrush(_scheme, HmiBrushRole.Text);
        var pen = Blocked ? _fault! : _outline;
        if (_activeSegment is { } active)
        {
            var shift = active.IsHorizontal ? new Vector2(0, _segmentOffset) : new Vector2(_segmentOffset, 0);
            var a = new Vector2(active.Start.X, active.Start.Y) + shift;
            var b = new Vector2(active.End.X, active.End.Y) + shift;
            if (Blocked)
            {
                // This is a dashed proposed span, not a fabricated successful route.
                int count = Math.Clamp((int)(Vector2.Distance(a, b) * zoom / 10), 1, 128);
                for (int j = 0; j < count; j++) context.DrawLine(pen, Vector2.Lerp(a, b, (float)j / count), Vector2.Lerp(a, b, (j + .5f) / count));
            }
            else context.DrawLine(pen, a, b);
            Grip(new(active.Index, new(a.X, a.Y), new(b.X, b.Y)), true);
            if (LabelFont != null)
            {
                var label = (a + b) / 2 + new Vector2(12, -30) / zoom;
                context.DrawRoundedRectangle(surface, pen, new Rect(label.X, label.Y, 160 / zoom, 22 / zoom), 4 / zoom);
                context.DrawText(Blocked ? "BLOCKED / Escape cancels" : FormattableString.Invariant($"{(active.IsHorizontal ? "Y" : "X")} offset {_segmentOffset:0.#}"), LabelFont, 10 / zoom, text, label + new Vector2(5, 3) / zoom);
            }
        }
        else if (!Locked) foreach (var segment in _segments) if (ShowsGrip(segment)) Grip(segment, false);
        void Grip(HmiRouteSegment segment, bool active)
        {
            var center = new Vector2(segment.Midpoint.X, segment.Midpoint.Y);
            float w = (segment.IsHorizontal ? 20 : 10) / zoom, h = (segment.IsHorizontal ? 10 : 20) / zoom;
            context.DrawRoundedRectangle(active ? pen.Brush : surface, pen, new Rect(center.X - w / 2, center.Y - h / 2, w, h), 3 / zoom);
            for (int stripe = -1; stripe <= 1; stripe++)
            {
                var d = segment.IsHorizontal ? new Vector2(stripe * 4, 0) / zoom : new Vector2(0, stripe * 4) / zoom;
                var arm = segment.IsHorizontal ? new Vector2(0, 2) / zoom : new Vector2(2, 0) / zoom;
                context.DrawLine(active ? _grip! : pen, center + d - arm, center + d + arm);
            }
        }
        float r = 5 / zoom;
        for (int i = 0; i < _points.Length; i++)
        {
            var point = new Vector2(_points[i].X, _points[i].Y);
            context.DrawRectangle(i == SelectedIndex ? pen.Brush : surface, pen, new Rect(point.X - r, point.Y - r, r * 2, r * 2));
            if (i == SelectedIndex)
                context.DrawEllipse(null, pen, point, 9 / zoom, 9 / zoom);
            if (Locked)
                context.DrawLine(pen, point - new Vector2(r * .5f), point + new Vector2(r * .5f));
            if (LabelFont != null)
            {
                context.DrawRoundedRectangle(surface, null, new Rect(point.X + 9 / zoom, point.Y - 18 / zoom, 20 / zoom, 15 / zoom), 3 / zoom);
                context.DrawText(Labels[i], LabelFont, 10 / zoom, text, point + new Vector2(13 / zoom, -18 / zoom));
            }
        }
    }
}
