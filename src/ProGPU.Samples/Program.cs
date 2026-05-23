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

    // Active diagnostic metric stats
    private static RichTextBlock? _statsText;
    private static Vector2 _mousePos;
    private static string _activeFocusedName = "None";

    // Category pages and sidebar selections
    private static string _activeCategory = "Basic Input";
    private static NavigationView? _navigationView;

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
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 800);
        options.Title = "ProGPU Substrate - High-Performance WinUI Gallery Dashboard";
        options.API = GraphicsAPI.None;

        _window = Window.Create(options);

        _window.Load += OnWindowLoad;
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
            ProGPU.WinUI.PopupService.DefaultFont = _font;
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

        // 3. BODY WORKSPACE (Premium Sidebar Navigation View)
        _navigationView = new NavigationView
        {
            Font = _font,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Persistent page visual trees
        var basicInputItem = new NavigationViewItem("Basic Input", "🖱", CreateBasicInputView());
        var panelsItem = new NavigationViewItem("Layout Panels", "🔲", CreateLayoutPanelsView());
        var textItem = new NavigationViewItem("Text & Documents", "📄", CreateTextDocumentsView());
        var dataItem = new NavigationViewItem("Data Virtualization", "📊", CreateDataVirtualizationView());
        var computeItem = new NavigationViewItem("Compute FX", "⚙", CreateComputeFxView());
        var motionAnimationsItem = new NavigationViewItem("Motion & Animations", "🎬", CreateMotionAnimationsView());
        var advancedItem = new NavigationViewItem("Advanced Controls", "🛠", CreateAdvancedControlsView());

        _navigationView.MenuItems.Add(basicInputItem);
        _navigationView.MenuItems.Add(panelsItem);
        _navigationView.MenuItems.Add(textItem);
        _navigationView.MenuItems.Add(dataItem);
        _navigationView.MenuItems.Add(computeItem);
        _navigationView.MenuItems.Add(motionAnimationsItem);
        _navigationView.MenuItems.Add(advancedItem);

        _navigationView.SelectionChanged += (s, e) =>
        {
            if (_navigationView.SelectedItem != null)
            {
                _activeCategory = _navigationView.SelectedItem.Text;
            }
            _rootGrid?.Invalidate();
        };

        // Select default category
        _navigationView.SelectedItem = basicInputItem;

        _rootGrid.AddChild(_navigationView);
        ProGPU.WinUI.Grid.SetRow(_navigationView, 1);

        // 4. BOTTOM DIAGNOSTICS STATUS BAR
        var statusBar = new Border
        {
            Background = new SolidColorBrush(0x0C0C12FF), // Deep dark status bar
            BorderBrush = new SolidColorBrush(0xFFFFFF15), // Thin boundary stroke
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

        // 4. TOOGLE SWITCH
        var toggleGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 15) };
        var interactiveToggle = new ToggleSwitch { IsOn = true };
        var toggleLabel = new RichTextBlock { Font = _font, FontSize = 12f };
        toggleLabel.Inlines.Add(new Run("Enable High-Fidelity Rendering"));
        interactiveToggle.Content = toggleLabel;

        var toggleStatusText = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(20, 4, 0, 0) };
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
        var comboTitle = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 4) };
        comboTitle.Inlines.Add(new Bold(new Run("UI Accent Theme Colors Selection:")));
        stack.AddChild(comboTitle);

        var customCombo = new ComboBox { Font = _font };
        customCombo.Items.Add(new ComboBoxItem("Segoe Blue (Default)"));
        customCombo.Items.Add(new ComboBoxItem("Emerald Green"));
        customCombo.Items.Add(new ComboBoxItem("Crimson Red"));
        customCombo.Items.Add(new ComboBoxItem("Amber Gold"));
        
        var comboStatus = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(0, 4, 0, 15) };
        comboStatus.Inlines.Add(new Run("Selected theme: Segoe Blue (Default)"));
        customCombo.SelectionChanged += (s, e) =>
        {
            if (customCombo.SelectedItem != null)
            {
                comboStatus.Inlines.Clear();
                comboStatus.Inlines.Add(new Run($"Selected theme: {customCombo.SelectedItem.Text}"));
                comboStatus.Invalidate();
            }
        };
        stack.AddChild(customCombo);
        stack.AddChild(comboStatus);

        return stack;
    }

    private static FrameworkElement CreateLayoutPanelsView()
    {
        var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(10) };
        grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
        grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Pivot tab showcase

        var descText = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
        descText.Inlines.Add(new Run("This page showcases standard WinUI layout panels enclosed inside a premium sliding "));
        descText.Inlines.Add(new Bold(new Run("Pivot")));
        descText.Inlines.Add(new Run(" control. Hover tabs or click to switch with smooth slide animations."));
        grid.AddChild(descText);
        ProGPU.WinUI.Grid.SetRow(descText, 0);

        // 1. Pivot Control
        var pivot = new Pivot
        {
            Font = _font,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Tab 1: Grid & Stack Panels
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
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        leftGroup.Child = innerGrid;
        showroomGrid.AddChild(leftGroup);
        ProGPU.WinUI.Grid.SetColumn(leftGroup, 0);

        // Column 1: StackPanel layout
        var rightStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
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
                CornerRadius = 4f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var itemText = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(10, 8, 0, 0) };
            itemText.Inlines.Add(new Run($"Stack Item #{i}"));
            item.Child = itemText;
            rightStack.AddChild(item);
        }

        var horizontalStackTitle = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(8, 12, 0, 8) };
        horizontalStackTitle.Inlines.Add(new Bold(new Run("Horizontal Flow Row")));
        rightStack.AddChild(horizontalStackTitle);

        var horzFlow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch };
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
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        rightGroup.Child = rightStack;
        showroomGrid.AddChild(rightGroup);
        ProGPU.WinUI.Grid.SetColumn(rightGroup, 1);

        var pivotItem1 = new PivotItem("Recursive Grids & Stacks", showroomGrid);
        pivot.Items.Add(pivotItem1);

        // Tab 2: Canvas Absolute Layout
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
                Width = 160f,
                Height = 60f,
                Background = new SolidColorBrush(cardColors[i]),
                CornerRadius = 6f
            };
            var overlappingText = new RichTextBlock { Font = _font, FontSize = 11f, Margin = new Thickness(12, 20, 0, 0) };
            overlappingText.Inlines.Add(new Bold(new Run($"Absolute Panel #{i + 1}")));
            overlappingCard.Child = overlappingText;
            
            canvasPanel.AddChild(overlappingCard);
            Canvas.SetLeft(overlappingCard, 50f + i * 110f);
            Canvas.SetTop(overlappingCard, 45f + i * 25f);
        }

        var canvasGroup = new Border
        {
            Margin = new Thickness(5),
            Background = new SolidColorBrush(0xFFFFFF08),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        canvasGroup.Child = canvasPanel;

        var pivotItem2 = new PivotItem("Absolute Canvas Positions", canvasGroup);
        pivot.Items.Add(pivotItem2);

        // Tab 3: TabView Control
        var tabViewContainer = new TabView
        {
            Font = _font,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Create standard default tabs
        var tabItem1 = new TabViewItem("Home Tab")
        {
            Content = new Border
            {
                Background = new SolidColorBrush(0x13131AFF),
                CornerRadius = 8f,
                Padding = new Thickness(20),
                Child = new RichTextBlock
                {
                    Font = _font,
                    FontSize = 14f,
                    Inlines = { new Bold(new Run("Welcome to your TabView Home Page!\n\n")), new Run("This TabView supports adding new tabs by clicking the '+' button on the right, and closing existing ones using the 'x' close buttons.") }
                }
            }
        };

        var tabItem2 = new TabViewItem("Analytics")
        {
            Content = new Border
            {
                Background = new SolidColorBrush(0x0C0C12FF),
                CornerRadius = 8f,
                Padding = new Thickness(20),
                Child = new RichTextBlock
                {
                    Font = _font,
                    FontSize = 14f,
                    Inlines = { new Bold(new Run("Real-Time Graphics Analytics Data\n\n")), new Run("WebGL/WebGPU performance is locked at a stable 60 FPS under massive parallel draw call buffers.") }
                }
            }
        };

        tabViewContainer.TabItems.Add(tabItem1);
        tabViewContainer.TabItems.Add(tabItem2);

        int nextTabId = 3;
        tabViewContainer.TabAddRequested += (s, e) =>
        {
            var newTab = new TabViewItem($"New Tab #{nextTabId}")
            {
                Content = new Border
                {
                    Background = new SolidColorBrush(0x13131AFF),
                    CornerRadius = 8f,
                    Padding = new Thickness(20),
                    Child = new RichTextBlock
                    {
                        Font = _font,
                        FontSize = 14f,
                        Inlines = { new Bold(new Run($"Active Dynamic Tab Room #{nextTabId}\n\n")), new Run("TabView leverages viewport virtualization logic to dynamically balance graphics render loads.") }
                    }
                }
            };
            nextTabId++;
            tabViewContainer.TabItems.Add(newTab);
            tabViewContainer.SelectedItem = newTab;
        };

        var pivotItem3 = new PivotItem("TabView Dynamic Pages", tabViewContainer);
        pivot.Items.Add(pivotItem3);

        grid.AddChild(pivot);
        ProGPU.WinUI.Grid.SetRow(pivot, 1);

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
            Width = 320f, 
            Height = 180f 
        };
        richEntry.Inlines.Add(new Run("Drag mouse to select text range!\nUse "));
        richEntry.Inlines.Add(new Bold(new Run("Ctrl+B (Bold)")));
        richEntry.Inlines.Add(new Run(", "));
        richEntry.Inlines.Add(new Italic(new Run("Ctrl+I (Italic)")));
        richEntry.Inlines.Add(new Run(", or "));
        richEntry.Inlines.Add(new Underline(new Run("Ctrl+U (Underline)")));
        richEntry.Inlines.Add(new Run(" to toggle style, or type over selection."));
        leftStack.AddChild(richEntry);

        // Formatting & Actions Buttons row (Undo, Redo, Bold, Italic, Underline, Copy, Paste)
        var actionBtns1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        
        var undoBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
        undoBtn.Content = new TextVisual { Text = "Undo", FontSize = 11f, Brush = new SolidColorBrush(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        undoBtn.Click += (s, e) => richEntry.Undo();

        var redoBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
        redoBtn.Content = new TextVisual { Text = "Redo", FontSize = 11f, Brush = new SolidColorBrush(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        redoBtn.Click += (s, e) => richEntry.Redo();

        var boldBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
        boldBtn.Content = new TextVisual { Text = "Bold", FontSize = 11f, Brush = new SolidColorBrush(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        boldBtn.Click += (s, e) => richEntry.ToggleStyle("bold");

        var italicBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
        italicBtn.Content = new TextVisual { Text = "Italic", FontSize = 11f, Brush = new SolidColorBrush(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        italicBtn.Click += (s, e) => richEntry.ToggleStyle("italic");

        var underlineBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f };
        underlineBtn.Content = new TextVisual { Text = "Underline", FontSize = 11f, Brush = new SolidColorBrush(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        underlineBtn.Click += (s, e) => richEntry.ToggleStyle("underline");

        actionBtns1.AddChild(undoBtn);
        actionBtns1.AddChild(redoBtn);
        actionBtns1.AddChild(boldBtn);
        actionBtns1.AddChild(italicBtn);
        actionBtns1.AddChild(underlineBtn);
        leftStack.AddChild(actionBtns1);

        var actionBtns2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        
        var copyBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
        copyBtn.Content = new TextVisual { Text = "Copy", FontSize = 11f, Brush = new SolidColorBrush(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        copyBtn.Click += (s, e) => richEntry.Copy();

        var cutBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
        cutBtn.Content = new TextVisual { Text = "Cut", FontSize = 11f, Brush = new SolidColorBrush(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cutBtn.Click += (s, e) => richEntry.Cut();

        var pasteBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f };
        pasteBtn.Content = new TextVisual { Text = "Paste", FontSize = 11f, Brush = new SolidColorBrush(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        pasteBtn.Click += (s, e) => richEntry.Paste();

        actionBtns2.AddChild(copyBtn);
        actionBtns2.AddChild(cutBtn);
        actionBtns2.AddChild(pasteBtn);
        leftStack.AddChild(actionBtns2);

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
        listDesc.Inlines.Add(new Run(", and click rows to change selected indices. Double-click any cell (or press Enter on selection) to "));
        listDesc.Inlines.Add(new Bold(new Run("edit inline")) { Foreground = new SolidColorBrush(0x0078D4FF) });
        listDesc.Inlines.Add(new Run(". Press Enter to commit or Escape to cancel."));
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
        dataGrid.Columns.Add(new DataGridColumn("Latency (ms)", 120f, "Latency"));

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
                    "Latency" => $"{log.Latency:F1}",
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

    private static FrameworkElement CreateMotionAnimationsView()
    {
        var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
        grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Showcase cards

        var descText = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
        descText.Inlines.Add(new Run("This page showcases modern high-performance GPU-accelerated motion and composition animations, including keyframe loops, spring wobbles, and dynamic expressions."));
        grid.AddChild(descText);
        ProGPU.WinUI.Grid.SetRow(descText, 0);

        var cardsGrid = new ProGPU.WinUI.Grid();
        cardsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        cardsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        cardsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        var keyframeCard = new KeyframeShowcaseCard(_font!);
        cardsGrid.AddChild(keyframeCard);
        ProGPU.WinUI.Grid.SetColumn(keyframeCard, 0);

        var springCard = new SpringWobbleShowcaseCard(_font!);
        cardsGrid.AddChild(springCard);
        ProGPU.WinUI.Grid.SetColumn(springCard, 1);

        var expressionCard = new ExpressionTrackingShowcaseCard(_font!);
        cardsGrid.AddChild(expressionCard);
        ProGPU.WinUI.Grid.SetColumn(expressionCard, 2);

        grid.AddChild(cardsGrid);
        ProGPU.WinUI.Grid.SetRow(cardsGrid, 1);

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
        _rootGrid?.UpdateAnimations((float)delta);
        _rootGrid?.UpdateSampleAnimations((float)delta);
        _rootGrid?.Invalidate();

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

        OnWindowUpdate(delta);

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

    private static FrameworkElement CreateAdvancedControlsView()
    {
        var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new GridLength(50f, GridUnitType.Absolute));   // Header description
        grid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Showcase column grids

        var descText = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
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

        var header1 = new RichTextBlock { Font = _font, FontSize = 16f, Margin = new Thickness(0f, 0f, 0f, 16f) };
        header1.Inlines.Add(new Bold(new Run("ContentDialog & ToolTips")));
        col1Stack.AddChild(header1);

        var dialogResultText = new RichTextBlock { Font = _font, FontSize = 12f, Foreground = new SolidColorBrush(0xFFFFFF80), Margin = new Thickness(0f, 0f, 0f, 12f) };
        dialogResultText.Inlines.Add(new Run("Last Dialog Response: None"));

        var triggerDialogBtnText = new RichTextBlock { Font = _font, FontSize = 12f, Foreground = new SolidColorBrush(0xFFFFFFFF) };
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
        var tooltipDesc = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0f, 16f, 0f, 8f) };
        tooltipDesc.Inlines.Add(new Run("Hover these buttons for 500ms to test ToolTips:"));
        col1Stack.AddChild(tooltipDesc);

        var tipBtn1Text = new RichTextBlock { Font = _font, FontSize = 12f, Foreground = new SolidColorBrush(0xFFFFFFFF) };
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

        var tipBtn2Text = new RichTextBlock { Font = _font, FontSize = 12f, Foreground = new SolidColorBrush(0xFFFFFFFF) };
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

        var header2 = new RichTextBlock { Font = _font, FontSize = 16f, Margin = new Thickness(0f, 0f, 0f, 16f) };
        header2.Inlines.Add(new Bold(new Run("Calendar & Date Selection")));
        col2Stack.AddChild(header2);

        // DatePicker input dropdown trigger
        var datePickerDesc = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
        datePickerDesc.Inlines.Add(new Run("Dropdown DatePicker selector:"));
        col2Stack.AddChild(datePickerDesc);

        var datePicker = new DatePicker { Header = "Select Frame Target", Margin = new Thickness(0f, 0f, 0f, 20f) };
        col2Stack.AddChild(datePicker);

        // Standalone calendar view grid
        var calendarDesc = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
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

        var header3 = new RichTextBlock { Font = _font, FontSize = 16f, Margin = new Thickness(0f, 0f, 0f, 16f) };
        header3.Inlines.Add(new Bold(new Run("Progress Status Loaders")));
        col3Stack.AddChild(header3);

        // Determinate progress section
        var detDesc = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
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
        var indetDesc = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
        indetDesc.Inlines.Add(new Run("Indeterminate sliding ProgressBar track:"));
        col3Stack.AddChild(indetDesc);

        var indetBar = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0f, 0f, 0f, 24f) };
        col3Stack.AddChild(indetBar);

        var ringDesc = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0f, 0f, 0f, 8f) };
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

// ==========================================
// Motion & Animation System Classes
// ==========================================

public interface IAnimatedElement
{
    void Update(float delta);
}

public static class VisualExtensions
{
    public static void UpdateSampleAnimations(this Visual visual, float delta)
    {
        if (visual == null) return;

        if (visual is IAnimatedElement animated)
        {
            animated.Update(delta);
        }

        if (visual is ContainerVisual container)
        {
            int count = container.Children.Count;
            for (int i = 0; i < count; i++)
            {
                if (i < container.Children.Count)
                {
                    container.Children[i].UpdateSampleAnimations(delta);
                }
            }
        }
    }
}

public class SpringScalarNaturalMotionAnimation
{
    public float TargetValue { get; set; }
    public float CurrentValue { get; set; }
    public float Velocity { get; set; }
    public float Stiffness { get; set; } = 150f;
    public float Damping { get; set; } = 15f;
    public float Mass { get; set; } = 1f;

    public void Update(float delta)
    {
        if (delta > 0.1f) delta = 0.1f;

        float force = -Stiffness * (CurrentValue - TargetValue) - Damping * Velocity;
        float acceleration = force / Mass;
        Velocity += acceleration * delta;
        CurrentValue += Velocity * delta;
    }
}

public class ExpressionAnimation
{
    private readonly Func<float> _expression;

    public ExpressionAnimation(Func<float> expression)
    {
        _expression = expression;
    }

    public float Evaluate() => _expression();
}

public class KeyframeAnimation<T>
{
    public List<(float Key, T Value)> Keyframes { get; } = new();
    public float Duration { get; set; } = 1f;
    public bool Loop { get; set; } = true;
    public float Time { get; set; }

    public void Update(float delta)
    {
        Time += delta;
        if (Time > Duration)
        {
            if (Loop)
            {
                Time %= Duration;
            }
            else
            {
                Time = Duration;
            }
        }
    }

    public T Evaluate(Func<T, T, float, T> interpolator)
    {
        if (Keyframes.Count == 0) return default!;
        if (Keyframes.Count == 1) return Keyframes[0].Value;

        float normalizedTime = Time / Duration;

        int nextIndex = 0;
        while (nextIndex < Keyframes.Count && Keyframes[nextIndex].Key < normalizedTime)
        {
            nextIndex++;
        }

        if (nextIndex == 0) return Keyframes[0].Value;
        if (nextIndex >= Keyframes.Count) return Keyframes[Keyframes.Count - 1].Value;

        var prev = Keyframes[nextIndex - 1];
        var next = Keyframes[nextIndex];

        float segmentDuration = next.Key - prev.Key;
        float t = segmentDuration > 0 ? (normalizedTime - prev.Key) / segmentDuration : 0f;

        return interpolator(prev.Value, next.Value, t);
    }
}

public class KeyframeShowcaseCard : Border, IAnimatedElement
{
    private readonly TtfFont _font;
    private readonly KeyframeAnimation<Vector2> _offsetAnimation;
    private readonly KeyframeAnimation<float> _opacityAnimation;
    private readonly KeyframeAnimation<float> _rotationAnimation;

    private readonly Border _slidingCard;
    private readonly RichTextBlock _fadingText;
    private readonly GearVisual _gearVisual;

    public KeyframeShowcaseCard(TtfFont font)
    {
        _font = font;
        Background = new SolidColorBrush(0xFFFFFF08);
        BorderBrush = new SolidColorBrush(0xFFFFFF15);
        BorderThickness = new Thickness(1f);
        CornerRadius = 8f;
        Padding = new Thickness(12);
        Margin = new Thickness(6);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var title = new RichTextBlock { Font = font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("Keyframe Showcase")));
        stack.AddChild(title);

        var desc = new RichTextBlock { Font = font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Looping scalar/vector translations. Notice the sliding offset card, fading opacity text, and spinning gear."));
        stack.AddChild(desc);

        // 1. Sliding card
        var slidingContainer = new Border
        {
            Height = 80f,
            Background = new SolidColorBrush(0x0C0C12FF),
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 15),
            Padding = new Thickness(8)
        };
        var canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _slidingCard = new Border
        {
            Width = 60f,
            Height = 40f,
            Background = new SolidColorBrush(0x0078D4FF),
            CornerRadius = 4f
        };
        var slidingText = new RichTextBlock { Font = font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        slidingText.Inlines.Add(new Bold(new Run("Slide")));
        _slidingCard.Child = slidingText;
        canvas.AddChild(_slidingCard);
        slidingContainer.Child = canvas;
        stack.AddChild(slidingContainer);

        // 2. Fading opacity text
        var fadingContainer = new Border
        {
            Height = 50f,
            Background = new SolidColorBrush(0x0C0C12FF),
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 15),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _fadingText = new RichTextBlock
        {
            Font = font,
            FontSize = 14f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _fadingText.Inlines.Add(new Bold(new Run("FADING OPACITY TEXT")));
        fadingContainer.Child = _fadingText;
        stack.AddChild(fadingContainer);

        // 3. Spinning Gear
        var gearContainer = new Border
        {
            Height = 120f,
            Background = new SolidColorBrush(0x0C0C12FF),
            CornerRadius = 6f,
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _gearVisual = new GearVisual
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        gearContainer.Child = _gearVisual;
        stack.AddChild(gearContainer);

        Child = stack;

        // Initialize keyframes
        _offsetAnimation = new KeyframeAnimation<Vector2> { Duration = 3f, Loop = true };
        _offsetAnimation.Keyframes.Add((0f, new Vector2(10f, 10f)));
        _offsetAnimation.Keyframes.Add((0.25f, new Vector2(120f, 10f)));
        _offsetAnimation.Keyframes.Add((0.5f, new Vector2(120f, 30f)));
        _offsetAnimation.Keyframes.Add((0.75f, new Vector2(10f, 30f)));
        _offsetAnimation.Keyframes.Add((1f, new Vector2(10f, 10f)));

        _opacityAnimation = new KeyframeAnimation<float> { Duration = 2.5f, Loop = true };
        _opacityAnimation.Keyframes.Add((0f, 0.1f));
        _opacityAnimation.Keyframes.Add((0.5f, 1.0f));
        _opacityAnimation.Keyframes.Add((1f, 0.1f));

        _rotationAnimation = new KeyframeAnimation<float> { Duration = 4f, Loop = true };
        _rotationAnimation.Keyframes.Add((0f, 0f));
        _rotationAnimation.Keyframes.Add((0.5f, (float)Math.PI));
        _rotationAnimation.Keyframes.Add((1f, (float)(Math.PI * 2f)));
    }

    public void Update(float delta)
    {
        _offsetAnimation.Update(delta);
        _opacityAnimation.Update(delta);
        _rotationAnimation.Update(delta);

        Vector2 currentOffset = _offsetAnimation.Evaluate((a, b, t) => Vector2.Lerp(a, b, t));
        Canvas.SetLeft(_slidingCard, currentOffset.X);
        Canvas.SetTop(_slidingCard, currentOffset.Y);

        float currentOpacity = _opacityAnimation.Evaluate((a, b, t) => a + (b - a) * t);
        _fadingText.Opacity = currentOpacity;

        float currentRotation = _rotationAnimation.Evaluate((a, b, t) => a + (b - a) * t);
        _gearVisual.GearRotation = currentRotation;

        Invalidate();
    }
}

public class SpringWobbleShowcaseCard : Border, IAnimatedElement
{
    private readonly SpringScalarNaturalMotionAnimation _springX;
    private readonly SpringScalarNaturalMotionAnimation _springY;
    private readonly Button _triggerBtn;
    private readonly Border _wobbleCard;

    public SpringWobbleShowcaseCard(TtfFont font)
    {
        _springX = new SpringScalarNaturalMotionAnimation
        {
            CurrentValue = 1.0f,
            TargetValue = 1.0f,
            Stiffness = 180f,
            Damping = 10f,
            Mass = 1.0f
        };
        _springY = new SpringScalarNaturalMotionAnimation
        {
            CurrentValue = 1.0f,
            TargetValue = 1.0f,
            Stiffness = 180f,
            Damping = 10f,
            Mass = 1.0f
        };

        Background = new SolidColorBrush(0xFFFFFF08);
        BorderBrush = new SolidColorBrush(0xFFFFFF15);
        BorderThickness = new Thickness(1f);
        CornerRadius = 8f;
        Padding = new Thickness(12);
        Margin = new Thickness(6);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var title = new RichTextBlock { Font = font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("Spring Wobble Showcase")));
        stack.AddChild(title);

        var desc = new RichTextBlock { Font = font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Natural spring mass-damping physics. Click the button to trigger a high-frequency elastic spring wobble on Scale."));
        stack.AddChild(desc);

        var wobbleContainer = new Border
        {
            Height = 150f,
            Background = new SolidColorBrush(0x0C0C12FF),
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 15),
            Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _wobbleCard = new Border
        {
            Width = 120f,
            Height = 80f,
            Background = new SolidColorBrush(0x0078D4FF),
            BorderBrush = new SolidColorBrush(0xFFFFFFFF),
            BorderThickness = new Thickness(1.5f),
            CornerRadius = 10f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var wobbleText = new RichTextBlock { Font = font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        wobbleText.Inlines.Add(new Bold(new Run("WOBBLE ME!")));
        _wobbleCard.Child = wobbleText;
        wobbleContainer.Child = _wobbleCard;
        stack.AddChild(wobbleContainer);

        _triggerBtn = new Button { Width = 150f, Height = 36f, CornerRadius = 6f, HorizontalAlignment = HorizontalAlignment.Center };
        var btnText = new RichTextBlock { Font = font, FontSize = 12f };
        btnText.Inlines.Add(new Bold(new Run("Trigger Spring")));
        _triggerBtn.Content = btnText;

        _triggerBtn.Click += (s, e) =>
        {
            _springX.CurrentValue = 0.5f;
            _springY.CurrentValue = 1.5f;
            _springX.TargetValue = 1.0f;
            _springY.TargetValue = 1.0f;
            _springX.Velocity = 20f;
            _springY.Velocity = -20f;
        };
        stack.AddChild(_triggerBtn);

        Child = stack;
    }

    public void Update(float delta)
    {
        _springX.Update(delta);
        _springY.Update(delta);

        Vector2 size = _wobbleCard.Size;
        Vector2 center = size / 2f;

        float sx = _springX.CurrentValue;
        float sy = _springY.CurrentValue;

        sx = Math.Max(0.1f, Math.Min(3.0f, sx));
        sy = Math.Max(0.1f, Math.Min(3.0f, sy));

        var transform = Matrix4x4.CreateTranslation(-center.X, -center.Y, 0)
                        * Matrix4x4.CreateScale(sx, sy, 1f)
                        * Matrix4x4.CreateTranslation(center.X, center.Y, 0);
        _wobbleCard.Transform = transform;
    }
}

public class ExpressionTrackingShowcaseCard : Border, IAnimatedElement
{
    private readonly ProGPU.WinUI.Slider _slider;
    private readonly ExpressionAnimation _scaleExpression;
    private readonly ExpressionAnimation _rotationExpression;
    private readonly Border _trackingCard;

    public ExpressionTrackingShowcaseCard(TtfFont font)
    {
        Background = new SolidColorBrush(0xFFFFFF08);
        BorderBrush = new SolidColorBrush(0xFFFFFF15);
        BorderThickness = new Thickness(1f);
        CornerRadius = 8f;
        Padding = new Thickness(12);
        Margin = new Thickness(6);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var title = new RichTextBlock { Font = font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("Expression Showcase")));
        stack.AddChild(title);

        var desc = new RichTextBlock { Font = font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Dynamic ExpressionAnimation binding. Move the slider below to drive the card's Scale and Rotation in real time."));
        stack.AddChild(desc);

        var trackingContainer = new Border
        {
            Height = 150f,
            Background = new SolidColorBrush(0x0C0C12FF),
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 15),
            Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _trackingCard = new Border
        {
            Width = 100f,
            Height = 80f,
            Background = new SolidColorBrush(0x00E5FFFF),
            BorderBrush = new SolidColorBrush(0xFFFFFFFF),
            BorderThickness = new Thickness(1.5f),
            CornerRadius = 10f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var cardText = new RichTextBlock { Font = font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cardText.Inlines.Add(new Bold(new Run("TRACKING")));
        _trackingCard.Child = cardText;
        trackingContainer.Child = _trackingCard;
        stack.AddChild(trackingContainer);

        var sliderTitle = new RichTextBlock { Font = font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 5) };
        sliderTitle.Inlines.Add(new Bold(new Run("Driver Slider: 50%")));
        stack.AddChild(sliderTitle);

        _slider = new ProGPU.WinUI.Slider { Minimum = 0f, Maximum = 100f, Value = 50f, Width = 220f, HorizontalAlignment = HorizontalAlignment.Center };
        _slider.ValueChanged += (s, e) =>
        {
            sliderTitle.Inlines.Clear();
            sliderTitle.Inlines.Add(new Bold(new Run($"Driver Slider: {_slider.Value:F0}%")));
            sliderTitle.Invalidate();
        };
        stack.AddChild(_slider);

        Child = stack;

        _scaleExpression = new ExpressionAnimation(() => 0.6f + (_slider.Value / 100f) * 0.8f);
        _rotationExpression = new ExpressionAnimation(() => (_slider.Value / 100f) * (float)Math.PI * 2f);
    }

    public void Update(float delta)
    {
        float scale = _scaleExpression.Evaluate();
        float rotation = _rotationExpression.Evaluate();

        Vector2 size = _trackingCard.Size;
        Vector2 center = size / 2f;

        var transform = Matrix4x4.CreateTranslation(-center.X, -center.Y, 0)
                        * Matrix4x4.CreateScale(scale, scale, 1f)
                        * Matrix4x4.CreateRotationZ(rotation)
                        * Matrix4x4.CreateTranslation(center.X, center.Y, 0);
        _trackingCard.Transform = transform;
    }


}

public class GearVisual : FrameworkElement
{
    public float GearRotation { get; set; }

    public GearVisual()
    {
        Width = 100f;
        Height = 100f;
    }

    public static PathGeometry CreateGearPathWithRotation(Vector2 center, float innerRadius, float outerRadius, int teethCount, float toothDepth, float rotation)
    {
        var path = new PathGeometry();
        var fig = new PathFigure { IsClosed = true, IsFilled = true };

        float angleStep = (float)(Math.PI * 2.0 / teethCount);

        for (int i = 0; i < teethCount; i++)
        {
            float angle = i * angleStep + rotation;

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
            float a = -(float)(i * Math.PI * 2.0 / circleSegments) + rotation;
            Vector2 pt = center + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * cutRadius;

            if (i == 0)
                cutoutFig.StartPoint = pt;
            else
                cutoutFig.Segments.Add(new LineSegment(pt));
        }
        path.Figures.Add(cutoutFig);

        return path;
    }

    public override void OnRender(DrawingContext context)
    {
        Vector2 center = Size / 2f;
        if (center.X <= 0 || center.Y <= 0) return;

        context.DrawRectangle(new SolidColorBrush(0x00000000), null, new Rect(Vector2.Zero, Size));

        var p = CreateGearPathWithRotation(center, 25f, 40f, 10, 8f, GearRotation);
        context.DrawPath(new SolidColorBrush(0x0078D4FF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p);
    }
}

public static class DialogPresenter
{
    public static void ShowResetDialog(RichTextBlock dialogResultText)
    {
        ShowAsyncAndCallback(dialogResultText);
    }

    private static async void ShowAsyncAndCallback(RichTextBlock dialogResultText)
    {
        var dialog = new ContentDialog
        {
            Title = "Perform Critical Diagnostics Reset?",
            Content = "Are you sure you want to completely flush the active GPU layout caches? This resets frame counters.",
            PrimaryButtonText = "Flush Cache",
            SecondaryButtonText = "Cancel"
        };
        var res = await dialog.ShowAsync();
        dialogResultText.Inlines.Clear();
        var run = new Run { Text = "Last Dialog Response: " + res.ToString() };
        dialogResultText.Inlines.Add(run);
        dialogResultText.Invalidate();
    }
}


