using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

internal static unsafe partial class NativeMethods
{
    internal const string LibraryName = "progpu_native";
    internal const uint AbiVersion = 1;
    internal const uint WgpuNativeMay2024BackendAbi = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct EngineOptions
    {
        internal uint StructSize;
        internal uint AbiVersion;
        internal uint BackendAbi;
        internal NativeRendererTextureFormat TargetFormat;
        internal nuint Device;
        internal nuint Queue;
        internal ulong Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeColor
    {
        internal float R;
        internal float G;
        internal float B;
        internal float A;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Frame
    {
        internal uint StructSize;
        internal uint Width;
        internal uint Height;
        internal float DpiScale;
        internal nuint TargetView;
        internal NativeColor ClearColor;
        internal NativeSolidRectangle* Rectangles;
        internal nuint RectangleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameMetrics
    {
        internal uint StructSize;
        internal uint DrawCallCount;
        internal uint VertexCount;
        internal uint Reserved;
        internal ulong VertexUploadBytes;
        internal ulong UniformUploadBytes;
        internal ulong SubmissionCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AnalyticFrame
    {
        internal uint StructSize;
        internal uint Width;
        internal uint Height;
        internal float DpiScale;
        internal nuint TargetView;
        internal NativeColor ClearColor;
        internal NativeAnalyticPrimitive* Primitives;
        internal nuint PrimitiveCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AnalyticFrameMetrics
    {
        internal uint StructSize;
        internal uint DrawCallCount;
        internal uint VertexCount;
        internal uint IndexCount;
        internal ulong VertexUploadBytes;
        internal ulong IndexUploadBytes;
        internal ulong UniformUploadBytes;
        internal ulong SubmissionCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal unsafe struct EngineInfo
    {
        internal uint StructSize;
        internal uint AbiVersion;
        internal uint BackendAbi;
        internal uint Reserved;
        internal ulong Capabilities;
        internal fixed byte Name[64];
    }

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_get_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_get_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte GetInfo(EngineInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Create(
        EngineOptions* options,
        nint* engine);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_render")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Render(
        nint engine,
        Frame* frame,
        FrameMetrics* metrics);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_render_analytic")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderAnalytic(
        nint engine,
        AnalyticFrame* frame,
        AnalyticFrameMetrics* metrics);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_get_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint GetLastError(
        nint engine,
        byte* destination,
        nuint destinationSize);
}
