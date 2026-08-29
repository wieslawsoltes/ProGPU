using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Direct2D;

internal static unsafe partial class ProGpuDirect2DNative
{
    internal const string LibraryName = "progpu_native_direct2d";
    internal const uint AbiVersion = 2U;
    internal const uint DxgiFormatB8G8R8A8Unorm = 87U;
    internal const uint D2D1AlphaModePremultiplied = 1U;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SurfaceOptions
    {
        internal uint StructSize;
        internal uint Flags;
        internal uint Width;
        internal uint Height;
        internal float DpiX;
        internal float DpiY;
        internal uint AdapterLuidLow;
        internal int AdapterLuidHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SurfaceDescriptor
    {
        internal uint StructSize;
        internal uint Flags;
        internal uint Width;
        internal uint Height;
        internal float DpiX;
        internal float DpiY;
        internal uint DxgiFormat;
        internal uint AlphaMode;
        internal uint AdapterLuidLow;
        internal int AdapterLuidHigh;
        internal nuint SharedNtHandle;
        internal ulong InitialAcquireKey;
        internal ulong InitialReleaseKey;
        internal ulong ContentVersion;
    }

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_get_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAbiVersion();

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreate(
        SurfaceOptions* options,
        nint* surface,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SurfaceDestroy(nint surface);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_get_descriptor")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceGetDescriptor(
        nint surface,
        SurfaceDescriptor* descriptor);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_get_interface")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceGetInterface(
        nint surface,
        ProGpuDirect2DInterfaceKind kind,
        nint* value);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_com_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ComRelease(nint value);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_begin_draw")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceBeginDraw(
        nint surface,
        ulong acquireKey,
        uint timeoutMilliseconds);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_end_draw")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceEndDraw(
        nint surface,
        ulong releaseKey,
        ulong* tag1,
        ulong* tag2,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_get_last_hresult")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SurfaceGetLastHResult(nint surface);
}
