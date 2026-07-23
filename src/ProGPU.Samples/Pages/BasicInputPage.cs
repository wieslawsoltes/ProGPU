using Thickness = Microsoft.UI.Xaml.Thickness;
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
using ProGPU.Compute;
using ProGPU.Virtualization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Button = Microsoft.UI.Xaml.Controls.Button;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public static class BasicInputPage
{
        public static FrameworkElement Create()
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };
    
            var title = new RichTextBlock { Font = AppState._font, FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
            title.Inlines.Add(new Bold(new Run("Basic Input Controls & State Routing")));
            stack.AddChild(title);
    
            var description = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
            description.Inlines.Add(new Run("This page showcases standard high-performance input controls. Pointer hovers, clicks, and drag operations are natively routed down the recursive SceneGraph with real-time UI invalidation."));
            stack.AddChild(description);
    
            // 1. BUTTON
            var btnGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            var interactiveBtn = new Button { Width = 180f, Height = 36f, CornerRadius = 6f };
            var btnText = new RichTextBlock { Font = AppState._font, FontSize = 12f };
            btnText.Inlines.Add(new Run($"Click Count: {AppState._clickCount}"));
            interactiveBtn.Content = btnText;
            
            interactiveBtn.Click += (s, e) =>
            {
                AppState._clickCount++;
                btnText.Inlines.Clear();
                btnText.Inlines.Add(new Run($"Click Count: {AppState._clickCount}"));
                btnText.Invalidate();
            };
            btnGroup.AddChild(interactiveBtn);
    
            var btnDesc = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(15, 8, 0, 0) };
            btnDesc.Inlines.Add(new Run("Hover and press. Clicks increment count state directly."));
            btnGroup.AddChild(btnDesc);
            stack.AddChild(btnGroup);
    
            // 2. CHECKBOX
            var checkGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            var customCheck = new CheckBox { IsChecked = AppState._checkboxStatus == "Checked" };
            var checkLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f };
            checkLabel.Inlines.Add(new Run("Enable high-fidelity render features"));
            customCheck.Content = checkLabel;
    
            var checkStatus = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(30, 4, 0, 0) };
            checkStatus.Inlines.Add(new Run($"Current state: {AppState._checkboxStatus}"));
    
            customCheck.CheckedChanged += (s, e) =>
            {
                AppState._checkboxStatus = customCheck.IsChecked ? "Checked" : "Unchecked";
                checkStatus.Inlines.Clear();
                checkStatus.Inlines.Add(new Run($"Current state: {AppState._checkboxStatus}"));
                checkStatus.Invalidate();
            };
    
            checkGroup.AddChild(customCheck);
            checkGroup.AddChild(checkStatus);
            stack.AddChild(checkGroup);
    
            // Disabled Option to demonstrate visual states
            var disabledCheck = new CheckBox { IsEnabled = false, IsChecked = true, Margin = new Thickness(0, 0, 0, 15) };
            var disabledLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f };
            disabledLabel.Inlines.Add(new Run("Disabled read-only setting (Always checked)"));
            disabledCheck.Content = disabledLabel;
            stack.AddChild(disabledCheck);
    
            // 3. SLIDER
            var sliderTitle = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 10, 0, 4) };
            sliderTitle.Inlines.Add(new Bold(new Run($"Accent Glow Intensity: {AppState._sliderValue:F0}%")));
            stack.AddChild(sliderTitle);
    
            var accentSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 100f, Value = AppState._sliderValue, Width = 300f, Margin = new Thickness(0, 0, 0, 15) };
            accentSlider.ValueChanged += (s, e) =>
            {
                AppState._sliderValue = (float)accentSlider.Value;
                sliderTitle.Inlines.Clear();
                sliderTitle.Inlines.Add(new Bold(new Run($"Accent Glow Intensity: {AppState._sliderValue:F0}%")));
                sliderTitle.Invalidate();
            };
            stack.AddChild(accentSlider);
    
            // 4. TOOGLE SWITCH
            var toggleGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 15) };
            var interactiveToggle = new ToggleSwitch { IsOn = true };
            var toggleLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f };
            toggleLabel.Inlines.Add(new Run("Enable High-Fidelity Rendering"));
            interactiveToggle.Content = toggleLabel;
    
            var toggleStatusText = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(20, 4, 0, 0) };
            toggleStatusText.Inlines.Add(new Run("State: Active"));
            interactiveToggle.Toggled += (s, e) =>
            {
                toggleStatusText.Inlines.Clear();
                toggleStatusText.Inlines.Add(new Run(interactiveToggle.IsOn ? "State: Active" : "State: Inactive"));
                toggleStatusText.Invalidate();
            };
            toggleGroup.AddChild(interactiveToggle);
            toggleGroup.AddChild(toggleStatusText);
            stack.AddChild(toggleGroup);
    
            // 5. COMBOBOX
            var comboTitle = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 4) };
            comboTitle.Inlines.Add(new Bold(new Run("UI Accent Theme Colors Selection:")));
            stack.AddChild(comboTitle);
    
            var customCombo = new ComboBox { Font = AppState._font };
            customCombo.Items.Add(new ComboBoxItem("Segoe Blue (Default)"));
            customCombo.Items.Add(new ComboBoxItem("Emerald Green"));
            customCombo.Items.Add(new ComboBoxItem("Crimson Red"));
            customCombo.Items.Add(new ComboBoxItem("Amber Gold"));
            
            var comboStatus = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 4, 0, 15) };
            comboStatus.Inlines.Add(new Run("Selected theme: Segoe Blue (Default)"));
            customCombo.SelectionChanged += (s, e) =>
            {
                if (customCombo.SelectedItem is ComboBoxItem selectedItem)
                {
                    comboStatus.Inlines.Clear();
                    comboStatus.Inlines.Add(new Run($"Selected theme: {selectedItem.Text}"));
                    comboStatus.Invalidate();
                }
            };
            stack.AddChild(customCombo);
            stack.AddChild(comboStatus);
    
            return stack;
        }
}
