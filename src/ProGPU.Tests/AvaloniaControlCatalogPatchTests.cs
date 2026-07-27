using System.Text.RegularExpressions;
using Xunit;

namespace ProGPU.Tests;

public sealed class AvaloniaControlCatalogPatchTests
{
    [Fact]
    public void ControlCatalogTitleIdentifiesTheActiveRuntimeBackends()
    {
        string runtimeTitle = File.ReadAllText(
            FindRepoFile(
                "integration",
                "AvaloniaControlCatalogHarness",
                "ControlCatalogRuntimeTitle.cs"));
        string sourceProgram = File.ReadAllText(
            FindRepoFile(
                "integration",
                "AvaloniaSourceControlCatalog",
                "Program.cs"));
        string skiaProgram = File.ReadAllText(
            FindRepoFile(
                "integration",
                "AvaloniaSkiaControlCatalogReference",
                "Program.cs"));

        Assert.Contains("Windowing: {backend.Windowing}", runtimeTitle);
        Assert.Contains("Rendering: {backend.Rendering}", runtimeTitle);
        Assert.Contains("Compositor: {backend.Compositor}", runtimeTitle);
        Assert.Contains("Text: {backend.TextShaping}", runtimeTitle);
        Assert.Contains(
            "Window.WindowOpenedEvent.AddClassHandler<MainWindow>",
            runtimeTitle);
        Assert.Contains(
            "ProGpuRenderingDiagnostics.FrameRendered +=",
            runtimeTitle);
        Assert.Contains(
            "\"DawnMetalIOSurface\"",
            runtimeTitle);
        Assert.Contains(
            "\"AvaloniaFramebuffer\"",
            runtimeTitle);
        Assert.Contains("\"ProGPU retained\"", sourceProgram);
        Assert.Contains("\"ProGPU OpenType\"", sourceProgram);
        Assert.Contains("\"Skia\"", skiaProgram);
        Assert.Contains("\"HarfBuzz\"", skiaProgram);
        Assert.DoesNotContain("System.Reflection", runtimeTitle);
    }

    [Fact]
    public void ControlCatalogPagesAreDeferredThroughTypedFactories()
    {
        string patch = File.ReadAllText(
            FindRepoFile(
                "eng",
                "avalonia",
                "12.0.5",
                "progpu-controlcatalog.patch"));
        int deferredFileStart = patch.IndexOf(
            "diff --git a/samples/ControlCatalog/DeferredCatalogPage.cs",
            StringComparison.Ordinal);

        Assert.True(deferredFileStart >= 0);
        string deferredFile = patch[deferredFileStart..];
        Assert.Contains(
            "+internal sealed class DeferredCatalogPage : ContentControl",
            deferredFile,
            StringComparison.Ordinal);
        Assert.Contains(
            "+    protected override void OnAttachedToVisualTree(",
            deferredFile,
            StringComparison.Ordinal);
        Assert.Contains(
            "+            Content = CreatePage(PageKind);",
            deferredFile,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Activator.CreateInstance", deferredFile, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", deferredFile, StringComparison.Ordinal);

        int typedFactories = Regex.Matches(
            deferredFile,
            @"^\+\s+CatalogPageKind\.\w+\s+=>\s+new\s+\w+\(\),$",
            RegexOptions.Multiline).Count;
        int deferredHosts = Regex.Matches(
            patch,
            @"^\+\s+<local:DeferredCatalogPage PageKind=""\w+"" />$",
            RegexOptions.Multiline).Count;

        Assert.Equal(69, typedFactories);
        Assert.Equal(70, deferredHosts);
    }

    [Fact]
    public void BenchmarkUsesTypedJsonAndPhysicalPixelScreenshots()
    {
        string benchmark = File.ReadAllText(
            FindRepoFile(
                "integration",
                "AvaloniaControlCatalogHarness",
                "ControlCatalogTelemetrySession.cs"));

        Assert.Contains(
            "new Utf8JsonWriter(",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "WriteDistribution(writer, \"Frame\", frameTime)",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "$\"{prefix}TimeSampleCount\"",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "_window.Bounds.Width *",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "_window.RenderScaling",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "bitmap.Render(_window);",
            benchmark,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "JsonSerializer.Serialize",
            benchmark,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopSmokeHarnessesWaitForSafeRenderedFrames()
    {
        string benchmark = File.ReadAllText(
            FindRepoFile(
                "integration",
                "AvaloniaControlCatalogHarness",
                "ControlCatalogTelemetrySession.cs"));
        string sourceSmoke = File.ReadAllText(
            FindRepoFile(
                "integration",
                "AvaloniaSourceSampleHost",
                "SourceSampleSmokeSession.cs"));
        string windowChromeSmoke = File.ReadAllText(
            FindRepoFile(
                "integration",
                "ProGpuAvaloniaPackageSmoke",
                "WindowChromeSmokeCoordinator.cs"));

        Assert.Contains(
            "Dispatcher.UIThread.Post(\n            Complete,",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "SilkNetPlatform.FramePreparing += OnFramePreparing;",
            sourceSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGpuRenderingDiagnostics.FrameRendered +=",
            windowChromeSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionFallbackNodes\"",
            windowChromeSmoke,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ControlCatalogProfilerBuildsPinnedNativeWithoutPackArtifacts()
    {
        string profiler = File.ReadAllText(
            FindRepoFile(
                "tools",
                "profile-avalonia-controlcatalog.sh"));
        string nativeBuilder = File.ReadAllText(
            FindRepoFile(
                "tools",
                "build-avalonia-native-dawn.sh"));

        Assert.Contains(
            "-p:PackAvaloniaNative=false",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "generate-headers.sh",
            nativeBuilder,
            StringComparison.Ordinal);
        Assert.Contains(
            "mkdir -p \"$(dirname \"$destination\")\"",
            nativeBuilder,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedAvaloniaFontsUseTypedAssemblyResourceSlices()
    {
        string patch = File.ReadAllText(
            FindRepoFile(
                "eng",
                "avalonia",
                "12.0.5",
                "progpu-compositor.patch"));
        string fontCatalog = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "AvaloniaFontCatalog.cs"));
        string ttfFont = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Text",
                "TtfFont.cs"));

        Assert.Contains(
            "+internal sealed class AssemblyResourceSliceStream : SlicedStream",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        return new AssemblyResourceSliceStream(",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "stream is AssemblyResourceSliceStream slice",
            fontCatalog,
            StringComparison.Ordinal);
        Assert.Contains(
            "TtfFont.LoadEmbeddedResourceSlice(",
            fontCatalog,
            StringComparison.Ordinal);
        Assert.Contains(
            "slice.OpenResourceStream()",
            fontCatalog,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "slice.Assembly",
            fontCatalog,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateEmbeddedSliceStorage(",
            ttfFont,
            StringComparison.Ordinal);
        Assert.Contains(
            "embeddedData.DataAddress + offset",
            ttfFont,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            fontCatalog,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceBackendManagerRecreatesAReportedLostRendererContext()
    {
        string patch = File.ReadAllText(
            FindRepoFile(
                "eng",
                "avalonia",
                "12.0.5",
                "progpu-compositor.patch"));

        Assert.Contains(
            "diff --git a/src/Avalonia.Base/Rendering/PlatformRenderInterfaceContextManager.cs",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+            _backend.IsLost ||",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "Lost_Backend_Context_Is_Disposed_And_Recreated_Without_Platform_Graphics",
            patch,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionProfileRequiresNativeCustomVisualExecution()
    {
        string profiler = File.ReadAllText(
            FindRepoFile(
                "tools",
                "profile-avalonia-controlcatalog.sh"));
        string benchmark = File.ReadAllText(
            FindRepoFile(
                "integration",
                "AvaloniaControlCatalogHarness",
                "ControlCatalogTelemetrySession.cs"));
        string scene = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "AvaloniaCompositionScene.cs"));

        Assert.Contains(
            "\"$page\" == \"Composition\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionCustomVisualNodes\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionCustomVisualCompilations\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionCustomVisualCompilations\"",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "source is ServerCompositionCustomVisual",
            scene,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutClipFixtureRequiresCompactSynchronizationTelemetry()
    {
        string profiler = File.ReadAllText(
            FindRepoFile(
                "tools",
                "profile-avalonia-controlcatalog.sh"));
        string benchmark = File.ReadAllText(
            FindRepoFile(
                "integration",
                "AvaloniaControlCatalogHarness",
                "ControlCatalogTelemetrySession.cs"));
        string scene = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "AvaloniaCompositionScene.cs"));

        Assert.Contains(
            "PROGPU_AVALONIA_BENCHMARK_LAYOUT_CLIP",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.ClipToBounds = true;",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainedCompositionLayoutClipSynchronizations",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_BENCHMARK_GEOMETRY_CLIP",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainedCompositionGeometryClipSynchronizations",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_CHANNEL",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainedCompositionBitmapCacheSynchronizations",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.CacheMode = new BitmapCache",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_BENCHMARK_EFFECT_CHANNEL",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainedCompositionEffectSynchronizations",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.Effect = new BlurEffect",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_BENCHMARK_OPACITY_MASK_CHANNEL",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainedCompositionOpacityMaskSynchronizations",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.OpacityMask = Brushes.White;",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_BENCHMARK_INHERITED_DRAWING_OPTIONS_CHANNEL",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainedCompositionInheritedDrawingOptionsSynchronizations",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_BENCHMARK_TOPOLOGY_CHANNEL",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "topologyFirstParent.Children.Add(topologyChild);",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "destination.Children.Add(_topologyChild);",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainedCompositionTopologySynchronizationsDuringMeasurement",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_BENCHMARK_ADORNER_CHANNEL",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "AdornerLayer.GetAdorner(firstTarget)",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "AdornerLayer.SetAdornedElement(",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainedCompositionAdornerSynchronizationsDuringMeasurement",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_LAYOUT_CLIP_FIXTURE",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionLayoutClipSynchronizations\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_GEOMETRY_CLIP_FIXTURE",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionGeometryClipSynchronizations\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_BITMAP_CACHE_FIXTURE",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionBitmapCacheSynchronizations\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_EFFECT_FIXTURE",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionEffectSynchronizations\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_OPACITY_MASK_FIXTURE",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionOpacityMaskSynchronizations\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_INHERITED_DRAWING_OPTIONS_FIXTURE",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionInheritedDrawingOptionsSynchronizations\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_TOPOLOGY_FIXTURE",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionTopologySynchronizationsDuringMeasurement\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionSceneFullSynchronizationsDuringMeasurement\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_AVALONIA_ADORNER_FIXTURE",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionAdornerSynchronizationsDuringMeasurement\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "LayoutClipSynchronizationCount++;",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "GeometryClipSynchronizationCount++;",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "BitmapCacheSynchronizationCount++;",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "EffectSynchronizationCount++;",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "OpacityMaskSynchronizationCount++;",
            scene,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ComplexAppearanceSynchronizationCount++;",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "InheritedDrawingOptionsSynchronizationCount++;",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "TrySynchronizeTopologyDelta(",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "TopologySynchronizationCount++;",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "AdornerSynchronizationCount++;",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "TrySynchronizeAdornerClips()",
            scene,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedDeltasUseCapturedGenerationCheckedHandles()
    {
        string patch = File.ReadAllText(
            FindRepoFile(
                "eng",
                "avalonia",
                "12.0.5",
                "progpu-compositor.patch"));
        string scene = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "AvaloniaCompositionScene.cs"));

        Assert.Contains(
            "+    internal readonly struct RetainedCompositionVisualDelta",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal ulong BackendHandle { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal Matrix3x2 Transform { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal ulong ContentRevision { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal bool IsVisible { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal float Opacity { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal RenderOptions RenderOptions { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal TextOptions TextOptions { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal Vector2 Size { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal bool ClipToBounds { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal IReadOnlyList<ServerCompositionVisual>? " +
            "TopologyChildren",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal bool AdornerIsClipped { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal ServerCompositionVisual? AdornedVisual { get; }",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        internal void NotifyRetainedSceneTopologyChanged" +
            "(ServerCompositionVisual visual)\n" +
            "+        {\n" +
            "+            if (_retainedDeltaTrackingEnabled)\n" +
            "+                QueueOrRefreshRetainedVisualDelta(visual);\n" +
            "+            AdvanceRetainedSceneRevision();",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        LayoutClip = 1 << 3",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        PrimitiveAppearance = 1 << 6",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        Adorner = 1 << 11",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+            | CompositionVisualChangedFields.AdornedVisual",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueOrRefreshRetainedVisualDelta(visual);",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "_retainedChangedVisuals[queueIndex] =",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "IReadOnlyList<RetainedCompositionVisualDelta>",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "backendOwner == 0",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "backendHandle == 0",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "delta.Source.RetainedBackendOwner == _ownerId",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "_visuals.TryGet(\n                backendHandle,\n                delta.RetainedId,",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceEquals(target.Source, delta.Source)",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.SynchronizeTransform(delta.Transform);",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.SourceRevision != delta.ContentRevision",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "delta.IsVisible,\n                delta.Opacity",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.SynchronizePrimitiveAppearance(",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.SynchronizeLayoutClip(",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.SynchronizeDrawingOptions(",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryRefreshInheritedDrawingOptionsSubtree(",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "delta.Size,\n                delta.ClipToBounds",
            scene,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "((changes & RetainedCompositionVisualChanges.Appearance) != 0 &&",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEffectivelyRenderable(target)",
            scene,
            StringComparison.Ordinal);
        Assert.Contains(
            "for (ProGPU.Scene.Visual? candidate = visual;",
            scene,
            StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, pathParts));
    }
}
