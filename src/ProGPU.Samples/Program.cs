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

    private static IWindow? _devToolsWindow;
    private static WgpuContext? _devToolsWgpuContext;
    private static Compositor? _devToolsCompositor;

    private static TtfFont? _font;
    private static ProGPU.WinUI.Grid? _rootGrid;
    private static ProGPU.WinUI.Grid? _topLevelGrid;
    private static ProGPU.WinUI.DevTools? _devToolsPanel;

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

        _window = Silk.NET.Windowing.Window.Create(options);

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
            Background = ThemeManager.GetBrush("HeaderBackground"), // Dynamic theme Mica backdrop
            BorderBrush = ThemeManager.GetBrush("ControlBorder"), // Thin dynamic border outline
            BorderThickness = new Thickness(0, 0, 0, 1f),
            Padding = new Thickness(20, 10, 20, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var headerGrid = new ProGPU.WinUI.Grid();
        headerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        headerGrid.ColumnDefinitions.Add(new GridLength(120f, GridUnitType.Absolute));
        headerGrid.ColumnDefinitions.Add(new GridLength(300f, GridUnitType.Absolute));

        var titleText = new RichTextBlock { Font = _font, FontSize = 20f, VerticalAlignment = VerticalAlignment.Center };
        var logoRun = new Run("Pro") { Foreground = ThemeManager.GetBrush("SystemAccentColor") };
        titleText.Inlines.Add(new Bold(logoRun));
        titleText.Inlines.Add(new Bold(new Run("GPU WinUI Gallery")));
        headerGrid.AddChild(titleText);
        ProGPU.WinUI.Grid.SetColumn(titleText, 0);

        // Sun/Moon dynamic theme selector toggle button
        var themeBtn = new Button
        {
            Width = 100f,
            Height = 32f,
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var themeBtnText = new RichTextBlock { Font = _font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var themeRun = new Run("🌙 Dark");
        themeBtnText.Inlines.Add(new Bold(themeRun));
        themeBtn.Content = themeBtnText;

        themeBtn.Click += (s, e) =>
        {
            if (ThemeManager.CurrentTheme == ElementTheme.Dark)
            {
                ThemeManager.CurrentTheme = ElementTheme.Light;
                themeRun.Text = "☀️ Light";
            }
            else
            {
                ThemeManager.CurrentTheme = ElementTheme.Dark;
                themeRun.Text = "🌙 Dark";
            }
        };
        headerGrid.AddChild(themeBtn);
        ProGPU.WinUI.Grid.SetColumn(themeBtn, 1);

        var subtitleText = new RichTextBlock 
        { 
            Font = _font, 
            FontSize = 11f, 
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };
        subtitleText.Inlines.Add(new Run(".NET 10 cross-platform high-performance engine showcase"));
        headerGrid.AddChild(subtitleText);
        ProGPU.WinUI.Grid.SetColumn(subtitleText, 2);

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
        var compositorItem = new NavigationViewItem("Compositor API", "🎨", CreateCompositorShowcaseView());
        var splitViewItem = new NavigationViewItem("SplitView Layout", "🪟", CreateSplitViewShowcaseView());
        var imageRepeatItem = new NavigationViewItem("Image & Buttons", "🖼️", CreateImageRepeatShowcaseView());
        var drawingContextItem = new NavigationViewItem("Drawing Context", "📐", SamplePagePresenter.CreateDrawingContextShowcaseView());
        var fileStorageItem = new NavigationViewItem("File Storage", "📁", SamplePagePresenter.CreateFileStorageShowcaseView());
        var stylesShowcaseItem = new NavigationViewItem("Styles Showcase", "💅", SamplePagePresenter.CreateStylesShowcaseView());
        var motionMarkItem = new NavigationViewItem("MotionMark Showcase", "🏁", SamplePagePresenter.CreateMotionMarkShowcaseView());

        _navigationView.MenuItems.Add(basicInputItem);
        _navigationView.MenuItems.Add(panelsItem);
        _navigationView.MenuItems.Add(textItem);
        _navigationView.MenuItems.Add(dataItem);
        _navigationView.MenuItems.Add(computeItem);
        _navigationView.MenuItems.Add(motionAnimationsItem);
        _navigationView.MenuItems.Add(advancedItem);
        _navigationView.MenuItems.Add(compositorItem);
        _navigationView.MenuItems.Add(splitViewItem);
        _navigationView.MenuItems.Add(imageRepeatItem);
        _navigationView.MenuItems.Add(drawingContextItem);
        _navigationView.MenuItems.Add(fileStorageItem);
        _navigationView.MenuItems.Add(stylesShowcaseItem);
        _navigationView.MenuItems.Add(motionMarkItem);

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
            Background = ThemeManager.GetBrush("HeaderBackground"), // Deep dark/light status bar
            BorderBrush = ThemeManager.GetBrush("ControlBorder"), // Thin boundary stroke
            BorderThickness = new Thickness(0, 1f, 0, 0),
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _statsText = new RichTextBlock
        {
            FontSize = 11f,
            Foreground = ThemeManager.GetBrush("TextSecondary"),
            Font = _font,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statsText.Inlines.Add(new Run("FPS: -- | CPU: -- ms | Cursor: (0, 0) | Focused Element: None"));
        statusBar.Child = _statsText;
        _rootGrid.AddChild(statusBar);
        ProGPU.WinUI.Grid.SetRow(statusBar, 2);

        // Track global ThemeManager theme change event
        ThemeManager.ThemeChanged += () =>
        {
            statusBar.Background = ThemeManager.GetBrush("HeaderBackground");
            statusBar.BorderBrush = ThemeManager.GetBrush("ControlBorder");
            _statsText.Foreground = ThemeManager.GetBrush("TextSecondary");
            headerBar.Background = ThemeManager.GetBrush("HeaderBackground");
            headerBar.BorderBrush = ThemeManager.GetBrush("ControlBorder");
            logoRun.Foreground = ThemeManager.GetBrush("SystemAccentColor");
            _rootGrid.Invalidate();
        };

        // 5. TOP LEVEL CONTAINER GRID (App container)
        _topLevelGrid = new ProGPU.WinUI.Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _topLevelGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        _topLevelGrid.ColumnDefinitions.Add(new GridLength(0f, GridUnitType.Absolute)); // Kept collapsed always

        _topLevelGrid.AddChild(_rootGrid);
        ProGPU.WinUI.Grid.SetColumn(_rootGrid, 0);

        _devToolsPanel = new ProGPU.WinUI.DevTools
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        DevToolsService.StateChanged += (s, ev) =>
        {
            if (DevToolsService.IsDevToolsActive)
            {
                OpenDevToolsWindow();
            }
            else
            {
                CloseDevToolsWindow();
            }
        };
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
            FontSize = 11.5f, 
            ColumnCount = 2, 
            ColumnGap = 22f,
            Height = 330f
        };
        
        flowDoc.Blocks.Add(new Paragraph(
            new Bold(new Run("GPU Substrate Typography\n")),
            new Run("The new text layout is powered by real-time SDF atlas packing, producing extremely sharp vector paths with synthetic "),
            new Bold(new Run("bold")),
            new Run(", "),
            new Italic(new Run("italic")),
            new Run(", and "),
            new Bold(new Italic(new Run("bold-italic obliques"))),
            new Run(" rendering seamlessly.")
        ));

        // Add a clean bullet list block
        var bulletList = new ListBlock { IsOrdered = false, Indentation = 18f };
        bulletList.Items.Add(new ListItem(new Run("GPU-accelerated text layout")));
        bulletList.Items.Add(new ListItem(new Run("Flow balance across column paths")));
        bulletList.Items.Add(new ListItem(new Run("Crisp vector borders and tables")));
        flowDoc.Blocks.Add(bulletList);

        flowDoc.Blocks.Add(new Paragraph(
            new Italic(new Run("Flow-Balanced Columns:\n")),
            new Run("Text flows between columns automatically, managing margins, alignment bounds, and paragraphs dynamically.")
        ));

        // Add a beautiful structured vector table
        var table = new Table
        {
            CellPadding = 5f,
            BorderThickness = 1f,
            BorderBrush = new SolidColorBrush(0xFFFFFF25),
            ColumnWidths = new List<float> { 70f, 100f }
        };

        // Table Header
        var headerRow = new TableRow(
            new TableCell(new Bold(new Run("Metric"))) { Background = new SolidColorBrush(0xFFFFFF15) },
            new TableCell(new Bold(new Run("Compositor Value"))) { Background = new SolidColorBrush(0xFFFFFF15) }
        );
        table.Rows.Add(headerRow);

        // Table Rows
        table.Rows.Add(new TableRow(
            new TableCell(new Run("FPS")),
            new TableCell(new Bold(new Run("60.0 fps")))
        ));
        table.Rows.Add(new TableRow(
            new TableCell(new Run("Shaders")),
            new TableCell(new Italic(new Run("SDF WebGPU")))
        ));

        flowDoc.Blocks.Add(table);

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
        if (_window == null || _topLevelGrid == null) return;

        var input = _window.CreateInput();
        
        // Initialize WinUI Input Routing System with top level container grid scene node
        InputSystem.Initialize(input, _topLevelGrid);

        // Bubble-up PointerMoved coordinate tracking in top level container grid status
        _topLevelGrid.PointerMoved += (sender, args) =>
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
        if (_rootGrid == null || _topLevelGrid == null || _wgpuContext == null || _window == null) return;
        if (_screenCompositor == null || _offscreenCompositor == null || _compute == null) return;

        OnWindowUpdate(delta);

        if (_devToolsWindow != null && _devToolsWindow.IsInitialized)
        {
            _devToolsWindow.DoEvents();
            _devToolsWindow.DoUpdate();
            _devToolsWindow.DoRender();
        }

        _frameStopwatch.Restart();

        // 1. Size negotiation: Measure & Arrange entire WinUI graph
        _topLevelGrid.Measure(new Vector2(_window.Size.X, _window.Size.Y));
        _topLevelGrid.Arrange(new Rect(0, 0, _window.Size.X, _window.Size.Y));

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
            _screenCompositor.RenderScene(_topLevelGrid, (uint)_window.Size.X, (uint)_window.Size.Y, targetView);
            
            _wgpuContext.Wgpu.SurfacePresent(_wgpuContext.Surface);
            _wgpuContext.Wgpu.TextureViewRelease(targetView);
        }
    }

    private static void OnWindowResize(Vector2D<int> newSize)
    {
        if (_wgpuContext == null) return;
        _wgpuContext.ConfigureSwapChain((uint)newSize.X, (uint)newSize.Y);
        _topLevelGrid?.Invalidate();
    }

    private static void OpenDevToolsWindow()
    {
        if (_devToolsWindow != null) return;

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(850, 600);
        options.Title = "ProGPU Developer Tools";
        options.API = GraphicsAPI.None;

        _devToolsWindow = Silk.NET.Windowing.Window.Create(options);

        _devToolsWindow.Load += OnDevToolsWindowLoad;
        _devToolsWindow.Render += OnDevToolsWindowRender;
        _devToolsWindow.Resize += OnDevToolsWindowResize;
        _devToolsWindow.Closing += OnDevToolsWindowClosing;

        _devToolsWindow.Initialize();
    }

    private static void OnDevToolsWindowLoad()
    {
        if (_devToolsWindow == null) return;

        _devToolsWgpuContext = new WgpuContext();
        _devToolsWgpuContext.Initialize(_devToolsWindow);

        _devToolsCompositor = new Compositor(_devToolsWgpuContext, _devToolsWgpuContext.SwapChainFormat);

        var inputContext = _devToolsWindow.CreateInput();
        DevToolsInputSystem.Initialize(inputContext, _devToolsPanel!);

        _devToolsPanel?.RefreshVisualTree();
    }

    private static void OnDevToolsWindowRender(double delta)
    {
        if (_devToolsWindow == null || _devToolsWgpuContext == null || _devToolsCompositor == null || _devToolsPanel == null) return;

        _devToolsPanel.Measure(new Vector2(_devToolsWindow.Size.X, _devToolsWindow.Size.Y));
        _devToolsPanel.Arrange(new Rect(0, 0, _devToolsWindow.Size.X, _devToolsWindow.Size.Y));

        if (_screenCompositor != null)
        {
            uint totalVertices = (uint)(_screenCompositor.VectorVertexCount + _screenCompositor.TextVertexCount + _screenCompositor.TextureVertexCount);
            uint totalDrawCalls = (uint)(_screenCompositor.TextureDrawCallCount + 1);
            _devToolsPanel.UpdatePerfPanel((float)_currentFps, (float)_cpuFrameTimeMs, totalVertices, totalDrawCalls);
        }

        TextureView* targetView = null;
        if (_devToolsWgpuContext.Surface != null)
        {
            var surfaceTexture = new SurfaceTexture();
            _devToolsWgpuContext.Wgpu.SurfaceGetCurrentTexture(_devToolsWgpuContext.Surface, &surfaceTexture);
            
            if (surfaceTexture.Status == SurfaceGetCurrentTextureStatus.Success)
            {
                var viewDesc = new TextureViewDescriptor
                {
                    Format = _devToolsWgpuContext.SwapChainFormat,
                    Dimension = TextureViewDimension.Dimension2D,
                    BaseMipLevel = 0,
                    MipLevelCount = 1,
                    BaseArrayLayer = 0,
                    ArrayLayerCount = 1,
                    Aspect = TextureAspect.All
                };
                targetView = _devToolsWgpuContext.Wgpu.TextureCreateView(surfaceTexture.Texture, &viewDesc);
            }
        }

        if (targetView != null)
        {
            _devToolsCompositor.RenderScene(_devToolsPanel, (uint)_devToolsWindow.Size.X, (uint)_devToolsWindow.Size.Y, targetView);
            
            _devToolsWgpuContext.Wgpu.SurfacePresent(_devToolsWgpuContext.Surface);
            _devToolsWgpuContext.Wgpu.TextureViewRelease(targetView);
        }
    }

    private static void OnDevToolsWindowResize(Vector2D<int> newSize)
    {
        if (_devToolsWgpuContext == null) return;
        _devToolsWgpuContext.ConfigureSwapChain((uint)newSize.X, (uint)newSize.Y);
        _devToolsPanel?.Invalidate();
    }

    private static void OnDevToolsWindowClosing()
    {
        DevToolsService.IsDevToolsActive = false;
        CloseDevToolsWindow();
    }

    private static void CloseDevToolsWindow()
    {
        if (_devToolsWindow == null) return;

        _devToolsWindow.Close();
        _devToolsWindow.Dispose();
        _devToolsWindow = null;

        _devToolsWgpuContext?.Dispose();
        _devToolsWgpuContext = null;

        _devToolsCompositor = null;
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

    public class GradientArtVisual : FrameworkElement, IAnimatedElement
    {
        private float _time = 0f;
        private readonly PathGeometry _starPath;
        private readonly PathGeometry _blobPath;

        public GradientArtVisual()
        {
            // Parse a beautiful star/gear path and a blob path
            _starPath = PathGeometry.Parse("M 100 10 L 125 70 L 190 75 L 140 120 L 155 185 L 100 150 L 45 185 L 60 120 L 10 75 L 75 70 Z");
            _blobPath = PathGeometry.Parse("M 150 150 C 250 50 350 250 250 250 C 150 250 50 350 150 150 Z");
            
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Height = 220f;
        }

        public void Update(float delta)
        {
            _time += delta;
            Invalidate();
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(0x0C0C12FF), null, new Rect(Vector2.Zero, Size));

            // 1. Overlapping Multi-stop Linear Gradient Card
            var startPt = new Vector2(20f + MathF.Sin(_time) * 10f, 20f);
            var endPt = new Vector2(240f + MathF.Cos(_time) * 10f, 200f);
            var linGrad = new LinearGradientBrush(startPt, endPt, new GradientStop[]
            {
                new GradientStop(new Vector4(1f, 0f, 0.5f, 1f), 0.0f),  // Deep Magenta/Red
                new GradientStop(new Vector4(0f, 0.5f, 1f, 0.8f), 0.5f), // Translucent Bright Blue
                new GradientStop(new Vector4(0f, 1f, 0.5f, 1f), 1.0f)   // Vivid Emerald
            });
            context.DrawRectangle(linGrad, new Pen(new SolidColorBrush(0xFFFFFFFF), 2f), new Rect(20, 20, 220, 180));

            // 2. Overlapping Multi-stop Radial Gradient star path
            var centerPt = new Vector2(100f + MathF.Sin(_time * 2f) * 20f, 100f + MathF.Cos(_time * 2f) * 20f);
            var radGrad = new RadialGradientBrush(centerPt, 80f, new GradientStop[]
            {
                new GradientStop(new Vector4(1f, 0.9f, 0.1f, 1f), 0.0f), // Neon Yellow center
                new GradientStop(new Vector4(1f, 0.4f, 0.0f, 0.9f), 0.5f), // Electric Orange middle
                new GradientStop(new Vector4(0f, 0f, 0f, 0f), 1.0f)      // Transparent outer ring
            });
            
            context.PushClip(new Rect(10, 10, Size.X - 20, Size.Y - 20));
            
            // Draw Radial Gradient star
            context.DrawPath(radGrad, null, _starPath);
            
            // Draw overlapping linear gradient blob
            var blobLin = new LinearGradientBrush(new Vector2(150, 50), new Vector2(250, 250), new GradientStop[]
            {
                new GradientStop(new Vector4(0.2f, 1.0f, 1.0f, 0.8f), 0.0f), // Neon Cyan
                new GradientStop(new Vector4(0.8f, 0.2f, 1.0f, 0.7f), 1.0f)  // Neon Purple
            });
            context.DrawPath(blobLin, new Pen(new SolidColorBrush(0xFFFFFF33), 1.5f), _blobPath);

            context.PopClip();
        }
    }

    public class SpringInteractiveCardWidget : Border, IAnimatedElement
    {
        private readonly SpringScalarNaturalMotionAnimation _springX;
        private readonly SpringScalarNaturalMotionAnimation _springY;
        private readonly Border _widgetCard;

        public SpringInteractiveCardWidget(TtfFont font)
        {
            _springX = new SpringScalarNaturalMotionAnimation
            {
                CurrentValue = 1.0f,
                TargetValue = 1.0f,
                Stiffness = 180f,
                Damping = 12f,
                Mass = 1.0f
            };
            _springY = new SpringScalarNaturalMotionAnimation
            {
                CurrentValue = 1.0f,
                TargetValue = 1.0f,
                Stiffness = 180f,
                Damping = 12f,
                Mass = 1.0f
            };

            Height = 120f;
            Background = new SolidColorBrush(0x0C0C12FF);
            CornerRadius = 6f;
            Padding = new Thickness(12);

            var grid = new ProGPU.WinUI.Grid();
            grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new GridLength(100f, GridUnitType.Absolute));

            var controlsStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            
            var sliderLabel = new RichTextBlock { Font = font, FontSize = 10f, Margin = new Thickness(0, 0, 0, 4) };
            sliderLabel.Inlines.Add(new Run("Spring Scale Multiplier:"));
            controlsStack.AddChild(sliderLabel);

            var slider = new Slider { Minimum = 0.5f, Maximum = 2.0f, Value = 1.0f, Margin = new Thickness(0, 0, 0, 8f) };
            slider.ValueChanged += (s, e) =>
            {
                _springX.TargetValue = slider.Value;
                _springY.TargetValue = slider.Value;
            };
            controlsStack.AddChild(slider);

            var triggerBtn = new Button { Height = 24f, CornerRadius = 4f };
            var btnText = new RichTextBlock { Font = font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            btnText.Inlines.Add(new Bold(new Run("Trigger Wobble")));
            triggerBtn.Content = btnText;
            triggerBtn.Click += (s, e) =>
            {
                _springX.CurrentValue = 0.4f;
                _springY.CurrentValue = 1.8f;
                _springX.Velocity = 15f;
                _springY.Velocity = -15f;
            };
            controlsStack.AddChild(triggerBtn);

            grid.AddChild(controlsStack);
            ProGPU.WinUI.Grid.SetColumn(controlsStack, 0);

            _widgetCard = new Border
            {
                Width = 60f,
                Height = 60f,
                Background = new SolidColorBrush(0x0078D4FF),
                BorderBrush = new SolidColorBrush(0xFFFFFFFF),
                BorderThickness = new Thickness(1.5f),
                CornerRadius = 30f, // Perfect circle!
                HorizontalAlignment = AlignmentCenterHelper(),
                VerticalAlignment = VerticalAlignment.Center
            };
            var widgetText = new RichTextBlock { Font = font, FontSize = 9f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            widgetText.Inlines.Add(new Bold(new Run("COMP")));
            _widgetCard.Child = widgetText;

            grid.AddChild(_widgetCard);
            ProGPU.WinUI.Grid.SetColumn(_widgetCard, 1);

            Child = grid;
        }

        private static HorizontalAlignment AlignmentCenterHelper() => HorizontalAlignment.Center;

        public void Update(float delta)
        {
            _springX.Update(delta);
            _springY.Update(delta);

            Vector2 size = _widgetCard.Size;
            Vector2 center = size / 2f;

            float sx = _springX.CurrentValue;
            float sy = _springY.CurrentValue;

            sx = Math.Max(0.1f, Math.Min(3.0f, sx));
            sy = Math.Max(0.1f, Math.Min(3.0f, sy));

            // Create 2D scaling transform around card center
            var transform = Matrix4x4.CreateTranslation(-center.X, -center.Y, 0)
                            * Matrix4x4.CreateScale(sx, sy, 1f)
                            * Matrix4x4.CreateTranslation(center.X, center.Y, 0);
            _widgetCard.Transform = transform;
        }
    }

    private static FrameworkElement CreateCompositorShowcaseView()
    {
        var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
        grid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Columns

        var descText = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
        descText.Inlines.Add(new Bold(new Run("Composition Subsystem & Multi-Column Document Nesting\n")));
        descText.Inlines.Add(new Run("This page showcases CPU-tessellated multi-stop gradients, dynamic clipping masks, real-time spring transformations, and interactive UI controls seamlessly embedded inline using the FlowDocument InlineUIContainer pipeline."));
        grid.AddChild(descText);
        ProGPU.WinUI.Grid.SetRow(descText, 0);

        var columnsGrid = new ProGPU.WinUI.Grid();
        columnsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        columnsGrid.ColumnDefinitions.Add(new GridLength(1.2f, GridUnitType.Star));

        // COLUMN 0: COMPOSITION & GRADIENT ART
        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
        
        var artCard = new Border
        {
            Background = new SolidColorBrush(0x1F1F24FA),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var artStack = new StackPanel { Orientation = Orientation.Vertical };
        var artHeader = new RichTextBlock { Font = _font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        artHeader.Inlines.Add(new Bold(new Run("High-Performance Tessellated Vector Gradients")));
        artStack.AddChild(artHeader);

        var artVisual = new GradientArtVisual();
        artStack.AddChild(artVisual);
        artCard.Child = artStack;
        leftStack.AddChild(artCard);

        // Spring transform controller card
        var springCard = new Border
        {
            Background = new SolidColorBrush(0x1F1F24FA),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f)
        };
        var springStack = new StackPanel { Orientation = Orientation.Vertical };
        var springHeader = new RichTextBlock { Font = _font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        springHeader.Inlines.Add(new Bold(new Run("Spring & Matrix Composition Transformation")));
        springStack.AddChild(springHeader);

        var springWidget = new SpringInteractiveCardWidget(_font!);
        springStack.AddChild(springWidget);
        springCard.Child = springStack;
        leftStack.AddChild(springCard);

        columnsGrid.AddChild(leftStack);
        ProGPU.WinUI.Grid.SetColumn(leftStack, 0);

        // COLUMN 1: INTERACTIVE FLOW DOCUMENT WITH INLINE WIDGETS
        var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
        
        var docCard = new Border
        {
            Background = new SolidColorBrush(0x1F1F24FA),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var docStack = new StackPanel { Orientation = Orientation.Vertical };
        var docHeader = new RichTextBlock { Font = _font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        docHeader.Inlines.Add(new Bold(new Run("FlowDocument Interactive Nesting")));
        docStack.AddChild(docHeader);

        var flowDoc = new FlowDocument
        {
            Font = _font,
            FontSize = 11.5f,
            ColumnCount = 2,
            ColumnGap = 16f,
            Height = 440f,
            Foreground = new SolidColorBrush(0xDDDDDDFF)
        };

        // Embedded Controls definitions
        var embedBtn = new Button { Width = 80f, Height = 22f, CornerRadius = 4f, Margin = new Thickness(0) };
        embedBtn.Content = new RichTextBlock { Font = _font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ((RichTextBlock)embedBtn.Content).Inlines.Add(new Run("Click Me!"));
        embedBtn.Click += (s, e) => {
            System.Console.WriteLine("Embedded Button Clicked!");
        };

        var embedToggle = new ToggleSwitch { Margin = new Thickness(0), Width = 45f, Height = 20f };
        var embedProgress = new ProgressBar { Minimum = 0f, Maximum = 100f, Value = 65f, Width = 80f, Height = 12f, CornerRadius = 3f };

        var p1 = new Paragraph(
            new Bold(new Run("Inline UI Embedding:\n")),
            new Run("We can embed framework elements directly inside the text streams. For example, a fully functional button: "),
            new InlineUIContainer(embedBtn),
            new Run(" or a live toggle switch control: "),
            new InlineUIContainer(embedToggle),
            new Run(" that layout, measure, wrap, and arrange seamlessly.")
        ) { MarginBottom = 10f, TextAlignment = TextAlignment.Justify };

        var p2 = new Paragraph(
            new Bold(new Run("Document Links & Stats:\n")),
            new Run("This document also flows live progress bars: "),
            new InlineUIContainer(embedProgress),
            new Run(" inline alongside styled runs. Try selecting text, or interact with elements directly! Links can also be clicked, e.g. "),
            new Hyperlink(new Bold(new Run("ProGPU Website"))) { Uri = "https://github.com/wieslawsoltes/ProGPU" },
            new Run(" to visit the repository or trigger routed event bubbles.")
        ) { MarginBottom = 10f, TextAlignment = TextAlignment.Justify };

        flowDoc.Paragraphs.Add(p1);
        flowDoc.Paragraphs.Add(p2);

        var docBorder = new Border
        {
            Background = new SolidColorBrush(0x0C0C12FF),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 6f,
            Child = flowDoc
        };
        docStack.AddChild(docBorder);
        docCard.Child = docStack;
        rightStack.AddChild(docCard);

        columnsGrid.AddChild(rightStack);
        ProGPU.WinUI.Grid.SetColumn(rightStack, 1);

        grid.AddChild(columnsGrid);
        ProGPU.WinUI.Grid.SetRow(columnsGrid, 1);

        return grid;
    }

    private static FrameworkElement CreateSplitViewShowcaseView()
    {
        var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
        grid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Main workspace

        var descText = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
        descText.Inlines.Add(new Bold(new Run("SplitView Responsive Layout Demonstration\n")));
        descText.Inlines.Add(new Run("Demonstrates collapsible navigation side panes with customizable display modes, positioning, and width metrics. Adjust states in real time."));
        grid.AddChild(descText);
        ProGPU.WinUI.Grid.SetRow(descText, 0);

        // Define SplitView
        var splitView = new SplitView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            PaneWidth = 200f,
            CompactPaneLength = 60f,
            IsPaneOpen = true,
            DisplayMode = SplitViewDisplayMode.CompactInline
        };

        // 1. Pane content
        var paneBorder = new Border
        {
            Background = new SolidColorBrush(0x1F1F24FA),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(0, 0, 1f, 0),
            Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var paneStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        var paneHeader = new RichTextBlock { Font = _font, FontSize = 13f, Margin = new Thickness(0, 0, 0, 10) };
        paneHeader.Inlines.Add(new Bold(new Run("Navigation Pane")));
        paneStack.AddChild(paneHeader);

        for (int i = 1; i <= 4; i++)
        {
            var pBtn = new Button
            {
                Width = 180f,
                Height = 32f,
                CornerRadius = 4f,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var btnLabel = new RichTextBlock { Font = _font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            btnLabel.Inlines.Add(new Run($"Pane Option {i}"));
            pBtn.Content = btnLabel;
            paneStack.AddChild(pBtn);
        }
        paneBorder.Child = paneStack;
        splitView.Pane = paneBorder;

        // 2. Main content of SplitView
        var contentGrid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
        contentGrid.ColumnDefinitions.Add(new GridLength(300, GridUnitType.Absolute)); // Controls
        contentGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Preview card

        var ctrlStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
        
        var ctrlTitle = new RichTextBlock { Font = _font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 10) };
        ctrlTitle.Inlines.Add(new Bold(new Run("SplitView Controllers")));
        ctrlStack.AddChild(ctrlTitle);

        // Toggle Pane Button
        var togglePaneBtn = new Button { Width = 200f, Height = 32f, CornerRadius = 6f, Margin = new Thickness(0, 0, 0, 15) };
        var toggleText = new RichTextBlock { Font = _font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        toggleText.Inlines.Add(new Run("Collapse / Expand Pane"));
        togglePaneBtn.Content = toggleText;
        togglePaneBtn.Click += (s, e) =>
        {
            splitView.IsPaneOpen = !splitView.IsPaneOpen;
        };
        ctrlStack.AddChild(togglePaneBtn);

        // ComboBox for DisplayMode
        var modeLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        modeLabel.Inlines.Add(new Run("SplitView DisplayMode:"));
        ctrlStack.AddChild(modeLabel);

        var modeCombo = new ComboBox { Font = _font, Width = 200f, Margin = new Thickness(0, 0, 0, 15) };
        var inlineItem = new ComboBoxItem("Inline");
        var overlayItem = new ComboBoxItem("Overlay");
        var compactInlineItem = new ComboBoxItem("CompactInline");
        var compactOverlayItem = new ComboBoxItem("CompactOverlay");
        modeCombo.Items.Add(inlineItem);
        modeCombo.Items.Add(overlayItem);
        modeCombo.Items.Add(compactInlineItem);
        modeCombo.Items.Add(compactOverlayItem);
        modeCombo.SelectedItem = compactInlineItem;
        
        modeCombo.SelectionChanged += (s, e) =>
        {
            if (modeCombo.SelectedItem != null)
            {
                splitView.DisplayMode = modeCombo.SelectedItem.Text switch
                {
                    "Inline" => SplitViewDisplayMode.Inline,
                    "Overlay" => SplitViewDisplayMode.Overlay,
                    "CompactInline" => SplitViewDisplayMode.CompactInline,
                    "CompactOverlay" => SplitViewDisplayMode.CompactOverlay,
                    _ => SplitViewDisplayMode.Inline
                };
            }
        };
        ctrlStack.AddChild(modeCombo);

        // ComboBox for Placement
        var placeLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        placeLabel.Inlines.Add(new Run("Pane Placement:"));
        ctrlStack.AddChild(placeLabel);

        var placeCombo = new ComboBox { Font = _font, Width = 200f, Margin = new Thickness(0, 0, 0, 15) };
        var leftItem = new ComboBoxItem("Left");
        var rightItem = new ComboBoxItem("Right");
        placeCombo.Items.Add(leftItem);
        placeCombo.Items.Add(rightItem);
        placeCombo.SelectedItem = leftItem;

        placeCombo.SelectionChanged += (s, e) =>
        {
            if (placeCombo.SelectedItem != null)
            {
                splitView.PanePlacement = placeCombo.SelectedItem.Text switch
                {
                    "Left" => PanePlacement.Left,
                    "Right" => PanePlacement.Right,
                    _ => PanePlacement.Left
                };
                paneBorder.BorderThickness = splitView.PanePlacement == PanePlacement.Left ? new Thickness(0, 0, 1f, 0) : new Thickness(1f, 0, 0, 0);
            }
        };
        ctrlStack.AddChild(placeCombo);

        contentGrid.AddChild(ctrlStack);
        ProGPU.WinUI.Grid.SetColumn(ctrlStack, 0);

        // Preview Card
        var previewCard = new Border
        {
            Background = new SolidColorBrush(0x1F1F24FA),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(24f),
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var previewText = new RichTextBlock
        {
            Font = _font,
            FontSize = 13f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        previewText.Inlines.Add(new Bold(new Run("Layout Workspace Canvas\n\n")));
        previewText.Inlines.Add(new Run("Observe how the main Content area dynamically scales and translates depending on the collapsible pane's metrics and display configurations. In overlay modes, the pane hovers above this content without pushing it."));
        previewCard.Child = previewText;
        contentGrid.AddChild(previewCard);
        ProGPU.WinUI.Grid.SetColumn(previewCard, 1);

        splitView.Content = contentGrid;
        grid.AddChild(splitView);
        ProGPU.WinUI.Grid.SetRow(splitView, 1);

        return grid;
    }

    private static int _repeatCount = 0;

    private static FrameworkElement CreateImageRepeatShowcaseView()
    {
        var grid = new ProGPU.WinUI.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
        grid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Main content Grid

        var descText = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
        descText.Inlines.Add(new Bold(new Run("Image Stretching & Button Extensions Showcase\n")));
        descText.Inlines.Add(new Run("Exhibits the uncompressed BMP local loader supporting None, Fill, Uniform, UniformToFill stretch structures, together with high-fidelity RepeatButtons and HyperlinkButtons."));
        grid.AddChild(descText);
        ProGPU.WinUI.Grid.SetRow(descText, 0);

        var contentGrid = new ProGPU.WinUI.Grid();
        contentGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star)); // Left column: Image Stretch
        contentGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star)); // Right column: Buttons

        // COLUMN 0: IMAGE STRETCH CARD
        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
        var imgCard = new Border
        {
            Background = new SolidColorBrush(0x1F1F24FA),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var imgStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        var imgHeader = new RichTextBlock { Font = _font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 10) };
        imgHeader.Inlines.Add(new Bold(new Run("Pure C# BMP Rendering & Stretching")));
        imgStack.AddChild(imgHeader);

        // Instantiate Image control
        var testImage = new Image
        {
            Width = 300f,
            Height = 200f,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 0, 15)
        };

        // Pass _canvasSourceTexture as the fallback source texture
        testImage.Source = _canvasSourceTexture;

        imgStack.AddChild(testImage);

        // ComboBox for Stretch Mode
        var stretchLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        stretchLabel.Inlines.Add(new Run("Image Stretch Mode:"));
        imgStack.AddChild(stretchLabel);

        var stretchCombo = new ComboBox { Font = _font, Width = 200f, Margin = new Thickness(0, 0, 0, 10) };
        var noneItem = new ComboBoxItem("None");
        var fillItem = new ComboBoxItem("Fill");
        var uniformItem = new ComboBoxItem("Uniform");
        var uniformToFillItem = new ComboBoxItem("UniformToFill");
        stretchCombo.Items.Add(noneItem);
        stretchCombo.Items.Add(fillItem);
        stretchCombo.Items.Add(uniformItem);
        stretchCombo.Items.Add(uniformToFillItem);
        stretchCombo.SelectedItem = uniformItem;

        stretchCombo.SelectionChanged += (s, e) =>
        {
            if (stretchCombo.SelectedItem != null)
            {
                testImage.Stretch = stretchCombo.SelectedItem.Text switch
                {
                    "None" => Stretch.None,
                    "Fill" => Stretch.Fill,
                    "Uniform" => Stretch.Uniform,
                    "UniformToFill" => Stretch.UniformToFill,
                    _ => Stretch.Uniform
                };
            }
        };
        imgStack.AddChild(stretchCombo);
        
        imgCard.Child = imgStack;
        leftStack.AddChild(imgCard);
        contentGrid.AddChild(leftStack);
        ProGPU.WinUI.Grid.SetColumn(leftStack, 0);

        // COLUMN 1: EXTENSION BUTTONS CARD
        var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
        var btnCard = new Border
        {
            Background = new SolidColorBrush(0x1F1F24FA),
            BorderBrush = new SolidColorBrush(0xFFFFFF15),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var btnStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
        var btnHeader = new RichTextBlock { Font = _font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 10) };
        btnHeader.Inlines.Add(new Bold(new Run("Interactive Button Extensions")));
        btnStack.AddChild(btnHeader);

        // RepeatButton demonstration
        var repeatLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 4) };
        repeatLabel.Inlines.Add(new Bold(new Run($"Hold button counter: {_repeatCount}")));
        btnStack.AddChild(repeatLabel);

        var repeatBtnStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        
        var decBtn = new RepeatButton { Width = 80f, Height = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0) };
        var decLabel = new RichTextBlock { Font = _font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        decLabel.Inlines.Add(new Run("- Decrement"));
        decBtn.Content = decLabel;
        decBtn.Click += (s, e) =>
        {
            _repeatCount--;
            repeatLabel.Inlines.Clear();
            repeatLabel.Inlines.Add(new Bold(new Run($"Hold button counter: {_repeatCount}")));
            repeatLabel.Invalidate();
        };
        repeatBtnStack.AddChild(decBtn);

        var incBtn = new RepeatButton { Width = 80f, Height = 32f, CornerRadius = 4f };
        var incLabel = new RichTextBlock { Font = _font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        incLabel.Inlines.Add(new Run("+ Increment"));
        incBtn.Content = incLabel;
        incBtn.Click += (s, e) =>
        {
            _repeatCount++;
            repeatLabel.Inlines.Clear();
            repeatLabel.Inlines.Add(new Bold(new Run($"Hold button counter: {_repeatCount}")));
            repeatLabel.Invalidate();
        };
        repeatBtnStack.AddChild(incBtn);
        btnStack.AddChild(repeatBtnStack);

        // HyperlinkButton demonstration
        var linkLabel = new RichTextBlock { Font = _font, FontSize = 12f, Margin = new Thickness(0, 5, 0, 4) };
        linkLabel.Inlines.Add(new Bold(new Run("Hyperlink Button Hover & Click:")));
        btnStack.AddChild(linkLabel);

        var hyperBtn = new HyperlinkButton { Height = 28f, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        var hyperText = new RichTextBlock { Font = _font, FontSize = 12f, Foreground = ThemeManager.GetBrush("SystemAccentColor") };
        hyperText.Inlines.Add(new Run("Visit ProGPU cross-platform github hub"));
        hyperBtn.Content = hyperText;

        var clickFeedback = new RichTextBlock { Font = _font, FontSize = 11f, Foreground = new SolidColorBrush(0x00E5FF25) };
        clickFeedback.Inlines.Add(new Run(""));
        
        hyperBtn.Click += (s, e) =>
        {
            clickFeedback.Inlines.Clear();
            clickFeedback.Inlines.Add(new Run("Routed hyperlink event triggered successfully!"));
            clickFeedback.Invalidate();
        };

        btnStack.AddChild(hyperBtn);
        btnStack.AddChild(clickFeedback);

        btnCard.Child = btnStack;
        rightStack.AddChild(btnCard);
        contentGrid.AddChild(rightStack);
        ProGPU.WinUI.Grid.SetColumn(rightStack, 1);

            grid.AddChild(contentGrid);
        ProGPU.WinUI.Grid.SetRow(contentGrid, 1);

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

public class DrawingShowcaseVisual : FrameworkElement
{
    public DrawingShowcaseVisual()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HeightConstraint = 350f;
    }

    public override void OnRender(DrawingContext context)
    {
        // background border
        context.DrawRectangle(ThemeManager.GetBrush("CardBackground"), new Pen(ThemeManager.GetBrush("ControlBorder"), 1f), new Rect(0, 0, Size.X, Size.Y));

        // Let's divide into regions to draw different shapes
        float cellWidth = Size.X / 4f;
        float centerY = Size.Y / 2f;

        // 1. Drawing Lines (Cell 0)
        float x0 = 0f;
        context.DrawText("Lines", Program.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x0 + 10f, 10f));
        context.DrawLine(new Pen(ThemeManager.GetBrush("SystemAccentColor"), 3f), new Vector2(x0 + 20f, centerY - 50f), new Vector2(x0 + cellWidth - 20f, centerY + 50f));
        context.DrawLine(new Pen(ThemeManager.GetBrush("TextPrimary"), 1f), new Vector2(x0 + 20f, centerY + 50f), new Vector2(x0 + cellWidth - 20f, centerY - 50f));

        // 2. Drawing Rounded Rectangles (Cell 1)
        float x1 = cellWidth;
        context.DrawText("Rounded Rects", Program.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x1 + 10f, 10f));
        
        var linearGrad = new LinearGradientBrush(
            new Vector2(0f, 0f), new Vector2(1f, 1f),
            new GradientStop[] {
                new GradientStop(new Vector4(0f, 0.47f, 0.83f, 1f), 0f),      // Blue
                new GradientStop(new Vector4(0.5f, 0.1f, 0.8f, 1f), 0.5f),    // Purple
                new GradientStop(new Vector4(0.9f, 0.2f, 0.4f, 1f), 1f)       // Magenta
            }
        );
        context.DrawRoundedRectangle(linearGrad, new Pen(ThemeManager.GetBrush("TextPrimary"), 2f), new Rect(x1 + 20f, centerY - 60f, cellWidth - 40f, 120f), 15f);

        // 3. Drawing Circles & Ellipses (Cell 2)
        float x2 = cellWidth * 2f;
        context.DrawText("Circles & Ellipses", Program.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x2 + 10f, 10f));
        
        var radialGrad = new RadialGradientBrush(
            new Vector2(0.5f, 0.5f), 0.5f,
            new GradientStop[] {
                new GradientStop(new Vector4(1f, 0.8f, 0.2f, 1f), 0f),       // Yellow
                new GradientStop(new Vector4(1f, 0.4f, 0.1f, 1f), 0.6f),     // Orange
                new GradientStop(new Vector4(0.8f, 0.1f, 0.1f, 1f), 1f)      // Red
            }
        );
        context.DrawCircle(radialGrad, new Pen(ThemeManager.GetBrush("TextPrimary"), 1.5f), new Vector2(x2 + cellWidth / 2f, centerY - 30f), 40f);
        context.DrawEllipse(ThemeManager.GetBrush("SystemAccentColor"), new Pen(ThemeManager.GetBrush("TextPrimary"), 1f), new Vector2(x2 + cellWidth / 2f, centerY + 45f), 55f, 25f);

        // 4. Combined Graphics Art (Cell 3)
        float x3 = cellWidth * 3f;
        context.DrawText("Dynamic WebGPU Art", Program.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(x3 + 10f, 10f));
        
        // Multi-layered visual geometry overlay using radial & linear brushes
        var artBg = new LinearGradientBrush(
            new Vector2(0f, 1f), new Vector2(0f, 0f),
            new GradientStop[] {
                new GradientStop(new Vector4(0.1f, 0.1f, 0.15f, 0.9f), 0f),
                new GradientStop(new Vector4(0.05f, 0.05f, 0.08f, 0.9f), 1f)
            }
        );
        context.DrawRoundedRectangle(artBg, null, new Rect(x3 + 20f, centerY - 70f, cellWidth - 40f, 140f), 8f);
        
        // Dynamic circles intersecting
        context.DrawCircle(new SolidColorBrush(new Vector4(0f, 0.8f, 0.6f, 0.4f)), null, new Vector2(x3 + cellWidth / 2f - 15f, centerY), 35f);
        context.DrawCircle(new SolidColorBrush(new Vector4(0f, 0.4f, 0.9f, 0.4f)), null, new Vector2(x3 + cellWidth / 2f + 15f, centerY), 35f);
    }
}

public static class SamplePagePresenter
{
    public static FrameworkElement CreateDrawingContextShowcaseView()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = Program.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Bold(new Run("WebGPU Shaders & DrawingContext Vector APIs")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        description.Inlines.Add(new Run("This page showcases the full GPU-accelerated drawing context. Gradients are computed smoothly in parallel per-pixel in WebGPU WGSL shaders. Shapes are dynamically tessellated on the GPU at maximum framerates."));
        stack.AddChild(description);

        var visual = new DrawingShowcaseVisual();
        stack.AddChild(visual);

        return stack;
    }

    public static FrameworkElement CreateFileStorageShowcaseView()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };

        var title = new RichTextBlock { Font = Program.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Bold(new Run("Native Storage File Pickers & Async I/O")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        description.Inlines.Add(new Run("Use standard native asynchronous pickers (FileOpenPicker, FileSavePicker) to query system dialogs. Reads and writes files asynchronously using WinUI's StorageFile platform subsystem."));
        stack.AddChild(description);

        var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
        
        var openBtn = new Button { Width = 160f, Height = 36f, CornerRadius = 6f, Margin = new Thickness(0, 0, 10, 0) };
        var openBtnText = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        openBtnText.Inlines.Add(new Run("Open Text File..."));
        openBtn.Content = openBtnText;

        var saveBtn = new Button { Width = 160f, Height = 36f, CornerRadius = 6f, Margin = new Thickness(0, 0, 10, 0) };
        var saveBtnText = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        saveBtnText.Inlines.Add(new Run("Save Copy As..."));
        saveBtn.Content = saveBtnText;

        var folderBtn = new Button { Width = 160f, Height = 36f, CornerRadius = 6f };
        var folderBtnText = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        folderBtnText.Inlines.Add(new Run("Select Folder..."));
        folderBtn.Content = folderBtnText;

        actionsRow.AddChild(openBtn);
        actionsRow.AddChild(saveBtn);
        actionsRow.AddChild(folderBtn);
        stack.AddChild(actionsRow);

        var statusHeader = new RichTextBlock { Font = Program.GetFont(), FontSize = 13f, Margin = new Thickness(0, 10, 0, 5) };
        statusHeader.Inlines.Add(new Bold(new Run("Subsystem Status:")));
        stack.AddChild(statusHeader);

        var statusText = new RichTextBlock { Font = Program.GetFont(), FontSize = 11.5f, Margin = new Thickness(0, 0, 0, 15), Foreground = ThemeManager.GetBrush("TextSecondary") };
        statusText.Inlines.Add(new Run("Idle. Waiting for picker interaction."));
        stack.AddChild(statusText);

        var contentHeader = new RichTextBlock { Font = Program.GetFont(), FontSize = 13f, Margin = new Thickness(0, 5, 0, 5) };
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
        var editorText = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
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

        var title = new RichTextBlock { Font = Program.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Bold(new Run("Fluent WinUI Styles & Setter Engine")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        description.Inlines.Add(new Run("Assign uniform looks to visual panels and buttons using C# styles. Below is a comparison between standard controls, and styled controls styled with setter objects."));
        stack.AddChild(description);

        var containerGrid = new ProGPU.WinUI.Grid();
        containerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        containerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        // Column 0: Standard Unstyled Controls
        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10f) };
        var leftHeader = new RichTextBlock { Font = Program.GetFont(), FontSize = 14f, Margin = new Thickness(0, 0, 0, 15) };
        leftHeader.Inlines.Add(new Bold(new Run("Standard Controls")));
        leftStack.AddChild(leftHeader);

        var normalBtn1 = new Button { Width = 160f, Height = 36f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 10f) };
        var normalBtnText1 = new RichTextBlock { Font = Program.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        normalBtnText1.Inlines.Add(new Run("Default Button 1"));
        normalBtn1.Content = normalBtnText1;
        leftStack.AddChild(normalBtn1);

        var normalBtn2 = new Button { Width = 160f, Height = 36f, CornerRadius = 4f, Margin = new Thickness(0, 0, 0, 10f) };
        var normalBtnText2 = new RichTextBlock { Font = Program.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        normalBtnText2.Inlines.Add(new Run("Default Button 2"));
        normalBtn2.Content = normalBtnText2;
        leftStack.AddChild(normalBtn2);

        containerGrid.AddChild(leftStack);
        ProGPU.WinUI.Grid.SetColumn(leftStack, 0);

        // Column 1: Styled Controls
        var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10f) };
        var rightHeader = new RichTextBlock { Font = Program.GetFont(), FontSize = 14f, Margin = new Thickness(0, 0, 0, 15) };
        rightHeader.Inlines.Add(new Bold(new Run("Styled via Reflection Setters")));
        rightStack.AddChild(rightHeader);

        // Create the Style instance
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter("Width", 200f));
        buttonStyle.Setters.Add(new Setter("Height", 44f));
        buttonStyle.Setters.Add(new Setter("CornerRadius", 10f));

        var styledBtn1 = new Button { Margin = new Thickness(0, 0, 0, 10f) };
        var styledBtnText1 = new RichTextBlock { Font = Program.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        styledBtnText1.Inlines.Add(new Run("Styled Premium Button 1"));
        styledBtn1.Content = styledBtnText1;
        styledBtn1.Style = buttonStyle; // Apply style
        rightStack.AddChild(styledBtn1);

        var styledBtn2 = new Button { Margin = new Thickness(0, 0, 0, 10f) };
        var styledBtnText2 = new RichTextBlock { Font = Program.GetFont(), FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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

        var title = new RichTextBlock { Font = Program.GetFont(), FontSize = 18f, Margin = new Thickness(0, 0, 0, 10) };
        title.Inlines.Add(new Bold(new Run("GPU Vector Benchmark - MotionMark Showcase")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 15) };
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
        var countLabel = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
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
        var strokeLabel = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
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
        var animLabel = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
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
        var splitLabel = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
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
        var colorLabel = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        colorLabel.Inlines.Add(new Bold(new Run("Color Palette:")));
        settingsStack.AddChild(colorLabel);
        
        var colorCombo = new ComboBox { Font = Program.GetFont(), WidthConstraint = 260f, Margin = new Thickness(0, 0, 0, 16) };
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
        var typeLabel = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 8) };
        typeLabel.Inlines.Add(new Bold(new Run("Segment Types Mix:")));
        settingsStack.AddChild(typeLabel);

        var lineCheckText = new RichTextBlock { Font = Program.GetFont(), FontSize = 11.5f };
        lineCheckText.Inlines.Add(new Run("Lines"));
        var lineCheck = new CheckBox { Content = lineCheckText, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        lineCheck.CheckedChanged += (s, e) => {
            visual.EnableLines = lineCheck.IsChecked;
            visual.RegenerateSegments();
        };
        settingsStack.AddChild(lineCheck);

        var quadCheckText = new RichTextBlock { Font = Program.GetFont(), FontSize = 11.5f };
        quadCheckText.Inlines.Add(new Run("Quadratic Curves"));
        var quadCheck = new CheckBox { Content = quadCheckText, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        quadCheck.CheckedChanged += (s, e) => {
            visual.EnableQuadBeziers = quadCheck.IsChecked;
            visual.RegenerateSegments();
        };
        settingsStack.AddChild(quadCheck);

        var cubicCheckText = new RichTextBlock { Font = Program.GetFont(), FontSize = 11.5f };
        cubicCheckText.Inlines.Add(new Run("Cubic Curves"));
        var cubicCheck = new CheckBox { Content = cubicCheckText, IsChecked = true, Margin = new Thickness(0, 0, 0, 16) };
        cubicCheck.CheckedChanged += (s, e) => {
            visual.EnableCubicBeziers = cubicCheck.IsChecked;
            visual.RegenerateSegments();
        };
        settingsStack.AddChild(cubicCheck);

        // Fills vs Strokes
        var fillToggleLabel = new RichTextBlock { Font = Program.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
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
}

public class MotionMarkShowcaseVisual : FrameworkElement
{
    public class PathSegmentElement
    {
        public PathSegment? OriginalSeg;
        public PathSegment? WobbledSeg;
        public Vector2 OriginalStartPoint;
        public Vector2 WobbledStartPoint;
        public Vector4 Color;
        public float Width;
        public bool IsSplit;
        public GridPoint GP;
        public int GridIndex;
        public SolidColorBrush? CachedBrush;
        public Pen? CachedPen;
    }

    public struct GridPoint
    {
        public int X;
        public int Y;

        public GridPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public Vector2 ToCoordinate(float width, float height)
        {
            float w = float.IsInfinity(width) || float.IsNaN(width) || width <= 0f ? 800f : width;
            float h = float.IsInfinity(height) || float.IsNaN(height) || height <= 0f ? 520f : height;
            float scaleX = w / 81f;
            float scaleY = (h - 60f) / 41f;
            return new Vector2((X + 0.5f) * scaleX, 30f + (Y + 0.5f) * scaleY);
        }
    }

    private readonly List<PathSegmentElement> _elements = new();
    private readonly Random _rand = new();
    private float _time = 0f;

    // Exposed settings
    public int ElementCount = 1000;
    public float StrokeThicknessMultiplier = 1.0f;
    public float SplitProbability = 0.5f;
    public float AnimationSpeed = 1.0f;
    public int ColorMode = 0;
    public bool FillShapes = false;
    public bool EnableLines = true;
    public bool EnableQuadBeziers = true;
    public bool EnableCubicBeziers = true;

    private static readonly (int, int)[] Offsets = { (-4, 0), (2, 0), (1, -2), (1, 2) };

    private static readonly Vector4[] VelloColors = {
        new Vector4(0.06f, 0.06f, 0.06f, 1.0f),
        new Vector4(0.50f, 0.50f, 0.50f, 1.0f),
        new Vector4(0.75f, 0.75f, 0.75f, 1.0f),
        new Vector4(0.06f, 0.06f, 0.06f, 1.0f),
        new Vector4(0.50f, 0.50f, 0.50f, 1.0f),
        new Vector4(0.75f, 0.75f, 0.75f, 1.0f),
        new Vector4(0.88f, 0.06f, 0.25f, 1.0f) // Crimson red accent
    };

    private static readonly Vector4[] FluentColors = {
        new Vector4(0f, 0.47f, 0.83f, 1f),    // Segoe Blue
        new Vector4(0.52f, 0.15f, 0.79f, 1f),  // Purple
        new Vector4(0.91f, 0.11f, 0.38f, 1f),  // Pink
        new Vector4(1f, 0.73f, 0f, 1f),        // Amber Yellow
        new Vector4(0.06f, 0.69f, 0.32f, 1f)   // Green
    };

    private static readonly Vector4[] MonochromeColors = {
        new Vector4(0.12f, 0.12f, 0.12f, 1f),
        new Vector4(0.24f, 0.24f, 0.24f, 1f),
        new Vector4(0.6f, 0.6f, 0.6f, 1f),
        new Vector4(0.9f, 0.9f, 0.9f, 1f)
    };

    public MotionMarkShowcaseVisual()
    {
        HeightConstraint = 520f;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private GridPoint GetRandomPoint(GridPoint last)
    {
        var offset = Offsets[_rand.Next(Offsets.Length)];
        int x = last.X + offset.Item1;
        if (x < 0 || x > 80)
        {
            x -= offset.Item1 * 2;
        }
        int y = last.Y + offset.Item2;
        if (y < 0 || y > 40)
        {
            y -= offset.Item2 * 2;
        }
        return new GridPoint(Math.Clamp(x, 0, 80), Math.Clamp(y, 0, 40));
    }

    private Vector4 GetColorForScheme()
    {
        return ColorMode switch
        {
            0 => VelloColors[_rand.Next(VelloColors.Length)],
            1 => FluentColors[_rand.Next(FluentColors.Length)],
            2 => HsvToRgb((float)_rand.NextDouble() * 360f, 0.85f, 0.95f),
            3 => MonochromeColors[_rand.Next(MonochromeColors.Length)],
            _ => VelloColors[_rand.Next(VelloColors.Length)]
        };
    }

    private Vector4 HsvToRgb(float h, float s, float v)
    {
        float c = v * s;
        float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        float m = v - c;
        float r = 0, g = 0, b = 0;
        if (h >= 0 && h < 60) { r = c; g = x; b = 0; }
        else if (h >= 60 && h < 120) { r = x; g = c; b = 0; }
        else if (h >= 120 && h < 180) { r = 0; g = c; b = x; }
        else if (h >= 180 && h < 240) { r = 0; g = x; b = c; }
        else if (h >= 240 && h < 300) { r = x; g = 0; b = c; }
        else if (h >= 300 && h <= 360) { r = c; g = 0; b = x; }
        return new Vector4(r + m, g + m, b + m, 1.0f);
    }

    public void SetComplexity(int count)
    {
        ElementCount = count;
        Resize(count);
    }

    public void Resize(int n)
    {
        int oldN = _elements.Count;
        if (n < oldN)
        {
            _elements.RemoveRange(n, oldN - n);
        }
        else if (n > oldN)
        {
            var lastGP = _elements.Count > 0 ? _elements[^1].GP : new GridPoint(40, 20);
            for (int i = oldN; i < n; i++)
            {
                var elem = CreateElement(lastGP, ref lastGP, i);
                _elements.Add(elem);
            }
        }
        Invalidate();
    }

    public void RegenerateColors()
    {
        foreach (var elem in _elements)
        {
            elem.Color = GetColorForScheme();
            elem.CachedBrush = new SolidColorBrush(elem.Color);
            elem.CachedPen = new Pen(elem.CachedBrush, elem.Width * StrokeThicknessMultiplier);
        }
        Invalidate();
    }

    public void UpdateCachedPens()
    {
        foreach (var elem in _elements)
        {
            if (elem.CachedBrush != null)
            {
                elem.CachedPen = new Pen(elem.CachedBrush, elem.Width * StrokeThicknessMultiplier);
            }
        }
        Invalidate();
    }

    public void RegenerateSegments()
    {
        _elements.Clear();
        Resize(ElementCount);
    }

    private PathSegment DuplicateSegment(PathSegment seg)
    {
        if (seg is LineSegment line) return new LineSegment(line.Point);
        if (seg is QuadraticBezierSegment quad) return new QuadraticBezierSegment(quad.ControlPoint, quad.Point);
        if (seg is CubicBezierSegment cubic) return new CubicBezierSegment(cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point);
        return seg;
    }

    private PathSegmentElement CreateElement(GridPoint last, ref GridPoint current, int gridIndex)
    {
        var activeTypes = new List<int>();
        if (EnableLines) activeTypes.Add(0);
        if (EnableQuadBeziers) activeTypes.Add(1);
        if (EnableCubicBeziers) activeTypes.Add(2);

        int segType = 0;
        if (activeTypes.Count > 0)
        {
            segType = activeTypes[_rand.Next(activeTypes.Count)];
        }

        var startPt = current.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f);
        var next = GetRandomPoint(current);
        PathSegment seg;

        if (segType == 0) // Line
        {
            seg = new LineSegment(next.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f));
            current = next;
        }
        else if (segType == 1) // Quad Bezier
        {
            var p2 = GetRandomPoint(next);
            seg = new QuadraticBezierSegment(next.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f), p2.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f));
            current = p2;
        }
        else // Cubic Bezier
        {
            var p2 = GetRandomPoint(next);
            var p3 = GetRandomPoint(next);
            seg = new CubicBezierSegment(next.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f), p2.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f), p3.ToCoordinate(Size.X > 0 ? Size.X : 1000f, Size.Y > 0 ? Size.Y : 520f));
            current = p3;
        }

        var color = GetColorForScheme();
        float width = (float)Math.Pow(_rand.NextDouble(), 5) * 20f + 1f;
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, width * StrokeThicknessMultiplier);

        return new PathSegmentElement
        {
            OriginalSeg = seg,
            WobbledSeg = DuplicateSegment(seg),
            OriginalStartPoint = startPt,
            WobbledStartPoint = startPt,
            Color = color,
            Width = width,
            IsSplit = _rand.NextDouble() < SplitProbability,
            GP = current,
            GridIndex = gridIndex,
            CachedBrush = brush,
            CachedPen = pen
        };
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = availableSize.X;
        float h = HeightConstraint ?? 520f;
        if (float.IsInfinity(w)) w = 800f;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float w = arrangeRect.Width;
        float h = arrangeRect.Height;
        if (float.IsInfinity(w) || float.IsNaN(w) || w <= 0f) w = 800f;
        if (float.IsInfinity(h) || float.IsNaN(h) || h <= 0f) h = 520f;

        bool sizeChanged = Size.X != w || Size.Y != h;
        Size = new Vector2(w, h);
        if (sizeChanged || _elements.Count == 0)
        {
            RegenerateSegments();
        }
    }

    public override void OnRender(DrawingContext context)
    {
        // 1. Draw card background outline
        var borderPen = new Pen(ThemeManager.GetBrush("ControlBorder"), 1.0f);
        var bg = ThemeManager.GetBrush("ControlBackground");
        context.DrawRoundedRectangle(bg, borderPen, new Rect(Vector2.Zero, Size), 8f);

        if (_elements.Count == 0) return;

        // 2. Dynamic Wobble Animation Tick
        _time += 0.016f * AnimationSpeed;
        
        for (int i = 0; i < _elements.Count; i++)
        {
            var elem = _elements[i];

            // Vello MotionMark animation: randomly toggle split state (0.5% chance per frame)
            if (_rand.NextDouble() > 0.995)
            {
                elem.IsSplit ^= true;
            }
            
            float phase = _time * 2.5f + elem.GridIndex * 0.04f;
            var offsetStart = new Vector2((float)Math.Sin(phase) * 12f, (float)Math.Cos(phase * 0.7f) * 12f);
            var offsetEnd = new Vector2((float)Math.Sin(phase * 1.3f) * 12f, (float)Math.Cos(phase * 0.9f) * 12f);

            elem.WobbledStartPoint = elem.OriginalStartPoint + offsetStart;

            if (elem.OriginalSeg is LineSegment line && elem.WobbledSeg is LineSegment wLine)
            {
                wLine.Point = line.Point + offsetEnd;
            }
            else if (elem.OriginalSeg is QuadraticBezierSegment quad && elem.WobbledSeg is QuadraticBezierSegment wQuad)
            {
                var ctrlOffset = new Vector2((float)Math.Sin(phase * 0.6f) * 15f, (float)Math.Cos(phase * 0.8f) * 15f);
                wQuad.ControlPoint = quad.ControlPoint + ctrlOffset;
                wQuad.Point = quad.Point + offsetEnd;
            }
            else if (elem.OriginalSeg is CubicBezierSegment cubic && elem.WobbledSeg is CubicBezierSegment wCubic)
            {
                var ctrlOffset1 = new Vector2((float)Math.Sin(phase * 0.5f) * 15f, (float)Math.Cos(phase * 0.7f) * 15f);
                var ctrlOffset2 = new Vector2((float)Math.Cos(phase * 0.6f) * 15f, (float)Math.Sin(phase * 0.8f) * 15f);
                wCubic.ControlPoint1 = cubic.ControlPoint1 + ctrlOffset1;
                wCubic.ControlPoint2 = cubic.ControlPoint2 + ctrlOffset2;
                wCubic.Point = cubic.Point + offsetEnd;
            }
        }

        if (FillShapes)
        {
            // 3. Batch path rendering based on splits (used for path fills)
            var path = new PathGeometry();
            PathFigure? fig = null;

            for (int i = 0; i < _elements.Count; i++)
            {
                var element = _elements[i];

                if (fig == null)
                {
                    fig = new PathFigure(element.WobbledStartPoint);
                }

                if (element.WobbledSeg != null)
                {
                    fig.Segments.Add(element.WobbledSeg);
                }

                if (element.IsSplit || i == _elements.Count - 1)
                {
                    path.Figures.Add(fig);
                    
                    var brush = element.CachedBrush ?? new SolidColorBrush(element.Color);
                    context.DrawPath(brush, null, path);

                    path = new PathGeometry();
                    fig = null;
                }
            }
        }
        else
        {
            // 3b. Ultra-fast direct primitive rendering for outline strokes (ZERO allocations!)
            for (int i = 0; i < _elements.Count; i++)
            {
                var element = _elements[i];
                var pen = element.CachedPen ?? new Pen(element.CachedBrush ?? new SolidColorBrush(element.Color), element.Width * StrokeThicknessMultiplier);

                if (element.WobbledSeg is LineSegment line)
                {
                    context.DrawLine(pen, element.WobbledStartPoint, line.Point);
                }
                else if (element.WobbledSeg is QuadraticBezierSegment quad)
                {
                    context.DrawQuadraticBezier(pen, element.WobbledStartPoint, quad.ControlPoint, quad.Point);
                }
                else if (element.WobbledSeg is CubicBezierSegment cubic)
                {
                    context.DrawCubicBezier(pen, element.WobbledStartPoint, cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point);
                }
            }
        }

        // 4. Draw HUD Benchmarking panel (FPS, item count, pipeline)
        if (Program.GetFont() != null)
        {
            var hudBrush = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.6f));
            var hudRect = new Rect(15f, 15f, 250f, 85f);
            context.DrawRoundedRectangle(hudBrush, new Pen(new SolidColorBrush(0xFFFFFF30), 1f), hudRect, 6f);

            string typText = (EnableLines ? "L " : "") + (EnableQuadBeziers ? "Q " : "") + (EnableCubicBeziers ? "C " : "");
            context.DrawText($"Active Shapes: {_elements.Count:N0}", Program.GetFont()!, 11.5f, new SolidColorBrush(Vector4.One), new Vector2(25f, 25f));
            context.DrawText($"Modes Mix: {typText}", Program.GetFont()!, 11f, new SolidColorBrush(new Vector4(0.8f, 0.8f, 0.8f, 1.0f)), new Vector2(25f, 43f));
            context.DrawText("Pipeline: ProGPU 100% GPU-Bound", Program.GetFont()!, 11f, ThemeManager.GetBrush("SystemAccentColor"), new Vector2(25f, 61f));
        }

        // Re-invalidate to animate smoothly at max monitor refresh rate
        Invalidate();
        base.OnRender(context);
    }
}


