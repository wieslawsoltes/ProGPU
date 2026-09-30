using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.Scene;

namespace ProGPU.WinUI.Hmi;

/// <summary>Lightweight live-vector toolbox preview; shares the runtime symbol renderer without creating input children.</summary>
public sealed class HmiSymbolIcon : Control
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(nameof(Symbol), typeof(HmiSymbol), typeof(HmiSymbolIcon),
        new PropertyMetadata(HmiSymbol.Tank) { AffectsRender = true });
    public static readonly DependencyProperty ColorSchemeProperty = DependencyProperty.Register(nameof(ColorScheme), typeof(HmiColorScheme), typeof(HmiSymbolIcon),
        new PropertyMetadata(HmiColorScheme.Light) { AffectsRender = true });
    public HmiSymbol Symbol
    {
        get => (HmiSymbol)(GetValue(SymbolProperty) ?? HmiSymbol.Tank);
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetValue(SymbolProperty, value); }
    }
    public HmiColorScheme ColorScheme
    {
        get => (HmiColorScheme)(GetValue(ColorSchemeProperty) ?? HmiColorScheme.Light);
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetValue(ColorSchemeProperty, value); }
    }
    public static readonly DependencyProperty GraphicStyleProperty = DependencyProperty.Register(nameof(GraphicStyle), typeof(HmiGraphicStyle), typeof(HmiSymbolIcon),
        new PropertyMetadata(HmiGraphicStyle.Process) { AffectsRender = true });
    public HmiGraphicStyle GraphicStyle
    {
        get => (HmiGraphicStyle)(GetValue(GraphicStyleProperty) ?? HmiGraphicStyle.Process);
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetValue(GraphicStyleProperty, value); }
    }
    public HmiSymbolIcon() { Width = 42; Height = 42; IsHitTestVisible = false; }
    public override void OnRender(DrawingContext context)
    {
        var size = Size;
        if (size.X < 8 || size.Y < 8) return;
        HmiSymbolRenderer.DrawGlyph(context, Symbol, new Rect(3, 3, size.X - 6, size.Y - 6), HmiPalette.Get(ColorScheme, GraphicStyle),
             62, 0, 100, false, false, HmiVisualTone.Normal, ports: false);
    }
}
