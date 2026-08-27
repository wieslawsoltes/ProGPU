using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

internal static unsafe partial class NativeMilMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BatchMetrics
    {
        internal uint StructSize;
        internal uint CommandCount;
        internal uint SupportedCommandCount;
        internal uint UnsupportedCommandCount;
        internal uint CreatedResourceCount;
        internal uint DeletedResourceCount;
        internal uint UpdatedResourceCount;
        internal uint TotalBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VisualSnapshot
    {
        internal uint StructSize;
        internal uint Handle;
        internal double OffsetX;
        internal double OffsetY;
        internal double Opacity;
        internal uint ContentHandle;
        internal uint ChildCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TargetSnapshot
    {
        internal uint StructSize;
        internal uint Handle;
        internal uint RootHandle;
        internal float ClearRed;
        internal float ClearGreen;
        internal float ClearBlue;
        internal float ClearAlpha;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SceneMetrics
    {
        internal uint StructSize;
        internal uint VisualCount;
        internal uint RectangleCount;
        internal uint BrushCount;
        internal uint MaximumVisualDepth;
        internal uint EllipseCount;
        internal ulong StreamBytes;
        internal uint RoundedRectangleCount;
        internal uint LineCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SceneBuildRequest
    {
        internal uint StructSize;
        internal uint Flags;
        internal uint TargetHandle;
        internal uint Reserved0;
        internal ulong SceneId;
        internal ulong Generation;
        internal double DpiScaleX;
        internal double DpiScaleY;
        internal ulong MonotonicTimeNanoseconds;
        internal ulong RequestSerial;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SceneBuildResult
    {
        internal uint StructSize;
        internal uint Flags;
        internal ulong RequestSerial;
        internal ulong NextDueTimeNanoseconds;
        internal ulong StreamBytes;
    }

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus Create(nint* channel);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint channel);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_apply")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus Apply(
        nint channel,
        void* batch,
        nuint batchSize,
        BatchMetrics* metrics);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_bitmap_source_rgba8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetBitmapSourceRgba8(
        nint channel,
        uint handle,
        uint width,
        uint height,
        uint rowBytes,
        void* pixels,
        nuint pixelSize);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_drawing_image_bounds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetDrawingImageBounds(
        nint channel,
        uint handle,
        double x,
        double y,
        double width,
        double height);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_drawing_group_bounds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetDrawingGroupBounds(
        nint channel,
        uint handle,
        double x,
        double y,
        double width,
        double height);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_visual_cache_bounds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetVisualCacheBounds(
        nint channel,
        uint handle,
        double x,
        double y,
        double width,
        double height);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_viewport3d_scene")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetViewport3DScene(
        nint channel,
        uint handle,
        NativeSceneCamera3D* camera,
        NativeImageRect viewport,
        NativeSceneMesh3D* meshes,
        nuint meshCount,
        NativeSceneMesh3DVertex* vertices,
        nuint vertexCount,
        uint* indices,
        nuint indexCount);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_viewport3d_scene_lights")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetViewport3DSceneLights(
        nint channel,
        uint handle,
        NativeSceneCamera3D* camera,
        NativeImageRect viewport,
        NativeSceneMesh3D* meshes,
        nuint meshCount,
        NativeSceneMesh3DVertex* vertices,
        nuint vertexCount,
        uint* indices,
        nuint indexCount,
        NativeSceneLight3D* lights,
        nuint lightCount);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_viewport3d_scene_materials")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetViewport3DSceneMaterials(
        nint channel,
        uint handle,
        NativeSceneCamera3D* camera,
        NativeImageRect viewport,
        NativeSceneMesh3D* meshes,
        nuint meshCount,
        NativeSceneMesh3DVertex* vertices,
        nuint vertexCount,
        uint* indices,
        nuint indexCount,
        NativeSceneLight3D* lights,
        nuint lightCount,
        NativeSceneBrush* materials,
        nuint materialCount,
        NativeSceneGradientStop* gradientStops,
        nuint gradientStopCount);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_glyph_run_font_sfnt")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetGlyphRunFontSfnt(
        nint channel,
        uint handle,
        uint faceIndex,
        uint styleSimulations,
        void* fontData,
        nuint fontSize);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_resource_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint GetResourceCount(nint channel);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_has_resource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte HasResource(nint channel, uint handle);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_resource_type")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetResourceType(nint channel, uint handle);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_resource_generation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong GetResourceGeneration(nint channel, uint handle);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_visual")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte GetVisual(
        nint channel,
        uint handle,
        VisualSnapshot* snapshot);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_visual_child")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte GetVisualChild(
        nint channel,
        uint handle,
        uint index,
        uint* childHandle);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_target")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte GetTarget(
        nint channel,
        uint handle,
        TargetSnapshot* snapshot);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_build_scene")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus BuildScene(
        nint channel,
        uint targetHandle,
        ulong sceneId,
        ulong generation,
        void* destination,
        nuint destinationSize,
        nuint* bytesWritten,
        SceneMetrics* metrics);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_build_scene_with_request")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus BuildSceneWithRequest(
        nint channel,
        SceneBuildRequest* request,
        void* destination,
        nuint destinationSize,
        nuint* bytesWritten,
        SceneMetrics* metrics,
        SceneBuildResult* buildResult);
}

internal static unsafe partial class NativeMilDawnMethods
{
    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus Create(nint* channel);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint channel);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_apply")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus Apply(
        nint channel,
        void* batch,
        nuint batchSize,
        NativeMilMethods.BatchMetrics* metrics);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_bitmap_source_rgba8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetBitmapSourceRgba8(
        nint channel,
        uint handle,
        uint width,
        uint height,
        uint rowBytes,
        void* pixels,
        nuint pixelSize);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_drawing_image_bounds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetDrawingImageBounds(
        nint channel,
        uint handle,
        double x,
        double y,
        double width,
        double height);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_drawing_group_bounds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetDrawingGroupBounds(
        nint channel,
        uint handle,
        double x,
        double y,
        double width,
        double height);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_visual_cache_bounds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetVisualCacheBounds(
        nint channel,
        uint handle,
        double x,
        double y,
        double width,
        double height);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_viewport3d_scene")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetViewport3DScene(
        nint channel,
        uint handle,
        NativeSceneCamera3D* camera,
        NativeImageRect viewport,
        NativeSceneMesh3D* meshes,
        nuint meshCount,
        NativeSceneMesh3DVertex* vertices,
        nuint vertexCount,
        uint* indices,
        nuint indexCount);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_viewport3d_scene_lights")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetViewport3DSceneLights(
        nint channel,
        uint handle,
        NativeSceneCamera3D* camera,
        NativeImageRect viewport,
        NativeSceneMesh3D* meshes,
        nuint meshCount,
        NativeSceneMesh3DVertex* vertices,
        nuint vertexCount,
        uint* indices,
        nuint indexCount,
        NativeSceneLight3D* lights,
        nuint lightCount);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_viewport3d_scene_materials")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetViewport3DSceneMaterials(
        nint channel,
        uint handle,
        NativeSceneCamera3D* camera,
        NativeImageRect viewport,
        NativeSceneMesh3D* meshes,
        nuint meshCount,
        NativeSceneMesh3DVertex* vertices,
        nuint vertexCount,
        uint* indices,
        nuint indexCount,
        NativeSceneLight3D* lights,
        nuint lightCount,
        NativeSceneBrush* materials,
        nuint materialCount,
        NativeSceneGradientStop* gradientStops,
        nuint gradientStopCount);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_set_glyph_run_font_sfnt")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus SetGlyphRunFontSfnt(
        nint channel,
        uint handle,
        uint faceIndex,
        uint styleSimulations,
        void* fontData,
        nuint fontSize);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_resource_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint GetResourceCount(nint channel);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_has_resource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte HasResource(nint channel, uint handle);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_resource_type")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetResourceType(nint channel, uint handle);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_resource_generation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong GetResourceGeneration(nint channel, uint handle);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_visual")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte GetVisual(
        nint channel,
        uint handle,
        NativeMilMethods.VisualSnapshot* snapshot);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_visual_child")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte GetVisualChild(
        nint channel,
        uint handle,
        uint index,
        uint* childHandle);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_get_target")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte GetTarget(
        nint channel,
        uint handle,
        NativeMilMethods.TargetSnapshot* snapshot);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_build_scene")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus BuildScene(
        nint channel,
        uint targetHandle,
        ulong sceneId,
        ulong generation,
        void* destination,
        nuint destinationSize,
        nuint* bytesWritten,
        NativeMilMethods.SceneMetrics* metrics);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_mil_channel_build_scene_with_request")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeMilStatus BuildSceneWithRequest(
        nint channel,
        NativeMilMethods.SceneBuildRequest* request,
        void* destination,
        nuint destinationSize,
        nuint* bytesWritten,
        NativeMilMethods.SceneMetrics* metrics,
        NativeMilMethods.SceneBuildResult* buildResult);
}
