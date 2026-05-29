using System;
using System.IO;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Text;
using ProGPU.Tests.Headless;
using ProGPU.Samples;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class SamplePagesTests
{
    private static void EnsureFontsAndStateLoaded()
    {
        if (PopupService.DefaultFont != null) return;

        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!File.Exists(fontPath))
        {
            fontPath = "Arial.ttf";
        }

        if (File.Exists(fontPath))
        {
            var font = new TtfFont(fontPath);
            PopupService.DefaultFont = font;
            AppState._font = font;
        }
        else
        {
            throw new FileNotFoundException("Arial.ttf is required to execute typography tests.");
        }

        // Load supplemental fonts safely with fallbacks
        string timesPath = "/System/Library/Fonts/Supplemental/Times New Roman.ttf";
        AppState._fontTimes = File.Exists(timesPath) ? new TtfFont(timesPath) : AppState._font;

        string courierPath = "/System/Library/Fonts/Supplemental/Courier New.ttf";
        AppState._fontCourier = File.Exists(courierPath) ? new TtfFont(courierPath) : AppState._font;

        string georgiaPath = "/System/Library/Fonts/Supplemental/Georgia.ttf";
        AppState._fontGeorgia = File.Exists(georgiaPath) ? new TtfFont(georgiaPath) : AppState._font;

        string comicPath = "/System/Library/Fonts/Supplemental/Comic Sans MS.ttf";
        AppState._fontComic = File.Exists(comicPath) ? new TtfFont(comicPath) : AppState._font;

        // Populate virtualized data
        AppState.GenerateLogItems();
    }

    private void RunPageTest(FrameworkElement page, string pageName)
    {
        EnsureFontsAndStateLoaded();

        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = page;

        // Double-render to guarantee layout stabilization and animation ticks
        window.Render();
        window.Render();

        byte[] pixels = window.ReadPixels();
        Assert.NotNull(pixels);
        Assert.Equal(1280 * 800 * 4, pixels.Length);

        // Validate that the page has rendered non-background pixels.
        // Default clear background color is approx RGBA: 20, 20, 30, 255.
        int nonBgCount = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte r = pixels[i + 0];
            byte g = pixels[i + 1];
            byte b = pixels[i + 2];

            if (Math.Abs(r - 20) > 5 || Math.Abs(g - 20) > 5 || Math.Abs(b - 30) > 5)
            {
                nonBgCount++;
            }
        }

        Console.WriteLine($"[SamplePagesTests] Page '{pageName}' rendered {nonBgCount} non-background pixels.");
        Assert.True(nonBgCount > 100, $"Page '{pageName}' rendered blank or empty. Only {nonBgCount} non-background pixels found.");

        string screenshotPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"page_{pageName.Replace(" ", "_").ToLower()}.png");
        window.SaveScreenshot(screenshotPath);
        Assert.True(File.Exists(screenshotPath));

        // Cleanup
        window.Content = null;
    }

    [Fact]
    public void Test_BasicInputPage_Renders()
    {
        RunPageTest(BasicInputPage.Create(), "Basic Input");
    }

    [Fact]
    public void Test_LayoutPanelsPage_Renders()
    {
        RunPageTest(LayoutPanelsPage.Create(), "Layout Panels");
    }

    [Fact]
    public void Test_LayoutPanelsPage_NavigationPane_State()
    {
        EnsureFontsAndStateLoaded();

        var nav = new NavigationView
        {
            Font = AppState._font,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var basicInputItem = new NavigationViewItem("Basic Input", "🖱", BasicInputPage.Create());
        var panelsItem = new NavigationViewItem("Layout Panels", "🔲", LayoutPanelsPage.Create());
        nav.MenuItems.Add(basicInputItem);
        nav.MenuItems.Add(panelsItem);

        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = nav;

        // Select basic input first
        nav.SelectedItem = basicInputItem;
        window.Render();

        // Switch to Layout Panels page
        nav.SelectedItem = panelsItem;
        window.Render();

        // Print details of the flat visible items
        var prop = typeof(NavigationView).GetProperty("FlatVisibleItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        var items = (System.Collections.Generic.List<NavigationViewItem>?)prop?.GetValue(nav);
        if (items != null)
        {
            foreach (var item in items)
            {
                Console.WriteLine($"[DIAG] Item: '{item.Text}', Parent: '{item.Parent?.GetType().Name}', Size: {item.Size}, Offset: {item.Offset}, Opacity: {item.Opacity}");
            }
        }

        // Clean up
        window.Content = null;
    }

    private void PrintVisualTree(ProGPU.Scene.Visual node, string indent)
    {
        Console.WriteLine($"{indent}- Type: {node.GetType().Name}, Size: {node.Size}, Offset: {node.Offset}, Opacity: {node.Opacity}");
        if (node is ProGPU.Scene.ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                PrintVisualTree(child, indent + "  ");
            }
        }
    }

    [Fact]
    public void Test_LayoutPanelsPage_FullTree()
    {
        EnsureFontsAndStateLoaded();

        var nav = new NavigationView
        {
            Font = AppState._font,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var basicInputItem = new NavigationViewItem("Basic Input", "🖱", BasicInputPage.Create());
        var panelsItem = new NavigationViewItem("Layout Panels", "🔲", LayoutPanelsPage.Create());
        nav.MenuItems.Add(basicInputItem);
        nav.MenuItems.Add(panelsItem);

        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = nav;

        // Select basic input first
        nav.SelectedItem = basicInputItem;
        window.Render();

        // Switch to Layout Panels page
        nav.SelectedItem = panelsItem;
        window.Render();

        Console.WriteLine("[DIAG_TREE] START");
        PrintVisualTree(nav, "");
        Console.WriteLine("[DIAG_TREE] END");

        // Clean up
        window.Content = null;
    }

    [Fact]
    public void Test_TextDocumentsPage_Renders()
    {
        RunPageTest(TextDocumentsPage.Create(), "Text & Documents");
    }

    [Fact]
    public void Test_ThemeShowcasePage_Renders()
    {
        RunPageTest(ThemeShowcasePage.Create(), "Theme Showcase");
    }

    [Fact]
    public void Test_TypographyScriptsPage_Renders()
    {
        RunPageTest(SamplePagePresenter.CreateTypographyScriptsView(), "Typography & Scripts");
    }

    [Fact]
    public void Test_DataVirtualizationPage_Renders()
    {
        RunPageTest(DataVirtualizationPage.Create(), "Data Virtualization");
    }

    [Fact]
    public void Test_FrameworkEffectsPage_Renders()
    {
        RunPageTest(FrameworkEffectsPage.Create(), "Framework Effects");
    }

    [Fact]
    public unsafe void Test_ComputeFxPage_Renders()
    {
        EnsureFontsAndStateLoaded();
        
        var context = HeadlessWindow.Shared.Context;
        AppState._offscreenCompositor = new Compositor(context, Silk.NET.WebGPU.TextureFormat.Rgba8Unorm);
        AppState._compute = new ProGPU.Compute.ComputeAccelerator(context);
        
        AppState._canvasSourceTexture = new ProGPU.Backend.GpuTexture(context, 600, 600, Silk.NET.WebGPU.TextureFormat.Rgba8Unorm, 
            Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.TextureBinding | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.CopyDst | Silk.NET.WebGPU.TextureUsage.StorageBinding);
        AppState._canvasTempTexture = new ProGPU.Backend.GpuTexture(context, 600, 600, Silk.NET.WebGPU.TextureFormat.Rgba8Unorm, 
            Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.TextureBinding | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.CopyDst | Silk.NET.WebGPU.TextureUsage.StorageBinding);
        AppState._canvasBlurTexture = new ProGPU.Backend.GpuTexture(context, 600, 600, Silk.NET.WebGPU.TextureFormat.Rgba8Unorm, 
            Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.TextureBinding | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.CopyDst | Silk.NET.WebGPU.TextureUsage.StorageBinding);
        AppState._canvasShadowTexture = new ProGPU.Backend.GpuTexture(context, 600, 600, Silk.NET.WebGPU.TextureFormat.Rgba8Unorm, 
            Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.TextureBinding | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.CopyDst | Silk.NET.WebGPU.TextureUsage.StorageBinding);

        // Render Page
        var page = ComputeFxPage.Create();
        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = page;

        // Perform initial measure and arrange to set sizes on layout controls
        window.Render();

        // Now simulate the offscreen cogs rendering just like MainWindowController.cs!
        if (AppState._gearCanvasVisual != null)
        {
            AppState._gearCanvasVisual.Measure(new Vector2(1280f - 300f, 800f - 140f));
            AppState._gearCanvasVisual.Arrange(new Rect(0, 0, 1280f - 300f, 800f - 140f));
            AppState._gearCanvasVisual.UpdateRotation(AppState._gearRotation);

            uint canvasW = (uint)Math.Max(1f, AppState._gearCanvasVisual.Size.X);
            uint canvasH = (uint)Math.Max(1f, AppState._gearCanvasVisual.Size.Y);

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

        // Render again to draw the populated textures onto the screen
        window.Render();

        // Save screenshot
        string screenshotPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "page_compute_fx.png");
        window.SaveScreenshot(screenshotPath);

        // Write diagnostics
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[DIAG] _gearCanvasVisual Size: {AppState._gearCanvasVisual?.Size}, Offset: {AppState._gearCanvasVisual?.Offset}");
        if (AppState._gearCanvasVisual != null)
        {
            sb.AppendLine($"[DIAG] _gearCanvasVisual Children count: {AppState._gearCanvasVisual.Children.Count}");
            for (int i = 0; i < AppState._gearCanvasVisual.Children.Count; i++)
            {
                var child = AppState._gearCanvasVisual.Children[i];
                sb.AppendLine($"  Child {i}: Type={child.GetType().Name}, Size={child.Size}, Offset={child.Offset}, Transform={child.Transform}");
                if (child is DrawingVisual dv)
                {
                    sb.AppendLine($"    Commands count: {dv.Context.Commands.Count}");
                }
            }
        }
        if (AppState._offscreenCompositor != null)
        {
            var drawCallsField = typeof(Compositor).GetField("_drawCalls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var drawCalls = (System.Collections.IList?)drawCallsField?.GetValue(AppState._offscreenCompositor);
            sb.AppendLine($"[DIAG] _offscreenCompositor Draw calls count: {drawCalls?.Count}");
            sb.AppendLine($"[DIAG] _offscreenCompositor VectorVertexCount: {AppState._offscreenCompositor.VectorVertexCount}");
        }

        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag_compute_fx.txt"), sb.ToString());

        // Cleanup
        window.Content = null;
        AppState._offscreenCompositor.Dispose();
        AppState._offscreenCompositor = null;
        AppState._compute.Dispose();
        AppState._compute = null;
        AppState._canvasSourceTexture.Dispose();
        AppState._canvasSourceTexture = null;
        AppState._canvasTempTexture.Dispose();
        AppState._canvasTempTexture = null;
        AppState._canvasBlurTexture.Dispose();
        AppState._canvasBlurTexture = null;
        AppState._canvasShadowTexture.Dispose();
        AppState._canvasShadowTexture = null;
    }

    [Fact]
    public void Test_KeyboardFocusPage_Renders()
    {
        RunPageTest(KeyboardParityPage.Create(), "Keyboard & Focus");
    }

    [Fact]
    public void Test_DxfViewerPage_Renders_GpuTransforms()
    {
        EnsureFontsAndStateLoaded();
        
        bool savedEnableGpuTransforms = AppState.EnableGpuTransforms;
        bool savedEnableStatic = AppState.EnableStaticGpuBuffers;
        bool savedEnableCaching = AppState.EnableCommandCaching;
        
        try
        {
            AppState.EnableGpuTransforms = true;
            AppState.EnableStaticGpuBuffers = false;
            AppState.EnableCommandCaching = false;
            
            var page = DxfViewerPage.Create();
            RunPageTest(page, "Dxf Viewer Gpu Transforms");
        }
        finally
        {
            AppState.EnableGpuTransforms = savedEnableGpuTransforms;
            AppState.EnableStaticGpuBuffers = savedEnableStatic;
            AppState.EnableCommandCaching = savedEnableCaching;
        }
    }


    [Fact]
    public void Benchmark_CacheAsLayer_Performance_Comparison()
    {
        EnsureFontsAndStateLoaded();

        // 1. Initialize NavigationView with items to populate the sidebar
        var nav = new NavigationView
        {
            Font = AppState._font,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsPaneOpen = true
        };
        var basicInputItem = new NavigationViewItem("Basic Input", "🖱", BasicInputPage.Create());
        var panelsItem = new NavigationViewItem("Layout Panels", "🔲", LayoutPanelsPage.Create());
        nav.MenuItems.Add(basicInputItem);
        nav.MenuItems.Add(panelsItem);

        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = nav;

        // Select item and render once to perform initial layout and stabilize
        nav.SelectedItem = basicInputItem;
        window.Render();
        window.Render();

        // Get the internal static navigation pane panel via reflection
        var panePanelField = typeof(NavigationView).GetField("_panePanel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(panePanelField);
        var panePanel = (Visual?)panePanelField.GetValue(nav);
        Assert.NotNull(panePanel);

        // ==========================================
        // SCENARIO 1: FULL APPLICATION VIEW (REAL-WORLD)
        // ==========================================

        // --- MEASURE UNCACHED ---
        panePanel.CacheAsLayer = false;
        panePanel.IsDirty = true;
        window.Render();

        int uncachedDrawCalls1 = GetCompositorDrawCallsCount(window.Compositor);
        int uncachedVectorVertices1 = window.Compositor.VectorVertexCount;
        int uncachedTextVertices1 = window.Compositor.TextVertexCount;
        int uncachedTextureVertices1 = window.Compositor.TextureVertexCount;

        for (int i = 0; i < 10; i++)
        {
            panePanel.IsDirty = true;
            window.Render();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int iterations = 200;
        for (int i = 0; i < iterations; i++)
        {
            panePanel.IsDirty = true; 
            window.Render();
        }
        sw.Stop();
        double uncachedAvgMs1 = sw.Elapsed.TotalMilliseconds / iterations;

        // --- MEASURE CACHED ---
        panePanel.CacheAsLayer = true;
        panePanel.IsDirty = true;
        window.Render();
        panePanel.IsDirty = false;
        window.Render();

        int cachedDrawCalls1 = GetCompositorDrawCallsCount(window.Compositor);
        int cachedVectorVertices1 = window.Compositor.VectorVertexCount;
        int cachedTextVertices1 = window.Compositor.TextVertexCount;
        int cachedTextureVertices1 = window.Compositor.TextureVertexCount;

        for (int i = 0; i < 10; i++)
        {
            window.Render();
        }

        sw.Restart();
        for (int i = 0; i < iterations; i++)
        {
            window.Render();
        }
        sw.Stop();
        double cachedAvgMs1 = sw.Elapsed.TotalMilliseconds / iterations;


        // ==========================================
        // SCENARIO 2: ISOLATED COMPONENT RENDERING
        // ==========================================
        
        // Render ONLY the sidebar pane to isolate the component
        window.Content = panePanel as FrameworkElement;
        window.Render();
        window.Render();

        // --- MEASURE UNCACHED ---
        panePanel.CacheAsLayer = false;
        panePanel.IsDirty = true;
        window.Render();

        int uncachedDrawCalls2 = GetCompositorDrawCallsCount(window.Compositor);
        int uncachedVectorVertices2 = window.Compositor.VectorVertexCount;
        int uncachedTextVertices2 = window.Compositor.TextVertexCount;
        int uncachedTextureVertices2 = window.Compositor.TextureVertexCount;

        for (int i = 0; i < 10; i++)
        {
            panePanel.IsDirty = true;
            window.Render();
        }

        sw.Restart();
        for (int i = 0; i < iterations; i++)
        {
            panePanel.IsDirty = true;
            window.Render();
        }
        sw.Stop();
        double uncachedAvgMs2 = sw.Elapsed.TotalMilliseconds / iterations;

        // --- MEASURE CACHED ---
        panePanel.CacheAsLayer = true;
        panePanel.IsDirty = true;
        window.Render();
        panePanel.IsDirty = false;
        window.Render();

        int cachedDrawCalls2 = GetCompositorDrawCallsCount(window.Compositor);
        int cachedVectorVertices2 = window.Compositor.VectorVertexCount;
        int cachedTextVertices2 = window.Compositor.TextVertexCount;
        int cachedTextureVertices2 = window.Compositor.TextureVertexCount;

        for (int i = 0; i < 10; i++)
        {
            window.Render();
        }

        sw.Restart();
        for (int i = 0; i < iterations; i++)
        {
            window.Render();
        }
        sw.Stop();
        double cachedAvgMs2 = sw.Elapsed.TotalMilliseconds / iterations;

        // --- PRINT RESULTS ---
        Console.WriteLine("\n================================================================================");
        Console.WriteLine("BENCHMARK RESULTS: CacheAsLayer VS Frame-by-Frame Redraw");
        Console.WriteLine("================================================================================");
        Console.WriteLine("SCENARIO 1: FULL APP VIEW (NavigationView + Active Page)");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"Uncached Draw Calls:       {uncachedDrawCalls1}");
        Console.WriteLine($"Cached Draw Calls:         {cachedDrawCalls1} (including 1 texture cache draw)");
        Console.WriteLine($"Uncached Vector Vertices:  {uncachedVectorVertices1}");
        Console.WriteLine($"Cached Vector Vertices:    {cachedVectorVertices1}");
        Console.WriteLine($"Uncached Text Vertices:    {uncachedTextVertices1}");
        Console.WriteLine($"Cached Text Vertices:      {cachedTextVertices1}");
        Console.WriteLine($"Uncached Texture Vertices: {uncachedTextureVertices1}");
        Console.WriteLine($"Cached Texture Vertices:   {cachedTextureVertices1}");
        Console.WriteLine($"Uncached Avg Render Time:  {uncachedAvgMs1:F4} ms / frame");
        Console.WriteLine($"Cached Avg Render Time:    {cachedAvgMs1:F4} ms / frame");
        double speedup1 = uncachedAvgMs1 / cachedAvgMs1;
        Console.WriteLine($"Acceleration Factor:       {speedup1:F2}x faster");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("SCENARIO 2: ISOLATED SIDEBAR COMPONENT (NavigationViewPane)");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"Uncached Draw Calls:       {uncachedDrawCalls2}");
        Console.WriteLine($"Cached Draw Calls:         {cachedDrawCalls2} (exactly 1 texture cache draw)");
        Console.WriteLine($"Uncached Vector Vertices:  {uncachedVectorVertices2}");
        Console.WriteLine($"Cached Vector Vertices:    {cachedVectorVertices2}");
        Console.WriteLine($"Uncached Text Vertices:    {uncachedTextVertices2}");
        Console.WriteLine($"Cached Text Vertices:      {cachedTextVertices2}");
        Console.WriteLine($"Uncached Texture Vertices: {uncachedTextureVertices2}");
        Console.WriteLine($"Cached Texture Vertices:   {cachedTextureVertices2}");
        Console.WriteLine($"Uncached Avg Render Time:  {uncachedAvgMs2:F4} ms / frame");
        Console.WriteLine($"Cached Avg Render Time:    {cachedAvgMs2:F4} ms / frame");
        double speedup2 = uncachedAvgMs2 / cachedAvgMs2;
        Console.WriteLine($"Acceleration Factor:       {speedup2:F2}x faster");
        Console.WriteLine("================================================================================\n");

        // Clean up
        window.Content = null;
    }

    private int GetCompositorDrawCallsCount(Compositor compositor)
    {
        var drawCallsField = typeof(Compositor).GetField("_drawCalls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var drawCalls = (System.Collections.IList?)drawCallsField?.GetValue(compositor);
        return drawCalls?.Count ?? 0;
    }

    [Fact]
    public void Test_Compositor_IsCacheAsLayerEnabled_Global_Setting()
    {
        EnsureFontsAndStateLoaded();

        var nav = new NavigationView
        {
            Font = AppState._font,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var basicInputItem = new NavigationViewItem("Basic Input", "🖱", BasicInputPage.Create());
        nav.MenuItems.Add(basicInputItem);

        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = nav;

        var panePanelField = typeof(NavigationView).GetField("_panePanel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var panePanel = (Visual?)panePanelField?.GetValue(nav);
        Assert.NotNull(panePanel);

        // Ensure panePanel has CacheAsLayer = true
        panePanel.CacheAsLayer = true;

        // --- Test 1: Global setting is ENABLED (default) ---
        Compositor.IsCacheAsLayerEnabled = true;
        panePanel.IsDirty = true;
        window.Render();
        panePanel.IsDirty = false;
        window.Render();

        // Cached texture draw call should be compiled (resulting in texture vertices)
        int cachedTextureVertices = window.Compositor.TextureVertexCount;
        Assert.True(cachedTextureVertices > 0, "Texture vertices should be greater than 0 when CacheAsLayer is globally enabled.");

        // --- Test 2: Global setting is DISABLED ---
        Compositor.IsCacheAsLayerEnabled = false;
        panePanel.IsDirty = true;
        window.Render();

        // No texture draw calls should be compiled for the cached layer
        int disabledTextureVertices = window.Compositor.TextureVertexCount;
        Assert.Equal(0, disabledTextureVertices);

        // Restore global setting to default
        Compositor.IsCacheAsLayerEnabled = true;
        window.Content = null;
    }

    [Fact]
    public void Test_DataGrid_ColumnResize()
    {
        EnsureFontsAndStateLoaded();

        var dataGrid = new DataGrid
        {
            Font = AppState._font,
            RowHeight = 28f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            WidthConstraint = 600f,
            HeightConstraint = 300f
        };

        // Define columns: 
        // ID: Pixel 70
        // Name: Star 1
        // Status: Auto
        // Latency: Pixel 120
        dataGrid.Columns.Add(new DataGridColumn("ID", 70f, "Id"));
        dataGrid.Columns.Add(new DataGridColumn("Activity Name", "*", "Name"));
        dataGrid.Columns.Add(new DataGridColumn("Status", "Auto", "Status"));
        dataGrid.Columns.Add(new DataGridColumn("Latency (ms)", 120f, "Latency"));

        // Add mock items to ensure Auto has size
        dataGrid.AddItem(new LogItem { Id = 1, Name = "Render Frame", Status = "Pending", Latency = 16.6f });
        dataGrid.AddItem(new LogItem { Id = 2, Name = "Compute Shader", Status = "CompletedSuccessfully", Latency = 4.2f });

        var window = HeadlessWindow.Shared;
        window.Resize(1000, 600);
        window.Content = dataGrid;

        // Perform initial stable layout
        window.Render();

        // 1. Check initial column widths
        // Available width inside layout will be WidthConstraint = 600f.
        // Allocated: ID (70) + Status (Auto, which is header length ~100px or items ~150px) + Latency (120)
        // Let's print actual resolved widths
        Console.WriteLine($"[DIAG_GRID] Initial columns widths:");
        for (int i = 0; i < dataGrid.Columns.Count; i++)
        {
            Console.WriteLine($"  Col {i} ({dataGrid.Columns[i].Header}): Width={dataGrid.Columns[i].Width.Value} ({dataGrid.Columns[i].Width.UnitType}), ActualWidth={dataGrid.Columns[i].ActualWidth}");
        }

        float col0_initialWidth = dataGrid.Columns[0].ActualWidth;
        float col1_initialWidth = dataGrid.Columns[1].ActualWidth;

        // 2. Drag separator of Column 0 (ID). The separator is at col0_initialWidth = 70f.
        float separatorX = col0_initialWidth;
        var pressEvent = new PointerRoutedEventArgs
        {
            Position = new Vector2(separatorX, 15f),
            IsLeftButtonPressed = true
        };
        dataGrid.OnPointerPressed(pressEvent);

        // Move mouse by 50px to the right to expand column 0
        var moveEvent = new PointerRoutedEventArgs
        {
            Position = new Vector2(separatorX + 50f, 15f),
            IsLeftButtonPressed = true
        };
        dataGrid.OnPointerMoved(moveEvent);

        // Release mouse
        var releaseEvent = new PointerRoutedEventArgs
        {
            Position = new Vector2(separatorX + 50f, 15f),
            IsLeftButtonPressed = false
        };
        dataGrid.OnPointerReleased(releaseEvent);

        // Render to stabilization layout
        window.Render();

        Console.WriteLine($"[DIAG_GRID] Resized columns widths:");
        for (int i = 0; i < dataGrid.Columns.Count; i++)
        {
            Console.WriteLine($"  Col {i} ({dataGrid.Columns[i].Header}): Width={dataGrid.Columns[i].Width.Value} ({dataGrid.Columns[i].Width.UnitType}), ActualWidth={dataGrid.Columns[i].ActualWidth}");
        }

        // Verify column 0 has been resized from 70f to 120f
        Assert.Equal(120f, dataGrid.Columns[0].ActualWidth);
        Assert.True(dataGrid.Columns[0].Width.IsPixel);

        // Verify column 1 (Star) shrunk by 50f to accommodate column 0's expansion
        Assert.Equal(col1_initialWidth - 50f, dataGrid.Columns[1].ActualWidth, 0.5f);

        // 3. Drag separator of Column 1 (Activity Name), which is a Star column.
        // Its separator is at Columns[0].ActualWidth + Columns[1].ActualWidth = 120f + (col1_initialWidth - 50f)
        float separatorX1 = dataGrid.Columns[0].ActualWidth + dataGrid.Columns[1].ActualWidth;
        var pressEvent1 = new PointerRoutedEventArgs
        {
            Position = new Vector2(separatorX1, 15f),
            IsLeftButtonPressed = true
        };
        dataGrid.OnPointerPressed(pressEvent1);

        // Drag by -30px to the left to shrink Column 1
        var moveEvent1 = new PointerRoutedEventArgs
        {
            Position = new Vector2(separatorX1 - 30f, 15f),
            IsLeftButtonPressed = true
        };
        dataGrid.OnPointerMoved(moveEvent1);

        var releaseEvent1 = new PointerRoutedEventArgs
        {
            Position = new Vector2(separatorX1 - 30f, 15f),
            IsLeftButtonPressed = false
        };
        dataGrid.OnPointerReleased(releaseEvent1);

        window.Render();

        Console.WriteLine($"[DIAG_GRID] After Star resize columns widths:");
        for (int i = 0; i < dataGrid.Columns.Count; i++)
        {
            Console.WriteLine($"  Col {i} ({dataGrid.Columns[i].Header}): Width={dataGrid.Columns[i].Width.Value} ({dataGrid.Columns[i].Width.UnitType}), ActualWidth={dataGrid.Columns[i].ActualWidth}");
        }

        // Verify Column 1 is now a Pixel column, and its ActualWidth has shrunk by 30f
        Assert.True(dataGrid.Columns[1].Width.IsPixel);
        Assert.Equal(col1_initialWidth - 50f - 30f, dataGrid.Columns[1].ActualWidth, 0.5f);

        // 4. Drag separator of Column 2 (Status), which is an Auto column.
        // Its separator is at Columns[0].ActualWidth + Columns[1].ActualWidth + Columns[2].ActualWidth
        float separatorX2 = dataGrid.Columns[0].ActualWidth + dataGrid.Columns[1].ActualWidth + dataGrid.Columns[2].ActualWidth;
        float col2_initialWidth = dataGrid.Columns[2].ActualWidth;
        var pressEvent2 = new PointerRoutedEventArgs
        {
            Position = new Vector2(separatorX2, 15f),
            IsLeftButtonPressed = true
        };
        dataGrid.OnPointerPressed(pressEvent2);

        // Drag by +40px to the right to expand Column 2
        var moveEvent2 = new PointerRoutedEventArgs
        {
            Position = new Vector2(separatorX2 + 40f, 15f),
            IsLeftButtonPressed = true
        };
        dataGrid.OnPointerMoved(moveEvent2);

        var releaseEvent2 = new PointerRoutedEventArgs
        {
            Position = new Vector2(separatorX2 + 40f, 15f),
            IsLeftButtonPressed = false
        };
        dataGrid.OnPointerReleased(releaseEvent2);

        window.Render();

        Console.WriteLine($"[DIAG_GRID] After Auto resize columns widths:");
        for (int i = 0; i < dataGrid.Columns.Count; i++)
        {
            Console.WriteLine($"  Col {i} ({dataGrid.Columns[i].Header}): Width={dataGrid.Columns[i].Width.Value} ({dataGrid.Columns[i].Width.UnitType}), ActualWidth={dataGrid.Columns[i].ActualWidth}");
        }

        // Verify Column 2 is now a Pixel column, and its ActualWidth has expanded by 40f
        Assert.True(dataGrid.Columns[2].Width.IsPixel);
        Assert.Equal(col2_initialWidth + 40f, dataGrid.Columns[2].ActualWidth, 0.5f);

        // Cleanup
        window.Content = null;
    }

    [Fact]
    public void Test_Compositor_GpuTransforms_BypassesScreenClipping()
    {
        EnsureFontsAndStateLoaded();

        var window = HeadlessWindow.Shared;
        var compositor = window.Compositor;

        var drawingContext = new ProGPU.Scene.DrawingContext();
        var pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 1f);

        // Push a clip that would normally crop/clamp coordinates outside [0, 100]
        drawingContext.PushClip(new Rect(0, 0, 100, 100));

        // Draw a line located far outside the clip boundaries (e.g. from 500 to 600)
        // Under standard CPU transforms, this line would be clamped to [100, 100]
        // But under GPU transforms (Option A), the vertices must preserve their original CAD coordinate values!
        drawingContext.DrawLine(pen, new Vector2(500f, 500f), new Vector2(600f, 600f));
        drawingContext.PopClip();

        // Compile with UseGpuTransforms = true
        var cmd = drawingContext.Commands[1];
        cmd.UseGpuTransforms = true;
        cmd.CameraView = Matrix4x4.Identity;

        var buffer = compositor.CompileStaticDxf(new List<ProGPU.Scene.RenderCommand> { cmd });

        // Retrieve compiled vertices from the buffer
        var vertices = buffer.VectorVertices;

        // Since UseGpuTransforms is active, the vertices should NOT have been clamped to [100, 100]!
        // They must preserve their original pre-centered coordinate values!
        var v0 = vertices[0];
        var v2 = vertices[2];

        Assert.Equal(500f, v0.Position.X);
        Assert.Equal(500f, v0.Position.Y);
        Assert.Equal(600f, v2.Position.X);
        Assert.Equal(600f, v2.Position.Y);

        buffer.Dispose();
        window.Content = null;
    }

    [Fact]
    public void Test_DxfCanvasControl_Invalidates_OnSizeChange()
    {
        EnsureFontsAndStateLoaded();

        bool savedEnableStatic = AppState.EnableStaticGpuBuffers;
        var savedCompositor = AppState._screenCompositor;

        try
        {
            AppState.EnableStaticGpuBuffers = true;
            var window = HeadlessWindow.Shared;
            AppState._screenCompositor = window.Compositor;

            var ctrl = new DxfCanvasControl();
            var doc = SampleDxfGenerator.GenerateSample();
            ctrl.LoadDocument(doc);

            window.Resize(800, 600);
            window.Content = ctrl;

            // Force initial render to build static buffer and cache commands
            window.Render();

            // Access internal private fields via reflection to verify they are set
            var lastSizeField = typeof(DxfCanvasControl).GetField("_lastSize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(lastSizeField);
            var initialLastSize = (Vector2?)lastSizeField.GetValue(ctrl);
            Assert.NotNull(initialLastSize);
            Assert.Equal(800f, initialLastSize.Value.X);
            Assert.Equal(600f, initialLastSize.Value.Y);

            // Get the compiled static buffer
            var staticBufferField = typeof(DxfCanvasControl).GetField("_staticBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(staticBufferField);
            var initialBuffer = staticBufferField.GetValue(ctrl);
            Assert.NotNull(initialBuffer);

            // Now resize the window!
            window.Resize(1024, 768);
            
            // Force render again, which should invalidate and rebuild because size changed!
            window.Render();

            var newLastSize = (Vector2?)lastSizeField.GetValue(ctrl);
            Assert.NotNull(newLastSize);
            Assert.Equal(1024f, newLastSize.Value.X);
            Assert.Equal(768f, newLastSize.Value.Y);

            // Clean up
            window.Content = null;
        }
        finally
        {
            AppState.EnableStaticGpuBuffers = savedEnableStatic;
            AppState._screenCompositor = savedCompositor;
        }
    }

    [Fact]
    public void Test_MarkdownTextBlock_Virtualization_ScrollAndRender()
    {
        EnsureFontsAndStateLoaded();

        string specPath = "/Users/wieslawsoltes/Downloads/spec.txt";
        string markdownContent = "";
        if (File.Exists(specPath))
        {
            markdownContent = File.ReadAllText(specPath);
        }
        else
        {
            // Fallback: Generate a massive markdown document of 2000 lines
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Mock Massive Markdown Document");
            for (int i = 0; i < 500; i++)
            {
                sb.AppendLine($"## Section {i}");
                sb.AppendLine($"This is paragraph {i} of a massive mock document to verify virtualized block-based flow layout and rendering performance.");
                sb.AppendLine($"- List item A for block {i}");
                sb.AppendLine($"- List item B for block {i}");
            }
            markdownContent = sb.ToString();
        }

        var scrollViewer = new ScrollViewer
        {
            WidthConstraint = 800f,
            HeightConstraint = 600f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var markdownBlock = new MarkdownTextBlock
        {
            Font = AppState._font,
            FontSize = 14f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        scrollViewer.Content = markdownBlock;

        var window = HeadlessWindow.Shared;
        window.Resize(800, 600);
        window.Content = scrollViewer;

        // 1. Initial Load & Render
        var sw = System.Diagnostics.Stopwatch.StartNew();
        markdownBlock.Markdown = markdownContent;
        window.Render();
        sw.Stop();

        Console.WriteLine($"[TEST_VIRTUALIZATION] Spec loaded and first frame rendered in {sw.Elapsed.TotalMilliseconds:F2} ms");
        
        // Assert that the initial load + layout pass was extremely fast under virtualization
        Assert.True(sw.Elapsed.TotalMilliseconds < 500, $"Initial virtualized layout took too long: {sw.Elapsed.TotalMilliseconds} ms");

        // Verify that we have some positioned characters rendered in the viewport
        Assert.NotEmpty(markdownBlock.PositionedChars);
        int initialCharCount = markdownBlock.PositionedChars.Count;
        Console.WriteLine($"[TEST_VIRTUALIZATION] Visible characters at offset 0: {initialCharCount}");

        // Retrieve total content height (scrollbar scrollable range)
        float totalHeight = scrollViewer.ContentHeight;
        Console.WriteLine($"[TEST_VIRTUALIZATION] Total scrollable content height: {totalHeight} px");
        Assert.True(totalHeight > 2000f, $"Total content height should be massive, but was {totalHeight} px");


        // 2. Scroll the entire document in 1000px increments
        float scrollStep = 1000f;
        int scrollFrames = 0;
        int maxCharsRendered = 0;
        var renderedBlocks = new HashSet<Block>();
        List<Block>? allBlocks = null;

        var propBlocks = typeof(MarkdownTextBlock).GetField("_blocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        for (float offset = 0f; offset <= scrollViewer.ContentHeight; offset += scrollStep)
        {
            scrollViewer.VerticalOffset = offset;
            
            // Force a render tick to arrange and layout newly visible blocks
            window.Render();
            scrollFrames++;

            var currentChars = markdownBlock.PositionedChars;
            Assert.NotEmpty(currentChars); // MUST have active characters in viewport at ALL scroll offsets!
            maxCharsRendered = Math.Max(maxCharsRendered, currentChars.Count);

            // Access the blocks to check the active memory recycling
            var blocks = (System.Collections.Generic.List<Block>?)propBlocks?.GetValue(markdownBlock);
            if (blocks != null)
            {
                allBlocks = blocks;
                int activeBlockCount = 0;
                foreach (var b in blocks)
                {
                    if (b.IsLayoutValid)
                    {
                        activeBlockCount++;
                        renderedBlocks.Add(b);
                    }
                }
                // Memory Recycling check: Only blocks intersecting viewport + buffer should be valid/measured
                // In a massive document of ~2,000 blocks, at most ~30-90 blocks should be valid at any scroll offset!
                Assert.True(activeBlockCount < 120, $"Memory retention failure: {activeBlockCount} blocks are currently holding layout data in memory.");
            }
        }

        // 3. Force one final render at the exact bottom to capture any trailing blocks
        scrollViewer.VerticalOffset = scrollViewer.ContentHeight;
        window.Render();

        var finalBlocks = (System.Collections.Generic.List<Block>?)propBlocks?.GetValue(markdownBlock);
        if (finalBlocks != null)
        {
            foreach (var b in finalBlocks)
            {
                if (b.IsLayoutValid)
                {
                    renderedBlocks.Add(b);
                }
            }
        }

        // Validate that ALL blocks were rendered at least once!
        Assert.NotNull(allBlocks);
        Console.WriteLine($"[TEST_VIRTUALIZATION] Total blocks parsed: {allBlocks.Count}, Unique blocks rendered during scroll: {renderedBlocks.Count}");
        
        if (allBlocks.Count != renderedBlocks.Count)
        {
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < allBlocks.Count; k++)
            {
                if (!renderedBlocks.Contains(allBlocks[k]))
                {
                    sb.AppendLine($"Block {k}/{allBlocks.Count}: {allBlocks[k].GetType().Name}, YOffset: {allBlocks[k].CachedYOffset}, Height: {allBlocks[k].CachedHeight}");
                }
            }
            throw new Exception($"Missing blocks:\n{sb}");
        }

        Assert.Equal(allBlocks.Count, renderedBlocks.Count);

        Console.WriteLine($"[TEST_VIRTUALIZATION] Successfully scrolled {scrollFrames} frames through the entire {scrollViewer.ContentHeight:F0}px document.");
        Console.WriteLine($"[TEST_VIRTUALIZATION] Maximum active characters in viewport: {maxCharsRendered}");
        Console.WriteLine($"[TEST_VIRTUALIZATION] Memory retention test PASSED: strictly < 120 active blocks held in memory at any time.");
        Console.WriteLine($"[TEST_VIRTUALIZATION] Completeness validation PASSED: 100% of the parsed blocks ({allBlocks.Count}/{allBlocks.Count}) were successfully laid out and rendered.");

        // Clean up
        window.Content = null;
    }

    [Fact]
    public void Test_ScrollViewer_Scrollbar_HoverAndDrag()
    {
        EnsureFontsAndStateLoaded();

        var scrollViewer = new ScrollViewer
        {
            WidthConstraint = 200f,
            HeightConstraint = 100f,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        // Put a tall child content so that the scrollbar is active
        var child = new Border
        {
            WidthConstraint = 200f,
            HeightConstraint = 1000f
        };
        scrollViewer.Content = child;

        var window = HeadlessWindow.Shared;
        window.Resize(200, 100);
        window.Content = scrollViewer;
        window.Render();

        // Initially pointer is not over scrollbar, vertical offset is 0
        var isPointerOverField = typeof(ScrollViewer).GetField("_isPointerOverScrollbar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var isDraggingField = typeof(ScrollViewer).GetField("_isDraggingVert", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.False((bool?)isPointerOverField?.GetValue(scrollViewer) ?? true);
        Assert.False((bool?)isDraggingField?.GetValue(scrollViewer) ?? true);
        Assert.Equal(0f, scrollViewer.VerticalOffset);

        // Simulate pointer entering the scrollbar area (right edge, e.g. x = 195, y = 10)
        // Since coordinate mapping uses ScreenPosition, we should set both Position and ScreenPosition.
        // The scrollbar width is 8px, track is at Size.X - scrollbarWidth - padding.
        // Size.X = 200. scrollbarWidth = 8, padding = 4 (for non-hover), so right edge hover threshold is x >= 200 - 12 = 188.
        var pointerMovedArgs = new PointerRoutedEventArgs
        {
            ScreenPosition = new Vector2(195f, 10f),
            Position = new Vector2(195f, 10f)
        };
        scrollViewer.OnPointerMoved(pointerMovedArgs);

        // Verify it detected hover!
        Assert.True((bool?)isPointerOverField?.GetValue(scrollViewer) ?? false);

        // Simulate pointer pressing the scrollbar thumb
        // When contentHeight = 1000, viewportHeight = 100.
        // thumbHeight = Max(20, (100 / 1000) * 100) = Max(20, 10) = 20.
        // thumbY = (0 / 900) * (100 - 20) = 0.
        // So thumb is at Y in [0, 20]. A press at (195, 10) should hit the thumb.
        var pointerPressedArgs = new PointerRoutedEventArgs
        {
            ScreenPosition = new Vector2(195f, 10f),
            Position = new Vector2(195f, 10f)
        };
        scrollViewer.OnPointerPressed(pointerPressedArgs);

        // Verify it started dragging!
        Assert.True((bool?)isDraggingField?.GetValue(scrollViewer) ?? false);

        // Simulate dragging the scrollbar thumb down by 20px (from y = 10 to y = 30)
        // trackLength = viewportHeight - thumbHeight = 100 - 20 = 80.
        // deltaY = 30 - 10 = 20.
        // Expected scroll = 0 + (20 / 80) * scrollableHeight = 0.25 * (1000 - 100) = 0.25 * 900 = 225.
        var dragMovedArgs = new PointerRoutedEventArgs
        {
            ScreenPosition = new Vector2(195f, 30f),
            Position = new Vector2(195f, 30f)
        };
        scrollViewer.OnPointerMoved(dragMovedArgs);

        Assert.Equal(225f, scrollViewer.VerticalOffset);

        // Release the drag
        var pointerReleasedArgs = new PointerRoutedEventArgs
        {
            ScreenPosition = new Vector2(195f, 30f),
            Position = new Vector2(195f, 30f)
        };
        scrollViewer.OnPointerReleased(pointerReleasedArgs);
        Assert.False((bool?)isDraggingField?.GetValue(scrollViewer) ?? true);

        // Exit pointer
        var pointerExitedArgs = new PointerRoutedEventArgs();
        scrollViewer.OnPointerExited(pointerExitedArgs);
        Assert.False((bool?)isPointerOverField?.GetValue(scrollViewer) ?? true);

        // Cleanup
        window.Content = null;
    }

    [Fact]
    public void Test_ScrollViewer_ScrollChaining()
    {
        EnsureFontsAndStateLoaded();

        // 1. Setup nested scroll structure
        var outerScroll = new ScrollViewer
        {
            WidthConstraint = 500f,
            HeightConstraint = 500f
        };
        var stackPanel = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            WidthConstraint = 500f,
            HeightConstraint = 2000f
        };
        outerScroll.Content = stackPanel;

        var nestedScroll = new ScrollViewer
        {
            WidthConstraint = 200f,
            HeightConstraint = 200f
        };
        var nestedContent = new Border
        {
            WidthConstraint = 200f,
            HeightConstraint = 1000f
        };
        nestedScroll.Content = nestedContent;

        stackPanel.Children.Add(nestedScroll);

        var window = HeadlessWindow.Shared;
        window.Resize(500, 500);
        window.Content = outerScroll;
        window.Render();

        Assert.Equal(0f, nestedScroll.VerticalOffset);
        Assert.Equal(0f, outerScroll.VerticalOffset);

        // 2. Scroll down on nested ScrollViewer (nested scroll is at 0, so it should scroll nested)
        var wheelDownArgs = new PointerRoutedEventArgs { WheelDelta = -1f };
        nestedScroll.OnPointerWheelChanged(wheelDownArgs);

        Assert.True(wheelDownArgs.Handled);
        Assert.Equal(30f, nestedScroll.VerticalOffset);
        Assert.Equal(0f, outerScroll.VerticalOffset);

        // 3. Move nested ScrollViewer to its bottom limit (1000 content - 200 viewport = 800 limit)
        nestedScroll.VerticalOffset = 800f;
        Assert.Equal(800f, nestedScroll.VerticalOffset);

        // 4. Scroll down further on nested ScrollViewer. It's at bottom limit, so it should NOT handle
        // and instead bubble up to outer scroll.
        var wheelDownLimitArgs = new PointerRoutedEventArgs { WheelDelta = -1f };
        nestedScroll.OnPointerWheelChanged(wheelDownLimitArgs);

        // Event should have bubbled to parent and scrolled outer scroll
        Assert.True(wheelDownLimitArgs.Handled);
        Assert.Equal(800f, nestedScroll.VerticalOffset); // nested stays at bottom
        Assert.Equal(30f, outerScroll.VerticalOffset); // outer scrolled!

        // 5. Scroll up on nested ScrollViewer when nested is still at bottom (800). It should scroll nested up.
        outerScroll.VerticalOffset = 0f;
        var wheelUpArgs = new PointerRoutedEventArgs { WheelDelta = 1f };
        nestedScroll.OnPointerWheelChanged(wheelUpArgs);

        Assert.True(wheelUpArgs.Handled);
        Assert.Equal(770f, nestedScroll.VerticalOffset); // nested scrolled up
        Assert.Equal(0f, outerScroll.VerticalOffset); // outer unchanged

        // 6. Reset nested ScrollViewer to top (0).
        nestedScroll.VerticalOffset = 0f;
        outerScroll.VerticalOffset = 100f;

        // 7. Scroll up on nested ScrollViewer. It's at top limit (0), so it should bubble up and scroll outer scroll up!
        var wheelUpLimitArgs = new PointerRoutedEventArgs { WheelDelta = 1f };
        nestedScroll.OnPointerWheelChanged(wheelUpLimitArgs);

        Assert.True(wheelUpLimitArgs.Handled);
        Assert.Equal(0f, nestedScroll.VerticalOffset); // nested stays at 0
        Assert.Equal(70f, outerScroll.VerticalOffset); // outer scrolled up!

        window.Content = null;
    }
}


