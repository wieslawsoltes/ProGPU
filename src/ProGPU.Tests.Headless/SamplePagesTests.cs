using System;
using System.IO;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.WinUI.Designer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Tests.Headless;
using ProGPU.Samples;
using Xunit;
using DxfDocument = netDxf.DxfDocument;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class SamplePagesTests : IDisposable
{
    public void Dispose()
    {
    }

    private static string GetArtifactPath(string fileName)
    {
        string artifactDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        return Path.Combine(artifactDirectory, fileName);
    }

    private static void EnsureFontsAndStateLoaded()
    {
        if (PopupService.DefaultFont == null || AppState._font == null)
        {
            SampleFontLoader.EnsureLoaded("[ProGPU.Tests.Headless]");
        }

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
    public void Test_SkiaSharpShimPage_Renders()
    {
        RunPageTest(SkiaSharpShimPage.Create(), "SkiaSharp Shim");
    }

    [Fact]
    public void Test_FontIconAndPathIcon_Renders()
    {
        EnsureFontsAndStateLoaded();

        var stack = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Vertical };
        
        var fontIcon = new FontIcon
        {
            Font = AppState._font,
            Glyph = "A",
            FontSize = 40f
        };
        stack.AddChild(fontIcon);

        var glyphIndexIcon = new FontIcon
        {
            Font = AppState._font,
            GlyphIndex = 42,
            FontSize = 48f
        };
        stack.AddChild(glyphIndexIcon);

        var pathIcon = new PathIcon
        {
            Data = "M 0 0 L 20 0 L 20 20 L 0 20 Z"
        };
        stack.AddChild(pathIcon);

        RunPageTest(stack, "Icons Test");
    }

    [Fact]
    public void Test_FontGlyphBrowserPage_Renders()
    {
        RunPageTest(FontGlyphBrowserPage.Create(), "Font Glyph Browser");
    }

    [Fact]
    public void Test_FontGlyphBrowserPage_Hover_Diagnostics()
    {
        EnsureFontsAndStateLoaded();

        var page = FontGlyphBrowserPage.Create();
        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = page;
        window.Render();

        var itemsControlField = typeof(FontGlyphBrowserPage).GetField("_itemsControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var itemsControl = (ItemsControl?)itemsControlField?.GetValue(null);
        Assert.NotNull(itemsControl);

        // Verify regression: ItemsControl background on pointer hover must be transparent, not solid white
        Assert.False(itemsControl.IsPointerOver);
        itemsControl.OnPointerEntered(new PointerRoutedEventArgs());
        Assert.True(itemsControl.IsPointerOver);

        var itemsControlBg = itemsControl.GetCurrentBackground();
        Assert.NotNull(itemsControlBg);
        var itemsControlSolidBg = itemsControlBg as SolidColorBrush;
        Assert.NotNull(itemsControlSolidBg);
        // Assert that the hover background is transparent (0, 0, 0, 0) and NOT solid white (1, 1, 1, 1)
        Assert.Equal(new Vector4(0f, 0f, 0f, 0f), itemsControlSolidBg.Color);

        // Reset hover state
        itemsControl.OnPointerExited(new PointerRoutedEventArgs());
        Assert.False(itemsControl.IsPointerOver);

        var activeVisualsField = typeof(UniformVirtualizingGridPanel).GetField("_activeVisuals", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var activeVisuals = (System.Collections.IDictionary?)activeVisualsField?.GetValue(itemsControl.ItemsPanel);
        Assert.NotNull(activeVisuals);
        Assert.NotEmpty(activeVisuals);

        // Get one active visual container (which is a Border representing a glyph card)
        Border? glyphBorder = null;
        foreach (var val in activeVisuals.Values)
        {
            if (val is Border b)
            {
                glyphBorder = b;
                break;
            }
        }
        Assert.NotNull(glyphBorder);

        // Inspect ActualTheme and ActualThemeFamily of the border container
        var theme = glyphBorder.ActualTheme;
        var family = glyphBorder.ActualThemeFamily;
        Console.WriteLine($"[DIAG_HOVER] Border ActualTheme={theme}, ActualThemeFamily={family}");

        // Set to hover background and border brush, then inspect resolved colors
        glyphBorder.Background = new ThemeResourceBrush("ControlBackgroundHover");
        glyphBorder.BorderBrush = new ThemeResourceBrush("ControlBorderHover");

        var bgBrush = glyphBorder.Background as SolidColorBrush;
        var borderBrush = glyphBorder.BorderBrush as SolidColorBrush;

        Assert.NotNull(bgBrush);
        Assert.NotNull(borderBrush);

        Console.WriteLine($"[DIAG_HOVER] Resolved Hover Background Color: {bgBrush.Color}");
        Console.WriteLine($"[DIAG_HOVER] Resolved Hover Border Color: {borderBrush.Color}");

        window.Content = null;
    }

    [Fact]
    public void Test_VirtualizationControlsPage_Renders()
    {
        RunPageTest(VirtualizationControlsPage.Create(), "Virtualization Controls");
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
    public async Task Test_MarkdownPage_RepeatedActivation_RemainsResponsiveAndHighlighted()
    {
        EnsureFontsAndStateLoaded();
        await FontApi.WarmUpSystemFontsAsync();
        await TextLayout.WarmUpFallbackMetadataAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        static VirtualizedCodeEditor? FindCodeEditor(Visual visual)
        {
            if (visual is VirtualizedCodeEditor editor)
            {
                return editor;
            }

            if (visual is ContainerVisual container)
            {
                foreach (var child in container.Children)
                {
                    var match = FindCodeEditor(child);
                    if (match != null)
                    {
                        return match;
                    }
                }
            }

            return null;
        }

        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        var primaryWarmup = new RichTextBlock { Font = AppState._font, FontSize = 14f };
        primaryWarmup.Inlines.Add(new Run("ProGPU Markdown navigation ★ ✔ ♠"));
        window.Content = primaryWarmup;
        window.Render();
        var codeWarmup = new RichTextBlock { Font = AppState._fontCourier ?? AppState._font, FontSize = 13f };
        codeWarmup.Inlines.Add(new Run("public void RenderFrame(DrawingContext context)"));
        window.Content = codeWarmup;
        window.Render();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        for (var iteration = 0; iteration < 3; iteration++)
        {
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            window.Content = MarkdownPage.Create();
            window.Render();
            for (var stage = 0; stage < 3; stage++)
            {
                ProGPU.Samples.UIThread.RunPending();
                window.Render();
            }
            stopwatch.Stop();

            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var editor = FindCodeEditor(window.Content!);
            Assert.NotNull(editor);
            Console.WriteLine(
                $"[MARKDOWN_REPEAT] iteration={iteration + 1} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2} " +
                $"allocatedBytes={allocatedBytes}");
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Markdown activation {iteration + 1} took {stopwatch.Elapsed.TotalMilliseconds:F2} ms.");
            Assert.True(
                allocatedBytes < 128L * 1024L * 1024L,
                $"Markdown activation {iteration + 1} allocated {allocatedBytes:N0} bytes.");

            Assert.True(editor.IsSyntaxHighlightingReady);
            Assert.True(editor.SyntaxTokenRunCount > 6);

            window.Content = null;
            ProGPU.Samples.UIThread.RunPending();
        }
    }

    [Fact]
    public void Test_ThemeShowcasePage_Renders()
    {
        RunPageTest(ThemeShowcasePage.Create(), "Theme Showcase");
    }

    [Fact]
    public void Test_ImageEffectsPage_Renders()
    {
        EnsureFontsAndStateLoaded();
        AppState._wgpuContext ??= HeadlessWindow.Shared.Context;
        AppState._gearCanvasVisual = null;

        var page = ImageEffectsPage.Create();

        Assert.NotNull(AppState._gearCanvasVisual);
        RunPageTest(page, "Image Effects");
    }

    [Fact]
    public void Test_TypographyScriptsPage_Renders()
    {
        RunPageTest(SamplePagePresenter.CreateTypographyScriptsView(), "Typography & Scripts");
    }

    [Fact]
    public void Test_PathOpsVisual_RetainsCompletedSnapshotWhileReplacementIsPending()
    {
        EnsureFontsAndStateLoaded();

        var visual = new PathOpsVisual();
        var pathA = PathGeometry.Parse("M 10 10 L 30 10 L 30 30 Z");
        var pathB = PathGeometry.Parse("M 20 20 L 40 20 L 40 40 Z");
        var result = PathGeometry.Parse("M 10 10 L 30 10 L 40 20 L 40 40 Z");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(PathOpsVisual).GetField("_pathA", flags)!.SetValue(visual, pathA);
        typeof(PathOpsVisual).GetField("_pathB", flags)!.SetValue(visual, pathB);
        typeof(PathOpsVisual).GetField("_result", flags)!.SetValue(visual, result);

        visual.OverlapOffset += 10f;

        Assert.Same(pathA, typeof(PathOpsVisual).GetField("_pathA", flags)!.GetValue(visual));
        Assert.Same(pathB, typeof(PathOpsVisual).GetField("_pathB", flags)!.GetValue(visual));
        Assert.Same(result, typeof(PathOpsVisual).GetField("_result", flags)!.GetValue(visual));

        var context = new DrawingContext();
        visual.OnRender(context);

        Assert.Equal(3, context.Commands.Count(static command => command.Type == RenderCommandType.DrawPath));
    }

    [Fact]
    public void Test_DataVirtualizationPage_Renders()
    {
        RunPageTest(DataVirtualizationPage.Create(), "Data Virtualization");
    }

    [Fact]
    public void Test_DataVirtualizationPage_GeneratesLogsOnDemand()
    {
        EnsureFontsAndStateLoaded();
        AppState._logItems.Clear();

        _ = DataVirtualizationPage.Create();

        Assert.Equal(10000, AppState._logItems.Count);
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
            Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.TextureBinding | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.CopyDst | Silk.NET.WebGPU.TextureUsage.StorageBinding,
            alphaMode: ProGPU.Backend.GpuTextureAlphaMode.Premultiplied);
        AppState._canvasTempTexture = new ProGPU.Backend.GpuTexture(context, 600, 600, Silk.NET.WebGPU.TextureFormat.Rgba8Unorm, 
            Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.TextureBinding | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.CopyDst | Silk.NET.WebGPU.TextureUsage.StorageBinding,
            alphaMode: ProGPU.Backend.GpuTextureAlphaMode.Premultiplied);
        AppState._canvasBlurTexture = new ProGPU.Backend.GpuTexture(context, 600, 600, Silk.NET.WebGPU.TextureFormat.Rgba8Unorm, 
            Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.TextureBinding | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.CopyDst | Silk.NET.WebGPU.TextureUsage.StorageBinding,
            alphaMode: ProGPU.Backend.GpuTextureAlphaMode.Premultiplied);
        AppState._canvasShadowTexture = new ProGPU.Backend.GpuTexture(context, 600, 600, Silk.NET.WebGPU.TextureFormat.Rgba8Unorm, 
            Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.TextureBinding | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.CopyDst | Silk.NET.WebGPU.TextureUsage.StorageBinding,
            alphaMode: ProGPU.Backend.GpuTextureAlphaMode.Premultiplied);

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
        if (AppState._offscreenCompositor != null)
        {
            AppState._offscreenCompositor.Dispose();
            AppState._offscreenCompositor = null;
        }
        if (AppState._compute != null)
        {
            AppState._compute.Dispose();
            AppState._compute = null;
        }
        if (AppState._canvasSourceTexture != null)
        {
            AppState._canvasSourceTexture.Dispose();
            AppState._canvasSourceTexture = null;
        }
        if (AppState._canvasTempTexture != null)
        {
            AppState._canvasTempTexture.Dispose();
            AppState._canvasTempTexture = null;
        }
        if (AppState._canvasBlurTexture != null)
        {
            AppState._canvasBlurTexture.Dispose();
            AppState._canvasBlurTexture = null;
        }
        if (AppState._canvasShadowTexture != null)
        {
            AppState._canvasShadowTexture.Dispose();
            AppState._canvasShadowTexture = null;
        }
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
    public void Test_DxfCanvasControl_ZoomRetainsGeometryAndGlyphOutlinesWithoutRecompilation()
    {
        EnsureFontsAndStateLoaded();

        bool savedEnableGpuTransforms = AppState.EnableGpuTransforms;
        bool savedEnableStatic = AppState.EnableStaticGpuBuffers;
        bool savedEnableCaching = AppState.EnableCommandCaching;
        var savedCompositor = AppState._screenCompositor;
        try
        {
            AppState.EnableGpuTransforms = false;
            AppState.EnableStaticGpuBuffers = true;
            AppState.EnableCommandCaching = false;

            using var window = new HeadlessWindow(800, 600);
            AppState._screenCompositor = window.Compositor;
            var control = new DxfCanvasControl();
            control.LoadDocument(SampleDxfGenerator.GenerateSample());
            window.Content = control;
            window.Render();

            Assert.Equal(1, control.DocumentRenderCount);
            Assert.Equal(1, control.StaticBufferCompileCount);
            Assert.Equal(0, control.StaticTextRecompileCount);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            const int zoomFrameCount = 20;
            for (var step = 0; step < zoomFrameCount; step++)
            {
                float scale = step < zoomFrameCount / 2 ? 1.08f : 1f / 1.08f;
                control.ZoomToPoint(new Vector2(400f, 300f), scale);
                window.Render();
            }
            stopwatch.Stop();

            Console.WriteLine(
                $"[DXF_RETAINED_ZOOM] frames={zoomFrameCount} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2} " +
                $"documentRenders={control.DocumentRenderCount} staticCompiles={control.StaticBufferCompileCount} " +
                $"textRefreshes={control.StaticTextRecompileCount}");

            // Wall time is only a broad hang guard because shared CI software
            // rendering differs substantially by host. The counters below are
            // the deterministic performance contract.
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Retained DXF zoom frames took {stopwatch.Elapsed.TotalMilliseconds:F2} ms.");
            Assert.Equal(1, control.DocumentRenderCount);
            Assert.Equal(1, control.StaticBufferCompileCount);
            Assert.Equal(0, control.StaticTextRecompileCount);

            var pixels = window.ReadPixels();
            int opaqueYellowPixels = 0;
            for (var pixelIndex = 0; pixelIndex < pixels.Length; pixelIndex += 4)
            {
                if (pixels[pixelIndex] >= 192 &&
                    pixels[pixelIndex + 1] >= 192 &&
                    pixels[pixelIndex + 2] <= 80 &&
                    pixels[pixelIndex + 3] >= 224)
                {
                    opaqueYellowPixels++;
                }
            }

            Console.WriteLine($"[DXF_RETAINED_ZOOM] opaqueYellowPixels={opaqueYellowPixels}");

            Assert.True(
                opaqueYellowPixels >= 64,
                $"Expected crisp retained-outline DXF labels after zoom, found {opaqueYellowPixels} opaque yellow pixels.");
        }
        finally
        {
            AppState.EnableGpuTransforms = savedEnableGpuTransforms;
            AppState.EnableStaticGpuBuffers = savedEnableStatic;
            AppState.EnableCommandCaching = savedEnableCaching;
            AppState._screenCompositor = savedCompositor;
        }
    }

    [Fact]
    public void Benchmark_DxfCanvasControl_ExternalLargeDrawingRetainsGeometryWhileZooming()
    {
        string? filePath = Environment.GetEnvironmentVariable("PROGPU_DXF_BENCHMARK_FILE");
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Console.WriteLine("[DXF_EXTERNAL_BENCHMARK] Set PROGPU_DXF_BENCHMARK_FILE to run the large-drawing benchmark.");
            return;
        }

        EnsureFontsAndStateLoaded();
        bool savedEnableGpuTransforms = AppState.EnableGpuTransforms;
        bool savedEnableStatic = AppState.EnableStaticGpuBuffers;
        bool savedEnableCaching = AppState.EnableCommandCaching;
        var savedCompositor = AppState._screenCompositor;
        try
        {
            AppState.EnableGpuTransforms = false;
            AppState.EnableStaticGpuBuffers = true;
            AppState.EnableCommandCaching = false;

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            using var stream = File.OpenRead(filePath);
            var document = DxfDocument.Load(stream);
            Assert.NotNull(document);

            using var window = new HeadlessWindow(1600, 1000);
            AppState._screenCompositor = window.Compositor;
            var control = new DxfCanvasControl();
            control.LoadDocument(document, filePath);
            window.Content = control;

            var initialCompile = System.Diagnostics.Stopwatch.StartNew();
            window.Render();
            initialCompile.Stop();

            var zoomFrames = System.Diagnostics.Stopwatch.StartNew();
            const int zoomFrameCount = 24;
            Span<double> frameMilliseconds = stackalloc double[zoomFrameCount];
            long allocatedBytesBeforeZoom = GC.GetAllocatedBytesForCurrentThread();
            for (var step = 0; step < zoomFrameCount; step++)
            {
                float scale = step < zoomFrameCount / 2 ? 1.06f : 1f / 1.06f;
                long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
                control.ZoomToPoint(new Vector2(800f, 500f), scale);
                window.Render();
                frameMilliseconds[step] = System.Diagnostics.Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
            }
            zoomFrames.Stop();
            long allocatedBytesDuringZoom = GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBeforeZoom;

            frameMilliseconds.Sort();
            double p95FrameMilliseconds = frameMilliseconds[(int)Math.Ceiling(zoomFrameCount * 0.95) - 1];
            double maximumFrameMilliseconds = frameMilliseconds[^1];

            string? screenshotPath = Environment.GetEnvironmentVariable("PROGPU_DXF_BENCHMARK_SCREENSHOT");
            if (!string.IsNullOrWhiteSpace(screenshotPath))
            {
                window.SaveScreenshot(screenshotPath);
            }

            Console.WriteLine(
                $"[DXF_EXTERNAL_BENCHMARK] file={filePath} initialMs={initialCompile.Elapsed.TotalMilliseconds:F2} " +
                $"zoomFrames={zoomFrameCount} zoomMs={zoomFrames.Elapsed.TotalMilliseconds:F2} " +
                $"averageFrameMs={zoomFrames.Elapsed.TotalMilliseconds / zoomFrameCount:F2} " +
                $"p95FrameMs={p95FrameMilliseconds:F2} maxFrameMs={maximumFrameMilliseconds:F2} " +
                $"allocatedBytes={allocatedBytesDuringZoom} " +
                $"documentRenders={control.DocumentRenderCount} staticCompiles={control.StaticBufferCompileCount} " +
                $"textRefreshes={control.StaticTextRecompileCount}");

            Assert.Equal(1, control.DocumentRenderCount);
            Assert.Equal(1, control.StaticBufferCompileCount);
            Assert.Equal(0, control.StaticTextRecompileCount);
            Assert.True(
                zoomFrames.Elapsed.TotalMilliseconds / zoomFrameCount < 8.33,
                $"Large retained DXF zoom must sustain at least 120 FPS; averaged {zoomFrames.Elapsed.TotalMilliseconds / zoomFrameCount:F2} ms per frame.");
            Assert.True(
                p95FrameMilliseconds < 8.33,
                $"Large retained DXF zoom must sustain at least 120 FPS through p95; measured {p95FrameMilliseconds:F2} ms.");
            Assert.True(
                allocatedBytesDuringZoom < 256 * 1024,
                $"Large retained DXF zoom allocated {allocatedBytesDuringZoom:N0} managed bytes across {zoomFrameCount} frames.");
            Assert.True(
                zoomFrames.Elapsed < TimeSpan.FromSeconds(20),
                $"Large retained DXF zoom benchmark took {zoomFrames.Elapsed.TotalMilliseconds:F2} ms.");
        }
        finally
        {
            AppState.EnableGpuTransforms = savedEnableGpuTransforms;
            AppState.EnableStaticGpuBuffers = savedEnableStatic;
            AppState.EnableCommandCaching = savedEnableCaching;
            AppState._screenCompositor = savedCompositor;
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
            Assert.Equal(1, ctrl.DocumentRenderCount);
            Assert.Equal(1, ctrl.StaticBufferCompileCount);

            ctrl.ZoomToPoint(new Vector2(400f, 300f), 1.1f);
            window.Render();
            Assert.Equal(1, ctrl.DocumentRenderCount);
            Assert.Equal(1, ctrl.StaticBufferCompileCount);

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
            Assert.Equal(2, ctrl.DocumentRenderCount);
            Assert.Equal(2, ctrl.StaticBufferCompileCount);

            ctrl.Context.EnableFlattening = !ctrl.Context.EnableFlattening;
            ctrl.Invalidate();
            window.Render();
            Assert.Equal(3, ctrl.DocumentRenderCount);
            Assert.Equal(3, ctrl.StaticBufferCompileCount);

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
            try
            {
                markdownContent = File.ReadAllText(specPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[Headless] Falling back to generated markdown because '{specPath}' is inaccessible: {ex.Message}");
            }
        }

        if (markdownContent.Length == 0)
        {
            // Fallback: Generate a massive dense markdown document.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Mock Massive Markdown Document");
            for (int i = 0; i < 1500; i++)
            {
                sb.AppendLine($"Paragraph {i}: This dense generated markdown content verifies virtualized block-based flow layout, scrolling, and rendering without depending on a local Downloads fixture.");
                sb.AppendLine();
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

        // Warm up text rendering and Markdown JIT compilation
        var warmupMarkdown = new MarkdownTextBlock { Markdown = "# Warmup Header\nSome regular text here." };
        window.Content = warmupMarkdown;
        window.Render();

        window.Content = scrollViewer;

        // 1. Initial Load & Render
        var sw = System.Diagnostics.Stopwatch.StartNew();
        markdownBlock.Markdown = markdownContent;
        window.Render();
        sw.Stop();

        Console.WriteLine($"[TEST_VIRTUALIZATION] Spec loaded and first frame rendered in {sw.Elapsed.TotalMilliseconds:F2} ms");
        
        // Assert that the initial load + layout pass was extremely fast under virtualization
        Assert.True(sw.Elapsed.TotalMilliseconds < 2000, $"Initial virtualized layout took too long: {sw.Elapsed.TotalMilliseconds} ms");

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

        var viewportHeight = scrollViewer.Size.Y > 0f ? scrollViewer.Size.Y : scrollViewer.HeightConstraint ?? 600f;
        var maxScrollOffset = Math.Max(0f, scrollViewer.ContentHeight - viewportHeight);
        for (float offset = 0f; offset <= maxScrollOffset; offset += scrollStep)
        {
            scrollViewer.VerticalOffset = offset;
            
            // Force a render tick to arrange and layout newly visible blocks
            window.Render();
            scrollFrames++;

            var currentChars = markdownBlock.PositionedChars;
            Assert.True(
                currentChars.Count > 0,
                $"Expected active characters at requested offset {offset}, actual offset {scrollViewer.VerticalOffset}, content height {scrollViewer.ContentHeight}, viewport height {viewportHeight}."); // MUST have active characters in viewport at ALL scroll offsets!
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

    [Fact]
    public void Test_WpfShowcasePage_Renders()
    {
        RunPageTest(WpfShowcasePage.Create(), "WPF Showcase");
    }

    [Fact]
    public void Test_ShaderToyPlaygroundPage_Renders()
    {
        RunPageTest(ShaderToyPlaygroundPage.Create(), "ShaderToy Playground");
    }

    [Fact]
    public void Test_ShaderToyControl_Preset2_Renders()
    {
        try
        {
            File.WriteAllText(GetArtifactPath("debug.txt"), "Test start\n");
        }
        catch (Exception ex)
        {
            Assert.Fail($"Failed to write test file: {ex.Message}");
        }

        var control = new ShaderToyControl();
        control.ShaderSource = ShaderToyPlaygroundPageGrid.Preset2_StarNest;
        
        EnsureFontsAndStateLoaded();
        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = control;
        window.Render();
        window.Render();
        
        if (control.CompileError != null)
        {
            throw new Exception("Preset 2 WebGPU Validation Error:\n" + control.CompileError);
        }
        
        byte[] pixels = window.ReadPixels();
        window.SaveScreenshot(GetArtifactPath("preset2.png"));

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
        string firstPixels = $"({pixels[0]},{pixels[1]},{pixels[2]},{pixels[3]}), ({pixels[4]},{pixels[5]},{pixels[6]},{pixels[7]})";
        Assert.True(nonBgCount > 100, $"Preset 2 rendered empty. First pixels: {firstPixels}. Total non-bg: {nonBgCount}");
        window.Content = null;
    }

    [Fact]
    public void Test_ShaderToyControl_Preset3_Renders()
    {
        var control = new ShaderToyControl();
        control.ShaderSource = ShaderToyPlaygroundPageGrid.Preset3_RaymarchedTorus;
        
        EnsureFontsAndStateLoaded();
        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = control;
        window.Render();
        window.Render();
        
        if (control.CompileError != null)
        {
            throw new Exception("Preset 3 WebGPU Validation Error:\n" + control.CompileError);
        }
        
        byte[] pixels = window.ReadPixels();
        window.SaveScreenshot(GetArtifactPath("preset3.png"));

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
        var firstPixels = $"({pixels[0]},{pixels[1]},{pixels[2]},{pixels[3]}), ({pixels[4]},{pixels[5]},{pixels[6]},{pixels[7]})";
        Assert.True(nonBgCount > 100, $"Preset 3 rendered empty. First pixels: {firstPixels}. Total non-bg: {nonBgCount}");
        window.Content = null;
    }

    [Fact]
    public void Test_ShaderToyControl_Preset4_Renders()
    {
        var control = new ShaderToyControl();
        control.ShaderSource = ShaderToyPlaygroundPageGrid.Preset4_RaymarchingPrimitives;
        
        EnsureFontsAndStateLoaded();
        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = control;
        window.Render();
        window.Render();
        
        if (control.CompileError != null)
        {
            throw new Exception("Preset 4 (GLSL Raymarching Primitives) WebGPU Transpilation/Validation Error:\n" + control.CompileError);
        }
        
        byte[] pixels = window.ReadPixels();
        window.SaveScreenshot(GetArtifactPath("preset4.png"));

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
        var firstPixels = $"({pixels[0]},{pixels[1]},{pixels[2]},{pixels[3]}), ({pixels[4]},{pixels[5]},{pixels[6]},{pixels[7]})";
        Assert.True(nonBgCount > 100, $"Preset 4 rendered empty. First pixels: {firstPixels}. Total non-bg: {nonBgCount}");
        window.Content = null;
    }

    [Fact]
    public void Test_ShaderToyControl_Preset5_Renders()
    {
        var control = new ShaderToyControl();
        control.ShaderSource = ShaderToyPlaygroundPageGrid.Preset5_StarNestGlsl;
        
        EnsureFontsAndStateLoaded();
        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = control;
        window.Render();
        window.Render();
        
        if (control.CompileError != null)
        {
            throw new Exception("Preset 5 (GLSL Star Nest) WebGPU Transpilation/Validation Error:\n" + control.CompileError);
        }
        
        byte[] pixels = window.ReadPixels();
        window.SaveScreenshot(GetArtifactPath("preset5.png"));

        int nonBgCount = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte r = pixels[i + 0];
            byte g = pixels[i + 1];
            byte b = pixels[i + 2];
            if (r > 2 || g > 2 || b > 2)
            {
                nonBgCount++;
            }
        }
        var firstPixels = $"({pixels[0]},{pixels[1]},{pixels[2]},{pixels[3]}), ({pixels[4]},{pixels[5]},{pixels[6]},{pixels[7]})";
        Assert.True(nonBgCount > 100, $"Preset 5 rendered empty. First pixels: {firstPixels}. Total non-bg: {nonBgCount}");
        window.Content = null;
    }

    [Fact]
    public void Test_ShaderToyPlaygroundPage_GLSL_Transpilation()
    {
        EnsureFontsAndStateLoaded();

        var page = new ShaderToyPlaygroundPageGrid();

        var editorField = typeof(ShaderToyPlaygroundPageGrid).GetField("_editor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var consoleField = typeof(ShaderToyPlaygroundPageGrid).GetField("_consoleText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(editorField);
        Assert.NotNull(consoleField);

        var editor = (RichEditBox?)editorField.GetValue(page);
        var consoleText = (RichTextBlock?)consoleField.GetValue(page);

        Assert.NotNull(editor);
        Assert.NotNull(consoleText);

        // 1. Set editor text to GLSL Preset 5 (Star Nest)
        editor.Text = ShaderToyPlaygroundPageGrid.Preset5_StarNestGlsl;

        // 2. Locate the "Transpile GLSL" button in children
        Button? transpileBtn = null;

        void FindTranspileBtn(Visual visual)
        {
            if (visual is Button b)
            {
                if (b.Content is TextBlock tb && tb.Text == "Transpile GLSL")
                {
                    transpileBtn = b;
                    return;
                }
            }
            if (visual is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    FindTranspileBtn(child);
                    if (transpileBtn != null) return;
                }
            }
            else if (visual is Border border && border.Child != null)
            {
                FindTranspileBtn(border.Child);
            }
        }

        FindTranspileBtn(page);
        Assert.NotNull(transpileBtn);

        // 3. Trigger Click on transpile button via reflection
        var clickField = typeof(Button).GetField("Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(clickField);
        var clickDelegate = (EventHandler?)clickField.GetValue(transpileBtn);
        Assert.NotNull(clickDelegate);

        clickDelegate.Invoke(transpileBtn, EventArgs.Empty);

        // 4. Verify that editor content is translated to WGSL (which uses `fn mainImage` instead of `void mainImage`)
        string translatedText = editor.Text;
        Assert.Contains("fn mainImage", translatedText);
        Assert.DoesNotContain("void mainImage", translatedText);

        // Let's also verify that it renders/compiles properly without WebGPU errors.
        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = page;
        window.Render();
        window.Render();

        var toyControlField = typeof(ShaderToyPlaygroundPageGrid).GetField("_toyControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var toyControl = (ShaderToyControl?)toyControlField?.GetValue(page);
        Assert.NotNull(toyControl);

        Assert.Null(toyControl.CompileError);

        window.Content = null;
    }

    [Fact]
    public void Test_ShaderToyTranspiler_TerrainShader()
    {
        string glsl = @"// CC0: A quick terrain hack
//  Created this in order to demonstrate how to implement a simple terrain
//  ray tracer for a friend.

// It would benefit from some TAA but that would make it more complicated

// This file is released under CC0 1.0 Universal (Public Domain Dedication).
// To the extent possible under law, mrange has waived all copyright
// and related or neighboring rights to this work.
// See <https://creativecommons.org/publicdomain/zero/1.0/> for details.

// License: WTFPL, author: sam hocevar, found: https://stackoverflow.com/a/17897228/418488
const vec4 hsv2rgb_K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
// License: WTFPL, author: sam hocevar, found: https://stackoverflow.com/a/17897228/418488
//  Macro version of above to enable compile-time constants
#define HSV2RGB(c)  (c.z * mix(hsv2rgb_K.xxx, clamp(abs(fract(c.xxx + hsv2rgb_K.xyz) * 6.0 - hsv2rgb_K.www) - hsv2rgb_K.xxx, 0.0, 1.0), c.y))

const float 
  max_distance=3e1
;

const vec3
  // Colors for various parts in the scene
  sun     = HSV2RGB(vec3(.06,.8,3e-3))
, sky     = HSV2RGB(vec3(.58,.7,.5))
, dust    = HSV2RGB(vec3(.05,.9,.8)) 
, ground  = HSV2RGB(vec3(.03,.8,1.))
  // Light direction
, light   = normalize(vec3(4,1,6))
  // Used to setup the ray direction
, Z       = normalize(vec3(0,-2,7))
, X       = normalize(cross(Z,vec3(0,1,0)))
, Y       = cross(X,Z)
;

// Returns terrain height
float hf(vec2 p) {
  p*=.25;
  float
    a=1.
  , h=0.
  ;
  vec2
    D=vec2(0)
  ;
  vec3
    w
  ;
  vec4
    C
  ;
  // Rotation + Scaling
  const mat2 R=mat2(6,8,-8,6)/5.;
  // FBM using 8 octaves: https://iquilezles.org/articles/fbm/
  for(int i=0;i<8;++i) {
    // Generates a Sin p.X*Sin p.Y waveform plus its' derivates
    C=cos(p.xxyy+vec4(11,0,11,0));
    w=C.yxx*C.zwz;
    
    // Accumulate the derivates
    D+=w.xy;
    // Accumulates height using the waveform and its' derivates
    //  Using the derivates creates a more interesting landscape
    //  Technique ""borrowed"" from IQ
    h+=(a*w.z+a)/(3.*dot(D,D)+1.);
    // Decrease amplitude with 50% 
    a*=.5;
    // Double frequency and rotate
    p*=R;
    // A bit of offset
    p+=1.23;
  }
  
  return h;
}

vec3 nf(vec2 p) {
  // Computes the normal
  const vec2 
    E = vec2(1e-3, 0)
  ;
  return normalize(vec3(
    hf(p-E.xy) - hf(p+E.xy)
  , 2.*E.x
  , hf(p-E.yx)-hf(p+E.yx)
  ));
}

float raymarch(vec3 RO, vec3 RD, float init) {
  // Simple raymarch loop to find the intersection with the terrain
  float
    z=init
  , h
  , d
  ;

  vec3 
    p
  ;

  for(int i=0;i<69;++i) {
    p=z*RD+RO;
    h=hf(p.xz);
    d=p.y-h;
    if(d<1e-3||z>max_distance) break;
    // Because d is an approximate distance use fraction of it to step
    z+=.8*d;
  }
  
  return z;
}

float sraymarch(vec3 RO, vec3 RD, float init) {
  // Simple raymarch loop to find the intersection with the terrain
  //  used for the shadows, less loops, greater step size
  float
    z=init
  , h
  , d
  ;

  vec3 
    p
  ;

  for(int i=0;i<44;++i) {
    p=z*RD+RO;
    h=hf(p.xz);
    d=p.y-h;
    if(d<5e-3||z>max_distance) break;
    z+=d;
  }
  
  return z;
}

// License: Unknown, author: Matt Taylor (https://github.com/64), found: https://64.github.io/tonemapping/
vec3 aces_approx(vec3 v) {
  const float
    a = 2.51
  , b = 0.03
  , c = 2.43
  , d = 0.59
  , e = 0.14
  ;
  v = max(v, 0.);
  v *= .6;
  return clamp((v*(a*v+b))/(v*(c*v+d)+e), 0., 1.);
}

void mainImage(out vec4 O, vec2 C) {
  vec2
    r2=iResolution.xy
  , c2=C-.5*r2
  ;
  vec3
    o=vec3(0)
  , y
  , RO=vec3(0,4,.3*iTime)
    // The ray direction
  , RD=normalize(c2.y*Y-c2.x*X+r2.y*Z)
  , p
  , n
  ;
  
  float
    z
  , s
    // Intersection above the mountain range
  , tz=(3.0-RO.y)/RD.y
  , i=0.
  ;
  
  // Computes the sky
  y=
      sun/max(1e-4, .999-dot(light,RD))
    + sky*smoothstep(.3,-.15,RD.y)
    + dust*smoothstep(.0,-.15,RD.y)
  ;
  
  if(tz>0.&&tz<max_distance) {
    // Only check intersection with terrain if we hit the plane in front of us and less than max distance
    z=raymarch(RO,RD,tz);
    // Current pos
    p=z*RD+RO;
    // The normal
    n=nf(p.xz);
    // Shadow ray intersection
    s=sraymarch(p+.05*n,light,0.);
    z=clamp(z,0.,max_distance);
    if(z<max_distance) {
      // We hit the ground
      if(s>=max_distance)
        // Diffuse light from the sun 
        i+=max(.0,dot(n,light));
      // Diffuse light from the sky
      i+=sqrt((1.-n.y))*.1;
      o=ground*i;
      z-=max_distance*.5;
      z=max(0.,z);
      // Fade the sky and the groun for a fog like effect
      o=mix(y,o,exp(-1e-2*z*z));
    } else {
      // Miss
      o=y;
    }
  } else {
      // Miss
    o=y;
  }

  // Post process  
  o*=2.;
  // Tone mapping
  o=aces_approx(o);
  // Approximate RBG => sRGB
  o=sqrt(o)-.04;
  O = vec4(o,1);
}";

        var control = new ShaderToyControl();
        control.ShaderSource = glsl;

        EnsureFontsAndStateLoaded();
        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = control;
        window.Render();
        window.Render();

        if (control.CompileError != null)
        {
            throw new Exception("Terrain Shader WebGPU Transpilation/Validation Error:\n" + control.CompileError);
        }

        byte[] pixels = window.ReadPixels();
        window.SaveScreenshot(GetArtifactPath("terrain.png"));

        int nonBgCount = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte r = pixels[i + 0];
            byte g = pixels[i + 1];
            byte b = pixels[i + 2];
            if (r > 2 || g > 2 || b > 2)
            {
                nonBgCount++;
            }
        }
        Assert.True(nonBgCount > 100, $"Terrain Shader rendered empty. Total non-bg: {nonBgCount}");
        window.Content = null;
    }

    [Fact]
    public void Test_ShaderToyTranspiler_SeascapeShader()
    {
        string glsl = @"/*
 * ""Seascape"" by Alexander Alekseev aka TDM - 2014
 * License Creative Commons Attribution-NonCommercial-ShareAlike 3.0 Unported License.
 * Contact: tdmaav@gmail.com
 */

const int NUM_STEPS = 32;
const float PI	 	= 3.141592;
const float EPSILON	= 1e-3;
#define EPSILON_NRM (0.1 / iResolution.x)
//#define AA

// sea
const int ITER_GEOMETRY = 3;
const int ITER_FRAGMENT = 5;
const float SEA_HEIGHT = 0.6;
const float SEA_CHOPPY = 4.0;
const float SEA_SPEED = 0.8;
const float SEA_FREQ = 0.16;
const vec3 SEA_BASE = vec3(0.0,0.09,0.18);
const vec3 SEA_WATER_COLOR = vec3(0.8,0.9,0.6)*0.6;
#define SEA_TIME (1.0 + iTime * SEA_SPEED)
const mat2 octave_m = mat2(1.6,1.2,-1.2,1.6);

// math
mat3 fromEuler(vec3 ang) {
	vec2 a1 = vec2(sin(ang.x),cos(ang.x));
    vec2 a2 = vec2(sin(ang.y),cos(ang.y));
    vec2 a3 = vec2(sin(ang.z),cos(ang.z));
    mat3 m;
    m[0] = vec3(a1.y*a3.y+a1.x*a2.x*a3.x,a1.y*a2.x*a3.x+a3.y*a1.x,-a2.y*a3.x);
	m[1] = vec3(-a2.y*a1.x,a1.y*a2.y,a2.x);
	m[2] = vec3(a3.y*a1.x*a2.x+a1.y*a3.x,a1.x*a3.x-a1.y*a3.y*a2.x,a2.y*a3.y);
	return m;
}
float hash( vec2 p ) {
	float h = dot(p,vec2(127.1,311.7));	
    return fract(sin(h)*43758.5453123);
}
float noise( in vec2 p ) {
    vec2 i = floor( p );
    vec2 f = fract( p );	
	vec2 u = f*f*(3.0-2.0*f);
    return -1.0+2.0*mix( mix( hash( i + vec2(0.0,0.0) ), 
                     hash( i + vec2(1.0,0.0) ), u.x),
                mix( hash( i + vec2(0.0,1.0) ), 
                     hash( i + vec2(1.0,1.0) ), u.x), u.y);
}

// lighting
float diffuse(vec3 n,vec3 l,float p) {
    return pow(dot(n,l) * 0.4 + 0.6,p);
}
float specular(vec3 n,vec3 l,vec3 e,float s) {    
    float nrm = (s + 8.0) / (PI * 8.0);
    return pow(max(dot(reflect(e,n),l),0.0),s) * nrm;
}

// sky
vec3 getSkyColor(vec3 e) {
    e.y = (max(e.y,0.0)*0.8+0.2)*0.8;
    return vec3(pow(1.0-e.y,2.0), 1.0-e.y, 0.6+(1.0-e.y)*0.4) * 1.1;
}

// sea
float sea_octave(vec2 uv, float choppy) {
    uv += noise(uv);        
    vec2 wv = 1.0-abs(sin(uv));
    vec2 swv = abs(cos(uv));    
    wv = mix(wv,swv,wv);
    return pow(1.0-pow(wv.x * wv.y,0.65),choppy);
}

float map(vec3 p) {
    float freq = SEA_FREQ;
    float amp = SEA_HEIGHT;
    float choppy = SEA_CHOPPY;
    vec2 uv = p.xz; uv.x *= 0.75;
    
    float d, h = 0.0;    
    for(int i = 0; i < ITER_GEOMETRY; i++) {        
    	d = sea_octave((uv+SEA_TIME)*freq,choppy);
    	d += sea_octave((uv-SEA_TIME)*freq,choppy);
        h += d * amp;        
    	uv *= octave_m; freq *= 1.9; amp *= 0.22;
        choppy = mix(choppy,1.0,0.2);
    }
    return p.y - h;
}

float map_detailed(vec3 p) {
    float freq = SEA_FREQ;
    float amp = SEA_HEIGHT;
    float choppy = SEA_CHOPPY;
    vec2 uv = p.xz; uv.x *= 0.75;
    
    float d, h = 0.0;    
    for(int i = 0; i < ITER_FRAGMENT; i++) {        
    	d = sea_octave((uv+SEA_TIME)*freq,choppy);
    	d += sea_octave((uv-SEA_TIME)*freq,choppy);
        h += d * amp;        
    	uv *= octave_m; freq *= 1.9; amp *= 0.22;
        choppy = mix(choppy,1.0,0.2);
    }
    return p.y - h;
}

vec3 getSeaColor(vec3 p, vec3 n, vec3 l, vec3 eye, vec3 dist) {  
    float fresnel = clamp(1.0 - dot(n, -eye), 0.0, 1.0);
    fresnel = min(fresnel * fresnel * fresnel, 0.5);
    
    vec3 reflected = getSkyColor(reflect(eye, n));    
    vec3 refracted = SEA_BASE + diffuse(n, l, 80.0) * SEA_WATER_COLOR * 0.12; 
    
    vec3 color = mix(refracted, reflected, fresnel);
    
    float atten = max(1.0 - dot(dist, dist) * 0.001, 0.0);
    color += SEA_WATER_COLOR * (p.y - SEA_HEIGHT) * 0.18 * atten;
    
    color += specular(n, l, eye, 600.0 * inversesqrt(dot(dist,dist)));
    
    return color;
}

// tracing
vec3 getNormal(vec3 p, float eps) {
    vec3 n;
    n.y = map_detailed(p);    
    n.x = map_detailed(vec3(p.x+eps,p.y,p.z)) - n.y;
    n.z = map_detailed(vec3(p.x,p.y,p.z+eps)) - n.y;
    n.y = eps;
    return normalize(n);
}

float heightMapTracing(vec3 ori, vec3 dir, out vec3 p) {  
    float tm = 0.0;
    float tx = 1000.0;    
    float hx = map(ori + dir * tx);
    if(hx > 0.0) {
        p = ori + dir * tx;
        return tx;   
    }
    float hm = map(ori);    
    for(int i = 0; i < NUM_STEPS; i++) {
        float tmid = mix(tm, tx, hm / (hm - hx));
        p = ori + dir * tmid;
        float hmid = map(p);        
        if(hmid < 0.0) {
            tx = tmid;
            hx = hmid;
        } else {
            tm = tmid;
            hm = hmid;
        }        
        if(abs(hmid) < EPSILON) break;
    }
    return mix(tm, tx, hm / (hm - hx));
}

vec3 getPixel(in vec2 coord, float time) {    
    vec2 uv = coord / iResolution.xy;
    uv = uv * 2.0 - 1.0;
    uv.x *= iResolution.x / iResolution.y;    
        
    // ray
    vec3 ang = vec3(sin(time*3.0)*0.1,sin(time)*0.2+0.3,time);    
    vec3 ori = vec3(0.0,3.5,time*5.0);
    vec3 dir = normalize(vec3(uv.xy,-2.0)); dir.z += length(uv) * 0.14;
    dir = normalize(dir) * fromEuler(ang);
    
    // tracing
    vec3 p;
    heightMapTracing(ori,dir,p);
    vec3 dist = p - ori;
    vec3 n = getNormal(p, dot(dist,dist) * EPSILON_NRM);
    vec3 light = normalize(vec3(0.0,1.0,0.8)); 
             
    // color
    return mix(
        getSkyColor(dir),
        getSeaColor(p,n,light,dir,dist),
    	pow(smoothstep(0.0,-0.02,dir.y),0.2));
}

// main
void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
    float time = iTime * 0.3 + iMouse.x*0.01;
	
#ifdef AA
    vec3 color = vec3(0.0);
    for(int i = -1; i <= 1; i++) {
        for(int j = -1; j <= 1; j++) {
        	vec2 uv = fragCoord+vec2(i,j)/3.0;
    		color += getPixel(uv, time);
        }
    }
    color /= 9.0;
#else
    vec3 color = getPixel(fragCoord, time);
#endif
    
    // post
	fragColor = vec4(pow(color,vec3(0.65)), 1.0);
}";

        var control = new ShaderToyControl();
        control.ShaderSource = glsl;

        EnsureFontsAndStateLoaded();
        var window = HeadlessWindow.Shared;
        window.Resize(1280, 800);
        window.Content = control;
        window.Render();
        window.Render();

        if (control.CompileError != null)
        {
            throw new Exception("Seascape Shader WebGPU Transpilation/Validation Error:\n" + control.CompileError);
        }

        byte[] pixels = window.ReadPixels();
        window.SaveScreenshot(GetArtifactPath("seascape.png"));

        int nonBgCount = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte r = pixels[i + 0];
            byte g = pixels[i + 1];
            byte b = pixels[i + 2];
            if (r > 2 || g > 2 || b > 2)
            {
                nonBgCount++;
            }
        }
        Assert.True(nonBgCount > 100, $"Seascape Shader rendered empty. Total non-bg: {nonBgCount}");
        window.Content = null;
    }
}
