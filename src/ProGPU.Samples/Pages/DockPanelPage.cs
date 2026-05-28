using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public static class DockPanelPage
{
    public static FrameworkElement Create()
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var mainStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        scrollViewer.Content = mainStack;

        // Header Title
        var title = new RichTextBlock { Font = AppState._font, FontSize = 22f, Margin = new Thickness(0, 0, 0, 6) };
        title.Inlines.Add(new Bold(new Run("DockPanel Control")));
        mainStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        desc.Inlines.Add(new Run("DockPanel arranges child elements along the perimeter of the panel (Left, Top, Right, Bottom). The final child can optionally stretch to fill all remaining central space."));
        mainStack.AddChild(desc);

        // Options control card
        var optionsCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(15),
            Margin = new Thickness(0, 0, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var optionsStack = new StackPanel { Orientation = Orientation.Horizontal };
        var toggleLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 6, 12, 0) };
        toggleLabel.Inlines.Add(new Run("LastChildFill:"));
        optionsStack.AddChild(toggleLabel);

        var dockPanel = new DockPanel
        {
            LastChildFill = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var toggleFill = new ToggleSwitch { IsOn = true };
        toggleFill.Toggled += (s, e) =>
        {
            dockPanel.LastChildFill = toggleFill.IsOn;
            dockPanel.InvalidateMeasure();
            dockPanel.Invalidate();
        };
        optionsStack.AddChild(toggleFill);

        optionsCard.Child = optionsStack;
        mainStack.AddChild(optionsCard);

        // The DockPanel container Card
        var containerCard = new Border
        {
            Height = 350f,
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(15),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // 1. Top docked header
        var topHeader = new Border { Height = 45f, Background = new SolidColorBrush(0xFF5555CC), Margin = new Thickness(4), CornerRadius = 4f };
        var topText = new RichTextBlock { Font = AppState._font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        topText.Inlines.Add(new Bold(new Run("HEADER: Top Docked")));
        topHeader.Child = topText;
        DockPanel.SetDock(topHeader, Dock.Top);
        dockPanel.Children.Add(topHeader);

        // 2. Bottom docked footer
        var bottomFooter = new Border { Height = 36f, Background = new SolidColorBrush(0x2E6BCCCC), Margin = new Thickness(4), CornerRadius = 4f };
        var bottomText = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        bottomText.Inlines.Add(new Bold(new Run("FOOTER: Bottom Docked")));
        bottomFooter.Child = bottomText;
        DockPanel.SetDock(bottomFooter, Dock.Bottom);
        dockPanel.Children.Add(bottomFooter);

        // 3. Left sidebar
        var leftSidebar = new Border { Width = 70f, Background = new SolidColorBrush(0x00FF88CC), Margin = new Thickness(4), CornerRadius = 4f };
        var leftText = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        leftText.Inlines.Add(new Bold(new Run("LEFT\nSIDE")));
        leftSidebar.Child = leftText;
        DockPanel.SetDock(leftSidebar, Dock.Left);
        dockPanel.Children.Add(leftSidebar);

        // 4. Right inspectbar
        var rightInspect = new Border { Width = 70f, Background = new SolidColorBrush(0xFF7A40CC), Margin = new Thickness(4), CornerRadius = 4f };
        var rightText = new RichTextBlock { Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        rightText.Inlines.Add(new Bold(new Run("RIGHT\nSIDE")));
        rightInspect.Child = rightText;
        DockPanel.SetDock(rightInspect, Dock.Right);
        dockPanel.Children.Add(rightInspect);

        // 5. Central area (last child fill)
        var centerArea = new Border { Background = new SolidColorBrush(0xA100FFCC), Margin = new Thickness(4), CornerRadius = 4f };
        var centerText = new RichTextBlock { Font = AppState._font, FontSize = 13f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        centerText.Inlines.Add(new Bold(new Run("CENTER AREA: Last Child Fill")));
        centerArea.Child = centerText;
        dockPanel.Children.Add(centerArea);

        containerCard.Child = dockPanel;
        mainStack.AddChild(containerCard);

        return scrollViewer;
    }
}
