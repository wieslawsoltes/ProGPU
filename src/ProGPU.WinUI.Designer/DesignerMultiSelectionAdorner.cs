using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI.Designer;

/// <summary>Shared noninteractive outlines for selection sets; the primary resize adorner remains canvas-owned.</summary>
public sealed class DesignerMultiSelectionAdorner : Control, IDisposable
{
    private readonly DesignerCanvas _canvas;
    private readonly DesignerSelectionService _selection;
    private readonly Pen _pen = new(new ThemeResourceBrush("SystemAccentColor"), 1.5f);
    public DesignerMultiSelectionAdorner(DesignerCanvas canvas, DesignerSelectionService selection)
    {
        _canvas = canvas; _selection = selection;
        IsHitTestVisible = false;
        Width = 32768; Height = 32768;
        selection.SelectionChanged += Invalidate;
    }
    public override void OnRender(DrawingContext context)
    {
        if (_selection.Selection.Count < 2) return;
        foreach (var element in _selection.Selection)
        {
            var rectangle = _canvas.GetElementRect(element);
            var a = new Vector2(rectangle.X, rectangle.Y);
            var b = new Vector2(rectangle.Right, rectangle.Y);
            var c = new Vector2(rectangle.Right, rectangle.Bottom);
            var d = new Vector2(rectangle.X, rectangle.Bottom);
            context.DrawLine(_pen, a, b); context.DrawLine(_pen, b, c);
            context.DrawLine(_pen, c, d); context.DrawLine(_pen, d, a);
        }
    }
    public void Dispose() => _selection.SelectionChanged -= Invalidate;
}
