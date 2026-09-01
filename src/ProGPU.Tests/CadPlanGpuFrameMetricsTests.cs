using ProGPU.CAD;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class CadPlanGpuFrameMetricsTests
{
    [Fact]
    public void CaptureProjectsPipelineWorkAndLogicalAllocations()
    {
        var source = new CompositorMetrics
        {
            FrameTimeMs = 8.25,
            VisualTreeCompileTimeMs = 1.5,
            GpuUploadTimeMs = 0.75,
            RenderPassTimeMs = 3.5,
            RenderTargetWidth = 1920,
            RenderTargetHeight = 1080,
            DpiScale = 2.0f,
            DrawCallsCount = 17,
            RecordedCommandCount = 43,
            SceneCacheHit = true,
            RenderBundleCacheHit = true,
            RenderBundleDrawCallCount = 11,
            IncrementalScenePageCount = 5,
            IncrementalScenePageHits = 4,
            IncrementalScenePageMisses = 1,
            IncrementalScenePageCompilations = 1,
            IncrementalSceneUploadPageWrites = 2,
            IncrementalSceneUploadBytes = 4096,
            SceneUploadBatchCount = 1,
            SceneUploadCopyCount = 3,
            VectorVerticesCount = 101,
            VectorIndicesCount = 202,
            TextVerticesCount = 303,
            SceneBufferBytes = 10,
            BrushStorageBufferBytes = 20,
            TextStyleStorageBufferBytes = 30,
            GradientStopStorageBufferBytes = 40,
            EffectParameterBufferBytes = 50,
            GlyphAtlasTextureBytes = 60,
            ColorGlyphAtlasTextureBytes = 70,
            PathAtlasTextureBytes = 80,
            MaskTexturePoolBytes = 90,
            TrackedIntermediateTextureBytes = 100,
        };

        CadPlanGpuFrameMetrics result = CadPlanGpuFrameMetrics.Capture(
            contentGeneration: 42,
            planRecordedCommandCount: 37,
            source);

        Assert.Equal(CadGpuMetricsScope.PipelineFrame, result.Scope);
        Assert.Equal(42UL, result.ContentGeneration);
        Assert.Equal(37, result.PlanRecordedCommandCount);
        Assert.Equal(17, result.PipelineDrawCallCount);
        Assert.Equal(43, result.PipelineRecordedCommandCount);
        Assert.True(result.SceneCacheHit);
        Assert.True(result.RenderBundleCacheHit);
        Assert.Equal(4, result.IncrementalPageHitCount);
        Assert.Equal(1, result.IncrementalPageCompilationCount);
        Assert.Equal(4096, result.IncrementalUploadByteCount);
        Assert.Equal(150UL, result.KnownBufferAllocationBytes);
        Assert.Equal(400UL, result.KnownTextureAllocationBytes);
        Assert.Equal(1920UL * 1080UL * 4UL, result.LogicalRgbaTargetBytes);
    }

    [Fact]
    public void CaptureSaturatesAccountingAndAllocatesNothingAfterWarmup()
    {
        var source = new CompositorMetrics
        {
            RenderTargetWidth = uint.MaxValue,
            RenderTargetHeight = uint.MaxValue,
            SceneBufferBytes = ulong.MaxValue,
            BrushStorageBufferBytes = 1,
            GlyphAtlasTextureBytes = ulong.MaxValue,
            PathAtlasTextureBytes = 1,
        };
        _ = CadPlanGpuFrameMetrics.Capture(1, 0, source);

        long before = GC.GetAllocatedBytesForCurrentThread();
        CadPlanGpuFrameMetrics result = default;
        for (int i = 0; i < 1_024; i++)
        {
            result = CadPlanGpuFrameMetrics.Capture(1, 0, source);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(ulong.MaxValue, result.KnownBufferAllocationBytes);
        Assert.Equal(ulong.MaxValue, result.KnownTextureAllocationBytes);
        Assert.Equal(ulong.MaxValue, result.LogicalRgbaTargetBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CadPlanGpuFrameMetrics.Capture(1, -1, source));
    }
}
