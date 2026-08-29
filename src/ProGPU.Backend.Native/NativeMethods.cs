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
    internal const uint PathFrameStagedSignedWinding = 1U << 2;
    internal const uint ClipChainStagedSignedWinding = 1U;
    internal const uint SceneStreamMagic = 0x31534750U;
    internal const uint SceneStreamVersion = 1U;
    internal const uint SceneStreamEndianMarker = 0x01020304U;
    internal const uint SceneMaximumStackDepth = 64U;
    internal const uint SceneMaximumStreamBytes = 256U * 1024U * 1024U;
    internal const uint SceneMaximumCommands = 1024U * 1024U;
    internal const uint SceneMaximumResources = 256U * 1024U;
    internal const uint SceneMaximumMaterializedLayers = 16U;
    internal const uint SceneMaximumLayerBytes = 256U * 1024U * 1024U;
    internal const uint SceneMaximumBrushes = 1024U * 1024U;
    internal const uint SceneMaximumGradientStops = 64U * 1024U;
    internal const uint SceneMaximumGuidelinesPerAxis = 65535U;
    internal const uint SceneMaximumDrawBrushIndices = 1024U * 1024U;
    internal const uint SceneMaximumTextStyles = 1024U * 1024U;
    internal const uint SceneNoIndex = uint.MaxValue;
    internal const uint SceneMetricsSnapshotReused = 1U;
    internal const ulong EngineGlyphIntrinsicSimdCpuFallback = 1UL;
    internal const ulong EngineGlyphRasterShaderFallback = 1UL << 1;
    internal const ulong EngineGlyphScalarCpuFallback = 1UL << 2;

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
        internal nuint GroupMask;
        internal nuint GroupEffect;
        internal nuint GroupEffectChain;
        internal GpuBlendMode GroupBlendMode;
        internal uint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GroupEffect
    {
        internal uint StructSize;
        internal NativeGroupEffectKind Kind;
        internal uint Flags;
        internal uint Revision;
        internal float SigmaX;
        internal float SigmaY;
        internal uint Reserved;
        internal uint Reserved2;
        internal float OffsetX;
        internal float OffsetY;
        internal float ColorR;
        internal float ColorG;
        internal float ColorB;
        internal float ColorA;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GroupEffectChain
    {
        internal uint StructSize;
        internal uint EffectCount;
        internal uint Revision;
        internal uint Reserved;
        internal GroupEffect* Effects;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GroupMask
    {
        internal uint StructSize;
        internal NativeGroupMaskKind Kind;
        internal uint Flags;
        internal uint Reserved;
        internal nuint ExternalView;
        internal uint Width;
        internal uint Height;
        internal NativeImageSampling Sampling;
        internal NativeMaskTextureFormat TextureFormat;
        internal uint Revision;
        internal uint Reserved2;
        internal NativeImageRect DestinationRect;
        internal NativeImageRect Bounds;
        internal Matrix3x2 Transform;
        internal Vector4 CornerRadiiX;
        internal Vector4 CornerRadiiY;
        internal float Opacity;
        internal uint Reserved3;
        internal ClipChain* ClipChain;
    }

    internal partial struct SceneCommand
    {
        internal NativeImageRect Bounds
        {
            readonly get => new(BoundsX, BoundsY, BoundsWidth, BoundsHeight);
            set
            {
                BoundsX = value.X;
                BoundsY = value.Y;
                BoundsWidth = value.Width;
                BoundsHeight = value.Height;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ClipChain
    {
        internal uint StructSize;
        internal uint Flags;
        internal NativeClipPath* Paths;
        internal nuint PathCount;
        internal NativePathSegment* Segments;
        internal nuint SegmentCount;
        internal NativePathBooleanNode* BooleanNodes;
        internal nuint BooleanNodeCount;
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
        internal NativePathBooleanNode* BooleanNodes;
        internal nuint BooleanNodeCount;
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
        internal float CubicB;
        internal float CubicC;
        internal uint MaxAnisotropy;
        internal uint Reserved3;
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
        internal NativeGroupMaskKind MaskKind;
        internal uint MaskRevision;
        internal uint MaskBindGroupGeneration;
        internal uint MaskBindGroupCacheHit;
        internal ulong MaskUniformUploadBytes;
        internal uint ClipPathCount;
        internal uint ClipRasterizedPathCount;
        internal uint ClipPassCount;
        internal uint ClipCacheHit;
        internal ulong ClipPathUploadBytes;
        internal ulong ClipCoverageStagingBytes;
        internal ulong ClipTextureBytes;
        internal NativeGroupEffectKind EffectKind;
        internal uint EffectRevision;
        internal uint EffectPassCount;
        internal uint EffectCacheHit;
        internal ulong EffectUniformUploadBytes;
        internal ulong EffectTextureBytes;
        internal uint EffectCount;
        internal uint EffectChainRevision;
        internal uint EffectTextureGeneration;
        internal uint EffectAllocationCount;
        internal GpuBlendMode BlendMode;
        internal uint BlendSourcePassCount;
        internal uint BlendPipelineCacheHit;
        internal uint BlendSourceTextureGeneration;
        internal uint BlendSourceAllocationCount;
        internal uint Reserved;
        internal ulong BlendSourceTextureBytes;
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

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_scene_validate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ValidateScene(
        void* stream,
        nuint streamSize,
        SceneMetrics* metrics);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Create(
        EngineOptions* options,
        nint* engine);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_mark_device_lost")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus MarkDeviceLost(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_recreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Recreate(
        nint source,
        EngineOptions* options,
        nint* replacement);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_update_scene")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus UpdateScene(
        nint engine,
        void* stream,
        nuint streamSize,
        SceneMetrics* metrics);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_engine_bind_scene_external_images")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BindSceneExternalImages(
        nint engine,
        SceneExternalImageBinding* bindings,
        nuint bindingCount);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_render_scene")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus RenderScene(
        nint engine,
        SceneFrame* frame,
        SceneFrameMetrics* metrics);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_begin_hit_test")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BeginHitTest(
        nint engine,
        NativeGpuHitTestQuery* query,
        ulong* requestToken);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_engine_poll_hit_test")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus PollHitTest(
        nint engine,
        ulong requestToken,
        NativeGpuHitTestResult* results,
        uint resultCapacity,
        uint* resultCount,
        NativeGpuHitTestResult* summary,
        byte* complete);

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

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_get_shape_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextShapeRequirements(
        NativeTextShapeRequest* request,
        NativeTextShapeRequirements* requirements);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CreateTextContext(
        uint abiVersion,
        byte* fontData,
        nuint fontSize,
        uint faceIndex,
        byte* normalizationData,
        nuint normalizationDataSize,
        nint* context);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyTextContext(nint context);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_context_add_fallback_font")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus AddTextContextFallbackFont(
        nint context,
        byte* fontData,
        nuint fontSize,
        uint faceIndex,
        ulong identity,
        uint* fontIndex);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_context_get_shape_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextContextShapeRequirements(
        nint context,
        NativeTextShapeRequest* request,
        NativeTextShapeRequirements* requirements);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_shape")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ShapeTextContext(
        nint context,
        NativeTextShapeRequest* request,
        NativeTextShapingGlyph* glyphs,
        uint glyphCapacity,
        byte* scratch,
        nuint scratchSize,
        NativeTextShapeResult* result);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_shape")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ShapeText(
        NativeTextShapeRequest* request,
        NativeTextShapingGlyph* glyphs,
        uint glyphCapacity,
        byte* scratch,
        nuint scratchSize,
        NativeTextShapeResult* result);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_layout_get_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextLayoutRequirements(
        NativeTextLayoutRequest* request,
        NativeTextLayoutRequirements* requirements);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_layout")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutText(
        NativeTextLayoutRequest* request,
        NativePositionedTextGlyph* glyphs,
        uint glyphCapacity,
        NativePositionedTextLine* lines,
        uint lineCapacity,
        byte* scratch,
        nuint scratchSize,
        NativeTextLayoutResult* result);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_vertical_layout_get_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextVerticalLayoutRequirements(
        NativeTextLayoutRequest* request,
        NativeTextVerticalLayoutRequirements* requirements);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_vertical_layout")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutTextVertical(
        NativeTextLayoutRequest* request,
        NativePositionedTextGlyph* glyphs,
        uint glyphCapacity,
        NativePositionedTextColumn* columns,
        uint columnCapacity,
        byte* scratch,
        nuint scratchSize,
        NativeTextVerticalLayoutResult* result);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_get_line_break_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextLineBreakRequirements(
        NativeTextScalar* input,
        uint inputCount,
        NativeTextLineBreakRequirements* requirements);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_resolve_line_breaks")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ResolveTextLineBreaks(
        NativeTextScalar* input,
        uint inputCount,
        NativeTextLineBreakKind* breaksAfter,
        uint breakCapacity,
        byte* scratch,
        nuint scratchSize,
        NativeTextLineBreakResult* result);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_get_bidi_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextBidiRequirements(
        NativeTextScalar* input,
        uint inputCount,
        NativeTextBidiRequirements* requirements);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_resolve_bidi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ResolveTextBidi(
        NativeTextScalar* input,
        uint inputCount,
        int requestedParagraphLevel,
        NativeTextBidiLevel* levels,
        uint levelCapacity,
        byte* scratch,
        nuint scratchSize,
        NativeTextBidiResult* result);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_context_get_paragraph_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextContextParagraphRequirements(
        nint context,
        NativeTextShapeRequest* shaping,
        NativeTextLayoutOptions* layout,
        NativeTextParagraphRequirements* requirements);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_text_context_layout_paragraph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutTextContextParagraph(
        nint context,
        NativeTextShapeRequest* shaping,
        NativeTextLayoutOptions* layout,
        NativePositionedTextGlyph* glyphs,
        uint glyphCapacity,
        NativePositionedTextLine* lines,
        uint lineCapacity,
        byte* scratch,
        nuint scratchSize,
        NativeTextParagraphResult* result);
}
