namespace ProGPU.Backend.Native;

internal enum NativeRendererInteropKind : byte
{
    WgpuNative,
    Dawn
}

/// <summary>
/// Routes the unchanged stable renderer ABI to the selected native module.
/// The branch is fixed for an engine lifetime and performs no allocation.
/// </summary>
internal static unsafe class NativeRendererInterop
{
    internal static uint GetAbiVersion(NativeRendererInteropKind kind) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.GetNativeAbiVersion()
            : NativeMethods.GetAbiVersion();

    internal static byte GetInfo(
        NativeRendererInteropKind kind,
        NativeMethods.EngineInfo* info) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.GetInfo(info)
            : NativeMethods.GetInfo(info);

    internal static NativeRendererStatus ValidateScene(
        NativeRendererInteropKind kind,
        void* stream,
        nuint streamSize,
        NativeMethods.SceneMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.ValidateScene(stream, streamSize, metrics)
            : NativeMethods.ValidateScene(stream, streamSize, metrics);

    internal static NativeRendererStatus MarkDeviceLost(
        NativeRendererInteropKind kind,
        nint engine) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.MarkDeviceLost(engine)
            : NativeMethods.MarkDeviceLost(engine);

    internal static void Destroy(
        NativeRendererInteropKind kind,
        nint engine)
    {
        if (kind == NativeRendererInteropKind.Dawn)
        {
            NativeDawnMethods.Destroy(engine);
        }
        else
        {
            NativeMethods.Destroy(engine);
        }
    }

    internal static NativeRendererStatus UpdateScene(
        NativeRendererInteropKind kind,
        nint engine,
        void* stream,
        nuint streamSize,
        NativeMethods.SceneMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.UpdateScene(
                engine, stream, streamSize, metrics)
            : NativeMethods.UpdateScene(
                engine, stream, streamSize, metrics);

    internal static NativeRendererStatus RenderScene(
        NativeRendererInteropKind kind,
        nint engine,
        NativeMethods.SceneFrame* frame,
        NativeMethods.SceneFrameMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.RenderScene(engine, frame, metrics)
            : NativeMethods.RenderScene(engine, frame, metrics);

    internal static NativeRendererStatus Render(
        NativeRendererInteropKind kind,
        nint engine,
        NativeMethods.Frame* frame,
        NativeMethods.FrameMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.Render(engine, frame, metrics)
            : NativeMethods.Render(engine, frame, metrics);

    internal static NativeRendererStatus RenderAnalytic(
        NativeRendererInteropKind kind,
        nint engine,
        NativeMethods.AnalyticFrame* frame,
        NativeMethods.AnalyticFrameMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.RenderAnalytic(engine, frame, metrics)
            : NativeMethods.RenderAnalytic(engine, frame, metrics);

    internal static NativeRendererStatus RenderGeometry(
        NativeRendererInteropKind kind,
        nint engine,
        NativeMethods.GeometryFrame* frame,
        NativeMethods.GeometryFrameMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.RenderGeometry(engine, frame, metrics)
            : NativeMethods.RenderGeometry(engine, frame, metrics);

    internal static NativeRendererStatus RenderPaths(
        NativeRendererInteropKind kind,
        nint engine,
        NativeMethods.PathFrame* frame,
        NativeMethods.PathFrameMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.RenderPaths(engine, frame, metrics)
            : NativeMethods.RenderPaths(engine, frame, metrics);

    internal static NativeRendererStatus RenderGlyphs(
        NativeRendererInteropKind kind,
        nint engine,
        NativeMethods.GlyphFrame* frame,
        NativeMethods.GlyphFrameMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.RenderGlyphs(engine, frame, metrics)
            : NativeMethods.RenderGlyphs(engine, frame, metrics);

    internal static NativeRendererStatus RenderImage(
        NativeRendererInteropKind kind,
        nint engine,
        NativeMethods.ImageFrame* frame,
        NativeMethods.ImageFrameMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.RenderImage(engine, frame, metrics)
            : NativeMethods.RenderImage(engine, frame, metrics);

    internal static NativeRendererStatus GetLastSubmission(
        NativeRendererInteropKind kind,
        nint engine,
        ulong* submissionIndex) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.GetLastSubmission(engine, submissionIndex)
            : NativeMethods.GetLastSubmission(engine, submissionIndex);

    internal static NativeRendererStatus GetLayerMetrics(
        NativeRendererInteropKind kind,
        nint engine,
        NativeMethods.LayerMetrics* metrics) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.GetLayerMetrics(engine, metrics)
            : NativeMethods.GetLayerMetrics(engine, metrics);

    internal static NativeRendererStatus PollSubmission(
        NativeRendererInteropKind kind,
        nint engine,
        ulong submissionIndex,
        byte wait,
        byte* complete) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.PollSubmission(
                engine, submissionIndex, wait, complete)
            : NativeMethods.PollSubmission(
                engine, submissionIndex, wait, complete);

    internal static nuint GetLastError(
        NativeRendererInteropKind kind,
        nint engine,
        byte* destination,
        nuint destinationSize) =>
        kind == NativeRendererInteropKind.Dawn
            ? NativeDawnMethods.GetLastError(
                engine, destination, destinationSize)
            : NativeMethods.GetLastError(
                engine, destination, destinationSize);
}
