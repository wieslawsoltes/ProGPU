using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

internal sealed class HmiDesignerCanvas : DesignerCanvas
{
    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        // Keep shared pan/zoom behavior while blocking direct manipulation of locked equipment.
        if (SelectedElement is HmiControl { IsDesignLocked: true } && e.IsLeftButtonPressed && !e.IsMiddleButtonPressed) return;
        base.OnPointerMoved(e);
    }
}
