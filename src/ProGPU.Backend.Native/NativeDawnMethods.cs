using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

internal static unsafe partial class NativeDawnMethods
{
    internal const string LibraryName = "progpu_native_dawn";
    internal const uint AdapterAbiVersion = 1;
    internal const uint RequiredProviderAbiVersion = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct EngineOptions
    {
        internal uint StructSize;
        internal uint NativeAbiVersion;
        internal uint AdapterAbiVersion;
        internal uint ProviderAbiVersion;
        internal NativeRendererTextureFormat TargetFormat;
        internal uint Reserved;
        internal nint ResolverContext;
        internal nint ResolveProc;
        internal nuint Instance;
        internal nuint Device;
        internal nuint Queue;
        internal ulong Flags;
    }

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_get_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetNativeAbiVersion();

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_get_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte GetInfo(NativeMethods.EngineInfo* info);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_dawn_get_adapter_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAdapterAbiVersion();

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_scene_validate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ValidateScene(
        void* stream,
        nuint streamSize,
        NativeMethods.SceneMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_dawn_engine_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Create(
        EngineOptions* options,
        nint* engine);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_mark_device_lost")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus MarkDeviceLost(nint engine);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_dawn_engine_recreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Recreate(
        nint source,
        EngineOptions* options,
        nint* replacement);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint engine);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_update_scene")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus UpdateScene(
        nint engine,
        void* stream,
        nuint streamSize,
        NativeMethods.SceneMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_render_scene")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderScene(
        nint engine,
        NativeMethods.SceneFrame* frame,
        NativeMethods.SceneFrameMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_render")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Render(
        nint engine,
        NativeMethods.Frame* frame,
        NativeMethods.FrameMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_render_analytic")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderAnalytic(
        nint engine,
        NativeMethods.AnalyticFrame* frame,
        NativeMethods.AnalyticFrameMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_render_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderGeometry(
        nint engine,
        NativeMethods.GeometryFrame* frame,
        NativeMethods.GeometryFrameMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_render_paths")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderPaths(
        nint engine,
        NativeMethods.PathFrame* frame,
        NativeMethods.PathFrameMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_render_glyphs")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderGlyphs(
        nint engine,
        NativeMethods.GlyphFrame* frame,
        NativeMethods.GlyphFrameMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_render_image")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderImage(
        nint engine,
        NativeMethods.ImageFrame* frame,
        NativeMethods.ImageFrameMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_get_last_submission")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetLastSubmission(
        nint engine,
        ulong* submissionIndex);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_get_layer_metrics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetLayerMetrics(
        nint engine,
        NativeMethods.LayerMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_poll_submission")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus PollSubmission(
        nint engine,
        ulong submissionIndex,
        byte wait,
        byte* complete);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_get_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint GetLastError(
        nint engine,
        byte* destination,
        nuint destinationSize);
}
