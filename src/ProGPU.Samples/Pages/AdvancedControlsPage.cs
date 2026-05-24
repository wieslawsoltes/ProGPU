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
using ProGPU.WinUI;
using Button = ProGPU.WinUI.Button;
using StackPanel = ProGPU.WinUI.StackPanel;

namespace ProGPU.Samples;

public static class AdvancedControlsPage
{
        public static FrameworkElement Create()
        {
            var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new GridLength(50f, GridUnitType.Absolute));   // Header description
            grid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Showcase column grids
    
            var descText = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
            descText.Inlines.Add(new Run("This page showcases advanced WinUI controls including dialog modals, absolute dropdown calendars, DatePickers, tooltips service delays, and determinate/indeterminate progress systems."));
            grid.AddChild(descText);
            ProGPU.WinUI.Grid.SetRow(descText, 0);
    
            var cardsGrid = new ProGPU.WinUI.Grid();
            cardsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            cardsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            cardsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
    
            // ================= COLUMN 1: ContentDialog & ToolTips =================
            var col1Stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8f) };
            var card1 = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA), // Mica dark card
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(20f),
                Child = col1Stack
            };
    
            var header1 = new RichTextBlock { Font = AppState._font, FontSize = 16f, Margin = new Thickness(0f, 0f, 0f, 16f) };
            header1.Inlines.Add(new Bold(new Run("ContentDialog & ToolTips")));
            col1Stack.AddChild(header1);
    
            var dialogResultText = new RichTextBlock { Font = AppState._font, FontSize = 12f, Foreground = new SolidColorBrush(0xFFFFFF80), Margin = new Thickness(0f, 0f, 0f, 12f) };
            dialogResultText.Inlines.Add(new Run("Last Dialog Response: None"));
    
            var triggerDialogBtnText = new RichTextBlock { Font = AppState._font, FontSize = 12f, Foreground = new SolidColorBrush(0xFFFFFFFF) };
            triggerDialogBtnText.Inlines.Add(new Run("Trigger Modal Dialog"));
    
            var triggerDialogBtn = new Button
            {
                Content = triggerDialogBtnText,
                Width = 160f,
                Height = 32f,
                Background = new SolidColorBrush(0x0078D4FF),
                Margin = new Thickness(0f, 0f, 0f, 16f)
            };
            triggerDialogBtn.Click += (s, e) =>
            {
                DialogPresenter.ShowResetDialog(dialogResultText);
            };
    
    
            col1Stack.AddChild(triggerDialogBtn);
            col1Stack.AddChild(dialogResultText);
    
            // ToolTip description
            var tooltipDesc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0f, 16f, 0f, 8f) };
            tooltipDesc.Inlines.Add(new Run("Hover these buttons for 500ms to test ToolTips:"));
            col1Stack.AddChild(tooltipDesc);
    
            var tipBtn1Text = new RichTextBlock { Font = AppState._font, FontSize = 12f, Foreground = new SolidColorBrush(0xFFFFFFFF) };
            tipBtn1Text.Inlines.Add(new Run("Hover Primary Action"));
            var tipBtn1 = new Button
            {
                Content = tipBtn1Text,
                Width = 160f,
                Height = 32f,
                Background = new SolidColorBrush(0xFFFFFF15),
                ToolTip = "Trigger a primary diagnostic frame capture trace.",
                Margin = new Thickness(0f, 0f, 0f, 8f)
            };
            col1Stack.AddChild(tipBtn1);
    
            var tipBtn2Text = new RichTextBlock { Font = AppState._font, FontSize = 12f, Foreground = new SolidColorBrush(0xFFFFFFFF) };
            tipBtn2Text.Inlines.Add(new Run("Hover Warning Info"));
            var tipBtn2 = new Button
            {
                Content = tipBtn2Text,
                Width = 160f,
                Height = 32f,
                Background = new SolidColorBrush(0xFFFFFF15),
                ToolTip = "Be careful: resetting caches will flush intermediate WebGPU resources.",
                Margin = new Thickness(0f, 0f, 0f, 8f)
            };
            col1Stack.AddChild(tipBtn2);
    
            cardsGrid.AddChild(card1);
            ProGPU.WinUI.Grid.SetColumn(card1, 0);
    
            // ================= COLUMN 2: Calendar & DatePicker =================
            var col2Stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8f) };
            var card2 = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(20f),
                Child = col2Stack
            };
    
            var header2 = new RichTextBlock { Font = AppState._font, FontSize = 16f, Margin = new Thickness(0f, 0f, 0f, 16f) };
            header2.Inlines.Add(new Bold(new Run("Calendar & Date Selection")));
            col2Stack.AddChild(header2);
    
            // DatePicker input dropdown trigger
            var datePickerDesc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
            datePickerDesc.Inlines.Add(new Run("Dropdown DatePicker selector:"));
            col2Stack.AddChild(datePickerDesc);
    
            var datePicker = new DatePicker { Header = "Select Frame Target", Margin = new Thickness(0f, 0f, 0f, 20f) };
            col2Stack.AddChild(datePicker);
    
            // Standalone calendar view grid
            var calendarDesc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
            calendarDesc.Inlines.Add(new Run("Standalone Calendar month grid:"));
            col2Stack.AddChild(calendarDesc);
    
            var calendar = new CalendarView { Width = 200f, Height = 220f };
            col2Stack.AddChild(calendar);
    
            cardsGrid.AddChild(card2);
            ProGPU.WinUI.Grid.SetColumn(card2, 1);
    
            // ================= COLUMN 3: Progress indicators =================
            var col3Stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8f) };
            var card3 = new Border
            {
                Background = new SolidColorBrush(0x1F1F24FA),
                BorderBrush = new SolidColorBrush(0xFFFFFF15),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(20f),
                Child = col3Stack
            };
    
            var header3 = new RichTextBlock { Font = AppState._font, FontSize = 16f, Margin = new Thickness(0f, 0f, 0f, 16f) };
            header3.Inlines.Add(new Bold(new Run("Progress Status Loaders")));
            col3Stack.AddChild(header3);
    
            // Determinate progress section
            var detDesc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
            detDesc.Inlines.Add(new Run("Determinate progress (controlled by slider):"));
            col3Stack.AddChild(detDesc);
    
            var progressBar = new ProgressBar { Minimum = 0f, Maximum = 100f, Value = 45f, Margin = new Thickness(0f, 0f, 0f, 16f) };
            col3Stack.AddChild(progressBar);
    
            var slider = new Slider { Minimum = 0f, Maximum = 100f, Value = 45f, Margin = new Thickness(0f, 0f, 0f, 20f) };
            slider.ValueChanged += (s, e) =>
            {
                progressBar.Value = slider.Value;
            };
            col3Stack.AddChild(slider);
    
            // Indeterminate progress sections
            var indetDesc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
            indetDesc.Inlines.Add(new Run("Indeterminate sliding ProgressBar track:"));
            col3Stack.AddChild(indetDesc);
    
            var indetBar = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0f, 0f, 0f, 24f) };
            col3Stack.AddChild(indetBar);
    
            var ringDesc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
            ringDesc.Inlines.Add(new Run("Indeterminate spinning ProgressRing loading dots:"));
            col3Stack.AddChild(ringDesc);
    
            var indetRing = new ProgressRing { Width = 36f, Height = 36f, Margin = new Thickness(0f) };
            col3Stack.AddChild(indetRing);
    
            cardsGrid.AddChild(card3);
            ProGPU.WinUI.Grid.SetColumn(card3, 2);
    
            grid.AddChild(cardsGrid);
            ProGPU.WinUI.Grid.SetRow(cardsGrid, 1);
    
            return grid;
        }
}
