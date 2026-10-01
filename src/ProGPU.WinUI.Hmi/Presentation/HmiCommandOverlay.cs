using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Scene;

namespace ProGPU.WinUI.Hmi;

/// <summary>
/// Native button input/automation without opaque disabled-state template chrome.
/// The equipment's actual drawing and quality remain visible behind the interaction surface.
/// </summary>
internal sealed class HmiCommandOverlay : Button
{
    internal HmiCommandOverlay()
    {
        Template = new ControlTemplate(typeof(HmiCommandOverlay), _ => new Grid { IsHitTestVisible = false });
        Background = HmiDrawing.Transparent;
        BorderBrush = HmiDrawing.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public override void OnRender(DrawingContext context)
    {
        var scheme = Parent is HmiControl control ? control.ColorScheme : ProGPU.Hmi.HmiColorScheme.Light;
        var palette = HmiPalette.Get(scheme);
        if (Size.X < 6 || Size.Y < 6) return;
        if (IsEnabled && (IsPointerOver || IsFocused && InputSystem.IsKeyboardFocusActive))
            context.DrawRoundedRectangle(null, IsPointerPressed ? palette.Strong : palette.AccentLine,
                new Rect(2, 2, Size.X - 4, Size.Y - 4), 6);
    }
}
