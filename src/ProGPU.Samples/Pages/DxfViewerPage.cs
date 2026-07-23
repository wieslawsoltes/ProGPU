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

    private DxfStaticBuffer? _staticBuffer;
    private bool _needsRecompile = true;
    private string? _lastFilePath;
    private string? _lastActiveLayout;
    private int _lastActiveLayersHash;
    private GpuPicture? _cachedPicture;
    private float _lastZoom;
    private Vector2 _lastPan;
    private bool _lastEnableGpuTransforms;
    private bool _lastEnableStaticGpuBuffers;
    private bool _lastEnableFlattening;
    private Vector2 _lastSize;

    public int DocumentRenderCount { get; private set; }
    public int StaticBufferCompileCount { get; private set; }
    public int StaticTextRecompileCount { get; private set; }

    private int GetActiveLayersHash()
    {
        int hash = 17;
        foreach (var l in Context.ActiveLayers)
        {
            hash = hash * 31 + l.GetHashCode();
        }
        return hash;
    }

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
        Unloaded += (_, _) => ReleaseRetainedContent();
    }

    public void LoadDocument(DxfDocument doc, string? filePath = null)
    {
        ReleaseRetainedContent();
        Context.FilePath = filePath;
        Document = doc;
        _firstLayout = true;
        _cachedPicture = null;
        _needsRecompile = true;
        DocumentRenderCount = 0;
        StaticBufferCompileCount = 0;
        StaticTextRecompileCount = 0;

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

        if (Size.X > 0f && Size.Y > 0f)
        {
            _firstLayout = false;
            UpdateZoomToFit();
        }

        Invalidate();
    }

    public void ZoomToFit()
    {
        if (!UpdateZoomToFit()) return;
        Invalidate();
    }

    private bool UpdateZoomToFit()
    {
        if (Document == null || Size.X <= 0 || Size.Y <= 0) return false;

        // Calculate drawing min/max bounds based on active visible layers only
        var (min, max) = DxfDocumentRenderer.CalculateBounds(Document, Context, Context.ActiveLayers);

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
        return true;
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

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        Context.ScreenCenter = Size * 0.5f;
        if (_firstLayout && Document is not null && Size.X > 0f && Size.Y > 0f)
        {
            _firstLayout = false;
            UpdateZoomToFit();
        }
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

        // Sync context's screen viewport parameters
        Context.ScreenCenter = Size * 0.5f;
        Context.EnableGpuTransforms = AppState.EnableGpuTransforms;

        if (AppState.EnableStaticGpuBuffers)
        {
            // --- OPTION B: Hardware-Accelerated Static WebGPU Buffers ---
            // Compile geometry once in screen space at Zoom=1 and retain it in GPU buffers.
            // Camera changes update uniforms; text coverage stays at or above device resolution
            // and is rebuilt only when its bounded supersampling window is crossed.
            int layersHash = GetActiveLayersHash();
            bool invalidateCache = _staticBuffer == null
                || Context.FilePath != _lastFilePath 
                || Document.ActiveLayout != _lastActiveLayout 
                || layersHash != _lastActiveLayersHash
                || AppState.EnableGpuTransforms != _lastEnableGpuTransforms
                || AppState.EnableStaticGpuBuffers != _lastEnableStaticGpuBuffers
                || Context.EnableFlattening != _lastEnableFlattening
                || Size != _lastSize;

            if (invalidateCache || _needsRecompile || _staticBuffer == null)
            {
                _needsRecompile = false;
                _lastFilePath = Context.FilePath;
                _lastActiveLayout = Document.ActiveLayout;
                _lastActiveLayersHash = layersHash;
                _lastZoom = Context.Zoom;
                _lastPan = Context.Pan;
                _lastEnableGpuTransforms = AppState.EnableGpuTransforms;
                _lastEnableStaticGpuBuffers = AppState.EnableStaticGpuBuffers;
                _lastEnableFlattening = Context.EnableFlattening;
                _lastSize = Size;

                _cachedPicture?.Dispose();
                _cachedPicture = null;
                _staticBuffer?.Dispose();
                _staticBuffer = null;

                if (AppState._screenCompositor != null)
                {
                    float savedZoom = Context.Zoom;
                    Vector2 savedPan = Context.Pan;
                    Vector2 savedCenter = Context.Center;
                    Vector2 savedScreenCenter = Context.ScreenCenter;
                    bool savedEnableGpuTransforms = Context.EnableGpuTransforms;
                    bool savedEnableLod = Context.EnableLod;
                    bool savedIsCompilingStatic = Context.IsCompilingStatic;

                    try
                    {
                        // Static compilation is itself the retained command boundary.
                        // Record the document once at a neutral camera without creating
                        // a second intermediate picture or dropping any quality LOD.
                        Context.Zoom = 1.0f;
                        Context.Pan = Vector2.Zero;
                        Context.Center = savedCenter;
                        Context.ScreenCenter = savedScreenCenter;
                        Context.EnableGpuTransforms = false;
                        Context.EnableLod = false;
                        Context.IsCompilingStatic = true;

                        Context.DrawingContext.Clear();
                        DxfDocumentRenderer.Render(Document, Context);
                        DocumentRenderCount++;

                        _staticBuffer = AppState._screenCompositor.CompileStaticDxf(
                            Context.DrawingContext,
                            staticZoom: 1.0f);
                        StaticBufferCompileCount++;
                    }
                    finally
                    {
                        Context.Zoom = savedZoom;
                        Context.Pan = savedPan;
                        Context.Center = savedCenter;
                        Context.ScreenCenter = savedScreenCenter;
                        Context.EnableGpuTransforms = savedEnableGpuTransforms;
                        Context.EnableLod = savedEnableLod;
                        Context.IsCompilingStatic = savedIsCompilingStatic;
                    }
                }
            }

            if (_staticBuffer != null && Size.X > 0 && Size.Y > 0)
            {
                _staticBuffer.UpdateViewport(
                    Matrix4x4.Identity,
                    Context.Zoom,
                    Context.Pan,
                    Context.Center,
                    Context.ScreenCenter);

                context.PushClip(new Rect(0f, 0f, Size.X, Size.Y));
                context.DrawStaticDxf(_staticBuffer);
                context.PopClip();
            }
        }
        else
        {
            // --- TRADITIONAL PATH / COMMAND CACHING (Option C) ---
            // Renders natively at perfect retina screen-space resolution on every camera change.
            // Bypasses static buffers to maintain 100% crisp text/paths and stable 1-pixel line thicknesses at all times.
            int layersHash = GetActiveLayersHash();
            bool invalidateCache = _cachedPicture == null
                || Context.FilePath != _lastFilePath 
                || Document.ActiveLayout != _lastActiveLayout 
                || layersHash != _lastActiveLayersHash
                || AppState.EnableGpuTransforms != _lastEnableGpuTransforms
                || AppState.EnableStaticGpuBuffers != _lastEnableStaticGpuBuffers
                || Context.EnableFlattening != _lastEnableFlattening
                || Size != _lastSize;

            if (!AppState.EnableGpuTransforms)
            {
                if (Context.Zoom != _lastZoom || Context.Pan != _lastPan)
                {
                    invalidateCache = true;
                }
            }

            if (!AppState.EnableCommandCaching)
            {
                invalidateCache = true;
            }

            if (invalidateCache || _cachedPicture == null)
            {
                _lastFilePath = Context.FilePath;
                _lastActiveLayout = Document.ActiveLayout;
                _lastActiveLayersHash = layersHash;
                _lastZoom = Context.Zoom;
                _lastPan = Context.Pan;
                _lastEnableGpuTransforms = AppState.EnableGpuTransforms;
                _lastEnableStaticGpuBuffers = AppState.EnableStaticGpuBuffers;
                _lastEnableFlattening = Context.EnableFlattening;
                _lastSize = Size;

                _cachedPicture?.Dispose();
                _cachedPicture = null;

                if (AppState.EnableCommandCaching)
                {
                    var recorder = new GpuPictureRecorder();
                    var recCtx = recorder.BeginRecording(new Rect(0, 0, Size.X, Size.Y));
                    var oldCtx = Context.DrawingContext;
                    Context.DrawingContext = recCtx;
                    DxfDocumentRenderer.Render(Document, Context);
                    DocumentRenderCount++;
                    Context.DrawingContext = oldCtx;
                    _cachedPicture = recorder.EndRecording();
                }
                else
                {
                    Context.DrawingContext.Clear();
                    DxfDocumentRenderer.Render(Document, Context);
                    DocumentRenderCount++;
                }
            }

            context.PushClip(new Rect(0f, 0f, Size.X, Size.Y));

            var viewMatrix = Matrix4x4.Identity;
            if (AppState.EnableGpuTransforms)
            {
                viewMatrix = new Matrix4x4(
                    Context.Zoom, 0f, 0f, 0f,
                    0f, -Context.Zoom, 0f, 0f,
                    0f, 0f, 1f, 0f,
                    -Context.Center.X * Context.Zoom + Context.ScreenCenter.X + Context.Pan.X,
                    Context.Center.Y * Context.Zoom + Context.ScreenCenter.Y + Context.Pan.Y,
                    0f, 1f
                );
            }

            if (_cachedPicture != null)
            {
                context.DrawPicture(_cachedPicture, viewMatrix);
            }
            else
            {
                if (AppState.EnableGpuTransforms)
                {
                    int pointOffset = context.PointBuffer.Count;
                    int doubleOffset = context.DoubleBuffer.Count;
                    int line3dOffset = context.Line3DBuffer.Count;
                    int floatOffset = context.FloatBuffer.Count;

                    context.PointBuffer.AddRange(Context.DrawingContext.PointBuffer);
                    context.DoubleBuffer.AddRange(Context.DrawingContext.DoubleBuffer);
                    context.Line3DBuffer.AddRange(Context.DrawingContext.Line3DBuffer);
                    context.FloatBuffer.AddRange(Context.DrawingContext.FloatBuffer);

                    foreach (var cmd in Context.DrawingContext.Commands)
                    {
                        var modifiedCmd = cmd;
                        if (modifiedCmd.PointBufferCount > 0)
                            modifiedCmd.PointBufferOffset += pointOffset;
                        if (modifiedCmd.DoubleBufferCount > 0)
                            modifiedCmd.DoubleBufferOffset += doubleOffset;
                        if (modifiedCmd.Line3DBufferCount > 0)
                            modifiedCmd.Line3DBufferOffset += line3dOffset;
                        if (modifiedCmd.FloatBufferCount > 0)
                            modifiedCmd.FloatBufferOffset += floatOffset;
                        if (modifiedCmd.WeightBufferCount > 0)
                            modifiedCmd.WeightBufferOffset += doubleOffset;

                        modifiedCmd.UseGpuTransforms = true;
                        modifiedCmd.CameraView = viewMatrix;
                        context.Commands.Add(modifiedCmd);
                    }
                }
                else
                {
                    context.Append(Context.DrawingContext);
                }
            }
            context.PopClip();
        }
    }

    private void ReleaseRetainedContent()
    {
        _staticBuffer?.Dispose();
        _staticBuffer = null;
        _cachedPicture?.Dispose();
        _cachedPicture = null;
        _needsRecompile = true;
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

        if (e.IsPreciseScrolling && !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            // UIKit scroll events report the content-following two-axis translation in
            // logical points. Preserve it exactly for trackpad panning.
            Context.Pan += new Vector2(e.WheelDeltaX, e.WheelDelta);
            Invalidate();
        }
        else if (e.IsPreciseScrolling)
        {
            // The iOS host maps the relative UIPinch scale to 120 * ln(scale).
            // This restores the exact multiplicative scale without quantized steps.
            ZoomToPoint(e.Position, MathF.Exp(e.WheelDelta / 120f));
        }
        else
        {
            // Preserve conventional stepped mouse-wheel zoom on desktop.
            float factor = e.WheelDelta > 0 ? 1.15f : 0.85f;
            ZoomToPoint(e.Position, factor);
        }
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
        var mainGrid = new Grid();

        var grid = new ResponsiveSplitView { OpenPaneLength = 280f };

        mainGrid.AddChild(grid);

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
        openBtnText.Inlines.Add(new Bold(new Run("Open DXF File...")));
        openBtn.Content = openBtnText;
        sidebarStack.AddChild(openBtn);

        var sampleBtn = new Button { HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 8f), HorizontalAlignment = HorizontalAlignment.Stretch };
        var sampleBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sampleBtnText.Inlines.Add(new Bold(new Run("Generate Sample Drawing")));
        sampleBtn.Content = sampleBtnText;
        sidebarStack.AddChild(sampleBtn);

        var fitBtn = new Button { HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 8f), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fitBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        fitBtnText.Inlines.Add(new Bold(new Run("Zoom to Fit Bounds")));
        fitBtn.Content = fitBtnText;
        sidebarStack.AddChild(fitBtn);

        var benchBtn = new Button { HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 16f), HorizontalAlignment = HorizontalAlignment.Stretch };
        var benchBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        benchBtnText.Inlines.Add(new Bold(new Run("Run Performance Benchmark")));
        benchBtn.Content = benchBtnText;
        sidebarStack.AddChild(benchBtn);

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

        // Flattening CheckBox
        var flatStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16f) };
        var flatText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f };
        flatText.Inlines.Add(new Run("Enable Entity Flattening"));
        var flatChk = new CheckBox
        {
            Content = flatText,
            IsChecked = true, // enabled by default!
            VerticalAlignment = VerticalAlignment.Center
        };
        flatChk.CheckedChanged += (s, e) =>
        {
            if (_canvas != null)
            {
                _canvas.Context.EnableFlattening = flatChk.IsChecked;
                _canvas.Invalidate();
                UpdateStatus($"Entity Flattening: {(flatChk.IsChecked ? "Enabled" : "Disabled")}");
            }
        };
        flatStack.AddChild(flatChk);
        sidebarStack.AddChild(flatStack);

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
            if (_layoutCombo.SelectedItem is ComboBoxItem selectedItem &&
                _canvas != null &&
                _canvas.Document != null)
            {
                string selectedLayout = selectedItem.Text;
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

        grid.PaneContent = sidebarCard;

        // 2. MAIN CAD CANVAS PANEL
        _canvas = new DxfCanvasControl();
        grid.MainContent = _canvas;

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
                        _canvas.LoadDocument(doc, file.Path);
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

        // Create overlay popup dialog controls
        var overlay = new Border
        {
            CornerRadius = 12f,
            BorderThickness = new Thickness(1.5f),
            BorderBrush = new ThemeResourceBrush("AccentTextFillColorPrimaryBrush"),
            Background = new ThemeResourceBrush("ControlBackground"),
            Padding = new Thickness(24f),
            WidthConstraint = 440f,
            HeightConstraint = 330f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var overlayStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        overlay.Child = overlayStack;

        var overlayHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 16f, Margin = new Thickness(0, 0, 0, 16f) };
        overlayHeader.Inlines.Add(new Bold(new Run("Performance Benchmark Results")));
        overlayStack.AddChild(overlayHeader);

        var overlayText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 20f), Foreground = new ThemeResourceBrush("TextPrimary") };
        overlayStack.AddChild(overlayText);

        var closeBtn = new Button { HeightConstraint = 32f, CornerRadius = 4f, WidthConstraint = 100f, HorizontalAlignment = HorizontalAlignment.Right };
        var closeBtnText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        closeBtnText.Inlines.Add(new Bold(new Run("Close")));
        closeBtn.Content = closeBtnText;
        overlayStack.AddChild(closeBtn);

        closeBtn.Click += (s, e) =>
        {
            mainGrid.RemoveChild(overlay);
        };

        // Hook up benchmark button click event
        benchBtn.Click += async (s, e) =>
        {
            if (_canvas == null || _canvas.Document == null) return;
            
            UpdateStatus("Running Benchmark...");
            benchBtn.IsEnabled = false;

            var frameTimes = new List<double>();
            var compileTimes = new List<double>();
            var uploadTimes = new List<double>();
            var renderTimes = new List<double>();
            var drawCalls = new List<int>();
            int initialGcCount = GC.CollectionCount(0);

            // Record initial camera state
            float initialZoom = _canvas.Context.Zoom;
            Vector2 initialPan = _canvas.Context.Pan;

            // Run 60 frames of animation
            for (int frame = 0; frame < 60; frame++)
            {
                float angle = (float)(frame * Math.PI * 2.0 / 60.0);
                _canvas.Context.Pan = initialPan + new Vector2(MathF.Cos(angle) * 80f, MathF.Sin(angle) * 80f);
                _canvas.Context.Zoom = initialZoom * (1.0f + MathF.Sin(angle) * 0.12f);

                _canvas.Invalidate();
                
                await System.Threading.Tasks.Task.Delay(16);

                var metrics = AppState._screenCompositor != null ? AppState._screenCompositor.Metrics : new CompositorMetrics();
                frameTimes.Add(AppState._cpuFrameTimeMs);
                compileTimes.Add(metrics.VisualTreeCompileTimeMs);
                uploadTimes.Add(metrics.GpuUploadTimeMs);
                renderTimes.Add(metrics.RenderPassTimeMs);
                drawCalls.Add(metrics.DrawCallsCount);
            }

            // Restore camera state
            _canvas.Context.Zoom = initialZoom;
            _canvas.Context.Pan = initialPan;
            _canvas.Invalidate();

            int finalGcCount = GC.CollectionCount(0);
            int gcCollections = finalGcCount - initialGcCount;

            double avgFrame = 0, peakFrame = 0;
            double avgCompile = 0, avgUpload = 0, avgRender = 0;
            double avgDrawCalls = 0;

            if (frameTimes.Count > 0)
            {
                foreach (var t in frameTimes)
                {
                    avgFrame += t;
                    if (t > peakFrame) peakFrame = t;
                }
                avgFrame /= frameTimes.Count;
                
                foreach (var t in compileTimes) avgCompile += t;
                avgCompile /= compileTimes.Count;

                foreach (var t in uploadTimes) avgUpload += t;
                avgUpload /= uploadTimes.Count;

                foreach (var t in renderTimes) avgRender += t;
                avgRender /= renderTimes.Count;

                foreach (var dc in drawCalls) avgDrawCalls += dc;
                avgDrawCalls /= drawCalls.Count;
            }

            benchBtn.IsEnabled = true;
            UpdateStatus("Benchmark Complete!");

            // Update overlay text and show it by adding to the main Grid layout
            overlayText.Inlines.Clear();
            overlayText.Inlines.Add(new Run("Completed 60 frames of panning/zooming simulations.\n\n"));
            
            overlayText.Inlines.Add(new Run("• Average CPU Frame: "));
            overlayText.Inlines.Add(new Bold(new Run($"{avgFrame:F2} ms\n")) { Foreground = new SolidColorBrush(0x0078D4FF) });
            
            overlayText.Inlines.Add(new Run("• Peak CPU Frame: "));
            overlayText.Inlines.Add(new Bold(new Run($"{peakFrame:F2} ms\n")) { Foreground = new SolidColorBrush(0x0078D4FF) });

            double avgLayout = avgFrame - (avgCompile + avgUpload + avgRender);
            if (avgLayout < 0) avgLayout = 0;
            overlayText.Inlines.Add(new Run($"  - Layout Pass: {avgLayout:F2} ms\n"));
            overlayText.Inlines.Add(new Run($"  - Tree Compile: {avgCompile:F2} ms\n"));
            overlayText.Inlines.Add(new Run($"  - GPU Buffer Upload: {avgUpload:F2} ms\n"));
            overlayText.Inlines.Add(new Run($"  - WebGPU Submission: {avgRender:F2} ms\n\n"));

            overlayText.Inlines.Add(new Run("• Average Draw Calls: "));
            overlayText.Inlines.Add(new Bold(new Run($"{avgDrawCalls:F1}\n")) { Foreground = new SolidColorBrush(0x0078D4FF) });

            overlayText.Inlines.Add(new Run("• GC Collections (Gen 0): "));
            overlayText.Inlines.Add(new Bold(new Run($"{gcCollections}")) { Foreground = new SolidColorBrush(0x0078D4FF) });
            
            mainGrid.AddChild(overlay);
            overlayText.Invalidate();
        };

        // Load the sample DXF by default so the user is wowed immediately!
        var initialDoc = SampleDxfGenerator.GenerateSample();
        _canvas.LoadDocument(initialDoc);
        PopulateLayers(initialDoc);
        PopulateLayouts(initialDoc);

        return mainGrid;
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
            _layoutCombo.SelectedItem = (ComboBoxItem)_layoutCombo.Items[0];
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
