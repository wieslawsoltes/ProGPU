using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Direct2D;

internal static unsafe partial class ProGpuDirect2DNative
{
    internal const string LibraryName = "progpu_native_direct2d";
    internal const uint AbiVersion = 25U;
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
    internal struct DeviceLossState
    {
        internal uint StructSize;
        internal uint Flags;
        internal int ReasonHResult;
        internal uint Reserved;
        internal ulong ResourceGeneration;
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
    internal struct NativeSizeF
    {
        internal float Width;
        internal float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeLayerParameters
    {
        internal ProGpuDirect2DRect ContentBounds;
        internal ProGpuDirect2DAntialiasMode MaskAntialiasMode;
        internal NativeMatrix3X2F MaskTransform;
        internal float Opacity;
        internal ProGpuDirect2DLayerOptions Options;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeTextFormatProperties
    {
        internal uint StructSize;
        internal uint FontWeight;
        internal ProGpuDirect2DFontStyle FontStyle;
        internal ProGpuDirect2DFontStretch FontStretch;
        internal float FontSize;
        internal ProGpuDirect2DTextAlignment TextAlignment;
        internal ProGpuDirect2DParagraphAlignment ParagraphAlignment;
        internal ProGpuDirect2DWordWrapping WordWrapping;
        internal ProGpuDirect2DReadingDirection ReadingDirection;
        internal ProGpuDirect2DFlowDirection FlowDirection;
        internal float IncrementalTabStop;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeTextRangeFormat
    {
        internal uint StructSize;
        internal ProGpuDirect2DTextRangeFormatFlags Flags;
        internal uint RangeStart;
        internal uint RangeLength;
        internal uint FontWeight;
        internal ProGpuDirect2DFontStyle FontStyle;
        internal ProGpuDirect2DFontStretch FontStretch;
        internal float FontSize;
        internal uint Underline;
        internal uint Strikethrough;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFontFaceProperties
    {
        internal uint StructSize;
        internal uint FontWeight;
        internal ProGpuDirect2DFontStyle FontStyle;
        internal ProGpuDirect2DFontStretch FontStretch;
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
        EntryPoint = "progpu_native_direct2d_surface_get_device_loss_state")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceGetDeviceLossState(
        nint surface,
        DeviceLossState* state);

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
        EntryPoint = "progpu_native_direct2d_surface_create_command_list")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateCommandList(
        nint surface,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_effect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateEffect(
        nint surface,
        NativeGuid* effectId,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_effect_set_input")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus EffectSetInput(
        nint surface,
        nint effect,
        uint inputIndex,
        nint image,
        uint invalidate,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_effect_set_input_effect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus EffectSetInputEffect(
        nint surface,
        nint effect,
        uint inputIndex,
        nint inputEffect,
        uint invalidate,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_effect_set_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus EffectSetValue(
        nint surface,
        nint effect,
        uint propertyIndex,
        ProGpuDirect2DEffectPropertyType propertyType,
        void* data,
        uint dataSize,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_effect_get_output")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus EffectGetOutput(
        nint surface,
        nint effect,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_layer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateLayer(
        nint surface,
        NativeSizeF* size,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_drawing_state_block")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceCreateDrawingStateBlock(
            nint surface,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_save_drawing_state")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceSaveDrawingState(
        nint surface,
        nint drawingStateBlock,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_restore_drawing_state")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceRestoreDrawingState(
        nint surface,
        nint drawingStateBlock,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_push_layer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfacePushLayer(
        nint surface,
        NativeLayerParameters* parameters,
        nint geometricMask,
        nint opacityBrush,
        nint layer,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_pop_layer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfacePopLayer(
        nint surface,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_text_format")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateTextFormat(
        nint surface,
        char* fontFamily,
        uint fontFamilyLength,
        char* localeName,
        uint localeNameLength,
        NativeTextFormatProperties* properties,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_draw_text")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceDrawText(
        nint surface,
        char* text,
        uint textLength,
        nint textFormat,
        ProGpuDirect2DRect* layoutRectangle,
        nint defaultFillBrush,
        ProGpuDirect2DDrawTextOptions options,
        ProGpuDirect2DMeasuringMode measuringMode,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_text_layout")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateTextLayout(
        nint surface,
        char* text,
        uint textLength,
        nint textFormat,
        float maximumWidth,
        float maximumHeight,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_text_layout_set_range_format")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus TextLayoutSetRangeFormat(
        nint surface,
        nint textLayout,
        NativeTextRangeFormat* formatting,
        nint drawingEffectBrush,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_typography")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateTypography(
        nint surface,
        ProGpuDirect2DTypographyFeature* features,
        uint featureCount,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_text_layout_set_typography")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus TextLayoutSetTypography(
        nint surface,
        nint textLayout,
        nint typography,
        uint rangeStart,
        uint rangeLength,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_system_font_face_reference")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceCreateSystemFontFaceReference(
            nint surface,
            char* fontFamily,
            uint fontFamilyLength,
            NativeFontFaceProperties* properties,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_font_face_reference_create_font_face")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        FontFaceReferenceCreateFontFace(
            nint surface,
            nint fontFaceReference,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_draw_glyph_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceDrawGlyphRun(
        nint surface,
        float baselineOriginX,
        float baselineOriginY,
        float fontEmSize,
        nint fontFace,
        ushort* glyphIndices,
        uint glyphCount,
        float* glyphAdvances,
        uint glyphAdvanceCount,
        ProGpuDirect2DGlyphOffset* glyphOffsets,
        uint glyphOffsetCount,
        uint isSideways,
        uint bidiLevel,
        nint foregroundBrush,
        ProGpuDirect2DMeasuringMode measuringMode,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_draw_color_glyph_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceDrawColorGlyphRun(
        nint surface,
        float baselineOriginX,
        float baselineOriginY,
        float fontEmSize,
        nint fontFace,
        ushort* glyphIndices,
        uint glyphCount,
        float* glyphAdvances,
        uint glyphAdvanceCount,
        ProGpuDirect2DGlyphOffset* glyphOffsets,
        uint glyphOffsetCount,
        uint isSideways,
        uint bidiLevel,
        nint foregroundBrush,
        uint colorPaletteIndex,
        ProGpuDirect2DMeasuringMode measuringMode,
        ProGpuDirect2DColorGlyphPath* selectedPath,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_svg_document")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceCreateSvgDocument(
        nint surface,
        byte* utf8Xml,
        uint utf8XmlByteCount,
        float viewportWidth,
        float viewportHeight,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_draw_svg_document")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceDrawSvgDocument(
        nint surface,
        nint svgDocument,
        float viewportWidth,
        float viewportHeight,
        float originX,
        float originY,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_draw_text_layout")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceDrawTextLayout(
        nint surface,
        float originX,
        float originY,
        nint textLayout,
        nint defaultFillBrush,
        ProGpuDirect2DDrawTextOptions options,
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
        EntryPoint = "progpu_native_direct2d_geometry_get_bounds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryGetBounds(
        nint surface,
        nint geometry,
        NativeMatrix3X2F* transform,
        ProGpuDirect2DRect* bounds,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_get_widened_bounds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryGetWidenedBounds(
        nint surface,
        nint geometry,
        float strokeWidth,
        nint strokeStyle,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        ProGpuDirect2DRect* bounds,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_fill_contains_point")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryFillContainsPoint(
        nint surface,
        nint geometry,
        NativePoint2F* point,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        uint* contains,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_stroke_contains_point")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryStrokeContainsPoint(
        nint surface,
        nint geometry,
        NativePoint2F* point,
        float strokeWidth,
        nint strokeStyle,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        uint* contains,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_compare")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryCompare(
        nint surface,
        nint geometry,
        nint inputGeometry,
        NativeMatrix3X2F* inputTransform,
        float flatteningTolerance,
        ProGpuDirect2DGeometryRelation* relation,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_compute_area")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryComputeArea(
        nint surface,
        nint geometry,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        float* area,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_compute_length")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryComputeLength(
        nint surface,
        nint geometry,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        float* length,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_compute_point_at_length")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryComputePointAtLength(
        nint surface,
        nint geometry,
        float length,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        NativePoint2F* point,
        NativePoint2F* unitTangent,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_simplify")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometrySimplify(
        nint surface,
        nint geometry,
        ProGpuDirect2DGeometrySimplificationOption option,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_outline")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryOutline(
        nint surface,
        nint geometry,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_widen")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryWiden(
        nint surface,
        nint geometry,
        float strokeWidth,
        nint strokeStyle,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        nint* value,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_geometry_tessellate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus GeometryTessellate(
        nint surface,
        nint geometry,
        NativeMatrix3X2F* transform,
        float flatteningTolerance,
        ProGpuDirect2DTriangle* triangles,
        uint triangleCapacity,
        uint* triangleCount,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_filled_geometry_realization")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceCreateFilledGeometryRealization(
            nint surface,
            nint geometry,
            float flatteningTolerance,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_create_stroked_geometry_realization")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceCreateStrokedGeometryRealization(
            nint surface,
            nint geometry,
            float flatteningTolerance,
            float strokeWidth,
            nint strokeStyle,
            nint* value,
            int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_draw_geometry_realization")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceDrawGeometryRealization(
            nint surface,
            nint realization,
            nint brush,
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
        EntryPoint = "progpu_native_direct2d_surface_begin_command_list_draw")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus
        SurfaceBeginCommandListDraw(
            nint surface,
            nint commandList);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_end_command_list_draw")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ProGpuDirect2DStatus SurfaceEndCommandListDraw(
        nint surface,
        ulong* tag1,
        ulong* tag2,
        int* nativeHResult);

    [LibraryImport(
        LibraryName,
        EntryPoint = "progpu_native_direct2d_surface_get_last_hresult")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SurfaceGetLastHResult(nint surface);
}
