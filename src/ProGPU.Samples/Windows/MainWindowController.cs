using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Diagnostics;
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
using Microsoft.UI.Xaml.HotReload;
using ProGPU.WinUI.Designer;
using Button = Microsoft.UI.Xaml.Controls.Button;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Window = Microsoft.UI.Xaml.Window;

namespace ProGPU.Samples;

public static unsafe class MainWindowController
{
    public static void Start(Window window)
    {
        SamplePerformanceBenchmark.AttachWindow(window);
        AppState._window = window.SilkWindow;
        AppState._wgpuContext = window.WgpuContext;
        AppState._screenCompositor = window.Compositor;
        if (AppState._screenCompositor != null)
        {
            AppState._screenCompositor.ClearColor = ThemeManager.GetColor("PageBackground");
            AppState._screenCompositor.VectorEngine = AppState.VectorEngine;
        }

        SampleFontLoader.EnsureLoaded();
        VirtualizedCodeEditor.WarmUpSyntaxHighlighting();
        MarkdownParser.WarmUp();

        ObjModels.EnsureSamplesExist("models");

        BuildSceneGraph();
        AttachPointerTracking();

        window.Content = AppState._topLevelGrid;
        AppState._hotReloadRegistration?.Dispose();
        AppState._hotReloadRegistration = HotReloadManager.RegisterUpdateHandler(context =>
        {
            if (context.IsTypeUpdated(typeof(MainWindowController)))
            {
                ReloadSceneGraph(window);
            }
        });
        if (AppState._hotReloadCompletedHandler != null)
        {
            HotReloadManager.UpdateCompleted -= AppState._hotReloadCompletedHandler;
        }
        AppState._hotReloadCompletedHandler = result =>
        {
            if (AppState._statsHotReloadRun != null)
            {
                AppState._statsHotReloadRun.Text = $"g{result.Generation} ({result.ReplacedElements + result.RefreshedFactories})";
            }
        };
        HotReloadManager.UpdateCompleted += AppState._hotReloadCompletedHandler;
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
            AppState._screenCompositor.VectorEngine = AppState.VectorEngine;
        }

        SampleFontLoader.EnsureLoaded("[ProGPU.Samples.Embedded]");
        VirtualizedCodeEditor.WarmUpSyntaxHighlighting();
        MarkdownParser.WarmUp();

        BuildSceneGraph();

        if (AppState._topLevelGrid != null)
        {
            AppState._topLevelGrid.PointerMoved += (sender, args) =>
            {
                AppState._mousePos = args.Position;
            };
        }
    }

    private static void BuildSceneGraph(string? selectedCategory = null)
    {
        if (AppState._wgpuContext == null || AppState._font == null) return;

        DetachSceneGraphHandlers();

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

        static NavigationViewItem PageItem(string text, string icon, Func<FrameworkElement?> createPage)
            => new(text, icon, createPage);

        // Page visual trees are created on first selection to keep startup focused on the default page.
        var basicInputItem = PageItem("Basic Input", "🖱", BasicInputPage.Create);
        var chartShowcaseItem = PageItem("GPU Charting", "📊", ChartShowcasePage.Create);
        var panelsItem = PageItem("Layout Panels", "🔲", LayoutPanelsPage.Create);
        var textItem = PageItem("Text & Documents", "📄", TextDocumentsPage.Create);
        var markdownPlaygroundItem = PageItem("Markdown Playground", "📝", MarkdownPage.Create);
        var dataItem = PageItem("Data Virtualization", "📊", DataVirtualizationPage.Create);
        var virtualizationControlsItem = PageItem("Virtualization Controls", "🎛️", VirtualizationControlsPage.Create);
        var frameworkEffectsItem = PageItem("Framework Effects", "✨", FrameworkEffectsPage.Create);
        var imageEffectsItem = PageItem("Image Effects", "🖼️", ImageEffectsPage.Create);
        var gdiShowcaseItem = PageItem("GDI Shim Showcase", "🎨", GdiShowcasePage.Create);
        var glyphRunShowcaseItem = PageItem("Glyph Run Showcase", "🔤", GlyphRunShowcasePage.Create);
        var wpfShowcaseItem = PageItem("WPF Shim Showcase", "📐", WpfShowcasePage.Create);

        var computeItem = PageItem("Compute FX", "⚙", ComputeFxPage.Create);
        var motionAnimationsItem = PageItem("Motion & Animations", "🎬", MotionAnimationsPage.Create);
        var advancedItem = PageItem("Advanced Controls", "🛠", AdvancedControlsPage.Create);
        var keyboardParityItem = PageItem("Keyboard & Focus", "⌨️", KeyboardParityPage.Create);
        var themeShowcaseItem = PageItem("Theme Showcase", "🎨", ThemeShowcasePage.Create);
        var compositorItem = PageItem("Compositor API", "🎨", CompositorShowcasePage.Create);
        var splitViewItem = PageItem("SplitView Layout", "🪟", SplitViewShowcasePage.Create);
        var imageRepeatItem = PageItem("Image & Buttons", "🖼️", ImageRepeatShowcasePage.Create);
        var drawingContextItem = PageItem("Drawing Context", "📐", SamplePagePresenter.CreateDrawingContextShowcaseView);
        var fileStorageItem = PageItem("File Storage", "📁", SamplePagePresenter.CreateFileStorageShowcaseView);
        var stylesShowcaseItem = PageItem("Styles Showcase", "💅", SamplePagePresenter.CreateStylesShowcaseView);
        var motionMarkItem = PageItem("MotionMark Showcase", "🏁", SamplePagePresenter.CreateMotionMarkShowcaseView);
        var scriptsItem = PageItem("Typography & Scripts", "🔤", SamplePagePresenter.CreateTypographyScriptsView);
        var textInputItem = PageItem("Interactive Input", "⌨️", SamplePagePresenter.CreateInteractiveInputView);
        var lolsItem = PageItem("LOL/s Benchmark", "💥", LolsPage.Create);
        var radioButtonItem = PageItem("Radio Button", "🔘", RadioButtonPage.Create);
        var ratingControlItem = PageItem("Rating Control", "⭐", RatingControlPage.Create);
        var passwordBoxItem = PageItem("Password Box", "🔒", PasswordBoxPage.Create);
        var dxfViewerItem = PageItem("DXF CAD Viewer", "📐", DxfViewerPage.Create);
        var visualDesignerItem = PageItem("Visual Designer", "📐", VisualDesignerPage.Create);
        var pictureCachingItem = PageItem("Picture Caching", "🖼️", PictureShowcasePage.Create);
        var fontGlyphBrowserItem = PageItem("Font Glyph Browser", "🔤", FontGlyphBrowserPage.Create);
        var mesh3DViewerItem = PageItem("3D Mesh Viewer", "🧊", Mesh3DViewerPage.Create);
        var shaderToyPlaygroundItem = PageItem("ShaderToy Playground", "🔮", ShaderToyPlaygroundPage.Create);

        var wrapPanelItem = PageItem("Wrap Panel", "🔲", WrapPanelPage.Create);
        var dockPanelItem = PageItem("Dock Panel", "🪟", DockPanelPage.Create);
        var gridSplitterItem = PageItem("Grid Splitter", "↔️", GridSplitterPage.Create);
        var colorPickerItem = PageItem("Color Picker", "🎨", ColorPickerPage.Create);
        var vectorShapesItem = PageItem("Vector Shapes", "📐", VectorShapesPage.Create);
        var skiaSharpShimItem = PageItem("SkiaSharp Shim", "🦊", SkiaSharpShimPage.Create);
        var pathOpsItem = PageItem("Path Operations", "✂️", PathOpsPage.Create);

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
        AppState._navigationView.MenuItems.Add(shaderToyPlaygroundItem);

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
            AppState._navigationView.SettingsItem.PageFactory = SettingsPage.Create;
        }

        // Select default category or an environment-requested performance probe page.
        var initialItem = mesh3DViewerItem;
        if (!string.IsNullOrEmpty(selectedCategory))
        {
            foreach (var menuItem in AppState._navigationView.MenuItems)
            {
                if (string.Equals(menuItem.Text, selectedCategory, StringComparison.OrdinalIgnoreCase))
                {
                    initialItem = menuItem;
                    break;
                }
            }
        }
        if (SamplePerformanceBenchmark.RequestedPage is { } requestedPage)
        {
            foreach (var menuItem in AppState._navigationView.MenuItems)
            {
                if (string.Equals(menuItem.Text, requestedPage, StringComparison.OrdinalIgnoreCase))
                {
                    initialItem = menuItem;
                    break;
                }
            }
        }

        AppState._navigationView.SelectedItem = initialItem;
        SamplePerformanceBenchmark.StartRequestedWorkload(initialItem.Text);

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
        var statusAccent = new ThemeResourceBrush("SystemAccentColor");
        AppState._statsFpsRun = new Run(" --");
        AppState._statsCpuRun = new Run("  --.-- ms");
        AppState._statsTimingRun = new Run(" (Layout: --.--ms, Compile: --.--ms, Upload: --.--ms, Render: --.--ms)");
        AppState._statsDrawsRun = new Run(" --");
        AppState._statsVerticesRun = new Run("    - (Vec),     - (Txt)");
        AppState._statsAtlasRun = new Run(" --");
        AppState._statsCursorRun = new Run("(   -,    -)");
        AppState._statsFocusedRun = new Run("None        ");
        AppState._statsHotReloadRun = new Run("ready");
        AppState._statsText.Inlines.Add(new Run("FPS: "));
        AppState._statsText.Inlines.Add(new Bold(AppState._statsFpsRun) { Foreground = statusAccent });
        AppState._statsText.Inlines.Add(new Run(" | CPU: "));
        AppState._statsText.Inlines.Add(new Bold(AppState._statsCpuRun) { Foreground = statusAccent });
        AppState._statsText.Inlines.Add(AppState._statsTimingRun);
        AppState._statsText.Inlines.Add(new Run(" | Draws: "));
        AppState._statsText.Inlines.Add(new Bold(AppState._statsDrawsRun) { Foreground = statusAccent });
        AppState._statsText.Inlines.Add(new Run(" | Verts: "));
        AppState._statsText.Inlines.Add(AppState._statsVerticesRun);
        AppState._statsText.Inlines.Add(new Run(" | Atlas: "));
        AppState._statsText.Inlines.Add(new Bold(AppState._statsAtlasRun) { Foreground = statusAccent });
        AppState._statsText.Inlines.Add(new Run(" | Cursor: "));
        AppState._statsText.Inlines.Add(AppState._statsCursorRun);
        AppState._statsText.Inlines.Add(new Run(" | Focused: "));
        AppState._statsText.Inlines.Add(new Bold(AppState._statsFocusedRun) { Foreground = statusAccent });
        AppState._statsText.Inlines.Add(new Run(" | HR: "));
        AppState._statsText.Inlines.Add(new Bold(AppState._statsHotReloadRun) { Foreground = statusAccent });
        statusBar.Child = AppState._statsText;
        AppState._rootGrid.AddChild(statusBar);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(statusBar, 2);

        // Track global ThemeManager theme change event
        AppState._themeChangedHandler = () =>
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
        ThemeManager.ThemeChanged += AppState._themeChangedHandler;

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

        AppState._devToolsStateChangedHandler = (s, ev) =>
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
        DevToolsService.StateChanged += AppState._devToolsStateChangedHandler;
    }

    private static void ReloadSceneGraph(Window window)
    {
        var selectedCategory = AppState._activeCategory;
        HotReloadManager.ReloadWindowContent(window, () =>
        {
            BuildSceneGraph(selectedCategory);
            AttachPointerTracking();
            return AppState._topLevelGrid
                ?? throw new InvalidOperationException("The sample shell builder did not produce a root element.");
        });
    }

    private static void AttachPointerTracking()
    {
        if (AppState._topLevelGrid == null)
        {
            return;
        }

        AppState._topLevelGrid.PointerMoved += (sender, args) =>
        {
            AppState._mousePos = args.Position;
        };
    }

    private static void DetachSceneGraphHandlers()
    {
        if (AppState._themeChangedHandler != null)
        {
            ThemeManager.ThemeChanged -= AppState._themeChangedHandler;
            AppState._themeChangedHandler = null;
        }

        if (AppState._devToolsStateChangedHandler != null)
        {
            DevToolsService.StateChanged -= AppState._devToolsStateChangedHandler;
            AppState._devToolsStateChangedHandler = null;
        }
    }

    private static void OnWindowUpdate(double delta)
    {
        long updateStart = Stopwatch.GetTimestamp();
        UIThread.RunPending();
        AppState._rootGrid?.UpdateSampleAnimations((float)delta);

        if (AppState._animateGear && IsGearPageActive())
        {
            AppState._gearRotation += (float)delta * 1.2f;
            if (AppState._gearRotation > Math.PI * 2) AppState._gearRotation -= (float)(Math.PI * 2);
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

        SamplePerformanceBenchmark.RecordHostUpdate(Stopwatch.GetElapsedTime(updateStart));
    }

    private static bool IsGearPageActive()
    {
        return AppState._activeCategory is "Compute FX" or "Image Effects" or "Image & Buttons";
    }

    internal static void EnsureEffectResources()
    {
        if (AppState._wgpuContext is not { } context ||
            (AppState._offscreenCompositor != null &&
             AppState._compute != null &&
             AppState._canvasSourceTexture != null &&
             AppState._canvasTempTexture != null &&
             AppState._canvasBlurTexture != null &&
             AppState._canvasShadowTexture != null &&
             AppState._gearCanvasVisual != null))
        {
            return;
        }

        AppState._offscreenCompositor ??= new Compositor(context, TextureFormat.Rgba8Unorm)
        {
            VectorEngine = AppState.VectorEngine
        };
        AppState._compute ??= new ComputeAccelerator(context);
        AppState._canvasSourceTexture ??= new GpuTexture(
            context,
            600,
            600,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.StorageBinding | TextureUsage.CopySrc,
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        AppState._canvasTempTexture ??= new GpuTexture(
            context,
            600,
            600,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.StorageBinding,
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        AppState._canvasBlurTexture ??= new GpuTexture(
            context,
            600,
            600,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.StorageBinding,
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        AppState._canvasShadowTexture ??= new GpuTexture(
            context,
            600,
            600,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.StorageBinding,
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        if (AppState._gearCanvasVisual == null && AppState._font != null)
        {
            AppState._gearCanvasVisual = new GearCanvasVisual(AppState._font)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
        }
    }

    public static void OnWindowRender(double delta)
    {
        if (AppState._rootGrid == null || AppState._topLevelGrid == null || AppState._wgpuContext == null) return;
        if (AppState._screenCompositor == null) return;

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
        if (IsGearPageActive() && AppState._gearCanvasVisual != null)
        {
            EnsureEffectResources();

            Vector2 logicalWindowSize = AppState._topLevelGrid.Size;
            if ((logicalWindowSize.X <= 0f || logicalWindowSize.Y <= 0f) && AppState._window != null)
            {
                float dpiScale = (float)DisplayScaleResolver.ResolveWindowDisplayScale(AppState._window);
                logicalWindowSize = new Vector2(
                    AppState._window.FramebufferSize.X / dpiScale,
                    AppState._window.FramebufferSize.Y / dpiScale);
            }

            float winX = logicalWindowSize.X;
            float winY = logicalWindowSize.Y;

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

                AppState._offscreenCompositor!.RenderScene(AppState._gearCanvasVisual, canvasW, canvasH, AppState._canvasSourceTexture.ViewPtr);

                if (AppState._shadowRadius > 0)
                {
                    var shadowColor = new Vector4(0f, 0f, 0f, 0.65f);
                    AppState._compute!.ApplyDropShadow(AppState._canvasSourceTexture, AppState._canvasTempTexture, AppState._canvasShadowTexture, AppState._shadowOffset, shadowColor, AppState._shadowRadius);
                }

                if (AppState._blurRadius > 0)
                {
                    AppState._compute!.ApplyGaussianBlur(AppState._canvasSourceTexture, AppState._canvasTempTexture, AppState._canvasBlurTexture, AppState._blurRadius);
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
            var metrics = AppState._screenCompositor.Metrics;
            double measureArrangeMs = AppState._cpuFrameTimeMs - metrics.FrameTimeMs;
            if (measureArrangeMs < 0) measureArrangeMs = 0;
            AppState._statsFpsRun!.Text = $"{AppState._currentFps,3:F0}";
            AppState._statsCpuRun!.Text = $"{AppState._cpuFrameTimeMs,6:F2} ms";
            AppState._statsTimingRun!.Text = $" (Layout: {measureArrangeMs,5:F2}ms, Compile: {metrics.VisualTreeCompileTimeMs,5:F2}ms, Upload: {metrics.GpuUploadTimeMs,5:F2}ms, Render: {metrics.RenderPassTimeMs,5:F2}ms)";
            AppState._statsDrawsRun!.Text = $"{metrics.DrawCallsCount,3}";
            AppState._statsVerticesRun!.Text = $"{metrics.VectorVerticesCount,5} (Vec), {metrics.TextVerticesCount,5} (Txt)";
            AppState._statsAtlasRun!.Text = $"{metrics.PathAtlasCachedCount,3}";
            AppState._statsCursorRun!.Text = $"({AppState._mousePos.X,4:F0}, {AppState._mousePos.Y,4:F0})";
            AppState._statsFocusedRun!.Text = $"{AppState._activeFocusedName,-12}";
        }

        SamplePerformanceBenchmark.ObserveFrame(delta);
    }

    private static void Cleanup()
    {
        AppState._hotReloadRegistration?.Dispose();
        AppState._hotReloadRegistration = null;
        if (AppState._hotReloadCompletedHandler != null)
        {
            HotReloadManager.UpdateCompleted -= AppState._hotReloadCompletedHandler;
            AppState._hotReloadCompletedHandler = null;
        }
        DetachSceneGraphHandlers();
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
