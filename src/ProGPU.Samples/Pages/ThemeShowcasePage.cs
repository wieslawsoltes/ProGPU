using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Button = Microsoft.UI.Xaml.Controls.Button;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace ProGPU.Samples;

public static class ThemeShowcasePage
{
    private static RichTextBlock CreateText(string text, Brush? foreground = null)
    {
        var rtb = new RichTextBlock();
        rtb.Inlines.Add(new Run(text) { Foreground = foreground });
        return rtb;
    }

    private static RichTextBlock CreateText(Run run)
    {
        var rtb = new RichTextBlock();
        rtb.Inlines.Add(run);
        return rtb;
    }

    public static FrameworkElement Create()
    {
        var scrollViewer = new ScrollViewer { Background = new ThemeResourceBrush("PageBackground"), Font = AppState._font };
        var mainStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(24) };
        scrollViewer.Content = mainStack;

        // Header Title
        var headerTitle = new RichTextBlock { Margin = new Thickness(0, 0, 0, 4) };
        headerTitle.Inlines.Add(new Bold(new Run("Concentric Theme Family Showcase") { FontSize = 28f, Foreground = new ThemeResourceBrush("TextPrimary") }));
        mainStack.AddChild(headerTitle);

        // Header Subtitle
        var headerSubtitle = new RichTextBlock { Margin = new Thickness(0, 0, 0, 24) };
        headerSubtitle.Inlines.Add(new Run("Demonstrating zero-overhead cascading theme families (Fluent next to Aqua running side-by-side simultaneously).") { FontSize = 14f, Foreground = new ThemeResourceBrush("TextSecondary") });
        mainStack.AddChild(headerSubtitle);

        // 2-Column Grid for Side-by-Side Comparison
        var comparisonGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        comparisonGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star)); // Col 0: WinUI Fluent
        comparisonGrid.ColumnDefinitions.Add(new GridLength(20f, GridUnitType.Absolute)); // Column spacing gap
        comparisonGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star)); // Col 1: macOS Aqua

        // Left Panel (WinUI)
        var leftContainer = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(20),
            RequestedThemeFamily = VisualThemeFamily.WinUI // Force Fluent Theme
        };
        var leftStack = new StackPanel { Orientation = Orientation.Vertical };
        leftContainer.Child = leftStack;
        PopulateControlsShowcase(leftStack, "WinUI 3 Fluent Design", VisualThemeFamily.WinUI);

        comparisonGrid.AddChild(leftContainer);
        Grid.SetColumn(leftContainer, 0);

        // Right Panel (macOS)
        var rightContainer = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(20),
            RequestedThemeFamily = VisualThemeFamily.macOS // Force Aqua Theme
        };
        var rightStack = new StackPanel { Orientation = Orientation.Vertical };
        rightContainer.Child = rightStack;
        PopulateControlsShowcase(rightStack, "macOS Aqua Aesthetics", VisualThemeFamily.macOS);

        comparisonGrid.AddChild(rightContainer);
        Grid.SetColumn(rightContainer, 2);

        mainStack.AddChild(comparisonGrid);
        return scrollViewer;
    }

    private static void PopulateControlsShowcase(StackPanel stack, string title, VisualThemeFamily family)
    {
        // Panel Header
        var titleText = new RichTextBlock { Margin = new Thickness(0, 0, 0, 16) };
        titleText.Inlines.Add(new Bold(new Run(title) { FontSize = 18f, Foreground = new ThemeResourceBrush("SystemAccentColor") }));
        stack.AddChild(titleText);

        // 1. Buttons
        AddLabel(stack, "Buttons");
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        
        var btnDefault = new Button { Margin = new Thickness(0, 0, 8, 0) };
        btnDefault.Content = CreateText("Default");
        btnRow.AddChild(btnDefault);

        var btnAccent = new Button { Margin = new Thickness(0, 0, 8, 0), Background = new ThemeResourceBrush("SystemAccentColor") };
        btnAccent.Content = CreateText(new Run("Accent") { Foreground = new ThemeResourceBrush("TextPrimary") });
        btnRow.AddChild(btnAccent);

        var btnDisabled = new Button { IsEnabled = false };
        btnDisabled.Content = CreateText("Disabled");
        btnRow.AddChild(btnDisabled);
        stack.AddChild(btnRow);

        // Toggle Buttons
        AddLabel(stack, "Toggle Buttons");
        var toggleBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        
        var tbtnUnchecked = new ToggleButton { IsChecked = false, Margin = new Thickness(0, 0, 8, 0) };
        tbtnUnchecked.Content = CreateText("Unchecked");
        toggleBtnRow.AddChild(tbtnUnchecked);

        var tbtnChecked = new ToggleButton { IsChecked = true, Margin = new Thickness(0, 0, 8, 0) };
        tbtnChecked.Content = CreateText("Checked");
        toggleBtnRow.AddChild(tbtnChecked);

        var tbtnDisabled = new ToggleButton { IsChecked = false, IsEnabled = false };
        tbtnDisabled.Content = CreateText("Disabled");
        toggleBtnRow.AddChild(tbtnDisabled);
        stack.AddChild(toggleBtnRow);

        // 2. CheckBoxes
        AddLabel(stack, "Checkboxes");
        var chkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        
        var chkChecked = new CheckBox { IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
        chkChecked.Content = CreateText("Active");
        chkRow.AddChild(chkChecked);

        var chkUnchecked = new CheckBox { IsChecked = false, Margin = new Thickness(0, 0, 12, 0) };
        chkUnchecked.Content = CreateText("Off");
        chkRow.AddChild(chkUnchecked);

        var chkDisabled = new CheckBox { IsChecked = true, IsEnabled = false };
        chkDisabled.Content = CreateText("Disabled");
        chkRow.AddChild(chkDisabled);
        stack.AddChild(chkRow);

        // 3. RadioButtons
        AddLabel(stack, "Radio Buttons");
        var radRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        string groupName = "Group_" + family.ToString();
        
        var rad1 = new RadioButton { IsChecked = true, GroupName = groupName, Margin = new Thickness(0, 0, 12, 0) };
        rad1.Content = CreateText("Option A");
        radRow.AddChild(rad1);

        var rad2 = new RadioButton { IsChecked = false, GroupName = groupName, Margin = new Thickness(0, 0, 12, 0) };
        rad2.Content = CreateText("Option B");
        radRow.AddChild(rad2);

        var radDisabled = new RadioButton { IsChecked = false, IsEnabled = false };
        radDisabled.Content = CreateText("Disabled");
        radRow.AddChild(radDisabled);
        stack.AddChild(radRow);

        // 4. Toggle Switches
        AddLabel(stack, "Toggle Switches");
        var togRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        
        var togOn = new ToggleSwitch { IsOn = true, Margin = new Thickness(0, 0, 16, 0) };
        togOn.Content = CreateText("On");
        togRow.AddChild(togOn);

        var togOff = new ToggleSwitch { IsOn = false, Margin = new Thickness(0, 0, 16, 0) };
        togOff.Content = CreateText("Off");
        togRow.AddChild(togOff);

        var togDisabled = new ToggleSwitch { IsEnabled = false };
        togDisabled.Content = CreateText("Disabled");
        togRow.AddChild(togDisabled);
        stack.AddChild(togRow);

        // 5. Sliders
        AddLabel(stack, "Sliders");
        var sldRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 150f, Margin = new Thickness(0, 0, 12, 0) };
        var sldValue = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center };
        sldValue.Inlines.Add(new Run("Value: ") { Foreground = new ThemeResourceBrush("TextSecondary") });
        var valRun = new Run("50") { Foreground = new ThemeResourceBrush("SystemAccentColor") };
        sldValue.Inlines.Add(valRun);
        slider.ValueChanged += (s, e) =>
        {
            valRun.Text = slider.Value.ToString("F0");
            sldValue.Invalidate();
        };
        sldRow.AddChild(slider);
        sldRow.AddChild(sldValue);
        stack.AddChild(sldRow);

        // Tahoe Liquid Glass Generic Card
        AddLabel(stack, "Tahoe Liquid Glass Effect (Generic Card)");
        var glassCard = new Border
        {
            Width = 220f,
            Height = 80f,
            CornerRadius = 16f,
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 0, 16),
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder")
        };
        
        var cardText = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        cardText.Inlines.Add(new Run("LIQUID GLASS CARD") { FontSize = 12f, Foreground = new ThemeResourceBrush("TextPrimary") });
        glassCard.Child = cardText;
        
        var genericGlass = new LiquidGlassEffect(0.7f);
        genericGlass.GlassColor = new Vector4(1f, 1f, 1f, 0.2f);
        genericGlass.FluidColor = new Vector4(0f, 0.7f, 0.9f, 0.85f); // Gorgeous cyan fluid
        glassCard.Effect = genericGlass;
        
        stack.AddChild(glassCard);

        // 6. Text Input
        AddLabel(stack, "TextBox / PasswordBox");
        var txtRow = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 16) };
        
        var textBox = new TextBox { PlaceholderText = "Placeholder...", Margin = new Thickness(0, 0, 0, 8), Width = 220f };
        txtRow.AddChild(textBox);

        var passwordBox = new PasswordBox { PlaceholderText = "Password...", Width = 220f };
        txtRow.AddChild(passwordBox);
        stack.AddChild(txtRow);

        // 7. Dropdown Selection
        AddLabel(stack, "ComboBox Selection");
        var combo = new ComboBox { PlaceholderText = "Choose flavor...", Width = 220f };
        combo.Items.Add(new ComboBoxItem { Text = "Vanilla Premium" });
        combo.Items.Add(new ComboBoxItem { Text = "Dark Chocolate" });
        combo.Items.Add(new ComboBoxItem { Text = "Strawberry Fields" });
        stack.AddChild(combo);
    }

    private static void AddLabel(StackPanel stack, string text)
    {
        var label = new RichTextBlock { Margin = new Thickness(0, 4, 0, 6) };
        label.Inlines.Add(new Bold(new Run(text) { FontSize = 12f, Foreground = new ThemeResourceBrush("TextSecondary") }));
        stack.AddChild(label);
    }
}
