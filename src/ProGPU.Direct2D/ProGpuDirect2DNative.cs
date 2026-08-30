using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Direct2D;

internal static unsafe partial class ProGpuDirect2DNative
{
    internal const string LibraryName = "progpu_native_direct2d";
    internal const uint AbiVersion = 12U;
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeColorF
    {
        internal float Red;
        internal float Green;
        internal float Blue;
        internal float Alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint2F
    {
        internal float X;
        internal float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMatrix3X2F
    {
        internal float M11;
        internal float M12;
        internal float M21;
        internal float M22;
        internal float M31;
        internal float M32;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBrushProperties
    {
        internal float Opacity;
        internal NativeMatrix3X2F Transform;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeLinearGradientBrushProperties
    {
        internal NativePoint2F StartPoint;
        internal NativePoint2F EndPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRadialGradientBrushProperties
    {
        internal NativePoint2F Center;
        internal NativePoint2F GradientOriginOffset;
        internal float RadiusX;
        internal float RadiusY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBitmapProperties
    {
        internal uint Width;
        internal uint Height;
        internal uint Stride;
        internal uint Reserved;
        internal float DpiX;
        internal float DpiY;
    }

    internal enum Win2DResourceKind
    {
        CanvasDevice = 1,
        CanvasRenderTarget = 2
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
        EntryPoint = "progpu_native_direct2d_surface_try_get_win2d_native_resource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceTryGetWin2DNativeResource(
            nint surface,
            Win2DResourceKind resourceKind,
            NativeGuid* interfaceId,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_solid_color_brush")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateSolidColorBrush(
        nint surface,
        NativeColorF* color,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_gradient_stop_collection")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceCreateGradientStopCollection(
            nint surface,
            ProGpuDirect2DGradientStop* stops,
            uint stopCount,
            ProGpuDirect2DColorSpace preInterpolationSpace,
            ProGpuDirect2DColorSpace postInterpolationSpace,
            ProGpuDirect2DBufferPrecision bufferPrecision,
            ProGpuDirect2DExtendMode extendMode,
            ProGpuDirect2DColorInterpolationMode interpolationMode,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_linear_gradient_brush")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceCreateLinearGradientBrush(
            nint surface,
            NativeLinearGradientBrushProperties* properties,
            NativeBrushProperties* brushProperties,
            nint gradientStopCollection,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_radial_gradient_brush")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceCreateRadialGradientBrush(
            nint surface,
            NativeRadialGradientBrushProperties* properties,
            NativeBrushProperties* brushProperties,
            nint gradientStopCollection,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_bitmap")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateBitmap(
        nint surface,
        NativeBitmapProperties* properties,
        byte* pixels,
        ulong pixelByteCount,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_bitmap_brush")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateBitmapBrush(
        nint surface,
        nint bitmap,
        ProGpuDirect2DBitmapBrushProperties* properties,
        NativeBrushProperties* brushProperties,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_image_brush")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateImageBrush(
        nint surface,
        nint image,
        ProGpuDirect2DImageBrushProperties* properties,
        NativeBrushProperties* brushProperties,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_rectangle_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateRectangleGeometry(
        nint surface,
        ProGpuDirect2DRect* rectangle,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_rounded_rectangle_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceCreateRoundedRectangleGeometry(
            nint surface,
            ProGpuDirect2DRect* rectangle,
            float radiusX,
            float radiusY,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_ellipse_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateEllipseGeometry(
        nint surface,
        NativePoint2F* center,
        float radiusX,
        float radiusY,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_path_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreatePathGeometry(
        nint surface,
        ProGpuDirect2DFillMode fillMode,
        ProGpuDirect2DPathFigure* figures,
        uint figureCount,
        ProGpuDirect2DPathSegment* segments,
        uint segmentCount,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_transformed_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceCreateTransformedGeometry(
            nint surface,
            nint geometry,
            NativeMatrix3X2F* transform,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_combine_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCombineGeometry(
        nint surface,
        nint geometryA,
        nint geometryB,
        ProGpuDirect2DCombineMode combineMode,
        NativeMatrix3X2F* geometryBTransform,
        float flatteningTolerance,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_stroke_style")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateStrokeStyle(
        nint surface,
        ProGpuDirect2DStrokeStyleProperties* properties,
        float* dashes,
        uint dashCount,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceTryGetOrCreateWin2DWrapper(
            nint surface,
            nint nativeResource,
            float dpi,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceTryGetWin2DWrapperNativeResource(
            nint surface,
            nint wrapper,
            float dpi,
            NativeGuid* interfaceId,
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
