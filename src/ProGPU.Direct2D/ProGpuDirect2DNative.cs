using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Direct2D;

internal static unsafe partial class ProGpuDirect2DNative
{
    internal const string LibraryName = "progpu_native_direct2d";
    internal const uint AbiVersion = 5U;
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeGuid
    {
        internal uint Data1;
        internal ushort Data2;
        internal ushort Data3;
        internal ulong Data4;

        internal static NativeGuid FromGuid(Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!value.TryWriteBytes(bytes))
            {
                throw new InvalidOperationException(
                    "The interface GUID could not be serialized.");
            }
            return new NativeGuid
            {
                Data1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                Data2 = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]),
                Data3 = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
                Data4 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..])
            };
        }
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
        EntryPoint = "progpu_native_direct2d_com_query_interface")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus ComQueryInterface(
        nint value,
        NativeGuid* interfaceId,
        nint* result,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_try_get_win2d_canvas_device")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceTryGetWin2DCanvasDevice(
            nint surface,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_try_get_win2d_canvas_render_target")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceTryGetWin2DCanvasRenderTarget(
            nint surface,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_acquire")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceAcquire(
        nint surface,
        ulong acquireKey,
        uint timeoutMilliseconds);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceRelease(
        nint surface,
        ulong releaseKey);

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
