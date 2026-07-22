using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace ProGPU.Samples;

internal static class SamplePerformanceBenchmark
{
    private const string PageVariable = "PROGPU_SAMPLE_BENCHMARK_PAGE";
    private const string WarmupFramesVariable = "PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES";
    private const string MeasureFramesVariable = "PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES";
    private const string VSyncVariable = "PROGPU_SAMPLE_BENCHMARK_VSYNC";
    private const string ScrollVariable = "PROGPU_SAMPLE_BENCHMARK_SCROLL";
    private const string ScrollStepVariable = "PROGPU_SAMPLE_BENCHMARK_SCROLL_STEP";
    private const string PreconditionPagesVariable = "PROGPU_SAMPLE_BENCHMARK_PRECONDITION_PAGES";
    private const string PreconditionFramesVariable = "PROGPU_SAMPLE_BENCHMARK_PRECONDITION_FRAMES";
    private const string MemoryVariable = "PROGPU_SAMPLE_BENCHMARK_MEMORY";
    private const string HoldMillisecondsVariable = "PROGPU_SAMPLE_BENCHMARK_HOLD_MS";

    private static readonly int s_warmupFrames = ReadPositiveInt(WarmupFramesVariable, 180);
    private static readonly int s_measureFrames = ReadPositiveInt(MeasureFramesVariable, 600);
    private static readonly Stopwatch s_wallClock = new();
    private static int s_frame;
    private static double s_deltaSeconds;
    private static double s_compileMilliseconds;
    private static double s_maxCompileMilliseconds;
    private static int s_maxCompileFrame;
    private static float s_maxCompileScrollOffset;
    private static float s_maxCompileScrollExtent;
    private static int s_compileFramesOverBudget;
    private static double s_uploadMilliseconds;
    private static double s_renderMilliseconds;
    private static double s_compositorMilliseconds;
    private static double s_hostUpdateMilliseconds;
    private static double s_maxHostUpdateMilliseconds;
    private static double s_layoutMilliseconds;
    private static double s_animationMilliseconds;
    private static double s_surfaceAcquireMilliseconds;
    private static double s_presentMilliseconds;
    private static Microsoft.UI.Xaml.Window? s_window;
    private static long s_allocatedBytesAtStart;
    private static long s_managedHeapBytesAtStart;
    private static TimeSpan s_gcPauseDurationAtStart;
    private static int s_gen0CollectionsAtStart;
    private static int s_gen1CollectionsAtStart;
    private static int s_gen2CollectionsAtStart;
    private static int s_lolsRenderedAtStart;
    private static int s_sceneCacheHitFrames;
    private static int s_glyphStateSamples;
    private static int s_glyphStateFailures;
    private static int s_minimumVisibleGlyph = int.MaxValue;
    private static int s_maximumVisibleGlyph = -1;
    private static int s_lastRealizedGlyphItems;
    private static int s_lastGlyphCommandItems;
    private static ulong s_glyphAtlasGenerationAtStart;
    private static ulong s_glyphAtlasEvictionsAtStart;
    private static ulong s_glyphAtlasClearsAtStart;
    private static ulong s_pathAtlasGenerationAtStart;
    private static int s_markdownStateSamples;
    private static int s_markdownStateFailures;
    private static int s_lastMarkdownCharacters;
    private static float s_maximumMarkdownOffset;
    private static bool s_workloadStarted;
    private static bool s_finished;
    private static int s_richTextStateSamples;
    private static int s_richTextStateFailures;
    private static int s_lastRealizedRichParagraphs;
    private static int s_lastVisibleRichCharacters;
    private static readonly bool s_scrollWorkload = ReadOptionalBool(ScrollVariable) == true;
    private static readonly float s_scrollStep = ReadPositiveFloat(ScrollStepVariable, 40f);
    private static readonly string[] s_preconditionPages = ReadPageList(PreconditionPagesVariable);
    private static readonly int s_preconditionFrames = ReadPositiveInt(PreconditionFramesVariable, 180);
    private static readonly bool s_memoryWorkload = ReadOptionalBool(MemoryVariable) == true;
    private static readonly int s_holdMilliseconds = ReadNonNegativeInt(HoldMillisecondsVariable, 0);
    private static int s_preconditionPageIndex;
    private static int s_preconditionFrame;
    private static bool s_isPreconditioning;

    public static string? RequestedPage { get; } = ReadRequestedPage();

    public static void AttachWindow(Microsoft.UI.Xaml.Window window)
    {
        s_window = window;
    }

    public static void StartRequestedWorkload(string selectedPage)
    {
        if (RequestedPage is null)
        {
            return;
        }

        if (ReadOptionalBool(VSyncVariable) is { } vsync && AppState._wgpuContext is { } context)
        {
            context.VSync = vsync;
            if (context.Window != null)
            {
                context.Window.VSync = vsync;
            }
        }

        if (s_preconditionPages.Length != 0)
        {
            s_isPreconditioning = true;
            SelectNavigationPage(s_preconditionPages[0]);
        }

        Console.WriteLine(
            $"[SampleBenchmark] page={selectedPage} warmupFrames={s_warmupFrames}" +
            $" measureFrames={s_measureFrames} vsync={AppState._wgpuContext?.VSync}" +
            $" scroll={s_scrollWorkload} scrollStep={s_scrollStep:F0}" +
            $" preconditionPages={string.Join(';', s_preconditionPages)}" +
            $" preconditionFrames={s_preconditionFrames}");
        SampleBenchmarkEventSource.Log.WorkloadStarted(selectedPage);
    }

    public static void ObserveFrame(double deltaSeconds)
    {
        if (RequestedPage is null || s_finished)
        {
            return;
        }

        if (!s_workloadStarted)
        {
            if (string.Equals(RequestedPage, "LOL/s Benchmark", StringComparison.OrdinalIgnoreCase))
            {
                if (!LolsPage.IsReady)
                {
                    return;
                }

                LolsPage.Start();
            }

            s_workloadStarted = true;
        }

        if (s_isPreconditioning)
        {
            AdvancePageScroll(s_preconditionPages[s_preconditionPageIndex]);
            s_preconditionFrame++;
            if (s_preconditionFrame >= s_preconditionFrames)
            {
                RecordPreconditionState(s_preconditionPages[s_preconditionPageIndex]);
                s_preconditionFrame = 0;
                s_preconditionPageIndex++;
                if (s_preconditionPageIndex < s_preconditionPages.Length)
                {
                    SelectNavigationPage(s_preconditionPages[s_preconditionPageIndex]);
                }
                else
                {
                    s_isPreconditioning = false;
                    SelectNavigationPage(RequestedPage!);
                }
            }
            return;
        }

        if (s_scrollWorkload)
        {
            AdvancePageScroll(RequestedPage);
            if (string.Equals(RequestedPage, "Font Glyph Browser", StringComparison.OrdinalIgnoreCase) &&
                s_frame > s_warmupFrames && s_frame % 60 == 0)
            {
                RecordGlyphBrowserState();
            }
            else if (string.Equals(RequestedPage, "Markdown Playground", StringComparison.OrdinalIgnoreCase) &&
                     s_frame > s_warmupFrames && s_frame % 60 == 0)
            {
                RecordMarkdownState();
            }
            else if (string.Equals(RequestedPage, "Text & Documents", StringComparison.OrdinalIgnoreCase) &&
                     s_frame > s_warmupFrames && s_frame % 60 == 0)
            {
                RecordRichTextState();
            }
        }

        s_frame++;
        if (s_frame <= s_warmupFrames)
        {
            return;
        }

        if (s_frame == s_warmupFrames + 1)
        {
            if (s_memoryWorkload)
            {
                CollectRetainedMemory();
            }

            s_wallClock.Restart();
            s_allocatedBytesAtStart = GC.GetTotalAllocatedBytes(precise: false);
            s_managedHeapBytesAtStart = GC.GetTotalMemory(forceFullCollection: false);
            s_gcPauseDurationAtStart = GC.GetTotalPauseDuration();
            s_gen0CollectionsAtStart = GC.CollectionCount(0);
            s_gen1CollectionsAtStart = GC.CollectionCount(1);
            s_gen2CollectionsAtStart = GC.CollectionCount(2);
            s_lolsRenderedAtStart = LolsPage.TotalRenderedCount;
            s_glyphAtlasGenerationAtStart = AppState._screenCompositor?.Atlas.Generation ?? 0;
            s_glyphAtlasEvictionsAtStart = AppState._screenCompositor?.Atlas.EvictionCount ?? 0;
            s_glyphAtlasClearsAtStart = AppState._screenCompositor?.Atlas.ClearCount ?? 0;
            s_pathAtlasGenerationAtStart = AppState._screenCompositor?.PathAtlas.Generation ?? 0;
            SampleBenchmarkEventSource.Log.MeasurementStarted(RequestedPage!);
        }

        s_deltaSeconds += deltaSeconds;
        if (s_window is { } window)
        {
            var frameMetrics = window.FrameMetrics;
            s_layoutMilliseconds += frameMetrics.LayoutTimeMs;
            s_animationMilliseconds += frameMetrics.AnimationTimeMs;
            s_surfaceAcquireMilliseconds += frameMetrics.SurfaceAcquireTimeMs;
            s_presentMilliseconds += frameMetrics.PresentTimeMs;
        }
        if (AppState._screenCompositor is { } compositor)
        {
            var metrics = compositor.Metrics;
            s_compileMilliseconds += metrics.VisualTreeCompileTimeMs;
            if (metrics.VisualTreeCompileTimeMs > s_maxCompileMilliseconds)
            {
                s_maxCompileMilliseconds = metrics.VisualTreeCompileTimeMs;
                s_maxCompileFrame = s_frame - s_warmupFrames;
                if (string.Equals(RequestedPage, "Inter Typeface", StringComparison.OrdinalIgnoreCase))
                {
                    InterShowcasePage.TryGetBenchmarkScrollState(
                        out s_maxCompileScrollOffset,
                        out s_maxCompileScrollExtent);
                }
            }
            if (metrics.VisualTreeCompileTimeMs > 16.667d)
            {
                s_compileFramesOverBudget++;
            }
            s_uploadMilliseconds += metrics.GpuUploadTimeMs;
            s_renderMilliseconds += metrics.RenderPassTimeMs;
            s_compositorMilliseconds += metrics.FrameTimeMs;
            if (metrics.SceneCacheHit) s_sceneCacheHitFrames++;
        }

        int measuredFrames = s_frame - s_warmupFrames;
        if (measuredFrames < s_measureFrames)
        {
            return;
        }

        s_wallClock.Stop();
        s_finished = true;
        LolsPage.Stop();
        SampleBenchmarkEventSource.Log.MeasurementStopped(RequestedPage!);

        double deltaFps = s_deltaSeconds > 0d ? measuredFrames / s_deltaSeconds : 0d;
        double wallFps = s_wallClock.Elapsed.TotalSeconds > 0d
            ? measuredFrames / s_wallClock.Elapsed.TotalSeconds
            : 0d;
        double divisor = Math.Max(1, measuredFrames);
        long allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - s_allocatedBytesAtStart);
        int gen0Collections = GC.CollectionCount(0) - s_gen0CollectionsAtStart;
        int gen1Collections = GC.CollectionCount(1) - s_gen1CollectionsAtStart;
        int gen2Collections = GC.CollectionCount(2) - s_gen2CollectionsAtStart;
        double gcPauseMilliseconds = (GC.GetTotalPauseDuration() - s_gcPauseDurationAtStart).TotalMilliseconds;
        if (s_memoryWorkload)
        {
            CollectRetainedMemory();
        }

        long managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        var gcMemoryInfo = GC.GetGCMemoryInfo();
        var generationInfo = gcMemoryInfo.GenerationInfo;
        long gen0Bytes = generationInfo.Length > 0 ? generationInfo[0].SizeAfterBytes : 0;
        long gen1Bytes = generationInfo.Length > 1 ? generationInfo[1].SizeAfterBytes : 0;
        long gen2Bytes = generationInfo.Length > 2 ? generationInfo[2].SizeAfterBytes : 0;
        long lohBytes = generationInfo.Length > 3 ? generationInfo[3].SizeAfterBytes : 0;
        long pohBytes = generationInfo.Length > 4 ? generationInfo[4].SizeAfterBytes : 0;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        ProcessMemorySnapshot processMemory = ProcessMemorySnapshot.Capture(process);
        var finalMetrics = AppState._screenCompositor?.Metrics;
        string workloadDetails = string.Empty;
        if (string.Equals(RequestedPage, "Font Glyph Browser", StringComparison.OrdinalIgnoreCase))
        {
            RecordGlyphBrowserState();
            if (s_glyphStateSamples == 0 || s_glyphStateFailures != 0)
            {
                throw new InvalidOperationException(
                    "Font Glyph Browser benchmark did not render populated glyph cells throughout the scroll sweep: " +
                    $"samples={s_glyphStateSamples}, failures={s_glyphStateFailures}, " +
                    $"realized={s_lastRealizedGlyphItems}, commands={s_lastGlyphCommandItems}, " +
                    $"range={s_minimumVisibleGlyph}-{s_maximumVisibleGlyph}.");
            }

            workloadDetails =
                $" glyphStateSamples={s_glyphStateSamples}" +
                $" realizedGlyphItems={s_lastRealizedGlyphItems}" +
                $" glyphCommandItems={s_lastGlyphCommandItems}" +
                $" visibleGlyphRange={s_minimumVisibleGlyph}-{s_maximumVisibleGlyph}" +
                $" glyphAtlasGenerationChanges={(finalMetrics == null ? 0 : AppState._screenCompositor!.Atlas.Generation - s_glyphAtlasGenerationAtStart)}" +
                $" glyphAtlasEvictions={(finalMetrics == null ? 0 : AppState._screenCompositor!.Atlas.EvictionCount - s_glyphAtlasEvictionsAtStart)}" +
                $" glyphAtlasClears={(finalMetrics == null ? 0 : AppState._screenCompositor!.Atlas.ClearCount - s_glyphAtlasClearsAtStart)}" +
                $" pathAtlasResets={(finalMetrics == null ? 0 : AppState._screenCompositor!.PathAtlas.Generation - s_pathAtlasGenerationAtStart)}";
        }
        if (string.Equals(RequestedPage, "LOL/s Benchmark", StringComparison.OrdinalIgnoreCase))
        {
            int renderedLols = Math.Max(0, LolsPage.TotalRenderedCount - s_lolsRenderedAtStart);
            double lolsPerSecond = s_wallClock.Elapsed.TotalSeconds > 0d
                ? renderedLols / s_wallClock.Elapsed.TotalSeconds
                : 0d;
            workloadDetails =
                $" lolsPerSecond={lolsPerSecond:F0}" +
                $" renderedLols={renderedLols}" +
                $" activeElements={LolsPage.ActiveElementCount}/{LolsPage.MaximumElementCount}" +
                $" pendingActions={UIThread.PendingCount}";
        }
        else if (string.Equals(RequestedPage, "Markdown Playground", StringComparison.OrdinalIgnoreCase))
        {
            RecordMarkdownState();
            if (s_markdownStateSamples == 0 || s_markdownStateFailures != 0)
            {
                throw new InvalidOperationException(
                    "Markdown benchmark did not retain populated visible text throughout the scroll sweep.");
            }

            workloadDetails =
                $" markdownStateSamples={s_markdownStateSamples}" +
                $" positionedCharacters={s_lastMarkdownCharacters}" +
                $" maximumScrollOffset={s_maximumMarkdownOffset:F0}";
        }
        else if (string.Equals(RequestedPage, "Data Virtualization", StringComparison.OrdinalIgnoreCase) &&
                 AppState._screenCompositor is { } dataCompositor)
        {
            workloadDetails =
                $" cachedTextLayouts={dataCompositor.CachedTextLayoutCount}" +
                $" cachedTextLayoutGlyphs={dataCompositor.CachedTextLayoutGlyphCount}" +
                $" glyphAtlasEntries={dataCompositor.Atlas.CachedGlyphCount}" +
                $" glyphAtlasEvictionsTotal={dataCompositor.Atlas.EvictionCount}" +
                $" glyphAtlasCapacityExceeded={dataCompositor.Atlas.CapacityExceeded}" +
                $" glyphAtlasGenerationChanges={dataCompositor.Atlas.Generation - s_glyphAtlasGenerationAtStart}" +
                $" glyphAtlasEvictions={dataCompositor.Atlas.EvictionCount - s_glyphAtlasEvictionsAtStart}" +
                $" glyphAtlasClears={dataCompositor.Atlas.ClearCount - s_glyphAtlasClearsAtStart}";
        }
        else if (string.Equals(RequestedPage, "Text & Documents", StringComparison.OrdinalIgnoreCase))
        {
            RecordRichTextState();
            if (s_richTextStateSamples == 0 || s_richTextStateFailures != 0)
                throw new InvalidOperationException("Rich text benchmark lost its virtualized visible document state.");
            workloadDetails =
                $" richTextStateSamples={s_richTextStateSamples}" +
                $" realizedParagraphs={s_lastRealizedRichParagraphs}" +
                $" visibleRichCharacters={s_lastVisibleRichCharacters}";
        }

        Console.WriteLine(
            $"[SampleBenchmark] RESULT page=\"{RequestedPage}\" frames={measuredFrames}" +
            $" deltaFps={deltaFps:F2} wallFps={wallFps:F2}" +
            $" compileMs={s_compileMilliseconds / divisor:F4}" +
            $" maxCompileMs={s_maxCompileMilliseconds:F4}" +
            $" maxCompileFrame={s_maxCompileFrame}" +
            (string.Equals(RequestedPage, "Inter Typeface", StringComparison.OrdinalIgnoreCase)
                ? $" maxCompileScroll={s_maxCompileScrollOffset:F0}/{s_maxCompileScrollExtent:F0}"
                : string.Empty) +
            $" compileFramesOverBudget={s_compileFramesOverBudget}" +
            $" uploadMs={s_uploadMilliseconds / divisor:F4}" +
            $" renderMs={s_renderMilliseconds / divisor:F4}" +
            $" compositorMs={s_compositorMilliseconds / divisor:F4}" +
            $" hostUpdateMs={s_hostUpdateMilliseconds / divisor:F4}" +
            $" maxHostUpdateMs={s_maxHostUpdateMilliseconds:F4}" +
            $" layoutMs={s_layoutMilliseconds / divisor:F4}" +
            $" animationMs={s_animationMilliseconds / divisor:F4}" +
            $" acquireMs={s_surfaceAcquireMilliseconds / divisor:F4}" +
            $" presentMs={s_presentMilliseconds / divisor:F4}" +
            $" allocatedBytesPerFrame={allocatedBytes / divisor:F0}" +
            $" sceneCacheHits={s_sceneCacheHitFrames}/{measuredFrames}" +
            $" sceneCacheMiss=\"{finalMetrics?.SceneCacheMissReason ?? "none"}\"" +
            workloadDetails +
            $" draws={finalMetrics?.DrawCallsCount ?? 0}" +
            $" vectorVertices={finalMetrics?.VectorVerticesCount ?? 0}" +
            $" textVertices={finalMetrics?.TextVerticesCount ?? 0}" +
            $" pathAtlasEntries={finalMetrics?.PathAtlasCachedCount ?? 0}" +
            $" pathAtlasCpuCacheBytes={finalMetrics?.PathAtlasCpuCacheBytes ?? 0}" +
            $" glyphAtlasTextureBytes={finalMetrics?.GlyphAtlasTextureBytes ?? 0}" +
            $" colorGlyphAtlasTextureBytes={finalMetrics?.ColorGlyphAtlasTextureBytes ?? 0}" +
            $" glyphCoverageStagingBytes={finalMetrics?.GlyphCoverageStagingBytes ?? 0}" +
            $" glyphOutlineGpuBytes={finalMetrics?.GlyphOutlineGpuBytes ?? 0}" +
            $" glyphOutlineCompiled={finalMetrics?.GlyphOutlineCompiledCount ?? 0}" +
            $" glyphOutlineCapacity={finalMetrics?.GlyphOutlineRecordCapacity ?? 0}" +
            $" glyphOutlineUploadWrites={finalMetrics?.GlyphOutlineUploadWrites ?? 0}" +
            $" glyphUniformUploadWrites={finalMetrics?.GlyphUniformUploadWrites ?? 0}" +
            $" glyphRasterBatchSubmissions={finalMetrics?.GlyphRasterBatchSubmissions ?? 0}" +
            $" glyphRasterBindGroupCreations={finalMetrics?.GlyphRasterBindGroupCreations ?? 0}" +
            $" glyphRasterComputePasses={finalMetrics?.GlyphRasterComputePasses ?? 0}" +
            $" glyphLastBatchNewGlyphs={finalMetrics?.GlyphLastBatchNewGlyphCount ?? 0}" +
            $" glyphAtlasSize={finalMetrics?.GlyphAtlasSize ?? 0}/{finalMetrics?.GlyphAtlasMaximumSize ?? 0}" +
            $" colorGlyphAtlasSize={finalMetrics?.ColorGlyphAtlasSize ?? 0}/{finalMetrics?.ColorGlyphAtlasMaximumSize ?? 0}" +
            $" pathAtlasTextureBytes={finalMetrics?.PathAtlasTextureBytes ?? 0}" +
            $" pathAtlasSize={finalMetrics?.PathAtlasSize ?? 0}/{finalMetrics?.PathAtlasMaximumSize ?? 0}" +
            $" pathRasterStagingBytes={finalMetrics?.PathRasterStagingBytes ?? 0}" +
            $" pathPeakRasterStagingBytes={finalMetrics?.PathPeakRasterStagingBytes ?? 0}" +
            $" managedHeapStartBytes={s_managedHeapBytesAtStart}" +
            $" managedHeapBytes={managedHeapBytes}" +
            $" managedHeapDeltaBytes={managedHeapBytes - s_managedHeapBytesAtStart}" +
            $" gcHeapSizeBytes={gcMemoryInfo.HeapSizeBytes}" +
            $" gcCommittedBytes={gcMemoryInfo.TotalCommittedBytes}" +
            $" managedFragmentedBytes={gcMemoryInfo.FragmentedBytes}" +
            $" gen0Bytes={gen0Bytes}" +
            $" gen1Bytes={gen1Bytes}" +
            $" gen2Bytes={gen2Bytes}" +
            $" lohBytes={lohBytes}" +
            $" pohBytes={pohBytes}" +
            $" gen0Collections={gen0Collections}" +
            $" gen1Collections={gen1Collections}" +
            $" gen2Collections={gen2Collections}" +
            $" gcPauseMs={gcPauseMilliseconds:F3}" +
            $" pinnedObjects={gcMemoryInfo.PinnedObjectsCount}" +
            $" finalizationPending={gcMemoryInfo.FinalizationPendingCount}" +
            $" processWorkingSetBytes={process.WorkingSet64}" +
            $" processPeakWorkingSetBytes={process.PeakWorkingSet64}" +
            $" processPrivateBytes={process.PrivateMemorySize64}" +
            $" processVirtualBytes={process.VirtualMemorySize64}" +
            $" processResidentBytes={processMemory.ResidentBytes}" +
            $" processWiredBytes={processMemory.WiredBytes}" +
            $" processPhysicalFootprintBytes={processMemory.PhysicalFootprintBytes}" +
            $" processLifetimeMaxPhysicalFootprintBytes={processMemory.LifetimeMaxPhysicalFootprintBytes}" +
            $" pathAtlasFillCacheEntries={AppState._screenCompositor?.PathAtlas.CachedFillPathCount ?? 0}" +
            $" pathAtlasHitTestCacheEntries={AppState._screenCompositor?.PathAtlas.CachedHitTestPathCount ?? 0}");

        if (s_holdMilliseconds > 0)
        {
            SampleBenchmarkEventSource.Log.SnapshotHoldStarted(RequestedPage!);
            Thread.Sleep(s_holdMilliseconds);
        }

        AppState._window?.Close();
    }

    private static void CollectRetainedMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    public static void RecordHostUpdate(TimeSpan elapsed)
    {
        if (RequestedPage is not null && !s_finished && s_frame > s_warmupFrames)
        {
            s_hostUpdateMilliseconds += elapsed.TotalMilliseconds;
            s_maxHostUpdateMilliseconds = Math.Max(s_maxHostUpdateMilliseconds, elapsed.TotalMilliseconds);
        }
    }

    private static void AdvancePageScroll(string? page)
    {
        if (string.Equals(page, "Font Glyph Browser", StringComparison.OrdinalIgnoreCase))
            FontGlyphBrowserPage.AdvanceBenchmarkScroll(s_scrollStep);
        else if (string.Equals(page, "Markdown Playground", StringComparison.OrdinalIgnoreCase))
            MarkdownPage.AdvanceBenchmarkScroll();
        else if (string.Equals(page, "Inter Typeface", StringComparison.OrdinalIgnoreCase))
            InterShowcasePage.AdvanceBenchmarkScroll(s_scrollStep);
        else if (string.Equals(page, "Data Virtualization", StringComparison.OrdinalIgnoreCase))
            DataVirtualizationPage.AdvanceBenchmarkScroll(s_scrollStep);
        else if (string.Equals(page, "Text & Documents", StringComparison.OrdinalIgnoreCase))
            TextDocumentsPage.AdvanceBenchmarkScroll(s_scrollStep);
    }

    private static void SelectNavigationPage(string page)
    {
        var navigationView = AppState._navigationView ??
            throw new InvalidOperationException("Benchmark navigation is not initialized.");
        foreach (var menuItem in navigationView.MenuItems)
        {
            if (string.Equals(menuItem.Text, page, StringComparison.OrdinalIgnoreCase))
            {
                navigationView.SelectedItem = menuItem;
                return;
            }
        }

        throw new InvalidOperationException($"Benchmark navigation page '{page}' was not found.");
    }

    private static void RecordPreconditionState(string page)
    {
        var compositor = AppState._screenCompositor;
        Console.WriteLine(
            $"[SampleBenchmark] PRECONDITION page=\"{page}\" frames={s_preconditionFrames}" +
            $" cachedTextLayouts={compositor?.CachedTextLayoutCount ?? 0}" +
            $" cachedTextLayoutGlyphs={compositor?.CachedTextLayoutGlyphCount ?? 0}" +
            $" glyphAtlasEntries={compositor?.Atlas.CachedGlyphCount ?? 0}" +
            $" glyphAtlasGeneration={compositor?.Atlas.Generation ?? 0}" +
            $" glyphAtlasEvictions={compositor?.Atlas.EvictionCount ?? 0}" +
            $" glyphAtlasCapacityExceeded={compositor?.Atlas.CapacityExceeded ?? false}" +
            $" pathAtlasEntries={compositor?.PathAtlas.CachedPathCount ?? 0}");
    }

    private static void RecordGlyphBrowserState()
    {
        if (!FontGlyphBrowserPage.TryGetBenchmarkGlyphState(
                out int realizedItems,
                out int glyphCommandItems,
                out int minimumGlyphIndex,
                out int maximumGlyphIndex))
        {
            s_glyphStateFailures++;
            return;
        }

        s_glyphStateSamples++;
        s_lastRealizedGlyphItems = realizedItems;
        s_lastGlyphCommandItems = glyphCommandItems;
        s_minimumVisibleGlyph = Math.Min(s_minimumVisibleGlyph, minimumGlyphIndex);
        s_maximumVisibleGlyph = Math.Max(s_maximumVisibleGlyph, maximumGlyphIndex);
        if (glyphCommandItems != realizedItems)
        {
            s_glyphStateFailures++;
        }
    }

    private static void RecordRichTextState()
    {
        if (!TextDocumentsPage.TryGetBenchmarkState(
                out int realizedParagraphs,
                out int visibleCharacters))
        {
            s_richTextStateFailures++;
            return;
        }
        s_richTextStateSamples++;
        s_lastRealizedRichParagraphs = realizedParagraphs;
        s_lastVisibleRichCharacters = visibleCharacters;
    }

    private static void RecordMarkdownState()
    {
        if (!MarkdownPage.TryGetBenchmarkRenderState(
                out int positionedCharacters,
                out float scrollOffset,
                out _))
        {
            s_markdownStateFailures++;
            return;
        }

        s_markdownStateSamples++;
        s_lastMarkdownCharacters = positionedCharacters;
        s_maximumMarkdownOffset = Math.Max(s_maximumMarkdownOffset, scrollOffset);
    }

    private static string? ReadRequestedPage()
    {
        string? value = Environment.GetEnvironmentVariable(PageVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string[] ReadPageList(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int ReadPositiveInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static int ReadNonNegativeInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int parsed) && parsed >= 0 ? parsed : fallback;
    }

    private static bool? ReadOptionalBool(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(value, out bool parsed) ? parsed : null;
    }

    private static float ReadPositiveFloat(string name, float fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return float.TryParse(value, out float parsed) && float.IsFinite(parsed) && parsed > 0f
            ? parsed
            : fallback;
    }
}

internal readonly record struct ProcessMemorySnapshot(
    long ResidentBytes,
    long WiredBytes,
    long PhysicalFootprintBytes,
    long LifetimeMaxPhysicalFootprintBytes)
{
    public static ProcessMemorySnapshot Capture(Process process)
    {
        if (OperatingSystem.IsMacOS() && MacOsProcessMemory.TryCapture(process.Id, out var mac))
            return mac;
        return new ProcessMemorySnapshot(
            process.WorkingSet64,
            0,
            process.WorkingSet64,
            process.PeakWorkingSet64);
    }
}

internal static unsafe class MacOsProcessMemory
{
    private const int RUsageInfoV4Flavor = 4;

    public static bool TryCapture(int processId, out ProcessMemorySnapshot snapshot)
    {
        var usage = default(RUsageInfoV4);
        if (ProcPidRUsage(processId, RUsageInfoV4Flavor, ref usage) == 0)
        {
            snapshot = new ProcessMemorySnapshot(
                checked((long)usage.ResidentSize),
                checked((long)usage.WiredSize),
                checked((long)usage.PhysicalFootprint),
                checked((long)usage.LifetimeMaxPhysicalFootprint));
            return true;
        }
        snapshot = default;
        return false;
    }

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pid_rusage")]
    private static extern int ProcPidRUsage(int processId, int flavor, ref RUsageInfoV4 buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct RUsageInfoV4
    {
        public fixed byte Uuid[16];
        public ulong UserTime;
        public ulong SystemTime;
        public ulong PackageIdleWakeups;
        public ulong InterruptWakeups;
        public ulong PageIns;
        public ulong WiredSize;
        public ulong ResidentSize;
        public ulong PhysicalFootprint;
        public ulong ProcessStartAbsoluteTime;
        public ulong ProcessExitAbsoluteTime;
        public ulong ChildUserTime;
        public ulong ChildSystemTime;
        public ulong ChildPackageIdleWakeups;
        public ulong ChildInterruptWakeups;
        public ulong ChildPageIns;
        public ulong ChildElapsedAbsoluteTime;
        public ulong DiskBytesRead;
        public ulong DiskBytesWritten;
        public ulong CpuTimeDefault;
        public ulong CpuTimeMaintenance;
        public ulong CpuTimeBackground;
        public ulong CpuTimeUtility;
        public ulong CpuTimeLegacy;
        public ulong CpuTimeUserInitiated;
        public ulong CpuTimeUserInteractive;
        public ulong BilledSystemTime;
        public ulong ServicedSystemTime;
        public ulong LogicalWrites;
        public ulong LifetimeMaxPhysicalFootprint;
        public ulong Instructions;
        public ulong Cycles;
        public ulong BilledEnergy;
        public ulong ServicedEnergy;
        public ulong IntervalMaxPhysicalFootprint;
        public ulong RunnableTime;
    }
}

[EventSource(Name = "ProGPU-SampleBenchmark")]
internal sealed class SampleBenchmarkEventSource : EventSource
{
    public static readonly SampleBenchmarkEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void WorkloadStarted(string page) => WriteEvent(1, page);

    [Event(2, Level = EventLevel.Informational)]
    public void MeasurementStarted(string page) => WriteEvent(2, page);

    [Event(3, Level = EventLevel.Informational)]
    public void MeasurementStopped(string page) => WriteEvent(3, page);

    [Event(4, Level = EventLevel.Informational)]
    public void SnapshotHoldStarted(string page) => WriteEvent(4, page);
}
