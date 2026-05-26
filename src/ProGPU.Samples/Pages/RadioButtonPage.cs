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

public static class RadioButtonPage
{
    private static string _implicitSelected = "Single Pane";
    private static string _explicitSelected = "Dynamic HSL Mica";

    public static FrameworkElement Create()
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        scrollViewer.Content = stack;

        // Page Header
        var title = new RichTextBlock { Font = AppState._font, FontSize = 22f, Margin = new Thickness(0, 0, 0, 6) };
        title.Inlines.Add(new Bold(new Run("RadioButton Control")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 24) };
        description.Inlines.Add(new Run("RadioButton allows a user to select one option from a mutually exclusive group of choices. It supports implicit grouping (among sibling controls) and explicit GroupName-based grouping."));
        stack.AddChild(description);

        var radioCard = CreateShowcaseCard("Group Mutual Exclusion & Keyboard Traversal");
        var radioStack = new StackPanel { Orientation = Orientation.Vertical };

        var radioDesc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 16) };
        radioDesc.Inlines.Add(new Run("Use the "));
        radioDesc.Inlines.Add(new Bold(new Run("Arrow Keys")));
        radioDesc.Inlines.Add(new Run(" to navigate and auto-select buttons in the group, and "));
        radioDesc.Inlines.Add(new Bold(new Run("Tab")));
        radioDesc.Inlines.Add(new Run(" to skip the group."));
        radioStack.AddChild(radioDesc);

        // Group A: Sibling Implicit Grouping
        var implicitTitle = new RichTextBlock { Font = AppState._font, FontSize = 13f, Margin = new Thickness(0, 0, 0, 10) };
        implicitTitle.Inlines.Add(new Bold(new Run("Implicit Group (Same Parent Sibling Scope):")));
        radioStack.AddChild(implicitTitle);

        var implicitContainer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10, 0, 0, 15) };
        
        var rb1 = new RadioButton { IsChecked = true };
        var rb1Label = new RichTextBlock { Font = AppState._font, FontSize = 12f };
        rb1Label.Inlines.Add(new Run("Single Pane"));
        rb1.Content = rb1Label;

        var rb2 = new RadioButton();
        var rb2Label = new RichTextBlock { Font = AppState._font, FontSize = 12f };
        rb2Label.Inlines.Add(new Run("Split Vertical"));
        rb2.Content = rb2Label;

        var rb3 = new RadioButton();
        var rb3Label = new RichTextBlock { Font = AppState._font, FontSize = 12f };
        rb3Label.Inlines.Add(new Run("Split Horizontal"));
        rb3.Content = rb3Label;

        var implicitStatus = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 8, 0, 16) };
        implicitStatus.Inlines.Add(new Run("Selected Layout: "));
        implicitStatus.Inlines.Add(new Bold(new Run(_implicitSelected)) { Foreground = new ThemeResourceBrush("SystemAccentColor") });

        rb1.Checked += (s, e) => UpdateImplicitStatus(implicitStatus, "Single Pane");
        rb2.Checked += (s, e) => UpdateImplicitStatus(implicitStatus, "Split Vertical");
        rb3.Checked += (s, e) => UpdateImplicitStatus(implicitStatus, "Split Horizontal");

        implicitContainer.AddChild(rb1);
        implicitContainer.AddChild(rb2);
        implicitContainer.AddChild(rb3);
        radioStack.AddChild(implicitContainer);
        radioStack.AddChild(implicitStatus);

        // Group B: Explicit GroupName Grouping
        var explicitTitle = new RichTextBlock { Font = AppState._font, FontSize = 13f, Margin = new Thickness(0, 0, 0, 10) };
        explicitTitle.Inlines.Add(new Bold(new Run("Explicit Group (Visual Tree GroupName Scope):")));
        radioStack.AddChild(explicitTitle);

        var explicitContainer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10, 0, 0, 15) };
        
        var erb1 = new RadioButton { GroupName = "ThemeGroup", IsChecked = true };
        var erb1Label = new RichTextBlock { Font = AppState._font, FontSize = 12f };
        erb1Label.Inlines.Add(new Run("Dynamic HSL Mica"));
        erb1.Content = erb1Label;

        var erb2 = new RadioButton { GroupName = "ThemeGroup" };
        var erb2Label = new RichTextBlock { Font = AppState._font, FontSize = 12f };
        erb2Label.Inlines.Add(new Run("Solid Acrylic"));
        erb2.Content = erb2Label;

        var erb3 = new RadioButton { GroupName = "ThemeGroup" };
        var erb3Label = new RichTextBlock { Font = AppState._font, FontSize = 12f };
        erb3Label.Inlines.Add(new Run("High Contrast Dark"));
        erb3.Content = erb3Label;

        var explicitStatus = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 8, 0, 8) };
        explicitStatus.Inlines.Add(new Run("Selected Design Theme: "));
        explicitStatus.Inlines.Add(new Bold(new Run(_explicitSelected)) { Foreground = new ThemeResourceBrush("SystemAccentColor") });

        erb1.Checked += (s, e) => UpdateExplicitStatus(explicitStatus, "Dynamic HSL Mica");
        erb2.Checked += (s, e) => UpdateExplicitStatus(explicitStatus, "Solid Acrylic");
        erb3.Checked += (s, e) => UpdateExplicitStatus(explicitStatus, "High Contrast Dark");

        explicitContainer.AddChild(erb1);
        explicitContainer.AddChild(erb2);
        explicitContainer.AddChild(erb3);
        radioStack.AddChild(explicitContainer);
        radioStack.AddChild(explicitStatus);

        radioCard.Child = radioStack;
        stack.AddChild(radioCard);

        return scrollViewer;
    }

    private static void UpdateImplicitStatus(RichTextBlock statusBlock, string text)
    {
        _implicitSelected = text;
        statusBlock.Inlines.Clear();
        statusBlock.Inlines.Add(new Run("Selected Layout: "));
        statusBlock.Inlines.Add(new Bold(new Run(text)) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
        statusBlock.Invalidate();
    }

    private static void UpdateExplicitStatus(RichTextBlock statusBlock, string text)
    {
        _explicitSelected = text;
        statusBlock.Inlines.Clear();
        statusBlock.Inlines.Add(new Run("Selected Design Theme: "));
        statusBlock.Inlines.Add(new Bold(new Run(text)) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
        statusBlock.Invalidate();
    }

    private static Border CreateShowcaseCard(string headerText)
    {
        var border = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var mainStack = new StackPanel { Orientation = Orientation.Vertical };
        var header = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 16) };
        header.Inlines.Add(new Bold(new Run(headerText)));
        
        mainStack.AddChild(header);
        
        // Horizontal divider stripe
        var divider = new Border
        {
            Height = 1f,
            Background = new ThemeResourceBrush("ControlBorder"),
            Margin = new Thickness(0, 0, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        mainStack.AddChild(divider);

        // Content placeholder panel
        var contentPanel = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        mainStack.AddChild(contentPanel);

        border.Child = mainStack;

        // Custom content wrapper
        border.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Border.Child) && border.Child != mainStack)
            {
                var content = border.Child;
                border.Child = mainStack;
                contentPanel.Child = content;
            }
        };

        return border;
    }
}
