using System.Collections;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Backend;
using ProGPU.Fonts.Inter;
using ProGPU.Fonts.Noto;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using ProGPU.Virtualization;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class SamplePerformanceRegressionTests
{
    [Fact]
    public void CoverageAtlasesUseCompactSingleChannelResidency()
    {
        using var glyphAtlas = new GlyphAtlas(
            HeadlessWindow.Shared.Context,
            atlasSize: 256,
            colorAtlasSize: 64,
            coverageRingBufferSize: 256 * 1024);
        using var pathAtlas = new PathAtlas(
            HeadlessWindow.Shared.Context,
            atlasSize: 256);

        Assert.Equal(TextureFormat.R8Unorm, glyphAtlas.AtlasTexture.Format);
        Assert.Equal(TextureFormat.Rgba8Unorm, glyphAtlas.ColorAtlasTexture.Format);
        Assert.Equal(TextureFormat.R8Unorm, pathAtlas.AtlasTexture.Format);
        Assert.Equal(65_536UL + 16_384UL, glyphAtlas.PersistentTextureBytes);
        Assert.Equal(65_536UL, pathAtlas.PersistentTextureBytes);
        Assert.Equal(256u, GpuCoverageUpload.GetBytesPerRow(1));
        Assert.Equal(512u, GpuCoverageUpload.GetBytesPerRow(257));
    }

    [Fact]
    public void SampleAtlasConfigurationStartsAtThreePercentOfLegacyResidency()
    {
        const ulong glyphSize = 2_560;
        const ulong pathSize = 2_048;
        const ulong legacyBytes =
            glyphSize * glyphSize * 4UL +
            pathSize * pathSize * 4UL;
        const ulong initialBytes =
            GlyphAtlas.DefaultInitialAtlasSize * GlyphAtlas.DefaultInitialAtlasSize +
            GlyphAtlas.DefaultInitialColorAtlasSize * GlyphAtlas.DefaultInitialColorAtlasSize * 4UL +
            GlyphAtlas.DefaultCoverageRingBufferSize +
            PathAtlas.DefaultInitialAtlasSize * PathAtlas.DefaultInitialAtlasSize;

        Assert.Equal(42_991_616UL, legacyBytes);
        Assert.Equal(1_048_576UL, initialBytes);
        Assert.True(initialBytes * 100UL / legacyBytes <= 3UL);
    }

    [Fact]
    public void CoverageAtlasesGrowWithoutMovingTexelCoordinatesOrChangingGeneration()
    {
        using var pathAtlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 1024);
        PathAtlas.PathInfo first = pathAtlas.GetOrCreatePath(
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 72f, 72f),
            scale: 1f);
        pathAtlas.RasterizePendingPaths();
        byte[] initialPixels = pathAtlas.AtlasTexture.ReadPixels();
        uint initialLocalX = checked((uint)(20 - (int)first.MinX));
        uint initialLocalY = checked((uint)(20 - (int)first.MinY));
        int initialCoverageOffset = checked((int)(
            (first.Y + initialLocalY) * pathAtlas.AtlasSize + first.X + initialLocalX));
        Assert.True(initialPixels[initialCoverageOffset] > 200);
        ulong generation = pathAtlas.Generation;
        ulong revision = pathAtlas.TextureRevision;

        for (int index = 1; index < 80 && pathAtlas.AtlasSize == PathAtlas.DefaultInitialAtlasSize; index++)
        {
            _ = pathAtlas.GetOrCreatePath(
                PrimitivePathGeometry.CreateRectangle(index * 100f, 0f, 72f, 72f),
                scale: 1f);
        }

        PathAtlas.PathInfo repeated = pathAtlas.GetOrCreatePath(
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 72f, 72f),
            scale: 1f);
        Assert.Equal(1024u, pathAtlas.AtlasSize);
        Assert.True(pathAtlas.TextureRevision > revision);
        Assert.Equal(generation, pathAtlas.Generation);
        Assert.Equal(first.X, repeated.X);
        Assert.Equal(first.Y, repeated.Y);
        Assert.Equal(first.Width, repeated.Width);
        Assert.Equal(first.Height, repeated.Height);
        Assert.Equal(
            repeated.X / (float)pathAtlas.AtlasSize,
            repeated.TexCoordMin.X,
            precision: 6);
        byte[] grownPixels = pathAtlas.AtlasTexture.ReadPixels();
        uint grownLocalX = checked((uint)(20 - (int)repeated.MinX));
        uint grownLocalY = checked((uint)(20 - (int)repeated.MinY));
        int grownCoverageOffset = checked((int)(
            (repeated.Y + grownLocalY) * pathAtlas.AtlasSize + repeated.X + grownLocalX));
        Assert.Equal(initialPixels[initialCoverageOffset], grownPixels[grownCoverageOffset]);
    }

    [Fact]
    public void GlyphAtlasGrowthPreservesResidentCoverageAndStableTexelCoordinates()
    {
        TtfFont font = LoadTestFont();
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 1024);
        ushort glyph = font.GetGlyphIndex('A');
        atlas.BeginBatch();
        GlyphInfo first;
        try
        {
            first = atlas.GetOrCreateGlyphByIndex(font, glyph, 128f, subpixelX: 0);
        }
        finally
        {
            atlas.EndBatch();
        }

        byte[] initialPixels = atlas.AtlasTexture.ReadPixels();
        uint sampleX = 0;
        uint sampleY = 0;
        byte expectedCoverage = 0;
        for (uint y = 0; y < first.Height; y++)
        {
            for (uint x = 0; x < first.Width; x++)
            {
                byte coverage = initialPixels[(first.Y + y) * atlas.AtlasSize + first.X + x];
                if (coverage > expectedCoverage)
                {
                    expectedCoverage = coverage;
                    sampleX = x;
                    sampleY = y;
                }
            }
        }
        Assert.True(expectedCoverage > 0);

        ulong generation = atlas.Generation;
        atlas.BeginBatch();
        try
        {
            for (byte phase = 1; phase < 64 && atlas.AtlasSize == GlyphAtlas.DefaultInitialAtlasSize; phase++)
            {
                _ = atlas.GetOrCreateGlyphByIndex(font, glyph, 128f, phase);
            }
        }
        finally
        {
            atlas.EndBatch();
        }

        GlyphInfo repeated = atlas.GetOrCreateGlyphByIndex(font, glyph, 128f, subpixelX: 0);
        Assert.Equal(1024u, atlas.AtlasSize);
        Assert.True(atlas.TextureRevision > 0);
        Assert.Equal(generation, atlas.Generation);
        Assert.Equal(first.X, repeated.X);
        Assert.Equal(first.Y, repeated.Y);
        byte[] grownPixels = atlas.AtlasTexture.ReadPixels();
        Assert.Equal(
            expectedCoverage,
            grownPixels[(repeated.Y + sampleY) * atlas.AtlasSize + repeated.X + sampleX]);
    }

    [Fact]
    public void GlyphAtlasCoalescesFirstUseQueueWritesAcrossACompilationBatch()
    {
        TtfFont font = LoadTestFont();
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 1024);
        const string text = "GPU text batching";
        int uniqueGlyphCount = text.Distinct().Count(character => character != ' ');
        ulong outlineWritesBefore = atlas.OutlineUploadWriteCount;
        ulong uniformWritesBefore = atlas.UniformUploadWriteCount;
        ulong submissionsBefore = atlas.RasterBatchSubmissionCount;
        ulong bindGroupsBefore = atlas.RasterBindGroupCreationCount;
        ulong computePassesBefore = atlas.RasterComputePassCount;

        atlas.BeginBatch();
        try
        {
            foreach (char character in text)
            {
                if (character != ' ')
                {
                    _ = atlas.GetOrCreateGlyph(font, character, 32f);
                }
            }
        }
        finally
        {
            atlas.EndBatch();
        }

        Assert.Equal(uniqueGlyphCount, atlas.LastBatchNewGlyphCount);
        Assert.InRange(atlas.OutlineUploadWriteCount - outlineWritesBefore, 1UL, 2UL);
        Assert.Equal(1UL, atlas.UniformUploadWriteCount - uniformWritesBefore);
        Assert.Equal(1UL, atlas.RasterBatchSubmissionCount - submissionsBefore);
        Assert.Equal(1UL, atlas.RasterBindGroupCreationCount - bindGroupsBefore);
        Assert.Equal(1UL, atlas.RasterComputePassCount - computePassesBefore);

        atlas.BeginBatch();
        atlas.EndBatch();
        Assert.Equal(submissionsBefore + 1UL, atlas.RasterBatchSubmissionCount);
    }

    [Fact]
    public void PathRasterizationReusesBoundedCoverageChunks()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 1024);
        for (int index = 0; index < 24; index++)
        {
            _ = atlas.GetOrCreatePath(
                PrimitivePathGeometry.CreateRectangle(index * 128f, 0f, 96f, 96f),
                scale: 1f);
        }

        atlas.RasterizePendingPaths();

        Assert.InRange(
            atlas.LastRasterStagingBytes,
            1u,
            PathAtlas.DefaultRasterStagingChunkBytes);
        Assert.Equal(atlas.LastRasterStagingBytes, atlas.PeakRasterStagingBytes);
    }

    [Fact]
    public void TextControlsDeclareTheirOwnedCommandCaches()
    {
        Assert.IsAssignableFrom<IOwnedRenderCommandCache>(new RichTextBlock());
        Assert.IsAssignableFrom<IOwnedRenderCommandCache>(new MarkdownTextBlock());
        Assert.IsAssignableFrom<IOwnedRenderCommandCache>(new FlowDocument());
    }

    [Fact]
    public void LolsPoolRetainsTextAndBrushStateWithoutRebuildingRuns()
    {
        string factory = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Samples", "Helpers", "TextDisplayFactory.cs"));
        string page = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Samples", "Pages", "LolsPage.cs"));

        Assert.Contains("private sealed class PooledTextDisplay", factory, StringComparison.Ordinal);
        Assert.Contains("public Run Run { get; }", factory, StringComparison.Ordinal);
        Assert.Contains("public SolidColorBrush ForegroundBrush { get; }", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("textBlock.Inlines.Clear()", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("new Run(text)", factory, StringComparison.Ordinal);
        Assert.Contains("SetForegroundColor(textControl, foreground)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("new SolidColorBrush(new Vector4", page, StringComparison.Ordinal);
        Assert.Contains("_canvas.BringChildToFront(oldest)", page, StringComparison.Ordinal);
        Assert.Contains("if (addToCanvas) _canvas.AddChild(textControl)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrollOffsetsTranslateRetainedContentWithoutRearrangingIt()
    {
        var content = new ArrangeCounter
        {
            WidthConstraint = 300f,
            HeightConstraint = 1_000f
        };
        var scrollViewer = new ScrollViewer
        {
            WidthConstraint = 300f,
            HeightConstraint = 200f,
            Content = content
        };

        scrollViewer.Measure(new Vector2(300f, 200f));
        scrollViewer.Arrange(new Rect(0f, 0f, 300f, 200f));
        var arrangeCount = content.ArrangeCount;

        scrollViewer.VerticalOffset = 120f;

        Assert.Equal(arrangeCount, content.ArrangeCount);
        Assert.Equal(new Vector2(0f, -120f), content.Offset);
        Assert.Equal(new Vector2(0f, -120f), content.LayoutTranslation);
    }

    [Fact]
    public void OwnerScrollNotifiesVirtualizedGridAndKeepsRealizedItemsPopulated()
    {
        var boundIndices = new HashSet<int>();
        var panel = new UniformVirtualizingGridPanel
        {
            ItemsCount = 1_000,
            ItemWidth = 80f,
            ItemHeight = 80f,
            CreateVisualFactory = static () => new Border(),
            BindVisualCallback = (visual, index) =>
            {
                visual.HitTestId = index + 1;
                boundIndices.Add(index);
            }
        };
        var scrollViewer = new ScrollViewer
        {
            WidthConstraint = 320f,
            HeightConstraint = 240f,
            Content = panel
        };

        scrollViewer.Measure(new Vector2(320f, 240f));
        scrollViewer.Arrange(new Rect(0f, 0f, 320f, 240f));
        Assert.NotEmpty(panel.Children);
        var initialMaximum = boundIndices.Max();

        scrollViewer.VerticalOffset = 1_600f;

        Assert.NotEmpty(panel.Children);
        Assert.Contains(boundIndices, index => index > initialMaximum);
        Assert.All(panel.Children, visual => Assert.True(visual.HitTestId > initialMaximum + 1));
        Assert.IsAssignableFrom<IScrollViewportAware>(panel);
    }

    [Fact]
    public void TextVisualReusesMeasuredShapingAcrossEquivalentArrange()
    {
        var text = new TextVisual
        {
            Text = "Retained shaping",
            Font = LoadTestFont(),
            FontSize = 18f,
            WidthConstraint = 240f
        };
        text.Measure(new Vector2(240f, 80f));
        text.Arrange(new Rect(0f, 0f, 240f, 80f));
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        TextLayout? first = text.GetOrUpdateLayout(atlas);

        text.Measure(new Vector2(240f, 80f));
        text.Arrange(new Rect(0f, 0f, 240f, 80f));
        TextLayout? second = text.GetOrUpdateLayout(atlas);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void TextVisualRelayoutsInfiniteMeasurementWhenArrangeRequiresWrapping()
    {
        var text = new TextVisual
        {
            Text = "A retained text run that must wrap after arrange",
            Font = LoadTestFont(),
            FontSize = 18f
        };
        text.Measure(new Vector2(float.PositiveInfinity, 120f));
        text.Arrange(new Rect(0f, 0f, text.DesiredSize.X, 120f));
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        TextLayout? measured = text.GetOrUpdateLayout(atlas);

        text.Arrange(new Rect(0f, 0f, 80f, 120f));
        TextLayout? arranged = text.GetOrUpdateLayout(atlas);

        Assert.NotNull(measured);
        Assert.NotNull(arranged);
        Assert.NotSame(measured, arranged);
        Assert.Equal(80f, arranged.MaxWidth);
        Assert.True(arranged.ContentSize.Y > measured.ContentSize.Y);
    }

    [Fact]
    public void DeferredTextVisualWarmsAfterLayoutWithoutChangingArrangedBounds()
    {
        var text = new TextVisual
        {
            Text = "Deferred retained shaping",
            Font = LoadTestFont(),
            FontSize = 18f,
            WidthConstraint = 240f,
            HeightConstraint = 48f,
            DeferLayoutUntilRender = true
        };

        text.Measure(new Vector2(240f, 48f));
        Assert.False(text.WarmDeferredLayout());
        text.Arrange(new Rect(0f, 0f, 240f, 48f));
        Assert.True(text.WarmDeferredLayout());

        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        TextLayout? layout = text.GetOrUpdateLayout(atlas);

        Assert.NotNull(layout);
        Assert.Equal(240f, layout.MaxWidth);
        Assert.Equal(new Vector2(240f, 48f), text.Size);
    }

    [Fact]
    public async Task DeferredTextVisualCanPublishBackgroundShaping()
    {
        var text = new TextVisual
        {
            Text = "Background retained shaping",
            Font = LoadTestFont(),
            FontSize = 18f,
            WidthConstraint = 240f,
            HeightConstraint = 48f,
            DeferLayoutUntilRender = true
        };

        text.Measure(new Vector2(240f, 48f));
        text.Arrange(new Rect(0f, 0f, 240f, 48f));

        bool[] prepared = await Task.WhenAll(
            Task.Run(text.WarmDeferredLayout),
            Task.Run(text.WarmDeferredLayout));

        Assert.All(prepared, Assert.True);
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        TextLayout? layout = text.GetOrUpdateLayout(atlas);
        Assert.NotNull(layout);
        Assert.Equal(text.Text, layout.Text);
        Assert.Equal(240f, layout.MaxWidth);
    }

    [Fact]
    public void ClippedBoundsSkipOnlyLocalCommandsAndStillTraverseOverflowDescendants()
    {
        var visibleGrandchild = new RenderCounterVisual();
        var clippedParent = new RenderCounterContainer(visibleGrandchild);
        using var window = new HeadlessWindow(100, 100);
        window.Content = new ClipCullHost(clippedParent);

        window.Render();

        Assert.Equal(0, clippedParent.RenderCount);
        Assert.Equal(1, visibleGrandchild.RenderCount);
    }

    [Fact]
    public void FontIconUsesBoundedGlyphAtlasByDefault()
    {
        var font = LoadTestFont();
        var icon = new FontIcon
        {
            Font = font,
            GlyphIndex = font.GetGlyphIndex('A'),
            FontSize = 36f
        };
        icon.Measure(new Vector2(48f, 48f));
        icon.Arrange(new Rect(0f, 0f, 48f, 48f));

        var context = new DrawingContext();
        icon.OnRender(context);

        var command = Assert.Single(context.Commands, static command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.True(command.PreferGlyphAtlas);
        Assert.False(command.UseLogicalGlyphAtlasResolution);
        Assert.DoesNotContain(context.Commands, static command => command.Type == RenderCommandType.DrawPath);
    }

    [Fact]
    public void DrawTextUsesBoundedGlyphAtlasByDefault()
    {
        var font = LoadTestFont();
        var context = new DrawingContext();

        context.DrawText(
            "Visible virtualized text",
            font,
            13f,
            new SolidColorBrush(Vector4.One),
            Vector2.Zero);

        var command = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawText);
        Assert.True(command.PreferGlyphAtlas);
        Assert.False(command.UseVectorGlyphRendering);
    }

    [Fact]
    public void GlyphAtlasRemembersCapacityFallbacksInsteadOfRetryingEveryFrame()
    {
        var font = LoadTestFont();
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 16);
        var glyph = font.GetGlyphIndex('A');

        var first = atlas.GetOrCreateGlyphByIndex(font, glyph, 36f);
        var second = atlas.GetOrCreateGlyphByIndex(font, glyph, 36f);

        Assert.True(atlas.CapacityExceeded);
        Assert.Equal(1, atlas.CachedGlyphCount);
        Assert.Equal(0u, first.Width);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Advance, second.Advance);
    }

    [Fact]
    public void GlyphAtlasCompilesOnlyRequestedGpuOutlines()
    {
        var font = LoadTestFont();
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 512);
        ushort firstGlyph = font.GetGlyphIndex('A');
        ushort secondGlyph = font.GetGlyphIndex('B');

        atlas.BeginBatch();
        try
        {
            Assert.True(atlas.GetOrCreateGlyphByIndex(font, firstGlyph, 32f).Width > 0);
        }
        finally
        {
            atlas.EndBatch();
        }

        Assert.Equal(1, atlas.CompiledGpuGlyphCount);
        Assert.True(atlas.CompiledGpuGlyphCount < font.NumGlyphs);
        Assert.True(atlas.AllocatedGpuGlyphRecordCapacity < font.NumGlyphs);

        atlas.BeginBatch();
        try
        {
            Assert.True(atlas.GetOrCreateGlyphByIndex(font, secondGlyph, 32f).Width > 0);
        }
        finally
        {
            atlas.EndBatch();
        }

        Assert.Equal(2, atlas.CompiledGpuGlyphCount);
    }

    [Fact]
    public void PreferredGlyphAtlasReusesLeastRecentlyUsedRegionWithoutClearingAtlas()
    {
        var font = LoadTestFont();
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, atlasSize: 64);
        var glyph = font.GetGlyphIndex('A');
        var generation = atlas.Generation;

        atlas.BeginBatch();
        GlyphInfo first;
        GlyphInfo missing;
        try
        {
            first = atlas.GetOrCreateGlyphByIndex(font, glyph, 36f, 0, preferGlyphAtlas: true);
            missing = atlas.GetOrCreateGlyphByIndex(font, glyph, 36f, 1, preferGlyphAtlas: true);
        }
        finally
        {
            atlas.EndBatch();
        }

        Assert.True(first.Width > 0);
        Assert.Equal(0u, missing.Width);
        Assert.True(atlas.CapacityExceeded);

        atlas.BeginBatch();
        GlyphInfo reused;
        try
        {
            reused = atlas.GetOrCreateGlyphByIndex(font, glyph, 36f, 1, preferGlyphAtlas: true);
        }
        finally
        {
            atlas.EndBatch();
        }

        Assert.Equal(first.X, reused.X);
        Assert.Equal(first.Y, reused.Y);
        Assert.Equal(first.Width, reused.Width);
        Assert.Equal(first.Height, reused.Height);
        Assert.False(atlas.CapacityExceeded);
        Assert.Equal(1ul, atlas.EvictionCount);
        Assert.Equal(1, atlas.CachedGlyphCount);
        Assert.True(atlas.Generation > generation);
    }

    [Fact]
    public void SampleEffectGpuResourcesAreCreatedOnlyByEffectPages()
    {
        var controller = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Samples", "Windows", "MainWindowController.cs"));

        Assert.DoesNotContain("AppState._offscreenCompositor = new Compositor", controller, StringComparison.Ordinal);
        Assert.Contains("internal static void EnsureEffectResources()", controller, StringComparison.Ordinal);
        Assert.Contains("AppState._offscreenCompositor ??= new Compositor", controller, StringComparison.Ordinal);

        foreach (var page in new[] { "ComputeFxPage.cs", "ImageEffectsPage.cs", "ImageRepeatShowcasePage.cs" })
        {
            var source = File.ReadAllText(FindRepoFile("src", "ProGPU.Samples", "Pages", page));
            Assert.Contains("MainWindowController.EnsureEffectResources();", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UncappedApplicationLoopHasNoMandatorySleep()
    {
        var appRunner = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.WinUI", "Core", "AppBuilder.cs"));

        Assert.DoesNotContain("Thread.Sleep(1)", appRunner, StringComparison.Ordinal);
        Assert.Contains("allWindowsUseVSync", appRunner, StringComparison.Ordinal);
        Assert.Contains("Thread.Yield()", appRunner, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleSampleCompositorRendersDirectlyToTarget()
    {
        using var window = new HeadlessWindow(
            64,
            48,
            CompositorOptions.Default with { PrimarySampleCount = 1 });
        window.Content = new Border
        {
            Background = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f))
        };

        window.Render();

        var pixels = window.ReadPixels();
        var center = (24 * 64 + 32) * 4;
        Assert.True(pixels[center] > 240);
        Assert.True(pixels[center + 1] < 15);
        Assert.True(pixels[center + 2] < 15);
    }

    [Fact]
    public void HighDpiWindowsAvoidRedundantMultisampleTargets()
    {
        var window = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.WinUI", "Core", "Window.cs"));
        var viewport = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.WinUI", "Controls", "Viewport3D.cs"));

        Assert.Contains("ResolveWindowDpiScale(framebufferSize) >= 1.5f ? 1u : 4u", window, StringComparison.Ordinal);
        Assert.Contains("PrimarySampleCount = sampleCount", window, StringComparison.Ordinal);
        Assert.Contains("dpiScale >= 1.5f ? 1u : 4u", viewport, StringComparison.Ordinal);
        Assert.Contains("_msaaColorTexture = sampleCount > 1", viewport, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionPipelinesFollowTheActiveCompositorSampleCount()
    {
        string[] extensions =
        [
            "AcisSolidExtensionPipeline.cs",
            "BackdropMaterialExtensionPipeline.cs",
            "CustomGridExtensionPipeline.cs",
            "GpuLineSeriesExtensionPipeline.cs",
            "GpuScatterSeriesExtensionPipeline.cs",
            "HatchExtensionPipeline.cs",
            "ImageEffectExtensionPipeline.cs",
            "Line3DExtensionPipeline.cs",
            "ShaderToyExtensionPipeline.cs",
            "WpfShaderEffectExtensionPipeline.cs"
        ];

        foreach (var extension in extensions)
        {
            var source = File.ReadAllText(FindRepoFile("src", "ProGPU.Scene", "Extensions", extension));
            Assert.Contains("isOffscreen ? 1u : compositor.Options.PrimarySampleCount", source, StringComparison.Ordinal);
            Assert.DoesNotContain("isOffscreen ? 1u : 4u", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NavigationContentSwitchDoesNotRebuildPaneItems()
    {
        var navigation = new NavigationView();
        var first = new NavigationViewItem("First", "", () => new Border());
        var second = new NavigationViewItem("Second", "", () => new Border());
        navigation.MenuItems.Add(first);
        navigation.MenuItems.Add(second);
        navigation.SelectedItem = first;

        var pane = Assert.IsAssignableFrom<Visual>(first.Parent);
        var paneVersion = pane.ChangeVersion;

        navigation.SelectedItem = second;
        navigation.SelectedItem = first;

        Assert.Same(pane, first.Parent);
        Assert.Same(pane, second.Parent);
        Assert.InRange(pane.ChangeVersion - paneVersion, 1, 8);
    }

    [Fact]
    public void ReparentingWithinTheSameThemeDoesNotInvalidateTheSubtree()
    {
        var firstParent = new Grid();
        var secondParent = new Grid();
        var child = new ThemeChangeCounter();
        child.AddChild(new ThemeChangeCounter());
        firstParent.AddChild(child);
        var initialCount = child.TotalThemeChanges;

        secondParent.AddChild(child);

        Assert.Equal(initialCount, child.TotalThemeChanges);

        secondParent.RemoveChild(child);
        secondParent.RequestedTheme = ThemeManager.CurrentTheme == ElementTheme.Dark
            ? ElementTheme.Light
            : ElementTheme.Dark;
        secondParent.AddChild(child);

        Assert.Equal(initialCount + 2, child.TotalThemeChanges);
    }

    [Fact]
    public void IndexedItemsSourceRemainsLazyForVirtualizedControls()
    {
        var items = new ThrowingIndexedList(65_535);
        var control = new ItemsControl
        {
            ItemsHost = new UniformVirtualizingGridPanel(),
            ItemsSource = items,
        };

        Assert.Equal(65_535, control.ItemCount);
        Assert.Empty(control.Items);
        Assert.Equal(42, control.GetItemAt(42));
        Assert.Equal(0, items.EnumerationCount);
    }

    [Fact]
    public void LayoutPropertyChangesInvalidateTheRetainedVisualTree()
    {
        var root = new Grid();
        var child = new Border();
        root.AddChild(child);
        root.Measure(new Vector2(400f, 300f));
        root.Arrange(new Rect(0f, 0f, 400f, 300f));
        root.IsDirty = false;
        child.IsDirty = false;
        var version = root.ChangeVersion;

        child.Padding = new Thickness(12f);

        Assert.True(child.IsDirty);
        Assert.True(root.IsDirty);
        Assert.True(root.ChangeVersion > version);
    }

    [Fact]
    public void MotionMarkSchedulesBeforeRenderAndReusesOfficialPathGeometry()
    {
        var previousFont = ProGPU.Samples.AppState._font;
        ProGPU.Samples.AppState._font = null;
        try
        {
            var visual = new ProGPU.Samples.MotionMarkShowcaseVisual();
            visual.Measure(new Vector2(900f, 620f));
            visual.Arrange(new Rect(0f, 0f, 900f, 620f));

            var firstContext = new DrawingContext();
            var versionBeforeRender = visual.ChangeVersion;
            visual.OnRender(firstContext);
            Assert.Equal(versionBeforeRender, visual.ChangeVersion);

            var firstPaths = firstContext.Commands
                .Where(static command => command.Type == RenderCommandType.DrawPath)
                .Select(static command => command.Path)
                .ToArray();
            var firstGeometryCaches = firstContext.Commands
                .Where(static command => command.Type == RenderCommandType.DrawPath)
                .Select(static command => command.GeometryCache)
                .ToArray();
            Assert.NotEmpty(firstPaths);
            Assert.True(firstPaths.Length < visual.ElementCount);
            Assert.DoesNotContain(firstContext.Commands, static command =>
                command.Type is RenderCommandType.DrawLine or RenderCommandType.DrawBezier or RenderCommandType.DrawCubicBezier);

            var secondContext = new DrawingContext();
            visual.OnRender(secondContext);
            var secondPaths = secondContext.Commands
                .Where(static command => command.Type == RenderCommandType.DrawPath)
                .Select(static command => command.Path)
                .ToArray();
            var secondGeometryCaches = secondContext.Commands
                .Where(static command => command.Type == RenderCommandType.DrawPath)
                .Select(static command => command.GeometryCache)
                .ToArray();
            Assert.Equal(firstPaths.Length, secondPaths.Length);
            for (var index = 0; index < firstPaths.Length; index++)
            {
                Assert.Same(firstPaths[index], secondPaths[index]);
                Assert.Same(firstGeometryCaches[index], secondGeometryCaches[index]);
            }

            var versionBeforeAdvance = visual.ChangeVersion;
            Assert.IsAssignableFrom<ProGPU.Samples.IAnimatedElement>(visual).Update(1f / 60f);
            Assert.True(visual.ChangeVersion > versionBeforeAdvance);
        }
        finally
        {
            ProGPU.Samples.AppState._font = previousFont;
        }
    }

    [Fact]
    public void MotionMarkAnimationIsDiscoveredThroughTheAttachedSampleTree()
    {
        var visual = new ProGPU.Samples.MotionMarkShowcaseVisual();
        var root = new Grid();
        root.AddChild(visual);
        root.Measure(new Vector2(900f, 620f));
        root.Arrange(new Rect(0f, 0f, 900f, 620f));
        var visualVersion = visual.ChangeVersion;
        var rootVersion = root.ChangeVersion;

        ProGPU.Samples.VisualExtensions.UpdateSampleAnimations(root, 1f / 60f);

        Assert.True(visual.ChangeVersion > visualVersion);
        Assert.True(root.ChangeVersion > rootVersion);
    }

    [Fact]
    public void MotionMarkAnimationUsesTheOriginalSixtyHertzCadence()
    {
        var visual = new ProGPU.Samples.MotionMarkShowcaseVisual();
        visual.Measure(new Vector2(900f, 620f));
        visual.Arrange(new Rect(0f, 0f, 900f, 620f));
        var initialVersion = visual.ChangeVersion;

        visual.Update(1f / 120f);
        Assert.Equal(initialVersion, visual.ChangeVersion);

        visual.Update(1f / 120f);
        Assert.True(visual.ChangeVersion > initialVersion);
    }

    [Fact]
    public void MotionMarkRetainedRecordingAllocationsStayBounded()
    {
        var previousFont = ProGPU.Samples.AppState._font;
        ProGPU.Samples.AppState._font = null;
        try
        {
            foreach (var useIndividualPaths in new[] { false, true })
            {
                foreach (var fillShapes in new[] { false, true })
                {
                    var visual = new ProGPU.Samples.MotionMarkShowcaseVisual
                    {
                        UseIndividualPaths = useIndividualPaths,
                        FillShapes = fillShapes
                    };
                    visual.Measure(new Vector2(900f, 620f));
                    visual.Arrange(new Rect(0f, 0f, 900f, 620f));
                    var context = new DrawingContext();

                    for (var frame = 0; frame < 240; frame++)
                    {
                        visual.Update(1f / 60f);
                        context.Clear();
                        visual.OnRender(context);
                    }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    for (var frame = 0; frame < 120; frame++)
                    {
                        visual.Update(1f / 60f);
                        context.Clear();
                        visual.OnRender(context);
                    }

                    long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    Assert.True(
                        allocatedBytes < 64 * 1024,
                        $"MotionMark retained recording allocated {allocatedBytes:N0} bytes across 120 " +
                        $"animated frames (individual={useIndividualPaths}, fill={fillShapes}).");
                }
            }
        }
        finally
        {
            ProGPU.Samples.AppState._font = previousFont;
        }
    }

    [Fact]
    public void MarkdownRelayoutsWhenItsAvailableWidthChanges()
    {
        var markdown = new MarkdownTextBlock
        {
            Font = LoadTestFont(),
            FontSize = 18f,
            Padding = new Thickness(0f),
            Markdown = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu"
        };

        markdown.Measure(new Vector2(600f, 1_000f));
        markdown.Arrange(new Rect(0f, 0f, 600f, 1_000f));
        var wideMaxY = markdown.PositionedChars.Max(static character => character.Position.Y);

        markdown.Measure(new Vector2(160f, 1_000f));
        markdown.Arrange(new Rect(0f, 0f, 160f, 1_000f));
        var narrowMaxY = markdown.PositionedChars.Max(static character => character.Position.Y);

        Assert.True(narrowMaxY > wideMaxY);
    }

    [Fact]
    public void UnchangedMarkdownReusesItsRecordedTextCommands()
    {
        var markdown = new MarkdownTextBlock
        {
            Font = LoadTestFont(),
            FontSize = 18f,
            Markdown = "# Retained markdown\n\nThe text commands remain stable."
        };
        markdown.Measure(new Vector2(500f, 300f));
        markdown.Arrange(new Rect(0f, 0f, 500f, 300f));

        var first = new DrawingContext();
        markdown.OnRender(first);
        var second = new DrawingContext();
        markdown.OnRender(second);
        var firstTexts = first.Commands
            .Where(static command => command.Type == RenderCommandType.DrawText)
            .Select(static command => command.Text)
            .ToArray();
        var secondTexts = second.Commands
            .Where(static command => command.Type == RenderCommandType.DrawText)
            .Select(static command => command.Text)
            .ToArray();

        Assert.NotEmpty(firstTexts);
        Assert.Equal(firstTexts.Length, secondTexts.Length);
        for (var index = 0; index < firstTexts.Length; index++)
        {
            Assert.Same(firstTexts[index], secondTexts[index]);
        }
    }

    [Fact]
    public void EmptyMarkdownClearsRetainedTextAndCommands()
    {
        var markdown = new MarkdownTextBlock
        {
            Font = LoadTestFont(),
            FontSize = 18f,
            Markdown = "Retained text must be discarded."
        };
        markdown.Measure(new Vector2(500f, 300f));
        markdown.Arrange(new Rect(0f, 0f, 500f, 300f));
        Assert.NotEmpty(markdown.PositionedChars);

        markdown.Markdown = string.Empty;
        markdown.Measure(new Vector2(500f, 300f));
        markdown.Arrange(new Rect(0f, 0f, 500f, 300f));
        var context = new DrawingContext();
        markdown.OnRender(context);

        Assert.Empty(markdown.PositionedChars);
        Assert.DoesNotContain(context.Commands, static command => command.Type == RenderCommandType.DrawText);
    }

    [Fact]
    public void EmptyRichTextClearsRetainedLayout()
    {
        var text = new RichTextBlock { Font = LoadTestFont(), FontSize = 18f };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run("Retained text must be discarded."));
        text.Measure(new Vector2(500f, 300f));
        text.Arrange(new Rect(0f, 0f, 500f, 300f));
        Assert.NotEmpty(text.PositionedChars);

        text.Inlines.Clear();
        text.Invalidate();
        text.Measure(new Vector2(500f, 300f));
        Assert.Empty(text.PositionedChars);
        var measuredContext = new DrawingContext();
        text.OnRender(measuredContext);
        Assert.DoesNotContain(
            measuredContext.Commands,
            static command => command.Type == RenderCommandType.DrawText);

        text.Arrange(new Rect(0f, 0f, 500f, 300f));
        var context = new DrawingContext();
        text.OnRender(context);

        Assert.Empty(text.PositionedChars);
        Assert.DoesNotContain(context.Commands, static command => command.Type == RenderCommandType.DrawText);
    }

    [Fact]
    public void NoWrapRichTextKeepsControlContentOnOneLine()
    {
        var text = new RichTextBlock
        {
            Font = LoadTestFont(),
            FontSize = 14f,
            TextWrapping = TextWrapping.NoWrap
        };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run("Browse"));

        text.Measure(new Vector2(12f, 80f));
        text.Arrange(new Rect(0f, 0f, 12f, 80f));

        Assert.True(text.DesiredSize.X > 12f);
        Assert.Single(text.PositionedChars.Select(static character => character.Position.Y).Distinct());
    }

    [Fact]
    public void WrapWholeWordsDoesNotSplitAnUnspacedToken()
    {
        var wholeWord = new RichTextBlock
        {
            Font = LoadTestFont(),
            FontSize = 14f,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        wholeWord.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run("LongFilenameWithoutBreaks.txt"));

        var characterWrap = new RichTextBlock
        {
            Font = wholeWord.Font,
            FontSize = wholeWord.FontSize,
            TextWrapping = TextWrapping.Wrap
        };
        characterWrap.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run("LongFilenameWithoutBreaks.txt"));

        wholeWord.Measure(new Vector2(48f, 200f));
        wholeWord.Arrange(new Rect(0f, 0f, 48f, 200f));
        characterWrap.Measure(new Vector2(48f, 200f));
        characterWrap.Arrange(new Rect(0f, 0f, 48f, 200f));

        Assert.Single(wholeWord.PositionedChars.Select(static character => character.Position.Y).Distinct());
        Assert.True(characterWrap.PositionedChars.Select(static character => character.Position.Y).Distinct().Count() > 1);
    }

    [Fact]
    public void NoWrapRichTextStaysInsideAnAutoSizedTrailingGridColumnAfterMutableRunUpdate()
    {
        var run = new Microsoft.UI.Xaml.Documents.Run("0");
        var text = new RichTextBlock
        {
            Font = LoadTestFont(),
            FontSize = 11f,
            TextWrapping = TextWrapping.NoWrap
        };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Bold(run));

        var row = new Grid();
        row.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        row.ColumnDefinitions.Add(new GridLength(0f, GridUnitType.Auto));
        row.AddChild(text);
        Grid.SetColumn(text, 1);

        row.Measure(new Vector2(337f, 100f));
        row.Arrange(new Rect(0f, 0f, 337f, 100f));
        run.Text = "5.80 x 1.40 x 5.80";
        row.Measure(new Vector2(337f, 100f));
        row.Arrange(new Rect(0f, 0f, 337f, 100f));

        Assert.True(text.Offset.X > 100f);
        Assert.InRange(text.Offset.X + text.Size.X, 336.99f, 337.01f);
        Assert.Single(text.PositionedChars.Select(static character => character.Position.Y).Distinct());
        Assert.All(text.PositionedChars, character => Assert.InRange(character.Position.X, 0f, text.Size.X));
    }

    [Fact]
    public void WrappedRichTextRetainsItsFiniteMeasureWidth()
    {
        var text = new RichTextBlock
        {
            Font = LoadTestFont(),
            FontSize = 14f,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run("Responsive controls remain readable"));

        text.Measure(new Vector2(64f, 200f));
        text.Arrange(new Rect(0f, 0f, 160f, 200f));

        Assert.InRange(text.DesiredSize.X, 63.99f, 64.01f);
        Assert.InRange(text.Size.X, 63.99f, 64.01f);
        Assert.True(text.PositionedChars.Select(static character => character.Position.Y).Distinct().Count() > 1);
    }

    [Fact]
    public void ContentPresenterUsesWinUiNoWrapDefaultForTextContent()
    {
        var presenter = new ContentPresenter { Content = "Button label" };

        var generatedText = Assert.IsType<RichTextBlock>(Assert.Single(presenter.Children));
        Assert.Equal(TextWrapping.NoWrap, presenter.TextWrapping);
        Assert.Equal(TextWrapping.NoWrap, generatedText.TextWrapping);
    }

    [Fact]
    public void MutableRunInvalidatesAndRelayoutsItsOwningTextBlock()
    {
        var font = LoadTestFont();
        var run = new Microsoft.UI.Xaml.Documents.Run("A");
        var text = new RichTextBlock { Font = font, FontSize = 18f };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Bold(run));
        text.Measure(new Vector2(300f, 100f));
        text.Arrange(new Rect(0f, 0f, 300f, 100f));
        Assert.Single(text.PositionedChars);
        var version = text.ChangeVersion;

        run.Text = "AAAA";

        Assert.True(text.ChangeVersion > version);
        text.Measure(new Vector2(300f, 100f));
        text.Arrange(new Rect(0f, 0f, 300f, 100f));
        Assert.Equal(4, text.PositionedChars.Count);
    }

    [Fact]
    public void RichEditCaretChangesReuseExistingTextLayout()
    {
        var editor = new RichEditBox { Font = LoadTestFont(), Text = "Caret layout remains retained." };
        editor.Measure(new Vector2(400f, 160f));
        editor.Arrange(new Rect(0f, 0f, 400f, 160f));
        var scrollViewer = Assert.IsType<ScrollViewer>(Assert.Single(editor.Children));
        var text = Assert.IsType<RichTextBlock>(scrollViewer.Content);
        var firstPositionedCharacter = Assert.IsType<PositionedRichChar>(text.PositionedChars[0]);

        editor.CaretIndex = 5;
        editor.Measure(new Vector2(400f, 160f));
        editor.Arrange(new Rect(0f, 0f, 400f, 160f));

        Assert.Same(firstPositionedCharacter, text.PositionedChars[0]);
    }

    [Fact]
    public void RichEditCaretHitTestingUsesResolvedFallbackAdvance()
    {
        NotoFontFamily.RegisterFallbacks();
        TtfFont baseFont = InterFontFamily.Regular;
        var editor = new RichEditBox
        {
            Font = baseFont,
            FontSize = 32f,
            Text = "\u4E00"
        };
        editor.Measure(new Vector2(200f, 80f));
        editor.Arrange(new Rect(0f, 0f, 200f, 80f));
        var scrollViewer = Assert.IsType<ScrollViewer>(Assert.Single(editor.Children));
        var text = Assert.IsType<RichTextBlock>(scrollViewer.Content);
        PositionedRichChar character = Assert.Single(text.PositionedChars);
        TtfFont fallbackFont = Assert.IsType<TtfFont>(character.Info.Font);
        Assert.NotSame(baseFont, fallbackFont);

        float fallbackAdvance = fallbackFont.GetAdvanceWidth(
            fallbackFont.GetGlyphIndex(character.Info.Character),
            character.Info.FontSize);
        float baseAdvance = baseFont.GetAdvanceWidth(
            baseFont.GetGlyphIndex(character.Info.Character),
            character.Info.FontSize);
        Assert.True(Math.Abs(fallbackAdvance - baseAdvance) > 0.01f);
        float withinCharacter = (fallbackAdvance + baseAdvance) * 0.25f;
        int expectedCaret = withinCharacter > fallbackAdvance * 0.5f ? 1 : 0;
        int baseFontCaret = withinCharacter > baseAdvance * 0.5f ? 1 : 0;
        Assert.NotEqual(baseFontCaret, expectedCaret);

        editor.OnPointerPressed(new PointerRoutedEventArgs
        {
            Position = new Vector2(
                editor.Padding.Left + character.Position.X + withinCharacter,
                editor.Padding.Top + character.Position.Y + character.Info.FontSize * 0.5f)
        });
        editor.OnPointerReleased(new PointerRoutedEventArgs());

        Assert.Equal(expectedCaret, editor.CaretIndex);
    }

    [Fact]
    public void VirtualizedVisibleRebindPreservesRealizedVisuals()
    {
        var bindCount = 0;
        var panel = new VirtualizingScrollPanel
        {
            ItemsCount = 1_000,
            ItemHeight = 24f,
            CreateVisualFactory = static () => new Border(),
            BindVisualCallback = (_, _) => bindCount++
        };
        panel.Measure(new Vector2(320f, 120f));
        panel.Arrange(new Rect(0f, 0f, 320f, 120f));
        var realized = panel.Children.ToArray();
        var initialBindCount = bindCount;

        panel.RebindVisibleItems();

        Assert.True(bindCount > initialBindCount);
        Assert.Equal(realized.Length, panel.Children.Count);
        for (var index = 0; index < realized.Length; index++)
        {
            Assert.Same(realized[index], panel.Children[index]);
        }
    }

    [Fact]
    public void FontIconUsesRetainedOutlineWithOfficialPathTransform()
    {
        var path = new[]
        {
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/Library/Fonts/Arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        }.FirstOrDefault(File.Exists);
        Assert.NotNull(path);

        var font = new TtfFont(path!);
        var glyph = font.GetGlyphIndex('A');
        var outline = font.GetGlyphOutline(glyph);
        Assert.NotNull(outline);

        var icon = new FontIcon
        {
            Font = font,
            GlyphIndex = glyph,
            FontSize = 48f,
            UseVectorGlyphRendering = true,
        };
        icon.Measure(new Vector2(80f, 80f));
        icon.Arrange(new Rect(0f, 0f, 80f, 80f));

        var context = new DrawingContext();
        icon.OnRender(context);

        var command = Assert.Single(context.Commands, static command => command.Type == RenderCommandType.DrawPath);
        Assert.Same(outline, command.Path);
        Assert.NotEqual(default, command.Transform);
        Assert.Contains(outline.Figures.SelectMany(static figure => figure.Segments), static segment =>
            segment is CubicBezierSegment or QuadraticBezierSegment or LineSegment);
    }

    private static TtfFont LoadTestFont()
    {
        var path = new[]
        {
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/Library/Fonts/Arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        }.FirstOrDefault(File.Exists);
        Assert.NotNull(path);
        return new TtfFont(path!);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(pathParts)} from {AppContext.BaseDirectory}.");
    }

    private sealed class ThrowingIndexedList : IList
    {
        public ThrowingIndexedList(int count)
        {
            Count = count;
        }

        public int EnumerationCount { get; private set; }
        public int Count { get; }
        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public object? this[int index]
        {
            get => index >= 0 && index < Count ? index : throw new ArgumentOutOfRangeException(nameof(index));
            set => throw new NotSupportedException();
        }

        public IEnumerator GetEnumerator()
        {
            EnumerationCount++;
            throw new InvalidOperationException("Indexed sources must not be enumerated eagerly.");
        }

        public bool Contains(object? value) => value is int index && index >= 0 && index < Count;
        public int IndexOf(object? value) => value is int index && index >= 0 && index < Count ? index : -1;
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }

    private sealed class ThemeChangeCounter : Grid
    {
        private int _themeChanges;

        public int TotalThemeChanges => _themeChanges + Children.OfType<ThemeChangeCounter>().Sum(static child => child.TotalThemeChanges);

        protected override void OnThemeChanged()
        {
            _themeChanges++;
            base.OnThemeChanged();
        }
    }

    private sealed class ArrangeCounter : FrameworkElement
    {
        public int ArrangeCount { get; private set; }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return new Vector2(WidthConstraint ?? availableSize.X, HeightConstraint ?? availableSize.Y);
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            ArrangeCount++;
        }
    }

    private sealed class ClipCullHost : FrameworkElement
    {
        private readonly RenderCounterContainer _child;

        public ClipCullHost(RenderCounterContainer child)
        {
            _child = child;
            AddChild(child);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            _child.Measure(new Vector2(20f, 20f));
            return availableSize;
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            ClipBounds = new Rect(0f, 0f, arrangeRect.Width, arrangeRect.Height);
            _child.Arrange(new Rect(0f, 200f, 20f, 20f));
        }
    }

    private sealed class RenderCounterContainer : FrameworkElement
    {
        private readonly RenderCounterVisual _child;

        public RenderCounterContainer(RenderCounterVisual child)
        {
            _child = child;
            AddChild(child);
        }

        public int RenderCount { get; private set; }
        public override Rect? LocalRenderBounds => new(Vector2.Zero, Size);

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            _child.Measure(new Vector2(20f, 20f));
            return new Vector2(20f, 20f);
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            _child.Arrange(new Rect(0f, -200f, 20f, 20f));
        }

        public override void OnRender(DrawingContext context)
        {
            RenderCount++;
        }
    }

    private sealed class RenderCounterVisual : FrameworkElement
    {
        public int RenderCount { get; private set; }
        public override Rect? LocalRenderBounds => new(Vector2.Zero, Size);

        protected override Vector2 MeasureOverride(Vector2 availableSize) => new(20f, 20f);

        public override void OnRender(DrawingContext context)
        {
            RenderCount++;
        }
    }
}
