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
    internal static IWindow? _window;
    internal static WgpuContext? _wgpuContext;
    internal static Compositor? _screenCompositor;
    internal static Compositor? _offscreenCompositor;
    internal static ComputeAccelerator? _compute;

    internal static IWindow? _devToolsWindow;
    internal static WgpuContext? _devToolsWgpuContext;
    internal static Compositor? _devToolsCompositor;

    internal static TtfFont? _font;
    internal static TtfFont? _fontTimes;
    internal static TtfFont? _fontCourier;
    internal static TtfFont? _fontGeorgia;
    internal static TtfFont? _fontComic;
    internal static ProGPU.WinUI.Grid? _rootGrid;
    internal static ProGPU.WinUI.Grid? _topLevelGrid;
    internal static ProGPU.WinUI.DevTools? _devToolsPanel;

    // Active diagnostic metric stats
    internal static RichTextBlock? _statsText;
    internal static Vector2 _mousePos;
    internal static string _activeFocusedName = "None";

    // Category pages and sidebar selections
    internal static string _activeCategory = "Basic Input";
    internal static NavigationView? _navigationView;

    // Framework Effects Page Variables
    internal static float _fxBlurRadius = 8f;
    internal static float _fxShadowRadius = 12f;
    internal static Vector2 _fxShadowOffset = new Vector2(5f, 5f);
    internal static Vector4 _fxShadowColor = new Vector4(0f, 0f, 0f, 0.6f);
    internal static Vector4 _fxNeonColor = new Vector4(0.85f, 0.08f, 0.52f, 0.8f);

    // Compute FX variables
    internal static float _blurRadius = 8f;
    internal static float _shadowRadius = 8f;
    internal static Vector2 _shadowOffset = new Vector2(4f, 4f);
    internal static bool _animateGear = true;
    internal static float _gearRotation = 0f;

    // Diagnostic timing
    internal static readonly Stopwatch _frameStopwatch = new();
    internal static double _fpsAccumulator = 0;
    internal static int _frameCount = 0;
    internal static double _currentFps = 60;
    internal static double _cpuFrameTimeMs = 0;

    // Compute effect textures
    internal static GpuTexture? _canvasSourceTexture;
    internal static GpuTexture? _canvasTempTexture;
    internal static GpuTexture? _canvasBlurTexture;
    internal static GpuTexture? _canvasShadowTexture;

    // Basic Input Page Interactive State
    internal static int _clickCount = 0;
    internal static string _checkboxStatus = "Unchecked";
    internal static float _sliderValue = 50f;

    // Data Virtualization Page Data Set
    public class LogItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public double Latency { get; set; }
    }
    internal static readonly List<object> _logItems = new();

    public static float GetBlurRadius() => _blurRadius;
    public static float GetShadowRadius() => _shadowRadius;
    public static TtfFont? GetFont() => _font;
    public static TtfFont? GetFontTimes() => _fontTimes;
    public static TtfFont? GetFontCourier() => _fontCourier;
    public static TtfFont? GetFontGeorgia() => _fontGeorgia;
    public static TtfFont? GetFontComic() => _fontComic;

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
        
        // Decoupled Screen Compositor Hooks Configuration
        _screenCompositor.PreRender += (w, h) => ProGPU.WinUI.PopupService.MeasureAndArrangePopups(new Vector2(w, h));
        _screenCompositor.GetExternalLayers = () => ProGPU.WinUI.PopupService.ActivePopups;
        _screenCompositor.GetTooltip = () => ProGPU.WinUI.InputSystem.ActiveToolTip;
        _screenCompositor.GetMousePosition = () => ProGPU.WinUI.InputSystem.LastMousePosition;
        _screenCompositor.RenderDiagnostics = (diagContext, w, h) =>
        {
            if (ProGPU.WinUI.DevToolsService.IsDevToolsActive)
            {
                ProGPU.WinUI.AdornerLayer.Render(diagContext, w, h);
            }
        };
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

        // Load other supplementary fonts safely with fallback to primary font
        string timesPath = "/System/Library/Fonts/Supplemental/Times New Roman.ttf";
        if (File.Exists(timesPath)) _fontTimes = new TtfFont(timesPath);
        else _fontTimes = _font;

        string courierPath = "/System/Library/Fonts/Supplemental/Courier New.ttf";
        if (File.Exists(courierPath)) _fontCourier = new TtfFont(courierPath);
        else _fontCourier = _font;

        string georgiaPath = "/System/Library/Fonts/Supplemental/Georgia.ttf";
        if (File.Exists(georgiaPath)) _fontGeorgia = new TtfFont(georgiaPath);
        else _fontGeorgia = _font;

        string comicPath = "/System/Library/Fonts/Supplemental/Comic Sans MS.ttf";
        if (File.Exists(comicPath)) _fontComic = new TtfFont(comicPath);
        else _fontComic = _font;


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

    internal static void GenerateLogItems()
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
        var themeStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var themeIcon = new ThemeToggleIconControl
        {
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var themeBtnText = new RichTextBlock { Font = _font, FontSize = 11f, VerticalAlignment = VerticalAlignment.Center };
        var themeRun = new Run("Dark");
        themeBtnText.Inlines.Add(new Bold(themeRun));
        themeStack.AddChild(themeIcon);
        themeStack.AddChild(themeBtnText);
        themeBtn.Content = themeStack;

        themeBtn.Click += (s, e) =>
        {
            if (ThemeManager.CurrentTheme == ElementTheme.Dark)
            {
                ThemeManager.CurrentTheme = ElementTheme.Light;
                themeRun.Text = "Light";
            }
            else
            {
                ThemeManager.CurrentTheme = ElementTheme.Dark;
                themeRun.Text = "Dark";
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
        var basicInputItem = new NavigationViewItem("Basic Input", "🖱", BasicInputPage.Create());
        var panelsItem = new NavigationViewItem("Layout Panels", "🔲", LayoutPanelsPage.Create());
        var textItem = new NavigationViewItem("Text & Documents", "📄", TextDocumentsPage.Create());
        var dataItem = new NavigationViewItem("Data Virtualization", "📊", DataVirtualizationPage.Create());
        var frameworkEffectsItem = new NavigationViewItem("Framework Effects", "✨", FrameworkEffectsPage.Create());
        var computeItem = new NavigationViewItem("Compute FX", "⚙", ComputeFxPage.Create());
        var motionAnimationsItem = new NavigationViewItem("Motion & Animations", "🎬", MotionAnimationsPage.Create());
        var advancedItem = new NavigationViewItem("Advanced Controls", "🛠", AdvancedControlsPage.Create());
        var compositorItem = new NavigationViewItem("Compositor API", "🎨", CompositorShowcasePage.Create());
        var splitViewItem = new NavigationViewItem("SplitView Layout", "🪟", SplitViewShowcasePage.Create());
        var imageRepeatItem = new NavigationViewItem("Image & Buttons", "🖼️", ImageRepeatShowcasePage.Create());
        var drawingContextItem = new NavigationViewItem("Drawing Context", "📐", SamplePagePresenter.CreateDrawingContextShowcaseView());
        var fileStorageItem = new NavigationViewItem("File Storage", "📁", SamplePagePresenter.CreateFileStorageShowcaseView());
        var stylesShowcaseItem = new NavigationViewItem("Styles Showcase", "💅", SamplePagePresenter.CreateStylesShowcaseView());
        var motionMarkItem = new NavigationViewItem("MotionMark Showcase", "🏁", SamplePagePresenter.CreateMotionMarkShowcaseView());
        var scriptsItem = new NavigationViewItem("Typography & Scripts", "🔤", SamplePagePresenter.CreateTypographyScriptsView());
        var textInputItem = new NavigationViewItem("Interactive Input", "⌨️", SamplePagePresenter.CreateInteractiveInputView());

        _navigationView.MenuItems.Add(basicInputItem);
        _navigationView.MenuItems.Add(panelsItem);
        _navigationView.MenuItems.Add(textItem);
        _navigationView.MenuItems.Add(dataItem);
        _navigationView.MenuItems.Add(frameworkEffectsItem);
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
        _navigationView.MenuItems.Add(scriptsItem);
        _navigationView.MenuItems.Add(textInputItem);


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








    internal static GearCanvasVisual? _gearCanvasVisual;

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

    internal static void OpenDevToolsWindow()
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

    internal static void CloseDevToolsWindow()
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



    internal static int _repeatCount = 0;


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

        // Vello MotionMark animation: randomly toggle split state (0.5% chance per frame) on CPU so it matches behavior
        for (int i = 0; i < _elements.Count; i++)
        {
            if (_rand.NextDouble() > 0.995)
            {
                _elements[i].IsSplit ^= true;
            }
        }

        // 2. Batch path rendering based on splits (matches Vello's exact stroke/color animation grouping)
        var path = new PathGeometry();
        PathFigure? fig = null;

        for (int i = 0; i < _elements.Count; i++)
        {
            var element = _elements[i];

            if (fig == null)
            {
                fig = new PathFigure(element.OriginalStartPoint);
            }

            if (element.OriginalSeg != null)
            {
                fig.Segments.Add(element.OriginalSeg);
            }

            if (element.IsSplit || i == _elements.Count - 1)
            {
                path.Figures.Add(fig);
                
                if (FillShapes)
                {
                    var brush = element.CachedBrush ?? new SolidColorBrush(element.Color);
                    context.DrawPath(brush, null, path);
                }
                else
                {
                    var pen = element.CachedPen ?? new Pen(element.CachedBrush ?? new SolidColorBrush(element.Color), element.Width * StrokeThicknessMultiplier);
                    context.DrawPath(null, pen, path);
                }

                path = new PathGeometry();
                fig = null;
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


