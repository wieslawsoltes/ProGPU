using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.WinUI.Designer;

/// <summary>Shared noninteractive region outline and dimension label in the existing adorner plane.</summary>
public sealed class DesignerRegionAdorner : Control
{
    private readonly DesignerCanvas _canvas;
    public DesignerRegionAdorner(DesignerCanvas canvas)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        IsHitTestVisible = false; IsVisible = false;
    }
    private Rect _region;
    private Rect[] _highlights = [];
    private string _caption = "";
    private bool _crossing;
    private Pen? _outline;
    private Brush? _lastBrush;
    private float _lastZoom;
    public TtfFont? LabelFont { get; set; }

    public void Show(Rect region, bool crossing, string caption, IEnumerable<Rect>? highlights = null)
    {
        DesignerDragRectangle.Validate(region);
        ArgumentNullException.ThrowIfNull(caption);
        if (caption.Length > 256) throw new ArgumentOutOfRangeException(nameof(caption), "The region caption is limited to 256 UTF-16 code units.");
        var snapshot = highlights?.Take(5001).ToArray() ?? [];
        if (snapshot.Length > 5000) throw new ArgumentOutOfRangeException(nameof(highlights), "At most 5000 selection highlights can be shown.");
        foreach (var bounds in snapshot) DesignerDragRectangle.Validate(bounds);
        // Failed enumeration/validation must not publish half a new preview.
        _region = region; _crossing = crossing; _caption = caption; _highlights = snapshot;
        IsVisible = true; Invalidate();
    }
    public void Hide() { IsVisible = false; _highlights = []; Invalidate(); }
    protected override Vector2 MeasureOverride(Vector2 availableSize) => _canvas.DocumentSize ?? _canvas.Size;
    public override void OnRender(DrawingContext context)
    {
        float zoom = _canvas.ZoomScale;
        var brush = BorderBrush;
        if (!IsVisible || brush == null || !float.IsFinite(zoom) || zoom <= 0) return;
        if (_outline == null || !ReferenceEquals(_lastBrush, brush) || _lastZoom != zoom)
        { _outline = new(brush, 1.5f / zoom); _lastZoom = zoom; _lastBrush = brush; }
        foreach (var bounds in _highlights) context.DrawRectangle(null, _outline, bounds);
        var a = new Vector2(_region.X, _region.Y); var b = new Vector2(_region.Right, _region.Y);
        var c = new Vector2(_region.Right, _region.Bottom); var d = new Vector2(_region.X, _region.Bottom);
        Edge(a, b); Edge(b, c); Edge(c, d); Edge(d, a);
        if (LabelFont != null && _caption.Length > 0)
        {
            // Keep the annotation inside the visible viewport even for captured drags outside it.
            float x = Math.Clamp(_region.X, (24 - _canvas.PanOffset.X) / zoom, Math.Max((24 - _canvas.PanOffset.X) / zoom, (_canvas.Size.X - 350 - _canvas.PanOffset.X) / zoom));
            float y = Math.Clamp(_region.Bottom + 8 / zoom, (24 - _canvas.PanOffset.Y) / zoom, Math.Max((24 - _canvas.PanOffset.Y) / zoom, (_canvas.Size.Y - 45 - _canvas.PanOffset.Y) / zoom));
            context.DrawRoundedRectangle(Background, null, new Rect(x, y, 325 / zoom, 22 / zoom), 4 / zoom);
            context.DrawText(_caption, LabelFont, 11 / zoom, Foreground ?? brush, new Vector2(x + 6 / zoom, y + 3 / zoom));
        }
        void Edge(Vector2 start, Vector2 end)
        {
            if (!_crossing) { context.DrawLine(_outline, start, end); return; }
            float length = Vector2.Distance(start, end);
            if (length <= 0) return;
            var unit = (end - start) / length;
            // Bounded dash geometry; long offscreen rectangles never emit an unbounded path.
            float step = Math.Max(10 / zoom, length / 512);
            for (int i = 0; i < 512 && i * step < length; i++)
                context.DrawLine(_outline, start + unit * (i * step), start + unit * Math.Min(length, i * step + step * .6f));
        }
    }
}
