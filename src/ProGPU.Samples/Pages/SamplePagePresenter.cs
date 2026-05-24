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

public static class SamplePagePresenter
{
    public static FrameworkElement CreateDrawingContextShowcaseView()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = AppState.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Bold(new Run("WebGPU Shaders & DrawingContext Vector APIs")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        description.Inlines.Add(new Run("This page showcases the full GPU-accelerated drawing context. Gradients are computed smoothly in parallel per-pixel in WebGPU WGSL shaders. Shapes are dynamically tessellated on the GPU at maximum framerates."));
        stack.AddChild(description);

        var visual = new DrawingShowcaseVisual();
        stack.AddChild(visual);

        return stack;
    }

    public static FrameworkElement CreateFileStorageShowcaseView()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = AppState.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Bold(new Run("Native Storage File Pickers & Async I/O")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        description.Inlines.Add(new Run("Use standard native asynchronous pickers (FileOpenPicker, FileSavePicker) to query system dialogs. Reads and writes files asynchronously using WinUI's StorageFile platform subsystem."));
        stack.AddChild(description);

        var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
        
        var openBtn = new Button { Width = 160f, Height = 36f, CornerRadius = 6f, Margin = new Thickness(0, 0, 10, 0) };
        var openBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        openBtnText.Inlines.Add(new Run("Open Text File..."));
        openBtn.Content = openBtnText;

        var saveBtn = new Button { Width = 160f, Height = 36f, CornerRadius = 6f, Margin = new Thickness(0, 0, 10, 0) };
        var saveBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        saveBtnText.Inlines.Add(new Run("Save Copy As..."));
        saveBtn.Content = saveBtnText;

        var folderBtn = new Button { Width = 160f, Height = 36f, CornerRadius = 6f };
        var folderBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        folderBtnText.Inlines.Add(new Run("Select Folder..."));
        folderBtn.Content = folderBtnText;

        actionsRow.AddChild(openBtn);
        actionsRow.AddChild(saveBtn);
        actionsRow.AddChild(folderBtn);
        stack.AddChild(actionsRow);

        var statusHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 13f, Margin = new Thickness(0, 10, 0, 5) };
        statusHeader.Inlines.Add(new Bold(new Run("Subsystem Status:")));
        stack.AddChild(statusHeader);

        var statusText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, Margin = new Thickness(0, 0, 0, 15), Foreground = ThemeManager.GetBrush("TextSecondary") };
        statusText.Inlines.Add(new Run("Idle. Waiting for picker interaction."));
        stack.AddChild(statusText);

        var contentHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 13f, Margin = new Thickness(0, 5, 0, 5) };
        contentHeader.Inlines.Add(new Bold(new Run("Storage File Content Workspace:")));
        stack.AddChild(contentHeader);

        var editorBorder = new Border
        {
            Background = ThemeManager.GetBrush("ControlBackground"),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 6f,
            Padding = new Thickness(12f),
            HeightConstraint = 200f
        };
        var editorText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        editorText.Inlines.Add(new Run("Open a file to load its raw text contents into this workspace..."));
        editorBorder.Child = editorText;
        stack.AddChild(editorBorder);

        // Async event hookups
        openBtn.Click += async (s, e) =>
        {
            statusText.Inlines.Clear();
            statusText.Inlines.Add(new Run("Launching system file dialog..."));
            statusText.Invalidate();

            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();

            if (file != null)
            {
                statusText.Inlines.Clear();
                statusText.Inlines.Add(new Run($"Successfully loaded file: {file.Path}"));
                statusText.Invalidate();

                try
                {
                    string txt = await file.ReadTextAsync();
                    editorText.Inlines.Clear();
                    editorText.Inlines.Add(new Run(txt));
                    editorText.Invalidate();
                }
                catch (Exception ex)
                {
                    editorText.Inlines.Clear();
                    editorText.Inlines.Add(new Run($"Error reading file contents: {ex.Message}"));
                    editorText.Invalidate();
                }
            }
            else
            {
                statusText.Inlines.Clear();
                statusText.Inlines.Add(new Run("User cancelled file dialog operation."));
                statusText.Invalidate();
            }
        };

        saveBtn.Click += async (s, e) =>
        {
            statusText.Inlines.Clear();
            statusText.Inlines.Add(new Run("Launching save dialog..."));
            statusText.Invalidate();

            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("Text Files", new List<string> { ".txt" });
            picker.SuggestedFileName = "my_progpu_file.txt";
            var file = await picker.PickSaveFileAsync();

            if (file != null)
            {
                try
                {
                    string textToSave = editorText.Inlines.Count > 0 ? ((Run)editorText.Inlines[0]).Text : string.Empty;
                    await file.WriteTextAsync(textToSave);
                    statusText.Inlines.Clear();
                    statusText.Inlines.Add(new Run($"Successfully saved file to: {file.Path}"));
                    statusText.Invalidate();
                }
                catch (Exception ex)
                {
                    statusText.Inlines.Clear();
                    statusText.Inlines.Add(new Run($"Error saving file: {ex.Message}"));
                    statusText.Invalidate();
                }
            }
            else
            {
                statusText.Inlines.Clear();
                statusText.Inlines.Add(new Run("User cancelled save dialog."));
                statusText.Invalidate();
            }
        };

        folderBtn.Click += async (s, e) =>
        {
            statusText.Inlines.Clear();
            statusText.Inlines.Add(new Run("Launching folder selection dialog..."));
            statusText.Invalidate();

            var picker = new FolderPicker();
            var folder = await picker.PickSingleFolderAsync();

            if (folder != null)
            {
                statusText.Inlines.Clear();
                statusText.Inlines.Add(new Run($"Successfully selected directory: {folder.Path}"));
                statusText.Invalidate();
            }
            else
            {
                statusText.Inlines.Clear();
                statusText.Inlines.Add(new Run("User cancelled folder dialog."));
                statusText.Invalidate();
            }
        };

        return stack;
    }

    public static FrameworkElement CreateStylesShowcaseView()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = AppState.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Bold(new Run("Fluent WinUI Styles & Setter Engine")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        description.Inlines.Add(new Run("Assign uniform looks to visual panels and buttons using C# styles. Below is a comparison between standard controls, and styled controls styled with setter objects."));
        stack.AddChild(description);

        var containerGrid = new ProGPU.WinUI.Grid();
        containerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        containerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        // Column 0: Standard Unstyled Controls
        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10f) };
        var leftHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 14f, Margin = new Thickness(0, 0, 0, 15) };
        leftHeader.Inlines.Add(new Bold(new Run("Standard Controls")));
        leftStack.AddChild(leftHeader);

        var normalBtn1 = new Button { Width = 160f, Height = 36f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 10f) };
        var normalBtnText1 = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        normalBtnText1.Inlines.Add(new Run("Default Button 1"));
        normalBtn1.Content = normalBtnText1;
        leftStack.AddChild(normalBtn1);

        var normalBtn2 = new Button { Width = 160f, Height = 36f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 10f) };
        var normalBtnText2 = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        normalBtnText2.Inlines.Add(new Run("Default Button 2"));
        normalBtn2.Content = normalBtnText2;
        leftStack.AddChild(normalBtn2);

        containerGrid.AddChild(leftStack);
        ProGPU.WinUI.Grid.SetColumn(leftStack, 0);

        // Column 1: Styled Controls
        var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10f) };
        var rightHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 14f, Margin = new Thickness(0, 0, 0, 15) };
        rightHeader.Inlines.Add(new Bold(new Run("Styled via Reflection Setters")));
        rightStack.AddChild(rightHeader);

        // Create the Style instance
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter("Width", 200f));
        buttonStyle.Setters.Add(new Setter("Height", 44f));
        buttonStyle.Setters.Add(new Setter("CornerRadius", 10f));

        var styledBtn1 = new Button { Margin = new Thickness(0, 0, 0, 10f) };
        var styledBtnText1 = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        styledBtnText1.Inlines.Add(new Run("Styled Premium Button 1"));
        styledBtn1.Content = styledBtnText1;
        styledBtn1.Style = buttonStyle; // Apply style
        rightStack.AddChild(styledBtn1);

        var styledBtn2 = new Button { Margin = new Thickness(0, 0, 0, 10f) };
        var styledBtnText2 = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        styledBtnText2.Inlines.Add(new Run("Styled Premium Button 2"));
        styledBtn2.Content = styledBtnText2;
        styledBtn2.Style = buttonStyle; // Apply style
        rightStack.AddChild(styledBtn2);

        containerGrid.AddChild(rightStack);
        ProGPU.WinUI.Grid.SetColumn(rightStack, 1);
        stack.AddChild(containerGrid);

        return stack;
    }

    public static FrameworkElement CreateMotionMarkShowcaseView()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = AppState.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Bold(new Run("GPU Vector Benchmark - MotionMark Showcase")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 15) };
        description.Inlines.Add(new Run("This page implements a native high-performance GPU vector graphics benchmark based on the MotionMark suite. Renders thousands of dynamic shapes (lines, circles, and direct GPU Beziers) with zero CPU triangulation or flattening, achieving ultimate frame rates."));
        stack.AddChild(description);

        var grid = new ProGPU.WinUI.Grid { HeightConstraint = 520f };
        grid.ColumnDefinitions.Add(new GridLength(300, GridUnitType.Absolute)); // Column 0: Settings Panel
        grid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Column 1: Visual Canvas Card

        var visual = new MotionMarkShowcaseVisual();

        // 1. Settings Card
        var settingsCard = new Border {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            Background = ThemeManager.GetBrush("ControlBackground"),
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 16, 0)
        };
        var settingsStack = new StackPanel { Orientation = Orientation.Vertical };
        settingsCard.Child = settingsStack;

        // Element Count
        var countLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        countLabel.Inlines.Add(new Bold(new Run("Element Count: 1,000")));
        settingsStack.AddChild(countLabel);
        
        var countSlider = new ProGPU.WinUI.Slider();
        countSlider.Maximum = 100000f;
        countSlider.Minimum = 1000f;
        countSlider.Value = 1000f;
        countSlider.Margin = new Thickness(0, 0, 0, 16);
        countSlider.ValueChanged += (s, e) => {
            int val = (int)(Math.Round(countSlider.Value / 1000f) * 1000f);
            if (val < 1000) val = 1000;
            if (Math.Abs(countSlider.Value - val) > 0.01f)
            {
                countSlider.Value = val;
                return;
            }
            visual.SetComplexity(val);
            countLabel.Inlines.Clear();
            countLabel.Inlines.Add(new Bold(new Run($"Element Count: {val:N0}")));
            countLabel.Invalidate();
        };
        settingsStack.AddChild(countSlider);

        // Stroke Width
        var strokeLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        strokeLabel.Inlines.Add(new Bold(new Run("Stroke Scale: 1.0x")));
        settingsStack.AddChild(strokeLabel);
        
        var strokeSlider = new ProGPU.WinUI.Slider { Minimum = 0.1f, Maximum = 5.0f, Value = 1.0f, Margin = new Thickness(0, 0, 0, 16) };
        strokeSlider.ValueChanged += (s, e) => {
            visual.StrokeThicknessMultiplier = strokeSlider.Value;
            visual.UpdateCachedPens();
            strokeLabel.Inlines.Clear();
            strokeLabel.Inlines.Add(new Bold(new Run($"Stroke Scale: {strokeSlider.Value:F1}x")));
            strokeLabel.Invalidate();
        };
        settingsStack.AddChild(strokeSlider);

        // Animation Speed
        var animLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        animLabel.Inlines.Add(new Bold(new Run("Wobble Animation Speed: 1.0x")));
        settingsStack.AddChild(animLabel);
        
        var animSlider = new ProGPU.WinUI.Slider { Minimum = 0.0f, Maximum = 5.0f, Value = 1.0f, Margin = new Thickness(0, 0, 0, 16) };
        animSlider.ValueChanged += (s, e) => {
            visual.AnimationSpeed = animSlider.Value;
            animLabel.Inlines.Clear();
            animLabel.Inlines.Add(new Bold(new Run($"Wobble Animation Speed: {animSlider.Value:F1}x")));
            animLabel.Invalidate();
        };
        settingsStack.AddChild(animSlider);

        // Split Chance
        var splitLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        splitLabel.Inlines.Add(new Bold(new Run("Segment Split Chance: 50%")));
        settingsStack.AddChild(splitLabel);
        
        var splitSlider = new ProGPU.WinUI.Slider { Minimum = 0.0f, Maximum = 1.0f, Value = 0.5f, Margin = new Thickness(0, 0, 0, 16) };
        splitSlider.ValueChanged += (s, e) => {
            visual.SplitProbability = splitSlider.Value;
            splitLabel.Inlines.Clear();
            splitLabel.Inlines.Add(new Bold(new Run($"Segment Split Chance: {(int)(splitSlider.Value * 100)}%")));
            splitLabel.Invalidate();
        };
        settingsStack.AddChild(splitSlider);

        // Color Palette
        var colorLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        colorLabel.Inlines.Add(new Bold(new Run("Color Palette:")));
        settingsStack.AddChild(colorLabel);
        
        var colorCombo = new ComboBox { Font = AppState.GetFont(), WidthConstraint = 260f, Margin = new Thickness(0, 0, 0, 16) };
        colorCombo.Items.Add(new ComboBoxItem("Standard Classical"));
        colorCombo.Items.Add(new ComboBoxItem("Fluent Vibrant"));
        colorCombo.Items.Add(new ComboBoxItem("Rainbow / Hue Wave"));
        colorCombo.Items.Add(new ComboBoxItem("Monochrome Dark"));
        colorCombo.SelectionChanged += (s, e) => {
            if (colorCombo.SelectedItem != null) {
                visual.ColorMode = colorCombo.SelectedItem.Text switch {
                    "Standard Classical" => 0,
                    "Fluent Vibrant" => 1,
                    "Rainbow / Hue Wave" => 2,
                    "Monochrome Dark" => 3,
                    _ => 0
                };
                visual.RegenerateColors();
            }
        };
        settingsStack.AddChild(colorCombo);

        // Segment Mix Checkboxes
        var typeLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 8) };
        typeLabel.Inlines.Add(new Bold(new Run("Segment Types Mix:")));
        settingsStack.AddChild(typeLabel);

        var lineCheckText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f };
        lineCheckText.Inlines.Add(new Run("Lines"));
        var lineCheck = new CheckBox { Content = lineCheckText, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        lineCheck.CheckedChanged += (s, e) => {
            visual.EnableLines = lineCheck.IsChecked;
            visual.RegenerateSegments();
        };
        settingsStack.AddChild(lineCheck);

        var quadCheckText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f };
        quadCheckText.Inlines.Add(new Run("Quadratic Curves"));
        var quadCheck = new CheckBox { Content = quadCheckText, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        quadCheck.CheckedChanged += (s, e) => {
            visual.EnableQuadBeziers = quadCheck.IsChecked;
            visual.RegenerateSegments();
        };
        settingsStack.AddChild(quadCheck);

        var cubicCheckText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f };
        cubicCheckText.Inlines.Add(new Run("Cubic Curves"));
        var cubicCheck = new CheckBox { Content = cubicCheckText, IsChecked = true, Margin = new Thickness(0, 0, 0, 16) };
        cubicCheck.CheckedChanged += (s, e) => {
            visual.EnableCubicBeziers = cubicCheck.IsChecked;
            visual.RegenerateSegments();
        };
        settingsStack.AddChild(cubicCheck);

        // Fills vs Strokes
        var fillToggleLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        fillToggleLabel.Inlines.Add(new Bold(new Run("Render Path Fills instead of Strokes:")));
        settingsStack.AddChild(fillToggleLabel);
        
        var fillToggle = new ToggleSwitch { IsOn = false, Margin = new Thickness(0, 0, 0, 8) };
        fillToggle.Toggled += (s, e) => {
            visual.FillShapes = fillToggle.IsOn;
            visual.Invalidate();
        };
        settingsStack.AddChild(fillToggle);

        grid.AddChild(settingsCard);
        ProGPU.WinUI.Grid.SetColumn(settingsCard, 0);

        grid.AddChild(visual);
        ProGPU.WinUI.Grid.SetColumn(visual, 1);

        stack.AddChild(grid);

        return stack;
    }

    public static FrameworkElement CreateTypographyScriptsView()
    {
        var scroll = new ScrollViewer { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16) };
        scroll.Content = stack;

        var title = new RichTextBlock { Font = AppState.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 8) };
        title.Inlines.Add(new Bold(new Run("🔤 Advanced Typography, Unicode & Language Scripts")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 20), Foreground = ThemeManager.GetBrush("TextSecondary") };
        description.Inlines.Add(new Run("This page showcases the high-performance rendering of different language scripts, custom system fonts, and Unicode symbol outlines on the GPU. Settings altered in the configuration card apply dynamically to all script panels."));
        stack.AddChild(description);

        var grid = new ProGPU.WinUI.Grid();
        grid.ColumnDefinitions.Add(new GridLength(280f, GridUnitType.Absolute)); // Settings Sidebar
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Scripts Dashboard

        // Left Panel: Configuration Card
        var settingsCard = new Border
        {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            Background = ThemeManager.GetBrush("ControlBackground"),
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        var settingsStack = new StackPanel { Orientation = Orientation.Vertical };
        settingsCard.Child = settingsStack;

        // ComboBox for Font Family
        var fontLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        fontLabel.Inlines.Add(new Bold(new Run("Choose Font Family:")));
        settingsStack.AddChild(fontLabel);

        var fontCombo = new ComboBox { Font = AppState.GetFont(), WidthConstraint = 240f, Margin = new Thickness(0, 0, 0, 16) };
        var itemArial = new ComboBoxItem("Arial (Primary)");
        var itemTimes = new ComboBoxItem("Times New Roman");
        var itemCourier = new ComboBoxItem("Courier New");
        var itemGeorgia = new ComboBoxItem("Georgia");
        var itemComic = new ComboBoxItem("Comic Sans MS");
        fontCombo.Items.Add(itemArial);
        fontCombo.Items.Add(itemTimes);
        fontCombo.Items.Add(itemCourier);
        fontCombo.Items.Add(itemGeorgia);
        fontCombo.Items.Add(itemComic);
        fontCombo.SelectedItem = itemArial;
        settingsStack.AddChild(fontCombo);

        // Slider for Size
        var sizeLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        sizeLabel.Inlines.Add(new Bold(new Run("Global Font Size: 16")));
        settingsStack.AddChild(sizeLabel);

        var sizeSlider = new ProGPU.WinUI.Slider { Minimum = 12f, Maximum = 32f, Value = 16f, Margin = new Thickness(0, 0, 0, 16) };
        settingsStack.AddChild(sizeSlider);

        // ComboBox for Alignment
        var alignLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        alignLabel.Inlines.Add(new Bold(new Run("Text Alignment:")));
        settingsStack.AddChild(alignLabel);

        var alignCombo = new ComboBox { Font = AppState.GetFont(), WidthConstraint = 240f, Margin = new Thickness(0, 0, 0, 16) };
        var alignLeft = new ComboBoxItem("Left");
        var alignCenter = new ComboBoxItem("Center");
        var alignRight = new ComboBoxItem("Right");
        var alignJustify = new ComboBoxItem("Justify");
        alignCombo.Items.Add(alignLeft);
        alignCombo.Items.Add(alignCenter);
        alignCombo.Items.Add(alignRight);
        alignCombo.Items.Add(alignJustify);
        alignCombo.SelectedItem = alignLeft;
        settingsStack.AddChild(alignCombo);

        // Slider for Contrast (Font Smoothing)
        var contrastLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        contrastLabel.Inlines.Add(new Bold(new Run($"Font Contrast/Dilation: {Compositor.DefaultTextContrast:F2}")));
        settingsStack.AddChild(contrastLabel);

        var contrastSlider = new ProGPU.WinUI.Slider { Minimum = 0.5f, Maximum = 2.5f, Value = Compositor.DefaultTextContrast, Margin = new Thickness(0, 0, 0, 16) };
        settingsStack.AddChild(contrastSlider);

        // Slider for Gamma Correction
        var gammaLabel = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        gammaLabel.Inlines.Add(new Bold(new Run($"Font Gamma: {Compositor.DefaultTextGamma:F2}")));
        settingsStack.AddChild(gammaLabel);

        var gammaSlider = new ProGPU.WinUI.Slider { Minimum = 1.0f, Maximum = 3.0f, Value = Compositor.DefaultTextGamma, Margin = new Thickness(0, 0, 0, 16) };
        settingsStack.AddChild(gammaSlider);

        grid.AddChild(settingsCard);
        ProGPU.WinUI.Grid.SetColumn(settingsCard, 0);

        // Right Panel: Scripts List
        var dashboardStack = new StackPanel { Orientation = Orientation.Vertical };
        grid.AddChild(dashboardStack);
        ProGPU.WinUI.Grid.SetColumn(dashboardStack, 1);

        var textBlocks = new List<RichTextBlock>();

        // Card 1: Latin
        var latinCard = new Border
        {
            CornerRadius = 6f,
            BorderThickness = new Thickness(1f),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            Background = ThemeManager.GetBrush("CardBackground"),
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 0, 16f)
        };
        var latinStack = new StackPanel { Orientation = Orientation.Vertical };
        latinCard.Child = latinStack;
        var latinHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 8f) };
        latinHeader.Inlines.Add(new Bold(new Run("Latin & English Formatted Runs")));
        latinStack.AddChild(latinHeader);
        var latinBody = new RichTextBlock { Font = AppState.GetFont(), FontSize = 16f, Foreground = ThemeManager.GetBrush("TextSecondary") };
        latinBody.Inlines.Add(new Run("Standard Roman characters can be formatted into custom runs: "));
        latinBody.Inlines.Add(new Bold(new Run("Bold weight run, ")));
        latinBody.Inlines.Add(new Italic(new Run("Italicized slant run, ")));
        latinBody.Inlines.Add(new Run("or "));
        latinBody.Inlines.Add(new Underline(new Run("Underlined highlight segments.")));
        latinStack.AddChild(latinBody);
        textBlocks.Add(latinBody);
        dashboardStack.AddChild(latinCard);

        // Card 2: Cyrillic
        var cyrillicCard = new Border
        {
            CornerRadius = 6f,
            BorderThickness = new Thickness(1f),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            Background = ThemeManager.GetBrush("CardBackground"),
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 0, 16f)
        };
        var cyrillicStack = new StackPanel { Orientation = Orientation.Vertical };
        cyrillicCard.Child = cyrillicStack;
        var cyrillicHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 8f) };
        cyrillicHeader.Inlines.Add(new Bold(new Run("Cyrillic Script (Russian Poetry & Pangram)")));
        cyrillicStack.AddChild(cyrillicHeader);
        var cyrillicBody = new RichTextBlock { Font = AppState.GetFont(), FontSize = 16f, Foreground = ThemeManager.GetBrush("TextSecondary") };
        cyrillicBody.Inlines.Add(new Run("Съешь же ещё этих мягких французских булок, да выпей чаю. Широкая электрификация южных губерний даст мощный толчок подъёму сельского хозяйства."));
        cyrillicStack.AddChild(cyrillicBody);
        textBlocks.Add(cyrillicBody);
        dashboardStack.AddChild(cyrillicCard);

        // Card 3: Greek
        var greekCard = new Border
        {
            CornerRadius = 6f,
            BorderThickness = new Thickness(1f),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            Background = ThemeManager.GetBrush("CardBackground"),
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 0, 16f)
        };
        var greekStack = new StackPanel { Orientation = Orientation.Vertical };
        greekCard.Child = greekStack;
        var greekHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 8f) };
        greekHeader.Inlines.Add(new Bold(new Run("Hellenic / Greek Script & Mathematical Formulas")));
        greekStack.AddChild(greekHeader);
        var greekBody = new RichTextBlock { Font = AppState.GetFont(), FontSize = 16f, Foreground = ThemeManager.GetBrush("TextSecondary") };
        greekBody.Inlines.Add(new Run("Φύλλα δάφνης στην κεφαλή των ποιητών. E = mc² | ∫(x²)dx = x³/3 | e^(iπ) + 1 = 0 | Σ(n) for n=1 to ∞."));
        greekStack.AddChild(greekBody);
        textBlocks.Add(greekBody);
        dashboardStack.AddChild(greekCard);

        // Card 4: Japanese CJK
        var cjkCard = new Border
        {
            CornerRadius = 6f,
            BorderThickness = new Thickness(1f),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            Background = ThemeManager.GetBrush("CardBackground"),
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 0, 16f)
        };
        var cjkStack = new StackPanel { Orientation = Orientation.Vertical };
        cjkCard.Child = cjkStack;
        var cjkHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 8f) };
        cjkHeader.Inlines.Add(new Bold(new Run("Japanese CJK Outlines (Hiragana, Katakana & Kanji)")));
        cjkStack.AddChild(cjkHeader);
        var cjkBody = new RichTextBlock { Font = AppState.GetFont(), FontSize = 16f, Foreground = ThemeManager.GetBrush("TextSecondary") };
        cjkBody.Inlines.Add(new Run("色は匂へと散りぬるを我が世誰ぞ常ならむ有為の奥山今日越えて浅き夢見じ酔ひもせず。プロジーピーユーへようこそ！"));
        cjkStack.AddChild(cjkBody);
        textBlocks.Add(cjkBody);
        dashboardStack.AddChild(cjkCard);

        // Card 5: Emoji Vector outlines
        var emojiCard = new Border
        {
            CornerRadius = 6f,
            BorderThickness = new Thickness(1f),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            Background = ThemeManager.GetBrush("CardBackground"),
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 0, 8f)
        };
        var emojiStack = new StackPanel { Orientation = Orientation.Vertical };
        emojiCard.Child = emojiStack;
        var emojiHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 8f) };
        emojiHeader.Inlines.Add(new Bold(new Run("Real Font-Driven Color Emoji / Unicode Outlines")));
        emojiStack.AddChild(emojiHeader);
        var emojiBody = new RichTextBlock { Font = AppState.GetFont(), FontSize = 16f, Foreground = ThemeManager.GetBrush("TextSecondary") };
        emojiBody.Inlines.Add(new Run("Unicode premium symbols: ★, ♠, ♦, ♣, ♥, ✔, ▲, ▼ parsed directly from the system TTF binary and rendered onto the GPU canvas with zero CPU triangulation overhead!"));
        emojiStack.AddChild(emojiBody);
        textBlocks.Add(emojiBody);
        dashboardStack.AddChild(emojiCard);

        // Dynamic Settings Hookups
        Action updateVisuals = () =>
        {
            TtfFont f = fontCombo.SelectedItem?.Text switch
            {
                "Arial (Primary)" => AppState.GetFont()!,
                "Times New Roman" => AppState.GetFontTimes() ?? AppState.GetFont()!,
                "Courier New" => AppState.GetFontCourier() ?? AppState.GetFont()!,
                "Georgia" => AppState.GetFontGeorgia() ?? AppState.GetFont()!,
                "Comic Sans MS" => AppState.GetFontComic() ?? AppState.GetFont()!,
                _ => AppState.GetFont()!
            };

            float sz = sizeSlider.Value;
            sizeLabel.Inlines.Clear();
            sizeLabel.Inlines.Add(new Bold(new Run($"Global Font Size: {sz:F0}")));
            sizeLabel.Invalidate();

            TextAlignment align = alignCombo.SelectedItem?.Text switch
            {
                "Left" => TextAlignment.Left,
                "Center" => TextAlignment.Center,
                "Right" => TextAlignment.Right,
                "Justify" => TextAlignment.Justify,
                _ => TextAlignment.Left
            };

            Compositor.DefaultTextContrast = contrastSlider.Value;
            contrastLabel.Inlines.Clear();
            contrastLabel.Inlines.Add(new Bold(new Run($"Font Contrast/Dilation: {Compositor.DefaultTextContrast:F2}")));
            contrastLabel.Invalidate();

            Compositor.DefaultTextGamma = gammaSlider.Value;
            gammaLabel.Inlines.Clear();
            gammaLabel.Inlines.Add(new Bold(new Run($"Font Gamma: {Compositor.DefaultTextGamma:F2}")));
            gammaLabel.Invalidate();

            foreach (var tb in textBlocks)
            {
                tb.Font = f;
                tb.FontSize = sz;
                tb.TextAlignment = align;
                tb.Invalidate();
            }
        };

        fontCombo.SelectionChanged += (s, e) => updateVisuals();
        sizeSlider.ValueChanged += (s, e) => updateVisuals();
        alignCombo.SelectionChanged += (s, e) => updateVisuals();
        contrastSlider.ValueChanged += (s, e) => updateVisuals();
        gammaSlider.ValueChanged += (s, e) => updateVisuals();

        // Run initial sizing update
        updateVisuals();

        stack.AddChild(grid);
        return scroll;
    }

    public static FrameworkElement CreateInteractiveInputView()
    {
        var grid = new ProGPU.WinUI.Grid();
        grid.ColumnDefinitions.Add(new GridLength(260f, GridUnitType.Absolute)); // Sidebar templates
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Interactive text inputs

        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16, 16, 0, 16) };
        grid.AddChild(leftStack);
        ProGPU.WinUI.Grid.SetColumn(leftStack, 0);

        var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16) };
        grid.AddChild(rightStack);
        ProGPU.WinUI.Grid.SetColumn(rightStack, 1);

        // Title and Header
        var title = new RichTextBlock { Font = AppState.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 8) };
        title.Inlines.Add(new Bold(new Run("⌨️ Multi-Script Interactive Text Input")));
        rightStack.AddChild(title);

        var description = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 20), Foreground = ThemeManager.GetBrush("TextSecondary") };
        description.Inlines.Add(new Run("Type, edit, and navigate through different scripts interactively. The caret details HUD decodes Unicode surrogate-pairs behind the cursor in real-time."));
        rightStack.AddChild(description);

        // Sidebar content: Templates Card
        var templatesCard = new Border
        {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            Background = ThemeManager.GetBrush("ControlBackground"),
            Padding = new Thickness(16f),
            VerticalAlignment = VerticalAlignment.Top
        };
        leftStack.AddChild(templatesCard);

        var templatesStack = new StackPanel { Orientation = Orientation.Vertical };
        templatesCard.Child = templatesStack;

        var sidebarTitle = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 12f) };
        sidebarTitle.Inlines.Add(new Bold(new Run("Load Sample Template:")));
        templatesStack.AddChild(sidebarTitle);

        // Inputs Setup
        var inputBox = new TextBox 
        { 
            Font = AppState.GetFont(), 
            Text = "Type or choose a visual template...", 
            WidthConstraint = 480f, 
            HeightConstraint = 36f, 
            FontSize = 14f,
            Margin = new Thickness(0, 0, 0, 20) 
        };

        var richPlayground = new RichEditBox 
        { 
            Font = AppState.GetFont(), 
            WidthConstraint = 480f, 
            HeightConstraint = 160f,
            FontSize = 14f,
            Margin = new Thickness(0, 0, 0, 20) 
        };

        // Templates Action Buttons
        var btnLatin = new Button { WidthConstraint = 210f, HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 8) };
        var btnLatinText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        btnLatinText.Inlines.Add(new Run("Standard English"));
        btnLatin.Content = btnLatinText;
        templatesStack.AddChild(btnLatin);

        var btnCyrillic = new Button { WidthConstraint = 210f, HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 8) };
        var btnCyrillicText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        btnCyrillicText.Inlines.Add(new Run("Russian (Cyrillic)"));
        btnCyrillic.Content = btnCyrillicText;
        templatesStack.AddChild(btnCyrillic);

        var btnGreek = new Button { WidthConstraint = 210f, HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 8) };
        var btnGreekText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        btnGreekText.Inlines.Add(new Run("Greek (Hellenic)"));
        btnGreek.Content = btnGreekText;
        templatesStack.AddChild(btnGreek);

        var btnJapanese = new Button { WidthConstraint = 210f, HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 8) };
        var btnJapaneseText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        btnJapaneseText.Inlines.Add(new Run("Japanese CJK"));
        btnJapanese.Content = btnJapaneseText;
        templatesStack.AddChild(btnJapanese);

        var btnSymbols = new Button { WidthConstraint = 210f, HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 8) };
        var btnSymbolsText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        btnSymbolsText.Inlines.Add(new Run("Unicode Symbols"));
        btnSymbols.Content = btnSymbolsText;
        templatesStack.AddChild(btnSymbols);

        // Sidebar templates click logic
        btnLatin.Click += (s, e) => { inputBox.Text = "ProGPU features high-performance typographic runs!"; inputBox.CaretIndex = inputBox.Text.Length; };
        btnCyrillic.Click += (s, e) => { inputBox.Text = "Привет от ProGPU! Высокопроизводительный рендеринг текста."; inputBox.CaretIndex = inputBox.Text.Length; };
        btnGreek.Click += (s, e) => { inputBox.Text = "Καλώς ορίσατε στο ProGPU! Υψηλής απόδοσης γραφικά vector."; inputBox.CaretIndex = inputBox.Text.Length; };
        btnJapanese.Click += (s, e) => { inputBox.Text = "プロジーピーユーへようこそ！最高速度のGPU描画エンジン。"; inputBox.CaretIndex = inputBox.Text.Length; };
        btnSymbols.Click += (s, e) => { inputBox.Text = "Dynamic symbols: ★ ♠ ♦ ♣ ♥ ✔ ▲ ▼ outlines parsed on GPU!"; inputBox.CaretIndex = inputBox.Text.Length; };

        // Right Content: TextBox & RichEditBox
        var labelInput = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        labelInput.Inlines.Add(new Bold(new Run("Interactive TextBox (Single-Line Inputs):")));
        rightStack.AddChild(labelInput);
        rightStack.AddChild(inputBox);

        var labelRich = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        labelRich.Inlines.Add(new Bold(new Run("Interactive RichEditBox Playground (Select and format text with Ctrl+B/I/U):")));
        rightStack.AddChild(labelRich);
        rightStack.AddChild(richPlayground);

        // HUD panel for Caret Details
        var hudPanel = new Border
        {
            CornerRadius = 6f,
            BorderThickness = new Thickness(1f),
            BorderBrush = ThemeManager.GetBrush("ControlBorder"),
            Background = ThemeManager.GetBrush("CardBackground"),
            Padding = new Thickness(14f),
            WidthConstraint = 480f
        };
        var hudStack = new StackPanel { Orientation = Orientation.Vertical };
        hudPanel.Child = hudStack;

        var hudTitle = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 8f) };
        hudTitle.Inlines.Add(new Bold(new Run("Caret HUD Status Subsystem")));
        hudStack.AddChild(hudTitle);

        var hudCharCount = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, Foreground = ThemeManager.GetBrush("TextSecondary"), Margin = new Thickness(0, 0, 0, 4f) };
        hudCharCount.Inlines.Add(new Run("Text Length: 0 characters"));
        hudStack.AddChild(hudCharCount);

        var hudCaretPos = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, Foreground = ThemeManager.GetBrush("TextSecondary"), Margin = new Thickness(0, 0, 0, 4f) };
        hudCaretPos.Inlines.Add(new Run("Caret Position: 0"));
        hudStack.AddChild(hudCaretPos);

        var hudCodePoint = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, Foreground = ThemeManager.GetBrush("SystemAccentColor") };
        hudCodePoint.Inlines.Add(new Run("Active CodePoint behind cursor: U+0000 ('N/A')"));
        hudStack.AddChild(hudCodePoint);

        rightStack.AddChild(hudPanel);

        // Update logic
        Action updateHud = () =>
        {
            string txt = inputBox.Text;
            int caret = inputBox.CaretIndex;

            hudCharCount.Inlines.Clear();
            hudCharCount.Inlines.Add(new Run($"Text Length: {txt.Length} characters"));
            hudCharCount.Invalidate();

            hudCaretPos.Inlines.Clear();
            hudCaretPos.Inlines.Add(new Run($"Caret Position: {caret}"));
            hudCaretPos.Invalidate();

            string activeCharText = "Active CodePoint behind cursor: U+0000 ('N/A')";
            if (caret > 0 && caret <= txt.Length)
            {
                int index = caret - 1;
                int codePoint = txt[index];
                if (char.IsLowSurrogate(txt[index]) && index > 0 && char.IsHighSurrogate(txt[index - 1]))
                {
                    codePoint = char.ConvertToUtf32(txt[index - 1], txt[index]);
                }
                string displayChar = char.IsControl((char)codePoint) ? "Control" : char.ConvertFromUtf32(codePoint);
                activeCharText = $"Active CodePoint behind cursor: U+{codePoint:X4} ('{displayChar}')";
            }
            hudCodePoint.Inlines.Clear();
            hudCodePoint.Inlines.Add(new Run(activeCharText));
            hudCodePoint.Invalidate();
            
            hudPanel.Invalidate();
        };

        // Hook TextBox events to trigger real-time HUD updates
        inputBox.TextChanged += (s, e) => updateHud();
        inputBox.CharacterReceived += (s, e) => updateHud();
        inputBox.KeyDown += (s, e) => updateHud();
        inputBox.PointerPressed += (s, e) => updateHud();

        // Load rich text runs in playground
        richPlayground.Inlines.Add(new Run("This formatting playground supports keyboard-driven formats. Use "));
        richPlayground.Inlines.Add(new Bold(new Run("Ctrl+B (Bold)")));
        richPlayground.Inlines.Add(new Run(", "));
        richPlayground.Inlines.Add(new Italic(new Run("Ctrl+I (Italic)")));
        richPlayground.Inlines.Add(new Run(", or "));
        richPlayground.Inlines.Add(new Underline(new Run("Ctrl+U (Underline)")));
        richPlayground.Inlines.Add(new Run(" to toggle active formats dynamically!"));

        // Hook up RichEditBox events
        richPlayground.CharacterReceived += (s, e) => updateHud();
        richPlayground.KeyDown += (s, e) => updateHud();
        richPlayground.PointerPressed += (s, e) => updateHud();

        // Run initial HUD update
        updateHud();

        return grid;
    }
}
