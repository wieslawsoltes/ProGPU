using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi.Workplace;

internal sealed class HmiObjectOutline : Control
{
    private Rect? _bounds;
    private Pen? _pen;
    private Brush? _brush;
    internal HmiObjectOutline() => IsHitTestVisible = false;
    internal void Update(Rect? bounds, Brush brush)
    {
        if (Equals(_bounds, bounds) && ReferenceEquals(_brush, brush)) return;
        _bounds = bounds;
        if (!ReferenceEquals(_brush, brush)) { _brush = brush; _pen = new(brush, 1.5f); }
        Invalidate();
    }
    public override void OnRender(DrawingContext context)
    {
        if (_bounds is not { } r || _pen == null) return;
        var a = new Vector2(r.X, r.Y); var b = new Vector2(r.Right, r.Y);
        var c = new Vector2(r.Right, r.Bottom); var d = new Vector2(r.X, r.Bottom);
        context.DrawLine(_pen, a, b); context.DrawLine(_pen, b, c);
        context.DrawLine(_pen, c, d); context.DrawLine(_pen, d, a);
    }
}
