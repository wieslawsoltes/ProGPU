using System.Numerics;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    public void ZoomToSelection()
    {
        var selected = _selection.Selection.OfType<HmiControl>().Select(c => c.CaptureDefinition()).ToArray();
        if (selected.Length == 0) { Fit(); return; }
        float left = selected.Min(e => e.X), top = selected.Min(e => e.Y);
        float right = selected.Max(e => e.X + e.Width), bottom = selected.Max(e => e.Y + e.Height);
        float width = Math.Max(100, _workspace.Size.X), height = Math.Max(100, _workspace.Size.Y);
        _canvas.ZoomScale = Math.Clamp(Math.Min((width - 64) / (right - left), (height - 64) / (bottom - top)), 0.15f, 4);
        _canvas.PanOffset = new Vector2(width / 2 - (left + right) * _canvas.ZoomScale / 2,
            height / 2 - (top + bottom) * _canvas.ZoomScale / 2);
        _canvas.ApplyTransforms(); _canvas.Invalidate();
        Status($"Selection framed · {_canvas.ZoomScale:P0}");
    }
}
