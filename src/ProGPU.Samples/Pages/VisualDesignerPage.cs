using ProGPU.Backend;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.WinUI.Designer;

namespace ProGPU.Samples;

public static class VisualDesignerPage
{
    public static FrameworkElement Create()
    {
        var tabs = new Pivot { Font = AppState._font, Margin = new Thickness(8) };
        tabs.Items.Add(new PivotItem("Visual UI", CreateVisualDesigner()));
        var information = new StackPanel { Padding = new Thickness(24) };
        information.AddChild(new TextBlock { Text = "Standalone DCS workplaces", Font = AppState._font, FontSize = 22 });
        information.AddChild(new TextBlock { Text = "The full operator and engineering application is now a standalone sample.\nRun: dotnet run --project samples/HmiDcs/HmiDcs.csproj -c Release\n\nThe reusable HmiDesignerHost remains available for embedding.", Font = AppState._font, FontSize = 14, Margin = new Thickness(0, 18) });
        tabs.Items.Add(new PivotItem("HMI / DCS", information));
        return tabs;
    }

    private static FrameworkElement CreateVisualDesigner()
    {
        var grid = new Grid { Margin = new Thickness(4) };
        grid.RowDefinitions.Add(GridLength.Auto);
        grid.RowDefinitions.Add(GridLength.Star(1f));
        var header = new StackPanel { Orientation = Orientation.Vertical };
        var title = new RichTextBlock { Font = AppState._font, FontSize = 18f, Margin = new Thickness(0, 0, 0, 8) };
        title.Inlines.Add(new Bold(new Run("Visual Designer Studio")));
        header.AddChild(title);
        var description = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 12) };
        description.Inlines.Add(new Run("Drag controls onto the shared design canvas, edit properties and explore the generated source. The standalone HMI/DCS application reuses this canvas, outline and layout-design infrastructure."));
        header.AddChild(description);
        grid.AddChild(header);
        var host = new DesignerHost
        {
            DesignerFont = AppState._font,
            DesignerFontCourier = AppState._fontCourier,
            GetDpiScale = () => (float)DisplayScaleResolver.ResolveWindowDisplayScale(AppState._window)
        };
        host.InitializeFonts(AppState._font, AppState._fontCourier);
        host.AddControlToCanvas("Button", 100f, 80f);
        host.AddControlToCanvas("TextBox", 300f, 80f);
        host.AddControlToCanvas("CheckBox", 100f, 160f);
        host.AddControlToCanvas("Slider", 300f, 160f);
        grid.AddChild(host); Grid.SetRow(host, 1);
        return grid;
    }
}
