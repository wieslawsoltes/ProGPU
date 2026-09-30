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
    private HmiColorScheme _scheme;
    private float _zoom;
    private Pen? _outline, _fault;
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
        if (_points.Length == 0 || !float.IsFinite(zoom) || zoom <= 0) return;
        if (_outline == null || _zoom != zoom)
        {
            _zoom = zoom;
            _outline = new(HmiThemeResources.GetBrush(_scheme, HmiBrushRole.Accent), 1.5f / zoom);
            _fault = new(HmiThemeResources.GetBrush(_scheme, HmiBrushRole.Warning), 1.5f / zoom);
        }
        var surface = HmiThemeResources.GetBrush(_scheme, HmiBrushRole.Surface);
        var text = HmiThemeResources.GetBrush(_scheme, HmiBrushRole.Text);
        var pen = Blocked ? _fault! : _outline;
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
