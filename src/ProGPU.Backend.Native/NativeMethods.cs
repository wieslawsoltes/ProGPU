using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

internal static unsafe partial class NativeMethods
{
    internal const string LibraryName = "progpu_native";
    internal const uint AbiVersion = 3;
    internal const uint WgpuNativeMay2024BackendAbi = 1;
    internal const uint DawnWebScene2026JulyBackendAbi = 2;
    internal const uint GeometryFrameCapturePayloadHash = 1U;
    internal const uint GeometryFrameRetainCompiledPayload = 1U << 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawState
    {
        internal uint StructSize;
        internal uint Flags;
        internal float Opacity;
        internal uint Reserved;
        internal NativeImageRect ClipRect;
        internal float GroupOpacity;
        internal uint GroupRevision;
    }

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
        internal DrawState* DrawState;
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
        internal DrawState* DrawState;
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct GeometryFrame
    {
        internal uint StructSize;
        internal uint Width;
        internal uint Height;
        internal float DpiScale;
        internal nuint TargetView;
        internal NativeColor ClearColor;
        internal NativeGeometryPrimitive* Primitives;
        internal nuint PrimitiveCount;
        internal uint Flags;
        internal uint Reserved;
        internal Vector2* Points;
        internal nuint PointCount;
        internal NativePolyline* Polylines;
        internal nuint PolylineCount;
        internal double* Doubles;
        internal nuint DoubleCount;
        internal NativeDashStyle* DashStyles;
        internal nuint DashStyleCount;
        internal NativeSpline* Splines;
        internal nuint SplineCount;
        internal DrawState* DrawState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GeometryFrameMetrics
    {
        internal uint StructSize;
        internal uint DrawCallCount;
        internal uint VertexCount;
        internal uint IndexCount;
        internal ulong VertexUploadBytes;
        internal ulong IndexUploadBytes;
        internal ulong BrushUploadBytes;
        internal ulong UniformUploadBytes;
        internal ulong SubmissionCount;
        internal ulong PayloadHash;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathFrame
    {
        internal uint StructSize;
        internal uint Width;
        internal uint Height;
        internal float DpiScale;
        internal nuint TargetView;
        internal NativeColor ClearColor;
        internal NativePathFill* Paths;
        internal nuint PathCount;
        internal NativePathSegment* Segments;
        internal nuint SegmentCount;
        internal uint Flags;
        internal uint ContentRevision;
        internal DrawState* DrawState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathFrameMetrics
    {
        internal uint StructSize;
        internal uint DrawCallCount;
        internal uint VertexCount;
        internal uint IndexCount;
        internal uint RasterizedPathCount;
        internal uint AtlasWidth;
        internal uint AtlasHeight;
        internal uint AtlasGeneration;
        internal ulong VertexUploadBytes;
        internal ulong IndexUploadBytes;
        internal ulong BrushUploadBytes;
        internal ulong PathUploadBytes;
        internal ulong CoverageStagingBytes;
        internal ulong UniformUploadBytes;
        internal ulong SubmissionCount;
        internal ulong PayloadHash;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GlyphFrame
    {
        internal uint StructSize;
        internal uint Width;
        internal uint Height;
        internal float DpiScale;
        internal nuint TargetView;
        internal NativeColor ClearColor;
        internal NativeGlyphOutline* Outlines;
        internal nuint OutlineCount;
        internal NativePathSegment* Segments;
        internal nuint SegmentCount;
        internal NativePositionedGlyph* Glyphs;
        internal nuint GlyphCount;
        internal uint Flags;
        internal uint ContentRevision;
        internal DrawState* DrawState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GlyphFrameMetrics
    {
        internal uint StructSize;
        internal uint DrawCallCount;
        internal uint GlyphCount;
        internal uint RasterizedGlyphCount;
        internal uint AtlasWidth;
        internal uint AtlasHeight;
        internal uint AtlasGeneration;
        internal uint AtlasGrowthCount;
        internal ulong InstanceUploadBytes;
        internal ulong OutlineUploadBytes;
        internal ulong CoverageStagingBytes;
        internal ulong UniformUploadBytes;
        internal ulong SubmissionCount;
        internal ulong PayloadHash;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ImageFrame
    {
        internal uint StructSize;
        internal uint Width;
        internal uint Height;
        internal float DpiScale;
        internal nuint TargetView;
        internal NativeColor ClearColor;
        internal byte* RgbaPixels;
        internal nuint PixelBytes;
        internal uint ImageWidth;
        internal uint ImageHeight;
        internal uint RowBytes;
        internal NativeImageSampling Sampling;
        internal uint ImageRevision;
        internal uint ContentRevision;
        internal NativeImageRect SourceRect;
        internal NativeImageRect DestinationRect;
        internal Matrix3x2 Transform;
        internal float Opacity;
        internal uint Reserved;
        internal nuint ExternalSourceView;
        internal uint SourceFlags;
        internal uint Reserved2;
        internal nuint ExternalMaskView;
        internal uint MaskWidth;
        internal uint MaskHeight;
        internal NativeImageRect MaskDestinationRect;
        internal uint MaskRevision;
        internal NativeImageSampling MaskSampling;
        internal DrawState* DrawState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ImageFrameMetrics
    {
        internal uint StructSize;
        internal uint DrawCallCount;
        internal uint VertexCount;
        internal uint IndexCount;
        internal uint TextureGeneration;
        internal uint Reserved;
        internal ulong VertexUploadBytes;
        internal ulong IndexUploadBytes;
        internal ulong TextureUploadBytes;
        internal ulong UniformUploadBytes;
        internal ulong SubmissionCount;
        internal ulong PayloadHash;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LayerMetrics
    {
        internal uint StructSize;
        internal uint TextureWidth;
        internal uint TextureHeight;
        internal uint TextureGeneration;
        internal uint AllocationCount;
        internal uint ContentPassCount;
        internal uint CompositePassCount;
        internal uint CacheHit;
        internal ulong TextureBytes;
        internal ulong VertexUploadBytes;
        internal ulong UniformUploadBytes;
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

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_render_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderGeometry(
        nint engine,
        GeometryFrame* frame,
        GeometryFrameMetrics* metrics);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_render_paths")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderPaths(
        nint engine,
        PathFrame* frame,
        PathFrameMetrics* metrics);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_render_glyphs")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderGlyphs(
        nint engine,
        GlyphFrame* frame,
        GlyphFrameMetrics* metrics);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_render_image")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderImage(
        nint engine,
        ImageFrame* frame,
        ImageFrameMetrics* metrics);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_get_last_submission")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetLastSubmission(
        nint engine,
        ulong* submissionIndex);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_get_layer_metrics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetLayerMetrics(
        nint engine,
        LayerMetrics* metrics);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_poll_submission")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus PollSubmission(
        nint engine,
        ulong submissionIndex,
        byte wait,
        byte* complete);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_get_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint GetLastError(
        nint engine,
        byte* destination,
        nuint destinationSize);
}
