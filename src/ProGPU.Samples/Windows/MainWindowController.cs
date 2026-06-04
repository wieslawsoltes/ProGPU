using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
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
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Button = Microsoft.UI.Xaml.Controls.Button;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Window = Microsoft.UI.Xaml.Window;

namespace ProGPU.Samples;

public static unsafe class MainWindowController
{
    public static void Start(Window window)
    {
        AppState._window = window.SilkWindow;
        AppState._wgpuContext = window.WgpuContext;
        AppState._screenCompositor = window.Compositor;
        if (AppState._screenCompositor != null)
        {
            AppState._screenCompositor.ClearColor = ThemeManager.GetColor("PageBackground");
        }

        AppState._offscreenCompositor = new Compositor(AppState._wgpuContext!, TextureFormat.Rgba8Unorm);
        AppState._compute = new ComputeAccelerator(AppState._wgpuContext!);

        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!File.Exists(fontPath))
        {
            fontPath = "Arial.ttf";
        }

        if (File.Exists(fontPath))
        {
            Console.WriteLine($"[ProGPU.Samples] Loading System Font: {fontPath}");
            AppState._font = new TtfFont(fontPath);
            Microsoft.UI.Xaml.Controls.PopupService.DefaultFont = AppState._font;
        }
        else
        {
            throw new FileNotFoundException("Arial.ttf is required to execute typography.");
        }

        string timesPath = "/System/Library/Fonts/Supplemental/Times New Roman.ttf";
        if (File.Exists(timesPath)) AppState._fontTimes = new TtfFont(timesPath);
        else AppState._fontTimes = AppState._font;

        string courierPath = "/System/Library/Fonts/Supplemental/Courier New.ttf";
        if (File.Exists(courierPath)) AppState._fontCourier = new TtfFont(courierPath);
        else AppState._fontCourier = AppState._font;

        string georgiaPath = "/System/Library/Fonts/Supplemental/Georgia.ttf";
        if (File.Exists(georgiaPath)) AppState._fontGeorgia = new TtfFont(georgiaPath);
        else AppState._fontGeorgia = AppState._font;

        string comicPath = "/System/Library/Fonts/Supplemental/Comic Sans MS.ttf";
        if (File.Exists(comicPath)) AppState._fontComic = new TtfFont(comicPath);
        else AppState._fontComic = AppState._font;

        AppState._canvasSourceTexture = new GpuTexture(AppState._wgpuContext!, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.StorageBinding | TextureUsage.CopySrc);
        AppState._canvasTempTexture = new GpuTexture(AppState._wgpuContext!, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);
        AppState._canvasBlurTexture = new GpuTexture(AppState._wgpuContext!, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);
        AppState._canvasShadowTexture = new GpuTexture(AppState._wgpuContext!, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);

        AppState.GenerateLogItems();

        ObjModels.EnsureSamplesExist("models");

        BuildSceneGraph();

        if (AppState._topLevelGrid != null)
        {
            AppState._topLevelGrid.PointerMoved += (sender, args) =>
            {
                AppState._mousePos = args.Position;
            };
        }

        window.Content = AppState._topLevelGrid;
        window.Rendering += (s, delta) => OnWindowRender(delta);
        window.Closed += (s, e) => Cleanup();
    }

    public static void StartEmbedded(WgpuContext context, Compositor compositor)
    {
        AppState._wgpuContext = context;
        AppState._screenCompositor = compositor;
        if (AppState._screenCompositor != null)
        {
            AppState._screenCompositor.ClearColor = ThemeManager.GetColor("PageBackground");
        }

        AppState._offscreenCompositor = new Compositor(AppState._wgpuContext!, TextureFormat.Rgba8Unorm);
        AppState._compute = new ComputeAccelerator(AppState._wgpuContext!);

        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!File.Exists(fontPath))
        {
            fontPath = "Arial.ttf";
        }

        if (File.Exists(fontPath))
        {
            Console.WriteLine($"[ProGPU.Samples] Loading Embedded System Font: {fontPath}");
            AppState._font = new TtfFont(fontPath);
            Microsoft.UI.Xaml.Controls.PopupService.DefaultFont = AppState._font;
        }
        else
        {
            throw new FileNotFoundException("Arial.ttf is required to execute typography.");
        }

        string timesPath = "/System/Library/Fonts/Supplemental/Times New Roman.ttf";
        if (File.Exists(timesPath)) AppState._fontTimes = new TtfFont(timesPath);
        else AppState._fontTimes = AppState._font;

        string courierPath = "/System/Library/Fonts/Supplemental/Courier New.ttf";
        if (File.Exists(courierPath)) AppState._fontCourier = new TtfFont(courierPath);
        else AppState._fontCourier = AppState._font;

        string georgiaPath = "/System/Library/Fonts/Supplemental/Georgia.ttf";
        if (File.Exists(georgiaPath)) AppState._fontGeorgia = new TtfFont(georgiaPath);
        else AppState._fontGeorgia = AppState._font;

        string comicPath = "/System/Library/Fonts/Supplemental/Comic Sans MS.ttf";
        if (File.Exists(comicPath)) AppState._fontComic = new TtfFont(comicPath);
        else AppState._fontComic = AppState._font;

        AppState._canvasSourceTexture = new GpuTexture(AppState._wgpuContext!, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.StorageBinding | TextureUsage.CopySrc);
        AppState._canvasTempTexture = new GpuTexture(AppState._wgpuContext!, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);
        AppState._canvasBlurTexture = new GpuTexture(AppState._wgpuContext!, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);
        AppState._canvasShadowTexture = new GpuTexture(AppState._wgpuContext!, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);

        AppState.GenerateLogItems();

        BuildSceneGraph();

        if (AppState._topLevelGrid != null)
        {
            AppState._topLevelGrid.PointerMoved += (sender, args) =>
            {
                AppState._mousePos = args.Position;
            };
        }
    }

    private static void BuildSceneGraph()
    {
        if (AppState._wgpuContext == null || AppState._font == null) return;

        // 1. Root Grid containing Header + Main Body + Bottom Diagnostics Bar
        AppState._rootGrid = new Microsoft.UI.Xaml.Controls.Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AppState._rootGrid.RowDefinitions.Add(new GridLength(70, GridUnitType.Absolute));  // Row 0: Header
        AppState._rootGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Row 1: Content Workspace
        AppState._rootGrid.RowDefinitions.Add(new GridLength(32, GridUnitType.Absolute));  // Row 2: Status bar

        // 2. HEADER
        var headerBar = new Border
        {
            Background = new ProGPU.Vector.ThemeResourceBrush("HeaderBackground"), // Dynamic theme Mica backdrop
            BorderBrush = new ProGPU.Vector.ThemeResourceBrush("ControlBorder"), // Thin dynamic border outline
            BorderThickness = new Thickness(0, 0, 0, 1f),
            Padding = new Thickness(20, 10, 20, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var headerGrid = new Microsoft.UI.Xaml.Controls.Grid();
        headerGrid.ColumnDefinitions.Add(new GridLength(45f, GridUnitType.Absolute));  // Column 0: Hamburger Button
        headerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Column 1: Title Logo
        headerGrid.ColumnDefinitions.Add(new GridLength(120f, GridUnitType.Absolute)); // Column 2: Theme Family Selector
        headerGrid.ColumnDefinitions.Add(new GridLength(120f, GridUnitType.Absolute)); // Column 3: Theme Selector
        headerGrid.ColumnDefinitions.Add(new GridLength(300f, GridUnitType.Absolute)); // Column 4: Subtitle text

        var hamburgerBtn = new Button
        {
            Width = 36f,
            Height = 36f,
            CornerRadius = 6f,
            Background = new SolidColorBrush(0x00000000), // Transparent background
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        hamburgerBtn.Content = new HamburgerIconVisual();
        hamburgerBtn.Click += (s, e) =>
        {
            if (AppState._navigationView != null)
            {
                AppState._navigationView.IsPaneOpen = !AppState._navigationView.IsPaneOpen;
            }
        };
        headerGrid.AddChild(hamburgerBtn);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(hamburgerBtn, 0);

        var titleText = new RichTextBlock { Font = AppState._font, FontSize = 20f, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var logoRun = new Run("Pro") { Foreground = new ProGPU.Vector.ThemeResourceBrush("SystemAccentColor") };
        titleText.Inlines.Add(new Bold(logoRun));
        titleText.Inlines.Add(new Bold(new Run("GPU WinUI Gallery")));
        headerGrid.AddChild(titleText);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(titleText, 1);

        // Theme Family selector button [ WinUI / macOS ]
        var familyBtn = new Button
        {
            Width = 100f,
            Height = 32f,
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var familyStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var familyBtnText = new RichTextBlock { Font = AppState._font, FontSize = 11f, VerticalAlignment = VerticalAlignment.Center };
        var familyRun = new Run("WinUI");
        familyBtnText.Inlines.Add(new Bold(familyRun));
        familyStack.AddChild(familyBtnText);
        familyBtn.Content = familyStack;

        familyBtn.Click += (s, e) =>
        {
            if (ThemeManager.CurrentThemeFamily == VisualThemeFamily.WinUI)
            {
                ThemeManager.CurrentThemeFamily = VisualThemeFamily.macOS;
            }
            else
            {
                ThemeManager.CurrentThemeFamily = VisualThemeFamily.WinUI;
            }
        };
        headerGrid.AddChild(familyBtn);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(familyBtn, 2);

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
        var themeBtnText = new RichTextBlock { Font = AppState._font, FontSize = 11f, VerticalAlignment = VerticalAlignment.Center };
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
            }
            else
            {
                ThemeManager.CurrentTheme = ElementTheme.Dark;
            }
        };
        headerGrid.AddChild(themeBtn);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(themeBtn, 3);

        var subtitleText = new RichTextBlock 
        { 
            Font = AppState._font, 
            FontSize = 11f, 
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };
        subtitleText.Inlines.Add(new Run(".NET 10 cross-platform high-performance engine showcase"));
        headerGrid.AddChild(subtitleText);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(subtitleText, 4);

        headerBar.Child = headerGrid;
        AppState._rootGrid.AddChild(headerBar);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(headerBar, 0);

        // 3. BODY WORKSPACE (Premium Sidebar Navigation View)
        AppState._navigationView = new NavigationView
        {
            Font = AppState._font,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Persistent page visual trees
        var basicInputItem = new NavigationViewItem("Basic Input", "🖱", BasicInputPage.Create());
        var chartShowcaseItem = new NavigationViewItem("GPU Charting", "📊", ChartShowcasePage.Create());
        var panelsItem = new NavigationViewItem("Layout Panels", "🔲", LayoutPanelsPage.Create());
        var textItem = new NavigationViewItem("Text & Documents", "📄", TextDocumentsPage.Create());
        var markdownPlaygroundItem = new NavigationViewItem("Markdown Playground", "📝", MarkdownPage.Create());
        var dataItem = new NavigationViewItem("Data Virtualization", "📊", DataVirtualizationPage.Create());
        var virtualizationControlsItem = new NavigationViewItem("Virtualization Controls", "🎛️", VirtualizationControlsPage.Create());
        var frameworkEffectsItem = new NavigationViewItem("Framework Effects", "✨", FrameworkEffectsPage.Create());
        var imageEffectsItem = new NavigationViewItem("Image Effects", "🖼️", ImageEffectsPage.Create());
        var gdiShowcaseItem = new NavigationViewItem("GDI Shim Showcase", "🎨", GdiShowcasePage.Create());
        var glyphRunShowcaseItem = new NavigationViewItem("Glyph Run Showcase", "🔤", GlyphRunShowcasePage.Create());
        var wpfShowcaseItem = new NavigationViewItem("WPF Shim Showcase", "📐", WpfShowcasePage.Create());

        var computeItem = new NavigationViewItem("Compute FX", "⚙", ComputeFxPage.Create());
        var motionAnimationsItem = new NavigationViewItem("Motion & Animations", "🎬", MotionAnimationsPage.Create());
        var advancedItem = new NavigationViewItem("Advanced Controls", "🛠", AdvancedControlsPage.Create());
        var keyboardParityItem = new NavigationViewItem("Keyboard & Focus", "⌨️", KeyboardParityPage.Create());
        var themeShowcaseItem = new NavigationViewItem("Theme Showcase", "🎨", ThemeShowcasePage.Create());
        var compositorItem = new NavigationViewItem("Compositor API", "🎨", CompositorShowcasePage.Create());
        var splitViewItem = new NavigationViewItem("SplitView Layout", "🪟", SplitViewShowcasePage.Create());
        var imageRepeatItem = new NavigationViewItem("Image & Buttons", "🖼️", ImageRepeatShowcasePage.Create());
        var drawingContextItem = new NavigationViewItem("Drawing Context", "📐", SamplePagePresenter.CreateDrawingContextShowcaseView());
        var fileStorageItem = new NavigationViewItem("File Storage", "📁", SamplePagePresenter.CreateFileStorageShowcaseView());
        var stylesShowcaseItem = new NavigationViewItem("Styles Showcase", "💅", SamplePagePresenter.CreateStylesShowcaseView());
        var motionMarkItem = new NavigationViewItem("MotionMark Showcase", "🏁", SamplePagePresenter.CreateMotionMarkShowcaseView());
        var scriptsItem = new NavigationViewItem("Typography & Scripts", "🔤", SamplePagePresenter.CreateTypographyScriptsView());
        var textInputItem = new NavigationViewItem("Interactive Input", "⌨️", SamplePagePresenter.CreateInteractiveInputView());
        var lolsItem = new NavigationViewItem("LOL/s Benchmark", "💥", LolsPage.Create());
        var radioButtonItem = new NavigationViewItem("Radio Button", "🔘", RadioButtonPage.Create());
        var ratingControlItem = new NavigationViewItem("Rating Control", "⭐", RatingControlPage.Create());
        var passwordBoxItem = new NavigationViewItem("Password Box", "🔒", PasswordBoxPage.Create());
        var dxfViewerItem = new NavigationViewItem("DXF CAD Viewer", "📐", DxfViewerPage.Create());
        var visualDesignerItem = new NavigationViewItem("Visual Designer", "📐", VisualDesignerPage.Create());
        var pictureCachingItem = new NavigationViewItem("Picture Caching", "🖼️", PictureShowcasePage.Create());
        var fontGlyphBrowserItem = new NavigationViewItem("Font Glyph Browser", "🔤", FontGlyphBrowserPage.Create());
        var mesh3DViewerItem = new NavigationViewItem("3D Mesh Viewer", "🧊", Mesh3DViewerPage.Create());

        var wrapPanelItem = new NavigationViewItem("Wrap Panel", "🔲", WrapPanelPage.Create());
        var dockPanelItem = new NavigationViewItem("Dock Panel", "🪟", DockPanelPage.Create());
        var gridSplitterItem = new NavigationViewItem("Grid Splitter", "↔️", GridSplitterPage.Create());
        var colorPickerItem = new NavigationViewItem("Color Picker", "🎨", ColorPickerPage.Create());
        var vectorShapesItem = new NavigationViewItem("Vector Shapes", "📐", VectorShapesPage.Create());
        var skiaSharpShimItem = new NavigationViewItem("SkiaSharp Shim", "🦊", SkiaSharpShimPage.Create());
        var pathOpsItem = new NavigationViewItem("Path Operations", "✂️", PathOpsPage.Create());

        AppState._navigationView.MenuItems.Add(basicInputItem);
        AppState._navigationView.MenuItems.Add(chartShowcaseItem);
        AppState._navigationView.MenuItems.Add(panelsItem);
        AppState._navigationView.MenuItems.Add(wrapPanelItem);
        AppState._navigationView.MenuItems.Add(dockPanelItem);
        AppState._navigationView.MenuItems.Add(gridSplitterItem);
        AppState._navigationView.MenuItems.Add(colorPickerItem);
        AppState._navigationView.MenuItems.Add(vectorShapesItem);
        AppState._navigationView.MenuItems.Add(skiaSharpShimItem);
        AppState._navigationView.MenuItems.Add(pathOpsItem);
        AppState._navigationView.MenuItems.Add(fontGlyphBrowserItem);
        AppState._navigationView.MenuItems.Add(textItem);
        AppState._navigationView.MenuItems.Add(markdownPlaygroundItem);
        AppState._navigationView.MenuItems.Add(dataItem);
        AppState._navigationView.MenuItems.Add(virtualizationControlsItem);
        AppState._navigationView.MenuItems.Add(frameworkEffectsItem);
        AppState._navigationView.MenuItems.Add(imageEffectsItem);
        AppState._navigationView.MenuItems.Add(gdiShowcaseItem);
        AppState._navigationView.MenuItems.Add(glyphRunShowcaseItem);
        AppState._navigationView.MenuItems.Add(wpfShowcaseItem);
        AppState._navigationView.MenuItems.Add(computeItem);
        AppState._navigationView.MenuItems.Add(motionAnimationsItem);
        AppState._navigationView.MenuItems.Add(advancedItem);
        AppState._navigationView.MenuItems.Add(keyboardParityItem);
        AppState._navigationView.MenuItems.Add(themeShowcaseItem);
        AppState._navigationView.MenuItems.Add(compositorItem);
        AppState._navigationView.MenuItems.Add(splitViewItem);
        AppState._navigationView.MenuItems.Add(imageRepeatItem);
        AppState._navigationView.MenuItems.Add(drawingContextItem);
        AppState._navigationView.MenuItems.Add(fileStorageItem);
        AppState._navigationView.MenuItems.Add(stylesShowcaseItem);
        AppState._navigationView.MenuItems.Add(motionMarkItem);
        AppState._navigationView.MenuItems.Add(scriptsItem);
        AppState._navigationView.MenuItems.Add(textInputItem);
        AppState._navigationView.MenuItems.Add(lolsItem);
        AppState._navigationView.MenuItems.Add(radioButtonItem);
        AppState._navigationView.MenuItems.Add(ratingControlItem);
        AppState._navigationView.MenuItems.Add(passwordBoxItem);
        AppState._navigationView.MenuItems.Add(dxfViewerItem);
        AppState._navigationView.MenuItems.Add(visualDesignerItem);
        AppState._navigationView.MenuItems.Add(pictureCachingItem);
        AppState._navigationView.MenuItems.Add(mesh3DViewerItem);

        AppState._navigationView.SelectionChanged += (s, e) =>
        {
            if (AppState._navigationView.SelectedItem != null)
            {
                AppState._activeCategory = AppState._navigationView.SelectedItem.Text;
            }
            if (AppState._activeCategory != "LOL/s Benchmark")
            {
                LolsPage.ResetAndStop();
            }
            AppState._rootGrid?.Invalidate();
        };

        if (AppState._navigationView.SettingsItem != null)
        {
            AppState._navigationView.SettingsItem.Page = SettingsPage.Create();
        }

        // Select default category
        AppState._navigationView.SelectedItem = mesh3DViewerItem;

        AppState._rootGrid.AddChild(AppState._navigationView);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(AppState._navigationView, 1);

        // 4. BOTTOM DIAGNOSTICS STATUS BAR
        var statusBar = new Border
        {
            Background = new ProGPU.Vector.ThemeResourceBrush("HeaderBackground"), // Deep status bar
            BorderBrush = new ProGPU.Vector.ThemeResourceBrush("ControlBorder"), // Thin boundary stroke
            BorderThickness = new Thickness(0, 1f, 0, 0),
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        AppState._statsText = new RichTextBlock
        {
            FontSize = 10f,
            Foreground = new ProGPU.Vector.ThemeResourceBrush("TextSecondary"),
            Font = AppState._fontCourier ?? AppState._font,
            VerticalAlignment = VerticalAlignment.Center
        };
        AppState._statsText.Inlines.Add(new Run("FPS:  -- | CPU:   --.-- ms (Layout: --.--ms, Compile: --.--ms, Upload: --.--ms, Render: --.--ms) | Draws:  -- | Verts:     - (Vec),     - (Txt) | Atlas:  -- | Cursor: (   -,    -) | Focused: None        "));
        statusBar.Child = AppState._statsText;
        AppState._rootGrid.AddChild(statusBar);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(statusBar, 2);

        // Track global ThemeManager theme change event
        ThemeManager.ThemeChanged += () =>
        {
            if (AppState._screenCompositor != null)
            {
                AppState._screenCompositor.ClearColor = ThemeManager.GetColor("PageBackground");
            }
            themeRun.Text = ThemeManager.CurrentTheme == ElementTheme.Dark ? "Dark" : "Light";
            familyRun.Text = ThemeManager.CurrentThemeFamily == VisualThemeFamily.WinUI ? "WinUI" : "macOS";
            AppState._topLevelGrid?.NotifyThemeChanged();
            foreach (var popup in Microsoft.UI.Xaml.Controls.PopupService.ActivePopups)
            {
                popup.NotifyThemeChanged();
            }
        };

        // 5. TOP LEVEL CONTAINER GRID (App container)
        AppState._topLevelGrid = new Microsoft.UI.Xaml.Controls.Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AppState._topLevelGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        AppState._topLevelGrid.ColumnDefinitions.Add(new GridLength(0f, GridUnitType.Absolute)); // Kept collapsed always

        AppState._topLevelGrid.AddChild(AppState._rootGrid);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(AppState._rootGrid, 0);

        AppState._devToolsPanel = new Microsoft.UI.Xaml.Controls.DevTools
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        DevToolsService.StateChanged += (s, ev) =>
        {
            if (DevToolsService.IsDevToolsActive)
            {
                DevToolsWindowController.OpenDevToolsWindow();
            }
            else
            {
                // Defer the closing/disposal to prevent crash when closed via native window close button
                AppState._needsCloseDevTools = true;
            }
        };
    }

    private static void OnWindowUpdate(double delta)
    {
        UIThread.RunPending();

        AppState._rootGrid?.UpdateAnimations((float)delta);
        AppState._rootGrid?.UpdateSampleAnimations((float)delta);
        AppState._rootGrid?.Invalidate();

        if (AppState._animateGear)
        {
            AppState._gearRotation += (float)delta * 1.2f;
            if (AppState._gearRotation > Math.PI * 2) AppState._gearRotation -= (float)(Math.PI * 2);
            AppState._rootGrid?.Invalidate();
        }

        // Keep active focused control tracking updated
        if (InputSystem.FocusedElement != null)
        {
            var typeName = InputSystem.FocusedElement.GetType().Name;
            AppState._activeFocusedName = string.IsNullOrEmpty(InputSystem.FocusedElement.Name) 
                ? typeName 
                : $"{typeName} ({InputSystem.FocusedElement.Name})";
        }
        else
        {
            AppState._activeFocusedName = "None";
        }
    }

    public static void OnWindowRender(double delta)
    {
        if (AppState._rootGrid == null || AppState._topLevelGrid == null || AppState._wgpuContext == null) return;
        if (AppState._screenCompositor == null || AppState._offscreenCompositor == null || AppState._compute == null) return;

        OnWindowUpdate(delta);

        if (AppState._needsCloseDevTools)
        {
            AppState._needsCloseDevTools = false;
            DevToolsWindowController.CloseDevToolsWindow();
        }

        if (AppState._devToolsWindow != null && AppState._devToolsWindow.IsInitialized)
        {
            AppState._devToolsWindow.DoEvents();
            AppState._devToolsWindow.DoUpdate();
            AppState._devToolsWindow.DoRender();
        }

        AppState._frameStopwatch.Restart();

        // Update animated cogs if currently in Compute FX, Image Effects, or Image & Buttons (ImageRepeatShowcasePage)
        if ((AppState._activeCategory == "Compute FX" || AppState._activeCategory == "Image Effects" || AppState._activeCategory == "Image & Buttons") && AppState._gearCanvasVisual != null)
        {
            float winX = AppState._window?.Size.X ?? AppState._topLevelGrid.Size.X;
            float winY = AppState._window?.Size.Y ?? AppState._topLevelGrid.Size.Y;

            AppState._gearCanvasVisual.Measure(new Vector2(winX - 300f, winY - 140f));
            AppState._gearCanvasVisual.Arrange(new Rect(0, 0, winX - 300f, winY - 140f));
            AppState._gearCanvasVisual.UpdateRotation(AppState._gearRotation);

            uint canvasW = (uint)Math.Max(1f, AppState._gearCanvasVisual.Size.X);
            uint canvasH = (uint)Math.Max(1f, AppState._gearCanvasVisual.Size.Y);

            if (AppState._canvasSourceTexture != null && AppState._canvasTempTexture != null && AppState._canvasBlurTexture != null && AppState._canvasShadowTexture != null)
            {
                AppState._canvasSourceTexture.Resize(canvasW, canvasH);
                AppState._canvasTempTexture.Resize(canvasW, canvasH);
                AppState._canvasBlurTexture.Resize(canvasW, canvasH);
                AppState._canvasShadowTexture.Resize(canvasW, canvasH);

                AppState._offscreenCompositor.RenderScene(AppState._gearCanvasVisual, canvasW, canvasH, AppState._canvasSourceTexture.ViewPtr);

                if (AppState._shadowRadius > 0)
                {
                    var shadowColor = new Vector4(0f, 0f, 0f, 0.65f);
                    AppState._compute.ApplyDropShadow(AppState._canvasSourceTexture, AppState._canvasTempTexture, AppState._canvasShadowTexture, AppState._shadowOffset, shadowColor, AppState._shadowRadius);
                }

                if (AppState._blurRadius > 0)
                {
                    AppState._compute.ApplyGaussianBlur(AppState._canvasSourceTexture, AppState._canvasTempTexture, AppState._canvasBlurTexture, AppState._blurRadius);
                }
            }
        }

        // Metrics & stats overlay updates (now measuring full frame time)
        AppState._cpuFrameTimeMs = AppState._frameStopwatch.Elapsed.TotalMilliseconds;
        
        AppState._frameCount++;
        AppState._fpsAccumulator += delta;
        if (AppState._fpsAccumulator >= 0.5)
        {
            AppState._currentFps = AppState._frameCount / AppState._fpsAccumulator;
            AppState._frameCount = 0;
            AppState._fpsAccumulator = 0;
        }

        if (AppState._statsText != null)
        {
            AppState._statsText.Inlines.Clear();
            
            var metrics = AppState._screenCompositor.Metrics;
            double measureArrangeMs = AppState._cpuFrameTimeMs - metrics.FrameTimeMs;
            if (measureArrangeMs < 0) measureArrangeMs = 0;

            AppState._statsText.Inlines.Add(new Run("FPS: "));
            AppState._statsText.Inlines.Add(new Bold(new Run($"{AppState._currentFps,3:F0}")) { Foreground = new SolidColorBrush(0x0078D4FF) });
            
            AppState._statsText.Inlines.Add(new Run(" | CPU: "));
            AppState._statsText.Inlines.Add(new Bold(new Run($"{AppState._cpuFrameTimeMs,6:F2} ms")) { Foreground = new SolidColorBrush(0x0078D4FF) });
            AppState._statsText.Inlines.Add(new Run($" (Layout: {measureArrangeMs,5:F2}ms, Compile: {metrics.VisualTreeCompileTimeMs,5:F2}ms, Upload: {metrics.GpuUploadTimeMs,5:F2}ms, Render: {metrics.RenderPassTimeMs,5:F2}ms)"));

            AppState._statsText.Inlines.Add(new Run(" | Draws: "));
            AppState._statsText.Inlines.Add(new Bold(new Run($"{metrics.DrawCallsCount,3}")) { Foreground = new SolidColorBrush(0x0078D4FF) });

            AppState._statsText.Inlines.Add(new Run(" | Verts: "));
            AppState._statsText.Inlines.Add(new Run($"{metrics.VectorVerticesCount,5} (Vec), {metrics.TextVerticesCount,5} (Txt)"));

            AppState._statsText.Inlines.Add(new Run(" | Atlas: "));
            AppState._statsText.Inlines.Add(new Bold(new Run($"{metrics.PathAtlasCachedCount,3}")) { Foreground = new SolidColorBrush(0x0078D4FF) });

            AppState._statsText.Inlines.Add(new Run(" | Cursor: "));
            AppState._statsText.Inlines.Add(new Run($"({AppState._mousePos.X,4:F0}, {AppState._mousePos.Y,4:F0})"));
            
            AppState._statsText.Inlines.Add(new Run(" | Focused: "));
            AppState._statsText.Inlines.Add(new Bold(new Run($"{AppState._activeFocusedName,-12}")) { Foreground = new SolidColorBrush(0x0078D4FF) });
            
            AppState._statsText.PerformRichLayout(1200f);
        }
    }

    private static void Cleanup()
    {
        AppState._canvasSourceTexture?.Dispose();
        AppState._canvasTempTexture?.Dispose();
        AppState._canvasBlurTexture?.Dispose();
        AppState._canvasShadowTexture?.Dispose();

        AppState._compute?.Dispose();
        AppState._offscreenCompositor?.Dispose();
    }
}

public class HamburgerIconVisual : FrameworkElement
{
    public HamburgerIconVisual()
    {
        WidthConstraint = 18f;
        HeightConstraint = 12f;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public override void OnRender(DrawingContext context)
    {
        var brush = ThemeManager.GetBrush("TextPrimary");
        float startX = (Size.X - 18f) / 2f;
        float startY = (Size.Y - 12f) / 2f;
        context.DrawRectangle(brush, null, new Rect(startX, startY, 18f, 2f));
        context.DrawRectangle(brush, null, new Rect(startX, startY + 5f, 18f, 2f));
        context.DrawRectangle(brush, null, new Rect(startX, startY + 10f, 18f, 2f));
        base.OnRender(context);
    }
}
