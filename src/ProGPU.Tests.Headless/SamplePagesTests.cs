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
    public void Test_KeyboardFocusPage_Renders()
    {
        RunPageTest(KeyboardParityPage.Create(), "Keyboard & Focus");
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
}

