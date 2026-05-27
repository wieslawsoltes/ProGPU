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
using ProGPU.Designer;

namespace ProGPU.Samples;

public static class VisualDesignerPage
{
    public static FrameworkElement Create()
    {
        var mainStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = AppState._font, FontSize = 18f, Margin = new Thickness(0, 0, 0, 8) };
        title.Inlines.Add(new Bold(new Run("Visual Designer Studio")));
        mainStack.AddChild(title);

        var description = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 16) };
        description.Inlines.Add(new Run("Construct and lay out dynamic UI interfaces in real-time. Drag and drop controls, customize properties reflectively, snap elements to physical subpixels, and serialize components to high-fidelity XAML."));
        mainStack.AddChild(description);

        var designerHost = new DesignerHost
        {
            Height = 700f,
            DesignerFont = AppState._font,
            DesignerFontCourier = AppState._fontCourier,
            GetDpiScale = () => {
                if (AppState._window != null && AppState._window.Size.X > 0)
                {
                    return (float)AppState._window.FramebufferSize.X / AppState._window.Size.X;
                }
                return 1.0f;
            }
        };

        // Initialize fonts for internal designer UI components
        designerHost.InitializeFonts(AppState._font, AppState._fontCourier);

        // Pre-populate with premium controls on the design canvas
        designerHost.AddControlToCanvas("Button", 100f, 80f);
        designerHost.AddControlToCanvas("TextBox", 300f, 80f);
        designerHost.AddControlToCanvas("CheckBox", 100f, 160f);
        designerHost.AddControlToCanvas("Slider", 300f, 160f);

        mainStack.AddChild(designerHost);

        return mainStack;
    }
}
