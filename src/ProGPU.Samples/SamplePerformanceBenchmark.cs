using System;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ProGPU.Backend;

namespace ProGPU.Samples;

internal static class SamplePerformanceBenchmark
{
    private const string PageVariable = "PROGPU_SAMPLE_BENCHMARK_PAGE";
    private const string WarmupFramesVariable = "PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES";
    private const string MeasureFramesVariable = "PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES";
    private const string VSyncVariable = "PROGPU_SAMPLE_BENCHMARK_VSYNC";
    private const string ScrollVariable = "PROGPU_SAMPLE_BENCHMARK_SCROLL";
    private const string ScrollStepVariable = "PROGPU_SAMPLE_BENCHMARK_SCROLL_STEP";
    private const string GpuCompletionVariable = "PROGPU_SAMPLE_BENCHMARK_GPU_COMPLETION";

    private static readonly int s_warmupFrames = ReadPositiveInt(WarmupFramesVariable, 180);
    private static readonly int s_measureFrames = ReadPositiveInt(MeasureFramesVariable, 600);
    private static readonly Stopwatch s_wallClock = new();
    private static int s_frame;
    private static double s_deltaSeconds;
    private static double s_compileMilliseconds;
    private static double s_maxCompileMilliseconds;
    private static int s_compileFramesOverBudget;
    private static double s_uploadMilliseconds;
    private static double s_renderMilliseconds;
    private static double s_compositorMilliseconds;
    private static double s_preRenderMilliseconds;
    private static double s_glyphAtlasMilliseconds;
    private static double s_dynamicBufferUploadMilliseconds;
    private static double s_sceneStateUploadMilliseconds;
    private static double s_pathAtlasMilliseconds;
    private static double s_sceneTransformPatchMilliseconds;
    private static double s_encoderCreationMilliseconds;
    private static double s_maskPassRecordMilliseconds;
    private static double s_primaryPassRecordMilliseconds;
    private static double s_commandFinishMilliseconds;
    private static double s_queueSubmitMilliseconds;
    private static double s_cleanupMilliseconds;
    private static long s_queueWriteCount;
    private static long s_uploadedBytes;
    private static long s_uploadedBufferBytes;
    private static long s_uploadedTextureBytes;
    private static long s_sceneFragmentReuses;
    private static long s_sceneFragmentUpdates;
    private static double s_hostUpdateMilliseconds;
    private static double s_layoutMilliseconds;
    private static double s_animationMilliseconds;
    private static double s_surfaceAcquireMilliseconds;
    private static double s_presentMilliseconds;
    private static Microsoft.UI.Xaml.Window? s_window;
    private static long s_allocatedBytesAtStart;
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
    private static readonly string? s_requestedPage = ReadRequestedPage();
    private static readonly bool s_scrollWorkload = ReadOptionalBool(ScrollVariable) == true;
    private static readonly bool s_scriptedScrollWorkload = s_scrollWorkload && IsScriptedScrollPage(RequestedPage);
    private static readonly bool s_gpuCompletionTracking = ReadOptionalBool(GpuCompletionVariable) != false;
    private static readonly float s_scrollStep = ReadPositiveFloat(ScrollStepVariable, 40f);
    private static readonly double[] s_frameIntervalSamples = new double[s_measureFrames];
    private static readonly double[] s_totalFrameSamples = new double[s_measureFrames];
    private static readonly double[] s_compileSamples = new double[s_measureFrames];
    private static readonly double[] s_compositorSamples = new double[s_measureFrames];
    private static readonly double[] s_surfaceAcquireSamples = new double[s_measureFrames];
    private static readonly double[] s_dynamicBufferUploadSamples = new double[s_measureFrames];
    private static readonly double[] s_primaryPassRecordSamples = new double[s_measureFrames];
    private static readonly double[] s_queueSubmitSamples = new double[s_measureFrames];
    private static readonly double[] s_uploadedByteSamples = new double[s_measureFrames];
    private static GpuFrameCompletionMetrics s_gpuCompletionAtStart;
    private static GpuTimestampMetrics s_gpuTimestampsAtStart;
    private static long s_blockingDeviceWaitsAtStart;
    private static long s_webGpuErrorsAtStart;

    public static string? RequestedPage => s_requestedPage;

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

        if (s_gpuCompletionTracking && AppState._wgpuContext is { } benchmarkContext)
        {
            benchmarkContext.EnableFrameCompletionTracking = true;
            benchmarkContext.EnableGpuTimestampTracking = true;
        }

        s_webGpuErrorsAtStart = AppState._wgpuContext?.UncapturedErrorCount ?? 0;

        Console.WriteLine(
            $"[SampleBenchmark] page={selectedPage} warmupFrames={s_warmupFrames}" +
            $" measureFrames={s_measureFrames} vsync={AppState._wgpuContext?.VSync}" +
            $" workload={(s_scriptedScrollWorkload ? "scroll" : "retained-replay")}" +
            $" scrollStep={s_scrollStep:F0} gpuCompletion={s_gpuCompletionTracking}");
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

        if (s_scriptedScrollWorkload)
        {
            if (string.Equals(RequestedPage, "Font Glyph Browser", StringComparison.OrdinalIgnoreCase))
            {
                FontGlyphBrowserPage.AdvanceBenchmarkScroll(s_scrollStep);
                if (s_frame > s_warmupFrames && s_frame % 60 == 0)
                {
                    RecordGlyphBrowserState();
                }
            }
            else if (string.Equals(RequestedPage, "Markdown Playground", StringComparison.OrdinalIgnoreCase))
            {
                MarkdownPage.AdvanceBenchmarkScroll();
                if (s_frame > s_warmupFrames && s_frame % 60 == 0)
                {
                    RecordMarkdownState();
                }
            }
            else if (string.Equals(RequestedPage, "Inter Typeface", StringComparison.OrdinalIgnoreCase))
            {
                InterShowcasePage.AdvanceBenchmarkScroll(s_scrollStep);
            }
            else if (string.Equals(RequestedPage, "Data Virtualization", StringComparison.OrdinalIgnoreCase))
            {
                DataVirtualizationPage.AdvanceBenchmarkScroll(s_scrollStep);
            }
        }

        s_frame++;
        if (s_frame <= s_warmupFrames)
        {
            return;
        }

        if (s_frame == s_warmupFrames + 1)
        {
            s_wallClock.Restart();
            s_allocatedBytesAtStart = GC.GetTotalAllocatedBytes(precise: false);
            s_lolsRenderedAtStart = LolsPage.TotalRenderedCount;
            s_glyphAtlasGenerationAtStart = AppState._screenCompositor?.Atlas.Generation ?? 0;
            s_glyphAtlasEvictionsAtStart = AppState._screenCompositor?.Atlas.EvictionCount ?? 0;
            s_glyphAtlasClearsAtStart = AppState._screenCompositor?.Atlas.ClearCount ?? 0;
            s_pathAtlasGenerationAtStart = AppState._screenCompositor?.PathAtlas.Generation ?? 0;
            s_gpuCompletionAtStart = AppState._wgpuContext?.FrameCompletionMetrics ?? default;
            s_gpuTimestampsAtStart = AppState._wgpuContext?.GpuTimestampMetrics ?? default;
            s_blockingDeviceWaitsAtStart = AppState._wgpuContext?.BlockingDeviceWaitCount ?? 0;
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
            s_maxCompileMilliseconds = Math.Max(s_maxCompileMilliseconds, metrics.VisualTreeCompileTimeMs);
            if (metrics.VisualTreeCompileTimeMs > 16.667d)
            {
                s_compileFramesOverBudget++;
            }
            s_uploadMilliseconds += metrics.GpuUploadTimeMs;
            s_renderMilliseconds += metrics.RenderPassTimeMs;
            s_compositorMilliseconds += metrics.FrameTimeMs;
            s_preRenderMilliseconds += metrics.PreRenderTimeMs;
            s_glyphAtlasMilliseconds += metrics.GlyphAtlasTimeMs;
            s_dynamicBufferUploadMilliseconds += metrics.DynamicBufferUploadTimeMs;
            s_sceneStateUploadMilliseconds += metrics.SceneStateUploadTimeMs;
            s_pathAtlasMilliseconds += metrics.PathAtlasTimeMs;
            s_sceneTransformPatchMilliseconds += metrics.SceneTransformPatchTimeMs;
            s_encoderCreationMilliseconds += metrics.EncoderCreationTimeMs;
            s_maskPassRecordMilliseconds += metrics.MaskPassRecordTimeMs;
            s_primaryPassRecordMilliseconds += metrics.PrimaryPassRecordTimeMs;
            s_commandFinishMilliseconds += metrics.CommandFinishTimeMs;
            s_queueSubmitMilliseconds += metrics.QueueSubmitTimeMs;
            s_cleanupMilliseconds += metrics.CleanupTimeMs;
            s_queueWriteCount += metrics.QueueWriteCount;
            s_uploadedBytes += metrics.UploadedBytes;
            s_uploadedBufferBytes += metrics.UploadedBufferBytes;
            s_uploadedTextureBytes += metrics.UploadedTextureBytes;
            s_sceneFragmentReuses += metrics.SceneFragmentReuseCount;
            s_sceneFragmentUpdates += metrics.SceneFragmentUpdateCount;
            if (metrics.SceneCacheHit) s_sceneCacheHitFrames++;
        }

        int sampleIndex = s_frame - s_warmupFrames - 1;
        if ((uint)sampleIndex < (uint)s_measureFrames)
        {
            s_frameIntervalSamples[sampleIndex] = deltaSeconds * 1000d;
            if (s_window is { } measuredWindow)
            {
                s_totalFrameSamples[sampleIndex] = measuredWindow.FrameMetrics.TotalTimeMs;
                s_surfaceAcquireSamples[sampleIndex] = measuredWindow.FrameMetrics.SurfaceAcquireTimeMs;
            }
            if (AppState._screenCompositor is { } measuredCompositor)
            {
                s_compileSamples[sampleIndex] = measuredCompositor.Metrics.VisualTreeCompileTimeMs;
                s_compositorSamples[sampleIndex] = measuredCompositor.Metrics.FrameTimeMs;
                s_dynamicBufferUploadSamples[sampleIndex] = measuredCompositor.Metrics.DynamicBufferUploadTimeMs;
                s_primaryPassRecordSamples[sampleIndex] = measuredCompositor.Metrics.PrimaryPassRecordTimeMs;
                s_queueSubmitSamples[sampleIndex] = measuredCompositor.Metrics.QueueSubmitTimeMs;
                s_uploadedByteSamples[sampleIndex] = measuredCompositor.Metrics.UploadedBytes;
            }
        }

        int measuredFrames = s_frame - s_warmupFrames;
        if (measuredFrames < s_measureFrames)
        {
            return;
        }

        s_wallClock.Stop();
        s_finished = true;
        LolsPage.Stop();

        double deltaFps = s_deltaSeconds > 0d ? measuredFrames / s_deltaSeconds : 0d;
        double wallFps = s_wallClock.Elapsed.TotalSeconds > 0d
            ? measuredFrames / s_wallClock.Elapsed.TotalSeconds
            : 0d;
        double divisor = Math.Max(1, measuredFrames);
        long allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - s_allocatedBytesAtStart);
        var finalMetrics = AppState._screenCompositor?.Metrics;
        var finalFrameMetrics = s_window?.FrameMetrics ?? default;
        var finalGpuCompletion = AppState._wgpuContext?.FrameCompletionMetrics ?? default;
        AppState._wgpuContext?.PollDevice(wait: false);
        var finalGpuTimestamps = AppState._wgpuContext?.GpuTimestampMetrics ?? default;
        long submittedGpuFrames = Math.Max(0, finalGpuCompletion.SubmittedFrames - s_gpuCompletionAtStart.SubmittedFrames);
        long completedGpuFrames = Math.Max(0, finalGpuCompletion.CompletedFrames - s_gpuCompletionAtStart.CompletedFrames);
        long failedGpuFrames = Math.Max(0, finalGpuCompletion.FailedFrames - s_gpuCompletionAtStart.FailedFrames);
        double completedGpuFps = s_wallClock.Elapsed.TotalSeconds > 0d
            ? completedGpuFrames / s_wallClock.Elapsed.TotalSeconds
            : 0d;
        long timestampedGpuFrames = Math.Max(0, finalGpuTimestamps.CompletedSamples - s_gpuTimestampsAtStart.CompletedSamples);
        long failedGpuTimestamps = Math.Max(0, finalGpuTimestamps.FailedSamples - s_gpuTimestampsAtStart.FailedSamples);
        long droppedGpuTimestamps = Math.Max(0, finalGpuTimestamps.DroppedSamples - s_gpuTimestampsAtStart.DroppedSamples);
        long blockingDeviceWaits = Math.Max(
            0,
            (AppState._wgpuContext?.BlockingDeviceWaitCount ?? 0) - s_blockingDeviceWaitsAtStart);
        long webGpuErrors = Math.Max(
            0,
            (AppState._wgpuContext?.UncapturedErrorCount ?? 0) - s_webGpuErrorsAtStart);
        if (webGpuErrors != 0)
        {
            throw new InvalidOperationException(
                $"Performance benchmark observed {webGpuErrors} uncaptured WebGPU error(s); " +
                "the run is invalid. Inspect the benchmark log for the first device error.");
        }
        double intervalP50 = Percentile(s_frameIntervalSamples, measuredFrames, 0.50d);
        double intervalP95 = Percentile(s_frameIntervalSamples, measuredFrames, 0.95d);
        double intervalP99 = Percentile(s_frameIntervalSamples, measuredFrames, 0.99d);
        double intervalMax = Maximum(s_frameIntervalSamples, measuredFrames);
        double totalFrameP95 = Percentile(s_totalFrameSamples, measuredFrames, 0.95d);
        double totalFrameMax = Maximum(s_totalFrameSamples, measuredFrames);
        double compileP95 = Percentile(s_compileSamples, measuredFrames, 0.95d);
        double compileP99 = Percentile(s_compileSamples, measuredFrames, 0.99d);
        double compileMax = Maximum(s_compileSamples, measuredFrames);
        double compositorP95 = Percentile(s_compositorSamples, measuredFrames, 0.95d);
        double compositorMax = Maximum(s_compositorSamples, measuredFrames);
        double acquireP95 = Percentile(s_surfaceAcquireSamples, measuredFrames, 0.95d);
        double acquireMax = Maximum(s_surfaceAcquireSamples, measuredFrames);
        double dynamicUploadP95 = Percentile(s_dynamicBufferUploadSamples, measuredFrames, 0.95d);
        double primaryRecordP95 = Percentile(s_primaryPassRecordSamples, measuredFrames, 0.95d);
        double queueSubmitP95 = Percentile(s_queueSubmitSamples, measuredFrames, 0.95d);
        double uploadedBytesP95 = Percentile(s_uploadedByteSamples, measuredFrames, 0.95d);
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

        Console.WriteLine(
            $"[SampleBenchmark] RESULT page=\"{RequestedPage}\" frames={measuredFrames}" +
            $" deltaFps={deltaFps:F2} wallFps={wallFps:F2}" +
            $" frameIntervalP50Ms={intervalP50:F4}" +
            $" frameIntervalP95Ms={intervalP95:F4}" +
            $" frameIntervalP99Ms={intervalP99:F4}" +
            $" frameIntervalMaxMs={intervalMax:F4}" +
            $" totalFrameP95Ms={totalFrameP95:F4}" +
            $" totalFrameMaxMs={totalFrameMax:F4}" +
            $" compileMs={s_compileMilliseconds / divisor:F4}" +
            $" compileP95Ms={compileP95:F4}" +
            $" compileP99Ms={compileP99:F4}" +
            $" maxCompileMs={compileMax:F4}" +
            $" compileFramesOverBudget={s_compileFramesOverBudget}" +
            $" uploadMs={s_uploadMilliseconds / divisor:F4}" +
            $" renderMs={s_renderMilliseconds / divisor:F4}" +
            $" compositorMs={s_compositorMilliseconds / divisor:F4}" +
            $" compositorP95Ms={compositorP95:F4}" +
            $" compositorMaxMs={compositorMax:F4}" +
            $" preRenderMs={s_preRenderMilliseconds / divisor:F4}" +
            $" glyphAtlasMs={s_glyphAtlasMilliseconds / divisor:F4}" +
            $" dynamicUploadMs={s_dynamicBufferUploadMilliseconds / divisor:F4}" +
            $" dynamicUploadP95Ms={dynamicUploadP95:F4}" +
            $" sceneStateUploadMs={s_sceneStateUploadMilliseconds / divisor:F4}" +
            $" pathAtlasMs={s_pathAtlasMilliseconds / divisor:F4}" +
            $" sceneTransformPatchMs={s_sceneTransformPatchMilliseconds / divisor:F4}" +
            $" encoderCreateMs={s_encoderCreationMilliseconds / divisor:F4}" +
            $" maskRecordMs={s_maskPassRecordMilliseconds / divisor:F4}" +
            $" primaryRecordMs={s_primaryPassRecordMilliseconds / divisor:F4}" +
            $" primaryRecordP95Ms={primaryRecordP95:F4}" +
            $" commandFinishMs={s_commandFinishMilliseconds / divisor:F4}" +
            $" queueSubmitMs={s_queueSubmitMilliseconds / divisor:F4}" +
            $" queueSubmitP95Ms={queueSubmitP95:F4}" +
            $" cleanupMs={s_cleanupMilliseconds / divisor:F4}" +
            $" blockingDeviceWaits={blockingDeviceWaits}" +
            $" queueWritesPerFrame={s_queueWriteCount / divisor:F2}" +
            $" uploadedBytesPerFrame={s_uploadedBytes / divisor:F0}" +
            $" uploadedBytesP95={uploadedBytesP95:F0}" +
            $" sceneFragmentReusesPerFrame={s_sceneFragmentReuses / divisor:F2}" +
            $" sceneFragmentUpdatesPerFrame={s_sceneFragmentUpdates / divisor:F2}" +
            $" hostUpdateMs={s_hostUpdateMilliseconds / divisor:F4}" +
            $" layoutMs={s_layoutMilliseconds / divisor:F4}" +
            $" animationMs={s_animationMilliseconds / divisor:F4}" +
            $" acquireMs={s_surfaceAcquireMilliseconds / divisor:F4}" +
            $" acquireP95Ms={acquireP95:F4}" +
            $" acquireMaxMs={acquireMax:F4}" +
            $" presentMs={s_presentMilliseconds / divisor:F4}" +
            $" gpuSubmittedFrames={submittedGpuFrames}" +
            $" gpuCompletedFrames={completedGpuFrames}" +
            $" gpuCompletedFps={completedGpuFps:F2}" +
            $" gpuFailedFrames={failedGpuFrames}" +
            $" gpuInFlightFrames={finalGpuCompletion.InFlightFrames}" +
            $" gpuMaxInFlightFrames={finalGpuCompletion.MaxInFlightFrames}" +
            $" gpuTimestampSupported={AppState._wgpuContext?.SupportsTimestampQueries == true}" +
            $" gpuTimestampedFrames={timestampedGpuFrames}" +
            $" gpuFrameAverageMs={finalGpuTimestamps.AverageFrameMilliseconds:F4}" +
            $" gpuFrameMaxMs={finalGpuTimestamps.MaximumFrameMilliseconds:F4}" +
            $" gpuTimestampFailures={failedGpuTimestamps}" +
            $" gpuTimestampDrops={droppedGpuTimestamps}" +
            $" allocatedBytesPerFrame={allocatedBytes / divisor:F0}" +
            $" sceneCacheHits={s_sceneCacheHitFrames}/{measuredFrames}" +
            $" sceneCacheMiss=\"{finalMetrics?.SceneCacheMissReason ?? "none"}\"" +
            workloadDetails +
            $" draws={finalMetrics?.DrawCallsCount ?? 0}" +
            $" vectorVertices={finalMetrics?.VectorVerticesCount ?? 0}" +
            $" textVertices={finalMetrics?.TextVerticesCount ?? 0}");

        WriteJsonResult(
            measuredFrames,
            deltaFps,
            wallFps,
            intervalP50,
            intervalP95,
            intervalP99,
            intervalMax,
            totalFrameP95,
            totalFrameMax,
            compileP95,
            compileP99,
            compileMax,
            compositorP95,
            compositorMax,
            acquireP95,
            acquireMax,
            allocatedBytes / divisor,
            submittedGpuFrames,
            completedGpuFrames,
            completedGpuFps,
            failedGpuFrames,
            finalGpuCompletion,
            timestampedGpuFrames,
            failedGpuTimestamps,
            droppedGpuTimestamps,
            finalGpuTimestamps,
            blockingDeviceWaits,
            finalFrameMetrics,
            finalMetrics);

        AppState._window?.Close();
    }

    public static void RecordHostUpdate(TimeSpan elapsed)
    {
        if (RequestedPage is not null && !s_finished && s_frame > s_warmupFrames)
        {
            s_hostUpdateMilliseconds += elapsed.TotalMilliseconds;
        }
    }

    private static double Percentile(double[] samples, int count, double percentile)
    {
        count = Math.Clamp(count, 0, samples.Length);
        if (count == 0)
        {
            return 0d;
        }

        var sorted = new double[count];
        Array.Copy(samples, sorted, count);
        Array.Sort(sorted);
        double rank = Math.Clamp(percentile, 0d, 1d) * (count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        double fraction = rank - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static double Maximum(double[] samples, int count)
    {
        count = Math.Clamp(count, 0, samples.Length);
        double maximum = 0d;
        for (int index = 0; index < count; index++)
        {
            maximum = Math.Max(maximum, samples[index]);
        }

        return maximum;
    }

    private static void WriteJsonResult(
        int measuredFrames,
        double deltaFps,
        double wallFps,
        double intervalP50,
        double intervalP95,
        double intervalP99,
        double intervalMax,
        double totalFrameP95,
        double totalFrameMax,
        double compileP95,
        double compileP99,
        double compileMax,
        double compositorP95,
        double compositorMax,
        double acquireP95,
        double acquireMax,
        double allocatedBytesPerFrame,
        long submittedGpuFrames,
        long completedGpuFrames,
        double completedGpuFps,
        long failedGpuFrames,
        GpuFrameCompletionMetrics gpuCompletion,
        long timestampedGpuFrames,
        long failedGpuTimestamps,
        long droppedGpuTimestamps,
        GpuTimestampMetrics gpuTimestamps,
        long blockingDeviceWaits,
        Microsoft.UI.Xaml.WindowFrameMetrics frameMetrics,
        ProGPU.Scene.CompositorMetrics? compositorMetrics)
    {
        var output = new ArrayBufferWriter<byte>(2048);
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 3);
            writer.WriteString("page", RequestedPage);
            writer.WriteString("platform", OperatingSystem.IsBrowser() ? "browser" : "desktop");
            writer.WriteString("backend", AppState._wgpuContext?.BackendKind.ToString());
            writer.WriteString("colorFormat", AppState._wgpuContext?.SwapChainFormat.ToString());
            writer.WriteString("workload", s_scriptedScrollWorkload ? "scroll" : "retained-replay");
            writer.WriteBoolean("vsync", AppState._wgpuContext?.VSync ?? false);
            writer.WriteBoolean("scroll", s_scriptedScrollWorkload);
            writer.WriteBoolean("gpuCompletionTracking", s_gpuCompletionTracking);
            writer.WriteNumber("scrollStep", s_scrollStep);
            writer.WriteNumber("warmupFrames", s_warmupFrames);
            writer.WriteNumber("measuredFrames", measuredFrames);
            writer.WriteNumber("renderTargetWidth", frameMetrics.RenderTargetWidth);
            writer.WriteNumber("renderTargetHeight", frameMetrics.RenderTargetHeight);
            writer.WriteNumber("dpiScale", frameMetrics.DpiScale);
            writer.WriteNumber("deltaFps", deltaFps);
            writer.WriteNumber("wallFps", wallFps);
            writer.WriteNumber("cpuSubmittedFps", wallFps);
            writer.WriteNumber("frameIntervalP50Ms", intervalP50);
            writer.WriteNumber("frameIntervalP95Ms", intervalP95);
            writer.WriteNumber("frameIntervalP99Ms", intervalP99);
            writer.WriteNumber("frameIntervalMaxMs", intervalMax);
            writer.WriteNumber("totalFrameP95Ms", totalFrameP95);
            writer.WriteNumber("totalFrameMaxMs", totalFrameMax);
            writer.WriteNumber("compileAverageMs", s_compileMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("compileP95Ms", compileP95);
            writer.WriteNumber("compileP99Ms", compileP99);
            writer.WriteNumber("compileMaxMs", compileMax);
            writer.WriteNumber("compositorAverageMs", s_compositorMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("compositorP95Ms", compositorP95);
            writer.WriteNumber("compositorMaxMs", compositorMax);
            writer.WriteNumber("preRenderAverageMs", s_preRenderMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("glyphAtlasAverageMs", s_glyphAtlasMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("dynamicBufferUploadAverageMs", s_dynamicBufferUploadMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("dynamicBufferUploadP95Ms", Percentile(s_dynamicBufferUploadSamples, measuredFrames, 0.95d));
            writer.WriteNumber("sceneStateUploadAverageMs", s_sceneStateUploadMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("pathAtlasAverageMs", s_pathAtlasMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("sceneTransformPatchAverageMs", s_sceneTransformPatchMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("encoderCreationAverageMs", s_encoderCreationMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("maskPassRecordAverageMs", s_maskPassRecordMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("primaryPassRecordAverageMs", s_primaryPassRecordMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("primaryPassRecordP95Ms", Percentile(s_primaryPassRecordSamples, measuredFrames, 0.95d));
            writer.WriteNumber("commandFinishAverageMs", s_commandFinishMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("queueSubmitAverageMs", s_queueSubmitMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("queueSubmitP95Ms", Percentile(s_queueSubmitSamples, measuredFrames, 0.95d));
            writer.WriteNumber("cleanupAverageMs", s_cleanupMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("blockingDeviceWaits", blockingDeviceWaits);
            writer.WriteNumber("queueWritesPerFrame", (double)s_queueWriteCount / Math.Max(1, measuredFrames));
            writer.WriteNumber("uploadedBytesPerFrame", (double)s_uploadedBytes / Math.Max(1, measuredFrames));
            writer.WriteNumber("uploadedBytesP95", Percentile(s_uploadedByteSamples, measuredFrames, 0.95d));
            writer.WriteNumber("uploadedBufferBytesPerFrame", (double)s_uploadedBufferBytes / Math.Max(1, measuredFrames));
            writer.WriteNumber("uploadedTextureBytesPerFrame", (double)s_uploadedTextureBytes / Math.Max(1, measuredFrames));
            writer.WriteNumber("sceneFragmentReusesPerFrame", (double)s_sceneFragmentReuses / Math.Max(1, measuredFrames));
            writer.WriteNumber("sceneFragmentUpdatesPerFrame", (double)s_sceneFragmentUpdates / Math.Max(1, measuredFrames));
            writer.WriteNumber("surfaceAcquireAverageMs", s_surfaceAcquireMilliseconds / Math.Max(1, measuredFrames));
            writer.WriteNumber("surfaceAcquireP95Ms", acquireP95);
            writer.WriteNumber("surfaceAcquireMaxMs", acquireMax);
            writer.WriteNumber("allocatedBytesPerFrame", allocatedBytesPerFrame);
            writer.WriteNumber("gpuSubmittedFrames", submittedGpuFrames);
            writer.WriteNumber("gpuCompletedFrames", completedGpuFrames);
            writer.WriteNumber("gpuCompletedFps", completedGpuFps);
            writer.WriteNumber("gpuFailedFrames", failedGpuFrames);
            writer.WriteNumber("gpuInFlightFrames", gpuCompletion.InFlightFrames);
            writer.WriteNumber("gpuMaxInFlightFrames", gpuCompletion.MaxInFlightFrames);
            writer.WriteBoolean("gpuTimestampSupported", AppState._wgpuContext?.SupportsTimestampQueries == true);
            writer.WriteNumber("gpuTimestampedFrames", timestampedGpuFrames);
            writer.WriteNumber("gpuFrameAverageMs", gpuTimestamps.AverageFrameMilliseconds);
            writer.WriteNumber("gpuFrameMaximumMs", gpuTimestamps.MaximumFrameMilliseconds);
            writer.WriteNumber("gpuTimestampFailures", failedGpuTimestamps);
            writer.WriteNumber("gpuTimestampDrops", droppedGpuTimestamps);
            writer.WriteNumber("sceneCacheHits", s_sceneCacheHitFrames);
            writer.WriteString("sceneCacheMiss", compositorMetrics?.SceneCacheMissReason ?? "none");
            writer.WriteNumber("draws", compositorMetrics?.DrawCallsCount ?? 0);
            writer.WriteNumber("vectorVertices", compositorMetrics?.VectorVerticesCount ?? 0);
            writer.WriteNumber("textVertices", compositorMetrics?.TextVerticesCount ?? 0);
            writer.WriteEndObject();
        }

        Console.WriteLine($"[SampleBenchmark] JSON {Encoding.UTF8.GetString(output.WrittenSpan)}");
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

    private static bool IsScriptedScrollPage(string? page) => page is not null &&
        (string.Equals(page, "Font Glyph Browser", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(page, "Markdown Playground", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(page, "Inter Typeface", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(page, "Data Virtualization", StringComparison.OrdinalIgnoreCase));

    private static int ReadPositiveInt(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
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
