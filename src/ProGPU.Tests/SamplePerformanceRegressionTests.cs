using System.Collections;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using ProGPU.Virtualization;
using Xunit;

namespace ProGPU.Tests;

public sealed class SamplePerformanceRegressionTests
{
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
        Assert.True(command.UseLogicalGlyphAtlasResolution);
        Assert.DoesNotContain(context.Commands, static command => command.Type == RenderCommandType.DrawPath);
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
            ItemsPanel = new UniformVirtualizingGridPanel(),
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
            Assert.Equal(firstPaths.Length, secondPaths.Length);
            for (var index = 0; index < firstPaths.Length; index++)
            {
                Assert.Same(firstPaths[index], secondPaths[index]);
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
}
