namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private void ToggleDataPanels() => SetDataPanelsVisible(!AreDataPanelsVisible);
}
