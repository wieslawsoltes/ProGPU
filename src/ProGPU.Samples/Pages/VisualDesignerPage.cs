using Thickness = Microsoft.UI.Xaml.Thickness;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.WinUI.Designer;

namespace ProGPU.Samples;

public static class VisualDesignerPage
{
    public static FrameworkElement Create()
    {
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(GridLength.Auto);
        grid.RowDefinitions.Add(GridLength.Star(1f));

        var headerStack = new StackPanel { Orientation = Orientation.Vertical };

        var title = new RichTextBlock { Font = AppState._font, FontSize = 18f, Margin = new Thickness(0, 0, 0, 8) };
        title.Inlines.Add(new Bold(new Run("Visual Designer Studio")));
        headerStack.AddChild(title);

        var description = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 16) };
        description.Inlines.Add(new Run("Construct and lay out dynamic UI interfaces in real-time. Drag and drop controls, customize properties reflectively, snap elements to physical subpixels, and serialize components to high-fidelity XAML."));
        headerStack.AddChild(description);

        grid.AddChild(headerStack);
        Grid.SetRow(headerStack, 0);

        var designerHost = new DesignerHost
        {
            DesignerFont = AppState._font,
            DesignerFontCourier = AppState._fontCourier,
            GetDpiScale = () => {
                return (float)DisplayScaleResolver.ResolveWindowDisplayScale(AppState._window);
            }
        };

        // Initialize fonts for internal designer UI components
        designerHost.InitializeFonts(AppState._font, AppState._fontCourier);

        // Pre-populate with premium controls on the design canvas
        designerHost.AddControlToCanvas("Button", 100f, 80f);
        designerHost.AddControlToCanvas("TextBox", 300f, 80f);
        designerHost.AddControlToCanvas("CheckBox", 100f, 160f);
        designerHost.AddControlToCanvas("Slider", 300f, 160f);

        grid.AddChild(designerHost);
        Grid.SetRow(designerHost, 1);

        return grid;
    }
}
