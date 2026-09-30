using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi.Designer;

/// <summary>Original vector command glyphs, independent of platform icon fonts.</summary>
internal sealed class HmiCommandIcon : Control
{
    private static readonly Pen Light = new(HmiThemeResources.GetBrush(HmiColorScheme.Light, HmiBrushRole.Text), 1.5f);
    private static readonly Pen Dark = new(HmiThemeResources.GetBrush(HmiColorScheme.Dark, HmiBrushRole.Text), 1.5f);
    private static readonly Pen Contrast = new(HmiThemeResources.GetBrush(HmiColorScheme.HighContrast, HmiBrushRole.Text), 1.5f);
    private readonly string _kind;
    internal HmiColorScheme ColorScheme { get; set; }
    internal HmiCommandIcon(string kind) { _kind = kind; Width = 18; Height = 18; IsHitTestVisible = false; }
    public override void OnRender(DrawingContext dc)
    {
        var pen = ColorScheme switch { HmiColorScheme.Dark => Dark, HmiColorScheme.HighContrast => Contrast, _ => Light };
        void L(float x, float y, float ex, float ey) => dc.DrawLine(pen, new Vector2(x, y), new Vector2(ex, ey));
        void Box(float x, float y, float w, float h) { L(x, y, x + w, y); L(x + w, y, x + w, y + h); L(x + w, y + h, x, y + h); L(x, y + h, x, y); }
        switch (_kind)
        {
            case "Save": Box(3, 2, 12, 14); Box(6, 3, 6, 4); Box(6, 11, 6, 5); break;
            case "Undo": L(3, 6, 13, 6); L(13, 6, 15, 9); L(15, 9, 15, 14); L(3, 6, 7, 2); L(3, 6, 7, 10); break;
            case "Redo": L(3, 6, 15, 6); L(3, 6, 2, 9); L(2, 9, 2, 14); L(15, 6, 11, 2); L(15, 6, 11, 10); break;
            case "Duplicate": Box(3, 2, 9, 10); Box(7, 6, 9, 10); break;
            case "Delete": L(3, 5, 15, 5); L(6, 2, 12, 2); L(5, 5, 6, 16); L(13, 5, 12, 16); L(6, 16, 12, 16); L(9, 8, 9, 13); break;
            case "Link": Box(1, 3, 4, 4); L(5, 5, 9, 5); L(9, 5, 9, 13); L(9, 13, 13, 13); Box(13, 11, 4, 4); break;
            case "Fit": Box(5, 5, 8, 8); L(2, 6, 2, 2); L(2, 2, 6, 2); L(12, 2, 16, 2); L(16, 2, 16, 6); L(16, 12, 16, 16); L(16, 16, 12, 16); L(6, 16, 2, 16); L(2, 16, 2, 12); break;
            case "Grid": for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Box(3 + i * 5, 3 + j * 5, 1, 1); break;
            case "Snap": L(3, 3, 3, 13); L(3, 13, 6, 16); L(6, 16, 12, 16); L(12, 16, 15, 13); L(15, 13, 15, 3); L(3, 7, 6, 7); L(12, 7, 15, 7); break;
            case "Run": L(5, 2, 15, 9); L(15, 9, 5, 16); L(5, 16, 5, 2); break;
            case "Pause": Box(4, 3, 3, 12); Box(11, 3, 3, 12); break;
            case "Step": L(3, 3, 12, 9); L(12, 9, 3, 15); L(3, 15, 3, 3); L(15, 3, 15, 15); break;
            case "Panels": Box(2, 2, 14, 14); L(6, 2, 6, 16); L(6, 12, 16, 12); break;
            case "Minus": L(4, 9, 14, 9); break;
            case "Plus": L(4, 9, 14, 9); L(9, 4, 9, 14); break;
            default: Box(3, 3, 12, 12); L(6, 9, 12, 9); break;
        }
    }
}
