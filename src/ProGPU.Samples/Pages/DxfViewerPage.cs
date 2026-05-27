using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using netDxf;
using ProGPU.Backend;
using ProGPU.Dxf;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Thickness = Microsoft.UI.Xaml.Thickness;

namespace ProGPU.Samples;

public class DxfCanvasControl : FrameworkElement
{
    public DxfDocument? Document { get; private set; }
    public DxfRenderContext Context { get; }

    private Vector2 _startPan;
    private Vector2 _startPointerPos;
    private bool _isPanning;
    private bool _firstLayout = true;

    public DxfCanvasControl()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // Initialize DxfRenderContext with ProGPU default font
        Context = new DxfRenderContext(new DrawingContext(), AppState.GetFont()!);
        Context.BackgroundBrush = ThemeManager.GetBrush("CardBackground");

        // Mouse pan and zoom events registration
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
    }

    public void LoadDocument(DxfDocument doc)
    {
        Document = doc;
        _firstLayout = true;

        // Initialize visible layer mappings and default colors
        Context.ActiveLayers.Clear();
        Context.LayerColors.Clear();
        
        foreach (var layer in doc.Layers)
        {
            Context.ActiveLayers.Add(layer.Name);
            // Translate AciColor to Vector4 brush color
            var aci = layer.Color;
            Context.LayerColors[layer.Name] = new Vector4(aci.R / 255f, aci.G / 255f, aci.B / 255f, 1f);
        }

        Invalidate();
    }

    public void ZoomToFit()
    {
        if (Document == null || Size.X <= 0 || Size.Y <= 0) return;

        // Calculate drawing min/max bounds based on active visible layers only
        var (min, max) = DxfDocumentRenderer.CalculateBounds(Document, Context.ActiveLayers);

        float dxfWidth = max.X - min.X;
        float dxfHeight = max.Y - min.Y;

        if (dxfWidth <= 0.001f) dxfWidth = 10f;
        if (dxfHeight <= 0.001f) dxfHeight = 10f;

        // Reposition model center and screen viewport projection center
        Context.Center = (min + max) * 0.5f;
        Context.ScreenCenter = Size * 0.5f;

        // Fit within 88% of canvas size with a safe margin
        float padding = 0.88f;
        float scaleX = (Size.X * padding) / dxfWidth;
        float scaleY = (Size.Y * padding) / dxfHeight;
        
        Context.Zoom = Math.Min(scaleX, scaleY);
        if (Context.Zoom <= 0.0001f) Context.Zoom = 1.0f;

        Context.Pan = Vector2.Zero;
        Invalidate();
    }

    public void ZoomToPoint(Vector2 mousePos, float scaleFactor)
    {
        // 1. Convert cursor screen coordinate to world CAD coordinate (accounting for Y-inversion)
        float localX = (mousePos.X - Context.ScreenCenter.X - Context.Pan.X) / Context.Zoom;
        float localY = -(mousePos.Y - Context.ScreenCenter.Y - Context.Pan.Y) / Context.Zoom;

        // 2. Apply clamp-bounded zoom scaling
        Context.Zoom = Math.Clamp(Context.Zoom * scaleFactor, 0.005f, 1000f);

        // 3. Shift Pan to keep the same world point static under the cursor
        Context.Pan = new Vector2(
            mousePos.X - Context.ScreenCenter.X - localX * Context.Zoom,
            mousePos.Y - Context.ScreenCenter.Y + localY * Context.Zoom
        );

        Invalidate();
    }

    public override void OnRender(DrawingContext context)
    {
        // Draw CAD charcoal background card using resolved brushes
        context.DrawRectangle(ThemeManager.GetBrush("CardBackground"), ThemeManager.GetPen("ControlBorder", 1f), new Rect(0, 0, Size.X, Size.Y));

        if (Document == null)
        {
            var warningBrush = ThemeManager.GetBrush("TextSecondary");
            context.DrawText("No DXF document loaded. Load a file or generate a sample.", AppState.GetFont()!, 13f, warningBrush, new Vector2(24, 24));
            return;
        }

        // Auto-center and fit on the first size-negotiation layout pass
        if (_firstLayout && Size.X > 0 && Size.Y > 0)
        {
            _firstLayout = false;
            ZoomToFit();
        }

        // Sync context's screen viewport parameters
        Context.ScreenCenter = Size * 0.5f;

        // Perform GPU-first vector rendering
        Context.DrawingContext.Clear();
        DxfDocumentRenderer.Render(Document, Context);

        // Push Clip region to the Canvas boundaries to prevent bleeding
        context.PushClip(new Rect(0f, 0f, Size.X, Size.Y));

        // Batch commands to screen compositor
        foreach (var cmd in Context.DrawingContext.Commands)
        {
            context.Commands.Add(cmd);
        }

        context.PopClip();
    }

    private void OnPointerPressed(object? sender, PointerRoutedEventArgs e)
    {
        if (Document == null) return;

        if (e.IsLeftButtonPressed)
        {
            _isPanning = true;
            _startPointerPos = e.Position;
            _startPan = Context.Pan;
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;

        var offset = e.Position - _startPointerPos;
        Context.Pan = _startPan + offset;
        Invalidate();
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerRoutedEventArgs e)
    {
        _isPanning = false;
    }

    private void OnPointerWheelChanged(object? sender, PointerRoutedEventArgs e)
    {
        if (Document == null) return;

        // Standard scroll zoom mapping (Scroll up = Zoom In, Scroll down = Zoom Out)
        float factor = e.WheelDelta > 0 ? 1.15f : 0.85f;
        ZoomToPoint(e.Position, factor);
        e.Handled = true;
    }
}

public static class DxfViewerPage
{
    private static DxfCanvasControl? _canvas;
    private static StackPanel? _layersPanel;
    private static RichTextBlock? _statusText;
    private static ComboBox? _layoutCombo;

    public static FrameworkElement Create()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new GridLength(280f, GridUnitType.Absolute)); // Sidebar
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Main canvas card

        // 1. LEFT SIDEBAR PANEL
        var sidebarCard = new Border
        {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Background = new ThemeResourceBrush("ControlBackground"),
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        
        var sidebarStack = new StackPanel { Orientation = Orientation.Vertical };
        sidebarCard.Child = sidebarStack;

        var title = new RichTextBlock { Font = AppState.GetFont(), FontSize = 16f, Margin = new Thickness(0, 0, 0, 8f) };
        title.Inlines.Add(new Bold(new Run("DXF CAD Viewer")));
        sidebarStack.AddChild(title);

        var description = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, Margin = new Thickness(0, 0, 0, 16f), Foreground = new ThemeResourceBrush("TextSecondary") };
        description.Inlines.Add(new Run("Drag "));
        description.Inlines.Add(new Bold(new Run("Left Mouse Button")));
        description.Inlines.Add(new Run(" to pan. Scroll "));
        description.Inlines.Add(new Bold(new Run("Mouse Wheel")));
        description.Inlines.Add(new Run(" to zoom-into cursor. Line sizes are fully preserved."));
        sidebarStack.AddChild(description);

        // Sidebar Actions Grid
        var openBtn = new Button { HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 8f), HorizontalAlignment = HorizontalAlignment.Stretch };
        var openBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        openBtnText.Inlines.Add(new Bold(new Run("📁 Open DXF File...")));
        openBtn.Content = openBtnText;
        sidebarStack.AddChild(openBtn);

        var sampleBtn = new Button { HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 8f), HorizontalAlignment = HorizontalAlignment.Stretch };
        var sampleBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sampleBtnText.Inlines.Add(new Bold(new Run("⚡ Generate Sample Drawing")));
        sampleBtn.Content = sampleBtnText;
        sidebarStack.AddChild(sampleBtn);

        var fitBtn = new Button { HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 16f), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fitBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        fitBtnText.Inlines.Add(new Bold(new Run("📐 Zoom to Fit Bounds")));
        fitBtn.Content = fitBtnText;
        sidebarStack.AddChild(fitBtn);

        // LOD Optimization CheckBox
        var lodStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16f) };
        var lodText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f };
        lodText.Inlines.Add(new Run("Enable LOD Optimization"));
        var lodChk = new CheckBox
        {
            Content = lodText,
            IsChecked = false, // disabled by default!
            VerticalAlignment = VerticalAlignment.Center
        };
        lodChk.CheckedChanged += (s, e) =>
        {
            if (_canvas != null)
            {
                _canvas.Context.EnableLod = lodChk.IsChecked;
                _canvas.Invalidate();
                UpdateStatus($"LOD Optimization: {(lodChk.IsChecked ? "Enabled" : "Disabled")}");
            }
        };
        lodStack.AddChild(lodChk);
        sidebarStack.AddChild(lodStack);

        // Layout Space Selection section header
        var layoutsHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 13f, Margin = new Thickness(0, 8, 0, 8) };
        layoutsHeader.Inlines.Add(new Bold(new Run("Layout Space Selection:")));
        sidebarStack.AddChild(layoutsHeader);

        _layoutCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            WidthConstraint = 248f,
            Margin = new Thickness(0, 0, 0, 16f),
            PlaceholderText = "Select Layout..."
        };
        _layoutCombo.SelectionChanged += (s, e) =>
        {
            if (_layoutCombo.SelectedItem != null && _canvas != null && _canvas.Document != null)
            {
                string selectedLayout = _layoutCombo.SelectedItem.Text;
                if (_canvas.Document.Layouts.Contains(selectedLayout))
                {
                    _canvas.Document.ActiveLayout = selectedLayout;
                    _canvas.ZoomToFit();
                    _canvas.Invalidate();
                    UpdateStatus($"Layout changed to: {selectedLayout}");
                }
            }
        };
        sidebarStack.AddChild(_layoutCombo);

        // Layers section header
        var layersHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 13f, Margin = new Thickness(0, 8, 0, 8) };
        layersHeader.Inlines.Add(new Bold(new Run("Layers Control:")));
        sidebarStack.AddChild(layersHeader);

        var layersBorder = new Border
        {
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            CornerRadius = 6f,
            Background = new ThemeResourceBrush("CardBackground"),
            Padding = new Thickness(10f),
            HeightConstraint = 240f
        };
        var layersScroll = new ScrollViewer { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _layersPanel = new StackPanel { Orientation = Orientation.Vertical };
        layersScroll.Content = _layersPanel;
        layersBorder.Child = layersScroll;
        sidebarStack.AddChild(layersBorder);

        // Status bar log
        _statusText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, Margin = new Thickness(0, 16, 0, 0), Foreground = new ThemeResourceBrush("TextSecondary") };
        _statusText.Inlines.Add(new Run("Status: Ready"));
        sidebarStack.AddChild(_statusText);

        grid.AddChild(sidebarCard);
        Grid.SetColumn(sidebarCard, 0);

        // 2. MAIN CAD CANVAS PANEL
        _canvas = new DxfCanvasControl();
        grid.AddChild(_canvas);
        Grid.SetColumn(_canvas, 1);

        // Hookup actions
        sampleBtn.Click += (s, e) =>
        {
            UpdateStatus("Generating sample DXF...");
            var sampleDoc = SampleDxfGenerator.GenerateSample();
            _canvas.LoadDocument(sampleDoc);
            PopulateLayers(sampleDoc);
            PopulateLayouts(sampleDoc);
            UpdateStatus("Sample DXF loaded successfully!");
        };

        fitBtn.Click += (s, e) =>
        {
            _canvas.ZoomToFit();
            UpdateStatus("Zoomed to fit bounds.");
        };

        openBtn.Click += async (s, e) =>
        {
            UpdateStatus("Opening file picker...");
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".dxf");
            var file = await picker.PickSingleFileAsync();

            if (file != null)
            {
                UpdateStatus($"Loading file: {Path.GetFileName(file.Path)}...");
                try
                {
                    // Asynchronously read file bytes, then parse in memory
                    byte[] bytes = await System.IO.File.ReadAllBytesAsync(file.Path);
                    using var stream = new MemoryStream(bytes);
                    
                    var doc = DxfDocument.Load(stream);
                    if (doc != null)
                    {
                        _canvas.LoadDocument(doc);
                        PopulateLayers(doc);
                        PopulateLayouts(doc);
                        UpdateStatus($"Loaded: {Path.GetFileName(file.Path)}");
                    }
                    else
                    {
                        UpdateStatus("Error: DXF document parsing returned null.");
                    }
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error: {ex.Message}");
                }
            }
            else
            {
                UpdateStatus("File selection cancelled.");
            }
        };

        // Load the sample DXF by default so the user is wowed immediately!
        var initialDoc = SampleDxfGenerator.GenerateSample();
        _canvas.LoadDocument(initialDoc);
        PopulateLayers(initialDoc);
        PopulateLayouts(initialDoc);

        return grid;
    }

    private static void PopulateLayouts(DxfDocument doc)
    {
        if (_layoutCombo == null) return;

        _layoutCombo.Items.Clear();

        ComboBoxItem? activeItem = null;
        foreach (var layout in doc.Layouts)
        {
            var item = new ComboBoxItem(layout.Name);
            _layoutCombo.Items.Add(item);

            if (layout.Name == doc.ActiveLayout)
            {
                activeItem = item;
            }
        }

        if (activeItem != null)
        {
            _layoutCombo.SelectedItem = activeItem;
        }
        else if (_layoutCombo.Items.Count > 0)
        {
            _layoutCombo.SelectedItem = _layoutCombo.Items[0];
        }
    }

    private static void PopulateLayers(DxfDocument doc)
    {
        if (_layersPanel == null || _canvas == null) return;

        _layersPanel.ClearChildren();

        foreach (var layer in doc.Layers)
        {
            var checkBoxStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6f) };
            
            // Layer color indicator square
            var colorSquare = new Border
            {
                Width = 10f,
                Height = 10f,
                CornerRadius = 2f,
                Background = new SolidColorBrush(new Vector4(layer.Color.R / 255f, layer.Color.G / 255f, layer.Color.B / 255f, 1f)),
                Margin = new Thickness(0, 5, 8, 0),
                VerticalAlignment = VerticalAlignment.Top
            };

            var chkText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f };
            chkText.Inlines.Add(new Run(layer.Name));

            var chk = new CheckBox
            {
                Content = chkText,
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            string layerName = layer.Name;
            chk.CheckedChanged += (s, e) =>
            {
                if (chk.IsChecked)
                {
                    _canvas.Context.ActiveLayers.Add(layerName);
                }
                else
                {
                    _canvas.Context.ActiveLayers.Remove(layerName);
                }
                _canvas.Invalidate();
            };

            checkBoxStack.AddChild(colorSquare);
            checkBoxStack.AddChild(chk);
            _layersPanel.AddChild(checkBoxStack);
        }
    }

    private static void UpdateStatus(string message)
    {
        if (_statusText == null) return;
        _statusText.Inlines.Clear();
        _statusText.Inlines.Add(new Run($"Status: {message}"));
        _statusText.Invalidate();
    }
}
