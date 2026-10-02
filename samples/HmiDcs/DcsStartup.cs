using Microsoft.UI.Xaml.Controls;
using ProGPU.Fonts.Inter;
using ProGPU.WinUI.Hmi.Designer;

namespace ProGPU.Hmi.Dcs.Sample;

internal static class DcsStartup
{
    internal static HmiDcsStudio CreateStudio(bool automaticTicks = true)
    {
        // Workplace controls capture their font while they are constructed.
        // Native Window.Load is later, and its optional Arial file is not a
        // cross-platform font dependency. Use the existing bundled face before
        // creating any labels, equipment or lazily constructed engineering UI.
        // An embedding host's explicit default remains authoritative.
        var font = PopupService.DefaultFont ?? InterFontFamily.Regular;
        PopupService.DefaultFont = font;
        return new HmiDcsStudio(font: font, automaticTicks: automaticTicks);
    }
}
