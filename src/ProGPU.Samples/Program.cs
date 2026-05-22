using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
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

public static unsafe class Program
{
    private static IWindow? _window;
    private static WgpuContext? _wgpuContext;
    private static Compositor? _screenCompositor;
    private static Compositor? _offscreenCompositor;
    private static ComputeAccelerator? _compute;

    private static TtfFont? _font;
    private static ProGPU.WinUI.Grid? _rootGrid;
    private static Border? _showcaseContainer;

    // Active diagnostic metric stats
    private static RichTextBlock? _statsText;
    private static Vector2 _mousePos;
    private static string _activeFocusedName = "None";

    // Category pages and sidebar selections
    private static string _activeCategory = "Basic Input";
    private static Button? _basicInputTabBtn;
    private static Button? _panelsTabBtn;
    private static Button? _textTabBtn;
    private static Button? _dataTabBtn;
    private static Button? _computeTabBtn;

    // Compute FX variables
    private static float _blurRadius = 8f;
    private static float _shadowRadius = 8f;
    private static Vector2 _shadowOffset = new Vector2(4f, 4f);
    private static bool _animateGear = true;
    private static float _gearRotation = 0f;

    // Diagnostic timing
    private static readonly Stopwatch _frameStopwatch = new();
    private static double _fpsAccumulator = 0;
    private static int _frameCount = 0;
    private static double _currentFps = 60;
    private static double _cpuFrameTimeMs = 0;

    // Compute effect textures
    private static GpuTexture? _canvasSourceTexture;
    private static GpuTexture? _canvasTempTexture;
    private static GpuTexture? _canvasBlurTexture;
    private static GpuTexture? _canvasShadowTexture;

    // Basic Input Page Interactive State
    private static int _clickCount = 0;
    private static string _checkboxStatus = "Unchecked";
    private static float _sliderValue = 50f;

    // Data Virtualization Page Data Set
    public class LogItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public double Latency { get; set; }
    }
    private static readonly List<object> _logItems = new();

    public static float GetBlurRadius() => _blurRadius;
    public static float GetShadowRadius() => _shadowRadius;
    public static TtfFont? GetFont() => _font;

    public static void Main()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 800);
        options.Title = "ProGPU Substrate - High-Performance WinUI Gallery Dashboard";
        options.API = GraphicsAPI.None;

        _window = Window.Create(options);

        _window.Load += OnWindowLoad;
        _window.Update += OnWindowUpdate;
        _window.Render += OnWindowRender;
        _window.Resize += OnWindowResize;

        Console.WriteLine("[ProGPU.Samples] Starting GPU-first UI Infrastructure Controls Gallery...");
        _window.Run();
        
        Cleanup();
    }

    private static void OnWindowLoad()
    {
        if (_window == null) return;

        _wgpuContext = new WgpuContext();
        _wgpuContext.Initialize(_window);

        _screenCompositor = new Compositor(_wgpuContext, _wgpuContext.SwapChainFormat);
        _offscreenCompositor = new Compositor(_wgpuContext, TextureFormat.Rgba8Unorm);
        _compute = new ComputeAccelerator(_wgpuContext);

        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!File.Exists(fontPath))
        {
            fontPath = "Arial.ttf";
        }

        if (File.Exists(fontPath))
        {
            Console.WriteLine($"[ProGPU.Samples] Loading System Font: {fontPath}");
            _font = new TtfFont(fontPath);
            ushort testIdx = _font.GetGlyphIndex('A');
            var testOutline = _font.GetGlyphOutline(testIdx);
            Console.WriteLine($"[ProGPU.Samples] Test Glyph 'A' Index: {testIdx}, Outline Figures: {testOutline?.Figures.Count ?? -1}");
            if (testOutline != null && testOutline.Figures.Count > 0)
            {
                var fig = testOutline.Figures[0];
                Console.WriteLine($"[ProGPU.Samples] Figure StartPoint: {fig.StartPoint}, Segments Count: {fig.Segments.Count}");
            }
        }
        else
        {
            throw new FileNotFoundException("Arial.ttf is required to execute typography. Ensure standard Arial TrueType font path is available.");
        }

        _canvasSourceTexture = new GpuTexture(_wgpuContext, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.StorageBinding | TextureUsage.CopySrc);
        _canvasTempTexture = new GpuTexture(_wgpuContext, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);
        _canvasBlurTexture = new GpuTexture(_wgpuContext, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);
        _canvasShadowTexture = new GpuTexture(_wgpuContext, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);

        // Pre-populate virtualized grid dataset
        GenerateLogItems();

        BuildSceneGraph();
        SetupInput();
    }

    private static void GenerateLogItems()
    {
        _logItems.Clear();
        for (int i = 0; i < 10000; i++)
        {
            _logItems.Add(new LogItem
            {
                Id = i + 1,
                Name = $"Dispatcher.QueueEvent #{i + 1:N0}",
                Status = (i % 3 == 0) ? "OK" : ((i % 3 == 1) ? "PENDING" : "WARNING"),
                Latency = Math.Abs(Math.Sin(i * 0.05) * 45.0 + Math.Cos(i * 0.2) * 5.0 + 10.0)
            });
        }
    }

    private static void BuildSceneGraph()
    {
        if (_wgpuContext == null || _font == null) return;

        // 1. Root Grid containing Header + Main Body + Bottom Diagnostics Bar
        _rootGrid = new ProGPU.WinUI.Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _rootGrid.RowDefinitions.Add(new GridLength(70, GridUnitType.Absolute));  // Row 0: Header
        _rootGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Row 1: Content Workspace
        _rootGrid.RowDefinitions.Add(new GridLength(32, GridUnitType.Absolute));  // Row 2: Status bar

        // 2. HEADER
        var headerBar = new Border
        {
            Background = new SolidColorBrush(0x13131AFF), // Premium dark Mica backdrop
            BorderBrush = new SolidColorBrush(0xFFFFFF15), // Thin translucent border outline
            BorderThickness = new Thickness(0, 0, 0, 1f),
            Padding = new Thickness(20, 10, 20, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var headerGrid = new ProGPU.WinUI.Grid();
        headerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        headerGrid.ColumnDefinitions.Add(new GridLength(300f, GridUnitType.Absolute));

        var titleText = new RichTextBlock { Font = _font, FontSize = 20f, VerticalAlignment = VerticalAlignment.Center };
        var logoRun = new Run("Pro") { Foreground = new SolidColorBrush(0x0078D4FF) };
        titleText.Inlines.Add(new Bold(logoRun));
        titleText.Inlines.Add(new Bold(new Run("GPU WinUI Gallery")));
        headerGrid.AddChild(titleText);
        ProGPU.WinUI.Grid.SetColumn(titleText, 0);

        var subtitleText = new RichTextBlock 
        { 
            Font = _font, 
            FontSize = 11f, 
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };
        subtitleText.Inlines.Add(new Run(".NET 10 cross-platform high-performance engine showcase"));
        headerGrid.AddChild(subtitleText);
        ProGPU.WinUI.Grid.SetColumn(subtitleText, 1);

        headerBar.Child = headerGrid;
        _rootGrid.AddChild(headerBar);
        ProGPU.WinUI.Grid.SetRow(headerBar, 0);

        // 3. BODY WORKSPACE (Sidebar + Showcase Area)
        var bodyGrid = new ProGPU.WinUI.Grid();
        bodyGrid.ColumnDefinitions.Add(new GridLength(280, GridUnitType.Absolute)); // Col 0: Sidebar selection
        bodyGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Col 1: active view

        // SIDEBAR CARD
        var sidebarCard = new Border
        {
            CornerRadius = 8f,
            Background = new SolidColorBrush(0x13131AFF), // Mica background styling
            BorderBrush = new SolidColorBrush(0xFFFFFF15), // Translucent border outline
            BorderThickness = new Thickness(1f),
            Padding = new Thickness(12),
            Margin = new Thickness(10)
        };

        var sidebarStack = new StackPanel { Orientation = Orientation.Vertical };
        
        var panelTitle = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(5, 5, 5, 15) };
        panelTitle.Inlines.Add(new Run("CONTROLS & PANELS") { Foreground = new SolidColorBrush(0x0078D4FF) });
        sidebarStack.AddChild(panelTitle);

        // Define beautiful tab switching buttons
        _basicInputTabBtn = CreateSidebarButton("Basic Input", "Basic Inputs");
        _basicInputTabBtn.Click += (s, e) => SwitchCategory("Basic Input");
        sidebarStack.AddChild(_basicInputTabBtn);

        _panelsTabBtn = CreateSidebarButton("Layout Panels", "Layout Panels");
        _panelsTabBtn.Click += (s, e) => SwitchCategory("Layout Panels");
        sidebarStack.AddChild(_panelsTabBtn);

        _textTabBtn = CreateSidebarButton("Text & Documents", "Text & Documents");
        _textTabBtn.Click += (s, e) => SwitchCategory("Text & Documents");
        sidebarStack.AddChild(_textTabBtn);

        _dataTabBtn = CreateSidebarButton("Data Virtualization", "Data Virtualization");
        _dataTabBtn.Click += (s, e) => SwitchCategory("Data Virtualization");
        sidebarStack.AddChild(_dataTabBtn);

        _computeTabBtn = CreateSidebarButton("Compute FX", "Compute FX");
        _computeTabBtn.Click += (s, e) => SwitchCategory("Compute FX");
        sidebarStack.AddChild(_computeTabBtn);

        sidebarCard.Child = sidebarStack;
        bodyGrid.AddChild(sidebarCard);
        ProGPU.WinUI.Grid.SetColumn(sidebarCard, 0);

        // SHOWCASE CONTAINER
        _showcaseContainer = new Border
        {
            CornerRadius = 8f,
            Background = new SolidColorBrush(0x13131AFF), // Mica background styling
            BorderBrush = new SolidColorBrush(0xFFFFFF15), // Translucent border outline
            BorderThickness = new Thickness(1f),
            Margin = new Thickness(0, 10, 10, 10),
            Padding = new Thickness(16)
        };

        bodyGrid.AddChild(_showcaseContainer);
        ProGPU.WinUI.Grid.SetColumn(_showcaseContainer, 1);

        _rootGrid.AddChild(bodyGrid);
        ProGPU.WinUI.Grid.SetRow(bodyGrid, 1);

        // 4. BOTTOM DIAGNOSTICS STATUS BAR
        var statusBar = new Border
        {
            Background = new SolidColorBrush(0x0C0C12FF), // Deep dark status bar
            BorderBrush = new SolidColorBrush(0xFFFFFF15), // Translucent border outline
            BorderThickness = new Thickness(0, 1f, 0, 0),
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _statsText = new RichTextBlock
        {
            FontSize = 11f,
            Foreground = new SolidColorBrush(0x888899FF),
            Font = _font,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statsText.Inlines.Add(new Run("FPS: -- | CPU: -- ms | Cursor: (0, 0) | Focused Element: None"));
        statusBar.Child = _statsText;
        _rootGrid.AddChild(statusBar);
        ProGPU.WinUI.Grid.SetRow(statusBar, 2);

        // Initial tab render
        SwitchCategory("Basic Input");
    }

    private static Button CreateSidebarButton(string categoryName, string displayText)
    {
        var btn = new Button
        {
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = 6f
        };
        
        var content = new RichTextBlock { Font = _font, FontSize = 12f };
        content.Inlines.Add(new Run(displayText));
        btn.Content = content;
        
        return btn;
    }

    private static void SwitchCategory(string categoryName)
    {
        _activeCategory = categoryName;

        // Dynamic visual selection updates
        UpdateSidebarButtonStates();

        if (_showcaseContainer == null || _font == null) return;

        // Instantiate selected categories
        _showcaseContainer.Child = categoryName switch
        {
            "Basic Input" => CreateBasicInputView(),
            "Layout Panels" => CreateLayoutPanelsView(),
            "Text & Documents" => CreateTextDocumentsView(),
            "Data Virtualization" => CreateDataVirtualizationView(),
            "Compute FX" => CreateComputeFxView(),
            _ => null
        };

        _rootGrid?.Invalidate();
    }

    private static void UpdateSidebarButtonStates()
    {
        var activeBrush = new SolidColorBrush(0x0078D4FF); // Segoe Blue active accent
        var normalBrush = new SolidColorBrush(0xFFFFFF0D); // translucent default

        if (_basicInputTabBtn != null) _basicInputTabBtn.Background = _activeCategory == "Basic Input" ? activeBrush : normalBrush;
        if (_panelsTabBtn != null) _panelsTabBtn.Background = _activeCategory == "Layout Panels" ? activeBrush : normalBrush;
        if (_textTabBtn != null) _textTabBtn.Background = _activeCategory == "Text & Documents" ? activeBrush : normalBrush;
        if (_dataTabBtn != null) _dataTabBtn.Background = _activeCategory == "Data Virtualization" ? activeBrush : normalBrush;
        if (_computeTabBtn != null) _computeTabBtn.Background = _activeCategory == "Compute FX" ? activeBrush : normalBrush;
    }

    // ===================================================
    // Page Creation Views
    // ===================================================

    private static FrameworkElement CreateBasicInputView()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };

        var title = new RichTextBlock { Font = _font, FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Bold(new Run("Basic Input Controls & State Routing")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        description.Inlines.Add(new Run("This page showcases standard high-performance input controls. Pointer hovers, clicks, and drag operations are natively routed down the recursive SceneGraph with real-time UI invalidation."));
        stack.AddChild(description);

        // 1. BUTTON
        var btnGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
        var interactiveBtn = new Button { Width = 180f, Height = 36f, CornerRadius = 6f };
        var btnText = new RichTextBlock { Font = _font, FontSize = 12f };
        btnText.Inlines.Add(new Run($"Click Count: {_clickCount}"));
        interactiveBtn.Content = btnText;
        
        interactiveBtn.Click += (s, e) =>
        {
            _clickCount++;
            btnText.Inlines.Clear();
            btnText.Inlines.Add(new Run($"Click Count: {_clickCount}"));
            btnText.Invalidate();
        };
        btnGroup.AddChild(interactiveBtn);

        var btnDesc = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(15, 8, 0, 0) };
        btnDesc.Inlines.Add(new Run("Hover and press. Clicks increment count state directly."));
        btnGroup.AddChild(btnDesc);
        stack.AddChild(btnGroup);

        // 2. CHECKBOX
        var checkGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
        var customCheck = new CheckBox { IsChecked = _checkboxStatus == "Checked" };
        var checkLabel = new RichTextBlock { Font = _font, FontSize = 12f };
        checkLabel.Inlines.Add(new Run("Enable high-fidelity render features"));
        customCheck.Content = checkLabel;

        var checkStatus = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(30, 4, 0, 0) };
        checkStatus.Inlines.Add(new Run($"Current state: {_checkboxStatus}"));

        customCheck.CheckedChanged += (s, e) =>
        {
            _checkboxStatus = customCheck.IsChecked ? "Checked" : "Unchecked";
            checkStatus.Inlines.Clear();
            checkStatus.Inlines.Add(new Run($"Current state: {_checkboxStatus}"));
            checkStatus.Invalidate();
        };

        checkGroup.AddChild(customCheck);
        checkGroup.AddChild(checkStatus);
        stack.AddChild(checkGroup);

        // Disabled Option to demonstrate visual states
        var disabledCheck = new CheckBox { IsEnabled = false, IsChecked = true, Margin = new Thickness(0, 0, 0, 15) };
        var disabledLabel = new RichTextBlock { Font = _font, FontSize = 12f };
        disabledLabel.Inlines.Add(new Run("Disabled read-only setting (Always checked)"));
        disabledCheck.Content = disabledLabel;
        stack.AddChild(disabledCheck);

        // 3. SLIDER
        var sliderTitle = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 10, 0, 4) };
        sliderTitle.Inlines.Add(new Bold(new Run($"Accent Glow Intensity: {_sliderValue:F0}%")));
        stack.AddChild(sliderTitle);

        var accentSlider = new ProGPU.WinUI.Slider { Minimum = 0f, Maximum = 100f, Value = _sliderValue, Width = 300f, Margin = new Thickness(0, 0, 0, 15) };
        accentSlider.ValueChanged += (s, e) =>
        {
            _sliderValue = accentSlider.Value;
            sliderTitle.Inlines.Clear();
            sliderTitle.Inlines.Add(new Bold(new Run($"Accent Glow Intensity: {_sliderValue:F0}%")));
            sliderTitle.Invalidate();
        };
        stack.AddChild(accentSlider);

        return stack;
    }

    private static FrameworkElement CreateLayoutPanelsView()
    {
        var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(5) };
        grid.RowDefinitions.Add(new GridLength(60, GridUnitType.Absolute));   // Description
        grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Main panels showcase
        grid.RowDefinitions.Add(new GridLength(140, GridUnitType.Absolute));  // Canvas absolute layout

        var descText = new RichTextBlock { Font = _font, FontSize = 12f };
        descText.Inlines.Add(new Run("Showcasing standard WinUI panels. "));
        descText.Inlines.Add(new Bold(new Run("Grid")));
        descText.Inlines.Add(new Run(" divides workspace recursively using star/fixed/auto cells, "));
        descText.Inlines.Add(new Bold(new Run("StackPanel")));
        descText.Inlines.Add(new Run(" manages vertical/horizontal flow packs, and "));
        descText.Inlines.Add(new Bold(new Run("Canvas")));
        descText.Inlines.Add(new Run(" allows absolute X/Y placements."));
        grid.AddChild(descText);
        ProGPU.WinUI.Grid.SetRow(descText, 0);

        // 1. Grid & Stack Panel Showroom
        var showroomGrid = new ProGPU.WinUI.Grid();
        showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        showroomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        // Column 0: 2x2 Grid cell attachments
        var innerGrid = new ProGPU.WinUI.Grid();
        innerGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        innerGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        innerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        innerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        var card1 = new Border { Margin = new Thickness(4), Background = new SolidColorBrush(0xFF555520), CornerRadius = 6f };
        var cardText1 = new RichTextBlock { Font = _font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cardText1.Inlines.Add(new Run("Cell (0, 0)"));
        card1.Child = cardText1;
        innerGrid.AddChild(card1);
        ProGPU.WinUI.Grid.SetRow(card1, 0);
        ProGPU.WinUI.Grid.SetColumn(card1, 0);

        var card2 = new Border { Margin = new Thickness(4), Background = new SolidColorBrush(0x00FF8820), CornerRadius = 6f };
        var cardText2 = new RichTextBlock { Font = _font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cardText2.Inlines.Add(new Run("Cell (0, 1)"));
        card2.Child = cardText2;
        innerGrid.AddChild(card2);
        ProGPU.WinUI.Grid.SetRow(card2, 0);
        ProGPU.WinUI.Grid.SetColumn(card2, 1);

        var card3 = new Border { Margin = new Thickness(4), Background = new SolidColorBrush(0x00E5FF20), CornerRadius = 6f };
        var cardText3 = new RichTextBlock { Font = _font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cardText3.Inlines.Add(new Run("Cell (1, 0)"));
        card3.Child = cardText3;
        innerGrid.AddChild(card3);
        ProGPU.WinUI.Grid.SetRow(card3, 1);
        ProGPU.WinUI.Grid.SetColumn(card3, 0);

        var card4 = new Border { Margin = new Thickness(4), Background = new SolidColorBrush(0xA100FF20), CornerRadius = 6f };
        var cardText4 = new RichTextBlock { Font = _font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cardText4.Inlines.Add(new Run("Cell (1, 1)"));
        card4.Child = cardText4;
        innerGrid.AddChild(card4);
        ProGPU.WinUI.Grid.SetRow(card4, 1);
        ProGPU.WinUI.Grid.SetColumn(card4, 1);

        var leftGroup = new Border
        {
            Margin = new Thickness(5),
            Background = new SolidColorBrush(0xFFFFFF08),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(8)
        };
        leftGroup.Child = innerGrid;
        showroomGrid.AddChild(leftGroup);
        ProGPU.WinUI.Grid.SetColumn(leftGroup, 0);

        // Column 1: StackPanel layout
        var rightStack = new StackPanel { Orientation = Orientation.Vertical };
        var stackTitle = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(8, 0, 0, 8) };
        stackTitle.Inlines.Add(new Bold(new Run("Vertical Stack Panel")));
        rightStack.AddChild(stackTitle);

        for (int i = 1; i <= 3; i++)
        {
            var item = new Border
            {
                Height = 32f,
                Margin = new Thickness(4),
                Background = new SolidColorBrush(0xFFFFFF15),
                CornerRadius = 4f
            };
            var itemText = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(10, 8, 0, 0) };
            itemText.Inlines.Add(new Run($"Stack Item #{i}"));
            item.Child = itemText;
            rightStack.AddChild(item);
        }

        var horizontalStackTitle = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(8, 12, 0, 8) };
        horizontalStackTitle.Inlines.Add(new Bold(new Run("Horizontal Flow Row")));
        rightStack.AddChild(horizontalStackTitle);

        var horzFlow = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 1; i <= 3; i++)
        {
            var item = new Border
            {
                Width = 72f,
                Height = 28f,
                Margin = new Thickness(4),
                Background = new SolidColorBrush(0x00E5FF25),
                CornerRadius = 4f
            };
            var itemText = new RichTextBlock { Font = _font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            itemText.Inlines.Add(new Run($"Flow #{i}"));
            item.Child = itemText;
            horzFlow.AddChild(item);
        }
        rightStack.AddChild(horzFlow);

        var rightGroup = new Border
        {
            Margin = new Thickness(5),
            Background = new SolidColorBrush(0xFFFFFF08),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(8)
        };
        rightGroup.Child = rightStack;
        showroomGrid.AddChild(rightGroup);
        ProGPU.WinUI.Grid.SetColumn(rightGroup, 1);

        grid.AddChild(showroomGrid);
        ProGPU.WinUI.Grid.SetRow(showroomGrid, 1);

        // 2. Canvas Absolute Layout (Row 2)
        var canvasPanel = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var canvasDesc = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(8, 4, 0, 0) };
        canvasDesc.Inlines.Add(new Bold(new Run("Absolute Canvas Coordinates:")));
        canvasPanel.AddChild(canvasDesc);
        Canvas.SetLeft(canvasDesc, 8f);
        Canvas.SetTop(canvasDesc, 4f);

        // Renders overlapping absolute positioned panels
        var cardColors = new uint[] { 0xFF5555CC, 0x00FF88CC, 0x00E5FFCC };
        for (int i = 0; i < 3; i++)
        {
            var overlappingCard = new Border
            {
                Width = 140f,
                Height = 45f,
                Background = new SolidColorBrush(cardColors[i]),
                CornerRadius = 6f
            };
            var overlappingText = new RichTextBlock { Font = _font, FontSize = 10f, Margin = new Thickness(12, 14, 0, 0) };
            overlappingText.Inlines.Add(new Bold(new Run($"Absolute Panel #{i + 1}")));
            overlappingCard.Child = overlappingText;
            
            canvasPanel.AddChild(overlappingCard);
            Canvas.SetLeft(overlappingCard, 30f + i * 90f);
            Canvas.SetTop(overlappingCard, 35f + i * 20f);
        }

        var canvasGroup = new Border
        {
            Margin = new Thickness(5),
            Background = new SolidColorBrush(0xFFFFFF08),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f
        };
        canvasGroup.Child = canvasPanel;
        grid.AddChild(canvasGroup);
        ProGPU.WinUI.Grid.SetRow(canvasGroup, 2);

        return grid;
    }

    private static FrameworkElement CreateTextDocumentsView()
    {
        var grid = new ProGPU.WinUI.Grid();
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(1.1f, GridUnitType.Star));

        // Column 0: Interactive text typing editors
        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
        
        var editorTitle = new RichTextBlock { Font = _font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
        editorTitle.Inlines.Add(new Bold(new Run("Caret-Interactive Input Arenas")));
        leftStack.AddChild(editorTitle);

        var editorDesc = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        editorDesc.Inlines.Add(new Run("Input focus is obtained on clicking, enabling caret positioning, arrow-key navigation, backspace deletions, and live character typing."));
        leftStack.AddChild(editorDesc);

        // TextBox (Single line)
        var textboxLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        textboxLabel.Inlines.Add(new Bold(new Run("Standard TextBox (Single Line)")));
        leftStack.AddChild(textboxLabel);

        var textEntry = new TextBox 
        { 
            Font = _font, 
            Text = "ProGPU typing", 
            Width = 300f, 
            Height = 32f, 
            Margin = new Thickness(0, 0, 0, 20) 
        };
        leftStack.AddChild(textEntry);

        // RichEditBox (Multi line)
        var richeditLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        richeditLabel.Inlines.Add(new Bold(new Run("Interactive RichEditBox (Formatted Runs)")));
        leftStack.AddChild(richeditLabel);

        var richEntry = new RichEditBox 
        { 
            Font = _font, 
            Width = 300f, 
            Height = 150f 
        };
        leftStack.AddChild(richEntry);

        grid.AddChild(leftStack);
        ProGPU.WinUI.Grid.SetColumn(leftStack, 0);

        // Column 1: Multi-column FlowDocument
        var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
        var docTitle = new RichTextBlock { Font = _font, FontSize = 14f, Margin = new Thickness(8, 0, 0, 5) };
        docTitle.Inlines.Add(new Bold(new Run("Multi-Column Structured FlowDocument")));
        rightStack.AddChild(docTitle);

        var flowDoc = new FlowDocument 
        { 
            Font = _font, 
            FontSize = 12f, 
            ColumnCount = 2, 
            ColumnGap = 20f,
            Height = 270f
        };
        
        flowDoc.Paragraphs.Add(new Paragraph(
            new Bold(new Run("High Performance Typography\n")),
            new Run("The new substrate text layout is powered by real-time signed distance field atlas packing, producing extremely sharp vector paths at any scale without performance hits.")
        ));

        flowDoc.Paragraphs.Add(new Paragraph(
            new Italic(new Run("Dual Column Balancing:\n")),
            new Run("Text flows perfectly between multiple adjacent columns. FlowDocument manages margin gaps, alignment bounds, and paragraphs dynamically on WebGPU substrates.")
        ));

        flowDoc.Paragraphs.Add(new Paragraph(
            new Run("This matches modern WinUI XAML document layers completely, delivering advanced visualization out of the box.")
        ));

        var docBorder = new Border
        {
            Background = new SolidColorBrush(0xFFFFFF0D),
            BorderBrush = new SolidColorBrush(0xFFFFFF20),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Margin = new Thickness(4)
        };
        docBorder.Child = flowDoc;
        rightStack.AddChild(docBorder);

        grid.AddChild(rightStack);
        ProGPU.WinUI.Grid.SetColumn(rightStack, 1);

        return grid;
    }

    private static FrameworkElement CreateDataVirtualizationView()
    {
        var grid = new ProGPU.WinUI.Grid();
        grid.RowDefinitions.Add(new GridLength(70, GridUnitType.Absolute));   // Header
        grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Recycled Grid

        var descStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
        var listTitle = new RichTextBlock { Font = _font, FontSize = 14f };
        listTitle.Inlines.Add(new Bold(new Run("10,000 Record Virtualized DataGrid")));
        descStack.AddChild(listTitle);

        var listDesc = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(0, 2, 0, 0) };
        listDesc.Inlines.Add(new Run("Ultra-fast vertical scroll recycling displays massive datasets at locked 60 FPS. Click on any header column to "));
        listDesc.Inlines.Add(new Bold(new Run("sort alphanumerically")));
        listDesc.Inlines.Add(new Run(", and click rows to change selected indices."));
        descStack.AddChild(listDesc);

        grid.AddChild(descStack);
        ProGPU.WinUI.Grid.SetRow(descStack, 0);

        // Virtualized DataGrid setup
        var dataGrid = new ProGPU.WinUI.DataGrid
        {
            Font = _font,
            RowHeight = 28f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(4)
        };

        // Define columns
        dataGrid.Columns.Add(new DataGridColumn("ID", 70f, "Id"));
        dataGrid.Columns.Add(new DataGridColumn("Activity Name", 230f, "Name"));
        dataGrid.Columns.Add(new DataGridColumn("Status", 110f, "Status"));
        dataGrid.Columns.Add(new DataGridColumn("Latency", 100f, "Latency"));

        // Setup direct, reflection-free binding for maximum speed
        dataGrid.CellValueBinding = (item, prop) =>
        {
            if (item is LogItem log)
            {
                return prop switch
                {
                    "Id" => log.Id.ToString(),
                    "Name" => log.Name,
                    "Status" => log.Status,
                    "Latency" => $"{log.Latency:F1} ms",
                    _ => string.Empty
                };
            }
            return string.Empty;
        };

        // Populate logs
        foreach (var log in _logItems)
        {
            dataGrid.AddItem(log);
        }

        grid.AddChild(dataGrid);
        ProGPU.WinUI.Grid.SetRow(dataGrid, 1);

        return grid;
    }

    private static FrameworkElement CreateComputeFxView()
    {
        var grid = new ProGPU.WinUI.Grid();
        grid.ColumnDefinitions.Add(new GridLength(280, GridUnitType.Absolute)); // Compute adjust sliders
        grid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));      // WebGPU offscreen effect canvas

        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
        var computeTitle = new RichTextBlock { Font = _font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
        computeTitle.Inlines.Add(new Bold(new Run("WGSL Compute Accelerator")));
        leftStack.AddChild(computeTitle);

        var computeDesc = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        computeDesc.Inlines.Add(new Run("Adjust dynamic WGSL pixel processors running in parallel with the scene compositing passes."));
        leftStack.AddChild(computeDesc);

        // Sliders for compute
        var blurLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        blurLabel.Inlines.Add(new Bold(new Run($"Backdrop Blur: {_blurRadius:F1} px")));
        leftStack.AddChild(blurLabel);

        var blurSlider = new ProGPU.WinUI.Slider { Minimum = 0f, Maximum = 20f, Value = _blurRadius, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
        blurSlider.ValueChanged += (s, e) =>
        {
            _blurRadius = blurSlider.Value;
            blurLabel.Inlines.Clear();
            blurLabel.Inlines.Add(new Bold(new Run($"Backdrop Blur: {_blurRadius:F1} px")));
            blurLabel.Invalidate();
        };
        leftStack.AddChild(blurSlider);

        var shadowLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        shadowLabel.Inlines.Add(new Bold(new Run($"Shadow Radius: {_shadowRadius:F1} px")));
        leftStack.AddChild(shadowLabel);

        var shadowSlider = new ProGPU.WinUI.Slider { Minimum = 0f, Maximum = 20f, Value = _shadowRadius, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
        shadowSlider.ValueChanged += (s, e) =>
        {
            _shadowRadius = shadowSlider.Value;
            shadowLabel.Inlines.Clear();
            shadowLabel.Inlines.Add(new Bold(new Run($"Shadow Radius: {_shadowRadius:F1} px")));
            shadowLabel.Invalidate();
        };
        leftStack.AddChild(shadowSlider);

        var offsetXLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {_shadowOffset.X:F1} px")));
        leftStack.AddChild(offsetXLabel);

        var offsetXSlider = new ProGPU.WinUI.Slider { Minimum = -20f, Maximum = 20f, Value = _shadowOffset.X, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
        offsetXSlider.ValueChanged += (s, e) =>
        {
            _shadowOffset.X = offsetXSlider.Value;
            offsetXLabel.Inlines.Clear();
            offsetXLabel.Inlines.Add(new Bold(new Run($"Shadow Offset X: {_shadowOffset.X:F1} px")));
            offsetXLabel.Invalidate();
        };
        leftStack.AddChild(offsetXSlider);

        var offsetYLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 2) };
        offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {_shadowOffset.Y:F1} px")));
        leftStack.AddChild(offsetYLabel);

        var offsetYSlider = new ProGPU.WinUI.Slider { Minimum = -20f, Maximum = 20f, Value = _shadowOffset.Y, Width = 250f, Margin = new Thickness(0, 0, 0, 15) };
        offsetYSlider.ValueChanged += (s, e) =>
        {
            _shadowOffset.Y = offsetYSlider.Value;
            offsetYLabel.Inlines.Clear();
            offsetYLabel.Inlines.Add(new Bold(new Run($"Shadow Offset Y: {_shadowOffset.Y:F1} px")));
            offsetYLabel.Invalidate();
        };
        leftStack.AddChild(offsetYSlider);

        // Toggle Cogs Animation Button
        var toggleAnimBtn = new Button { Width = 185f, Height = 34f, CornerRadius = 6f, Margin = new Thickness(0, 10, 0, 0) };
        var toggleBtnText = new RichTextBlock { Font = _font, FontSize = 12f };
        toggleBtnText.Inlines.Add(new Run(_animateGear ? "Stop Vector Rotation" : "Start Vector Rotation"));
        toggleAnimBtn.Content = toggleBtnText;

        toggleAnimBtn.Click += (s, e) =>
        {
            _animateGear = !_animateGear;
            toggleBtnText.Inlines.Clear();
            toggleBtnText.Inlines.Add(new Run(_animateGear ? "Stop Vector Rotation" : "Start Vector Rotation"));
            toggleBtnText.Invalidate();
        };
        leftStack.AddChild(toggleAnimBtn);

        grid.AddChild(leftStack);
        ProGPU.WinUI.Grid.SetColumn(leftStack, 0);

        // Center WebGPU texture offscreen render Canvas (Column 1)
        _gearCanvasVisual = new GearCanvasVisual(_font!)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var displayCanvas = new GpuTextureCanvas(_canvasSourceTexture!, _canvasShadowTexture!, _canvasBlurTexture!);
        
        var canvasContainer = new Border
        {
            CornerRadius = 8f,
            Background = new SolidColorBrush(0x0C0C12FF),
            BorderBrush = new SolidColorBrush(0x222230FF),
            BorderThickness = new Thickness(1f),
            Margin = new Thickness(5),
            Child = displayCanvas
        };

        grid.AddChild(canvasContainer);
        ProGPU.WinUI.Grid.SetColumn(canvasContainer, 1);

        return grid;
    }

    private static GearCanvasVisual? _gearCanvasVisual;

    private static void SetupInput()
    {
        if (_window == null || _rootGrid == null) return;

        var input = _window.CreateInput();
        
        // Initialize WinUI Input Routing System with root grid scene node
        InputSystem.Initialize(input, _rootGrid);

        // Bubble-up PointerMoved coordinate tracking in root grid status
        _rootGrid.PointerMoved += (sender, args) =>
        {
            _mousePos = args.Position;
        };
    }

    private static void OnWindowUpdate(double delta)
    {
        if (_animateGear)
        {
            _gearRotation += (float)delta * 1.2f;
            if (_gearRotation > Math.PI * 2) _gearRotation -= (float)(Math.PI * 2);
            _rootGrid?.Invalidate();
        }

        // Keep active focused control tracking updated
        if (InputSystem.FocusedElement != null)
        {
            var typeName = InputSystem.FocusedElement.GetType().Name;
            _activeFocusedName = string.IsNullOrEmpty(InputSystem.FocusedElement.Name) 
                ? typeName 
                : $"{typeName} ({InputSystem.FocusedElement.Name})";
        }
        else
        {
            _activeFocusedName = "None";
        }
    }

    private static void OnWindowRender(double delta)
    {
        if (_rootGrid == null || _wgpuContext == null || _window == null) return;
        if (_screenCompositor == null || _offscreenCompositor == null || _compute == null) return;

        _frameStopwatch.Restart();

        // 1. Size negotiation: Measure & Arrange entire WinUI graph
        _rootGrid.Measure(new Vector2(_window.Size.X, _window.Size.Y));
        _rootGrid.Arrange(new Rect(0, 0, _window.Size.X, _window.Size.Y));

        // Update animated cogs if currently in Compute FX Showcase View
        if (_activeCategory == "Compute FX" && _gearCanvasVisual != null)
        {
            _gearCanvasVisual.Measure(new Vector2(_window.Size.X - 300f, _window.Size.Y - 140f));
            _gearCanvasVisual.Arrange(new Rect(0, 0, _window.Size.X - 300f, _window.Size.Y - 140f));
            _gearCanvasVisual.UpdateRotation(_gearRotation);

            uint canvasW = (uint)Math.Max(1f, _gearCanvasVisual.Size.X);
            uint canvasH = (uint)Math.Max(1f, _gearCanvasVisual.Size.Y);

            if (_canvasSourceTexture != null && _canvasTempTexture != null && _canvasBlurTexture != null && _canvasShadowTexture != null)
            {
                _canvasSourceTexture.Resize(canvasW, canvasH);
                _canvasTempTexture.Resize(canvasW, canvasH);
                _canvasBlurTexture.Resize(canvasW, canvasH);
                _canvasShadowTexture.Resize(canvasW, canvasH);

                _offscreenCompositor.RenderScene(_gearCanvasVisual, canvasW, canvasH, _canvasSourceTexture.ViewPtr);

                if (_shadowRadius > 0)
                {
                    var shadowColor = new Vector4(0f, 0f, 0f, 0.65f);
                    _compute.ApplyDropShadow(_canvasSourceTexture, _canvasShadowTexture, _shadowOffset, shadowColor, _shadowRadius);
                }

                if (_blurRadius > 0)
                {
                    _compute.ApplyGaussianBlur(_canvasSourceTexture, _canvasTempTexture, _canvasBlurTexture);
                }
            }
        }

        // 2. Metrics & stats overlay updates
        _cpuFrameTimeMs = _frameStopwatch.Elapsed.TotalMilliseconds;
        
        _frameCount++;
        _fpsAccumulator += delta;
        if (_fpsAccumulator >= 0.5)
        {
            _currentFps = _frameCount / _fpsAccumulator;
            _frameCount = 0;
            _fpsAccumulator = 0;
        }

        if (_statsText != null)
        {
            _statsText.Inlines.Clear();
            
            _statsText.Inlines.Add(new Run("FPS: "));
            _statsText.Inlines.Add(new Bold(new Run($"{_currentFps:F0}")) { Foreground = new SolidColorBrush(0x0078D4FF) });
            
            _statsText.Inlines.Add(new Run("  |  CPU Frame: "));
            _statsText.Inlines.Add(new Bold(new Run($"{_cpuFrameTimeMs:F2} ms")) { Foreground = new SolidColorBrush(0x0078D4FF) });
            
            _statsText.Inlines.Add(new Run("  |  Cursor: "));
            _statsText.Inlines.Add(new Run($"({_mousePos.X:F0}, {_mousePos.Y:F0})"));
            
            _statsText.Inlines.Add(new Run("  |  Focused Element: "));
            _statsText.Inlines.Add(new Bold(new Run(_activeFocusedName)) { Foreground = new SolidColorBrush(0x0078D4FF) });
            
            _statsText.PerformRichLayout(1200f);
        }

        // 3. Swapchain present
        TextureView* targetView = null;
        if (_wgpuContext.Surface != null)
        {
            var surfaceTexture = new SurfaceTexture();
            _wgpuContext.Wgpu.SurfaceGetCurrentTexture(_wgpuContext.Surface, &surfaceTexture);
            
            if (surfaceTexture.Status == SurfaceGetCurrentTextureStatus.Success)
            {
                var viewDesc = new TextureViewDescriptor
                {
                    Format = _wgpuContext.SwapChainFormat,
                    Dimension = TextureViewDimension.Dimension2D,
                    BaseMipLevel = 0,
                    MipLevelCount = 1,
                    BaseArrayLayer = 0,
                    ArrayLayerCount = 1,
                    Aspect = TextureAspect.All
                };
                targetView = _wgpuContext.Wgpu.TextureCreateView(surfaceTexture.Texture, &viewDesc);
            }
        }

        if (targetView != null)
        {
            _screenCompositor.RenderScene(_rootGrid, (uint)_window.Size.X, (uint)_window.Size.Y, targetView);
            
            _wgpuContext.Wgpu.SurfacePresent(_wgpuContext.Surface);
            _wgpuContext.Wgpu.TextureViewRelease(targetView);
        }
    }

    private static void OnWindowResize(Vector2D<int> newSize)
    {
        if (_wgpuContext == null) return;
        _wgpuContext.ConfigureSwapChain((uint)newSize.X, (uint)newSize.Y);
        _rootGrid?.Invalidate();
    }

    private static void Cleanup()
    {
        _canvasSourceTexture?.Dispose();
        _canvasTempTexture?.Dispose();
        _canvasBlurTexture?.Dispose();
        _canvasShadowTexture?.Dispose();

        _compute?.Dispose();
        _offscreenCompositor?.Dispose();
        _screenCompositor?.Dispose();
        _wgpuContext?.Dispose();
    }

    public static PathGeometry CreateGearPath(Vector2 center, float innerRadius, float outerRadius, int teethCount, float toothDepth)
    {
        var path = new PathGeometry();
        var fig = new PathFigure { IsClosed = true, IsFilled = true };

        float angleStep = (float)(Math.PI * 2.0 / teethCount);
        
        for (int i = 0; i < teethCount; i++)
        {
            float angle = i * angleStep;
            
            float a0 = angle;
            float a1 = angle + angleStep * 0.25f;
            float a2 = angle + angleStep * 0.55f;
            float a3 = angle + angleStep * 0.8f;

            Vector2 pt0 = center + new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0)) * innerRadius;
            Vector2 pt1 = center + new Vector2((float)Math.Cos(a1), (float)Math.Sin(a1)) * outerRadius;
            Vector2 pt2 = center + new Vector2((float)Math.Cos(a2), (float)Math.Sin(a2)) * outerRadius;
            Vector2 pt3 = center + new Vector2((float)Math.Cos(a3), (float)Math.Sin(a3)) * innerRadius;

            if (i == 0)
            {
                fig.StartPoint = pt0;
            }
            else
            {
                fig.Segments.Add(new LineSegment(pt0));
            }
            
            fig.Segments.Add(new LineSegment(pt1));
            fig.Segments.Add(new LineSegment(pt2));
            fig.Segments.Add(new LineSegment(pt3));
        }
        
        path.Figures.Add(fig);

        var cutoutFig = new PathFigure { IsClosed = true, IsFilled = true };
        float cutRadius = innerRadius * 0.6f;
        int circleSegments = 32;
        for (int i = 0; i < circleSegments; i++)
        {
            float a = -(float)(i * Math.PI * 2.0 / circleSegments);
            Vector2 pt = center + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * cutRadius;
            
            if (i == 0)
                cutoutFig.StartPoint = pt;
            else
                cutoutFig.Segments.Add(new LineSegment(pt));
        }
        path.Figures.Add(cutoutFig);

        return path;
    }
}

// ==========================================
// Custom Sibling Layout & Rendering Visual Nodes
// ==========================================

public class GearCanvasVisual : FrameworkElement
{
    private readonly DrawingVisual _gear1;
    private readonly DrawingVisual _gear2;
    private readonly DrawingVisual _gear3;

    public GearCanvasVisual(TtfFont font)
    {
        _gear1 = new DrawingVisual();
        _gear2 = new DrawingVisual();
        _gear3 = new DrawingVisual();

        AddChild(_gear1);
        AddChild(_gear2);
        AddChild(_gear3);
    }

    public void UpdateRotation(float baseRotation)
    {
        Vector2 center = Size / 2f;
        if (center.X <= 0 || center.Y <= 0) return;

        if (_gear1.Context.Commands.Count == 0)
        {
            var p1 = Program.CreateGearPath(Vector2.Zero, 85f, 115f, 16, 20f);
            _gear1.Context.DrawPath(new SolidColorBrush(0x00E5FFFF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p1);

            var p2 = Program.CreateGearPath(Vector2.Zero, 52f, 78f, 12, 18f);
            _gear2.Context.DrawPath(new SolidColorBrush(0xA100FFFF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p2);

            var p3 = Program.CreateGearPath(Vector2.Zero, 35f, 55f, 8, 15f);
            _gear3.Context.DrawPath(new SolidColorBrush(0xFF007FFF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p3);
        }

        _gear1.Transform = Matrix4x4.CreateRotationZ(baseRotation) * Matrix4x4.CreateTranslation(center.X - 35f, center.Y, 0f);

        Vector2 g2Center = center + new Vector2(152f, 0f);
        float g2Rotation = -baseRotation * (16f / 12f) + (float)(Math.PI / 12.0);
        _gear2.Transform = Matrix4x4.CreateRotationZ(g2Rotation) * Matrix4x4.CreateTranslation(g2Center.X - 35f, g2Center.Y, 0f);

        float angleBL = (float)(Math.PI * 5.0 / 4.0);
        Vector2 g3Center = center + new Vector2((float)Math.Cos(angleBL), (float)Math.Sin(angleBL)) * 133f;
        float g3Rotation = -baseRotation * (16f / 8f) + (float)(Math.PI / 8.0);
        _gear3.Transform = Matrix4x4.CreateRotationZ(g3Rotation) * Matrix4x4.CreateTranslation(g3Center.X - 35f, g3Center.Y, 0f);
    }
}

public class GpuTextureCanvas : FrameworkElement
{
    private readonly GpuTexture _source;
    private readonly GpuTexture _shadow;
    private readonly GpuTexture _blur;

    public GpuTextureCanvas(GpuTexture source, GpuTexture shadow, GpuTexture blur)
    {
        _source = source;
        _shadow = shadow;
        _blur = blur;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(new SolidColorBrush(0x0C0C12FF), null, new Rect(Vector2.Zero, Size));

        Rect r = new Rect(Vector2.Zero, Size);

        if (Program.GetShadowRadius() > 0)
        {
            context.DrawTexture(_shadow, r);
        }

        context.DrawTexture(_source, r);

        if (Program.GetBlurRadius() > 0)
        {
            float cardW = Math.Min(310f, Size.X * 0.8f);
            float cardH = Math.Min(180f, Size.Y * 0.6f);
            float cardX = (Size.X - cardW) / 2f;
            float cardY = (Size.Y - cardH) / 2f;
            Rect cardRect = new Rect(cardX, cardY, cardW, cardH);

            context.PushClip(cardRect);
            context.DrawTexture(_blur, r);

            var glassBg = new SolidColorBrush(0xFFFFFF15);
            var glassBorder = new Pen(new SolidColorBrush(0xFFFFFF35), 1.2f);
            context.DrawRectangle(glassBg, glassBorder, cardRect);

            context.PopClip();

            var font = Program.GetFont();
            if (font != null)
            {
                context.DrawText("FROSTED ACROSS GLASS", font, 13f, new SolidColorBrush(0x00E5FFFF), new Vector2(cardX + 20f, cardY + 30f));
                context.DrawText("Dual-pass horizontal + vertical", font, 11f, new SolidColorBrush(0xE0E0E0FF), new Vector2(cardX + 20f, cardY + 60f));
                context.DrawText("Backdrop compute blur filter dispatches", font, 10f, new SolidColorBrush(0x888899FF), new Vector2(cardX + 20f, cardY + 85f));
                
                context.DrawText($"Blur Radius: {Program.GetBlurRadius():F1} px", font, 10f, new SolidColorBrush(0x00FF88FF), new Vector2(cardX + 20f, cardY + 115f));
                context.DrawText($"Shadow Radius: {Program.GetShadowRadius():F1} px", font, 10f, new SolidColorBrush(0xFF5588FF), new Vector2(cardX + 160f, cardY + 115f));
            }
        }
    }
}
