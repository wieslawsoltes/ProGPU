using Microsoft.UI.Xaml;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private void ToggleDataPanels() => _dataArea.Visibility =
        _dataArea.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
}
