using ProGPU.Scene;

namespace ProGPU.CAD;

/// <summary>Identifies the ownership boundary of one CAD GPU metrics sample.</summary>
public enum CadGpuMetricsScope : byte
{
    /// <summary>
    /// The values describe the complete compositor frame containing the CAD
    /// plan picture, including any host chrome or transient overlays.
    /// </summary>
    PipelineFrame = 0,
}

/// <summary>
/// A generation-correlated, allocation-free projection of the compositor's
/// current plan-rendering counters.
/// </summary>
/// <remarks>
/// These are renderer-owned logical allocation and frame-work counters, not
/// driver-reported physical residency. Capture is O(1), performs no GPU call,
/// and does not retain the compositor or mutable CAD state.
/// </remarks>
public readonly record struct CadPlanGpuFrameMetrics
{
    public CadGpuMetricsScope Scope { get; init; }
    public ulong ContentGeneration { get; init; }
    public int PlanRecordedCommandCount { get; init; }
    public double FrameTimeMilliseconds { get; init; }
    public double SceneCompileTimeMilliseconds { get; init; }
    public double UploadTimeMilliseconds { get; init; }
    public double RenderPassTimeMilliseconds { get; init; }
    public uint RenderTargetWidth { get; init; }
    public uint RenderTargetHeight { get; init; }
    public float DpiScale { get; init; }
    public int PipelineDrawCallCount { get; init; }
    public int PipelineRecordedCommandCount { get; init; }
    public bool SceneCacheHit { get; init; }
    public bool RenderBundleCacheHit { get; init; }
    public bool RenderBundleRecorded { get; init; }
    public int RenderBundleDrawCallCount { get; init; }
    public int IncrementalPageCount { get; init; }
    public int IncrementalPageHitCount { get; init; }
    public int IncrementalPageMissCount { get; init; }
    public int IncrementalPageCompilationCount { get; init; }
    public int IncrementalUploadPageWriteCount { get; init; }
    public long IncrementalUploadByteCount { get; init; }
    public int SceneUploadBatchCount { get; init; }
    public int SceneUploadCopyCount { get; init; }
    public int VectorVertexCount { get; init; }
    public int VectorIndexCount { get; init; }
    public int TextVertexCount { get; init; }
    public ulong KnownBufferAllocationBytes { get; init; }
    public ulong KnownTextureAllocationBytes { get; init; }
    public ulong LogicalRgbaTargetBytes { get; init; }

    /// <summary>
    /// Captures the current compositor frame and correlates it with one
    /// immutable CAD plan generation and its retained command count.
    /// </summary>
    public static CadPlanGpuFrameMetrics Capture(
        ulong contentGeneration,
        int planRecordedCommandCount,
        in CompositorMetrics metrics)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(planRecordedCommandCount);

        ulong knownBuffers = SaturatingSum(
            metrics.SceneBufferBytes,
            metrics.BrushStorageBufferBytes,
            metrics.TextStyleStorageBufferBytes,
            metrics.GradientStopStorageBufferBytes,
            metrics.EffectParameterBufferBytes);
        ulong knownTextures = SaturatingSum(
            metrics.GlyphAtlasTextureBytes,
            metrics.ColorGlyphAtlasTextureBytes,
            metrics.PathAtlasTextureBytes,
            metrics.MaskTexturePoolBytes,
            metrics.TrackedIntermediateTextureBytes);

        return new CadPlanGpuFrameMetrics
        {
            Scope = CadGpuMetricsScope.PipelineFrame,
            ContentGeneration = contentGeneration,
            PlanRecordedCommandCount = planRecordedCommandCount,
            FrameTimeMilliseconds = metrics.FrameTimeMs,
            SceneCompileTimeMilliseconds = metrics.VisualTreeCompileTimeMs,
            UploadTimeMilliseconds = metrics.GpuUploadTimeMs,
            RenderPassTimeMilliseconds = metrics.RenderPassTimeMs,
            RenderTargetWidth = metrics.RenderTargetWidth,
            RenderTargetHeight = metrics.RenderTargetHeight,
            DpiScale = metrics.DpiScale,
            PipelineDrawCallCount = metrics.DrawCallsCount,
            PipelineRecordedCommandCount = metrics.RecordedCommandCount,
            SceneCacheHit = metrics.SceneCacheHit,
            RenderBundleCacheHit = metrics.RenderBundleCacheHit,
            RenderBundleRecorded = metrics.RenderBundleRecorded,
            RenderBundleDrawCallCount = metrics.RenderBundleDrawCallCount,
            IncrementalPageCount = metrics.IncrementalScenePageCount,
            IncrementalPageHitCount = metrics.IncrementalScenePageHits,
            IncrementalPageMissCount = metrics.IncrementalScenePageMisses,
            IncrementalPageCompilationCount =
                metrics.IncrementalScenePageCompilations,
            IncrementalUploadPageWriteCount =
                metrics.IncrementalSceneUploadPageWrites,
            IncrementalUploadByteCount = metrics.IncrementalSceneUploadBytes,
            SceneUploadBatchCount = metrics.SceneUploadBatchCount,
            SceneUploadCopyCount = metrics.SceneUploadCopyCount,
            VectorVertexCount = metrics.VectorVerticesCount,
            VectorIndexCount = metrics.VectorIndicesCount,
            TextVertexCount = metrics.TextVerticesCount,
            KnownBufferAllocationBytes = knownBuffers,
            KnownTextureAllocationBytes = knownTextures,
            LogicalRgbaTargetBytes = SaturatingProduct(
                metrics.RenderTargetWidth,
                metrics.RenderTargetHeight,
                4UL),
        };
    }

    private static ulong SaturatingSum(
        ulong value0,
        ulong value1,
        ulong value2,
        ulong value3,
        ulong value4)
    {
        ulong result = 0;
        result = SaturatingAdd(result, value0);
        result = SaturatingAdd(result, value1);
        result = SaturatingAdd(result, value2);
        result = SaturatingAdd(result, value3);
        return SaturatingAdd(result, value4);
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static ulong SaturatingProduct(
        uint width,
        uint height,
        ulong bytesPerPixel)
    {
        ulong pixels = (ulong)width * height;
        return pixels != 0 && ulong.MaxValue / pixels < bytesPerPixel
            ? ulong.MaxValue
            : pixels * bytesPerPixel;
    }
}
