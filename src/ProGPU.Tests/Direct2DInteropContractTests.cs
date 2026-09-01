using ProGPU.Direct2D;
using Xunit;

namespace ProGPU.Tests;

public sealed class Direct2DInteropContractTests
{
    [Fact]
    public void PortableDirect2DWebGpuGateCoversD3D12MetalAndVulkan()
    {
        string nativeTest = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "tests",
            "progpu_native_direct2d_webgpu_tests.cpp");
        string cmake = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "CMakeLists.txt");
        string unixBuild = ReadRepoFile("eng", "build-progpu-native.sh");
        string windowsBuild = ReadRepoFile(
            "eng",
            "build-progpu-native-windows.ps1");
        string comparator = ReadRepoFile(
            "eng",
            "progpu-compare-direct2d-webgpu.py");
        string workflow = ReadRepoFile(
            ".github",
            "workflows",
            "build.yml");

        Assert.Contains("WGPUInstanceBackend_DX12", nativeTest, StringComparison.Ordinal);
        Assert.Contains("WGPUInstanceBackend_Metal", nativeTest, StringComparison.Ordinal);
        Assert.Contains("WGPUInstanceBackend_Vulkan", nativeTest, StringComparison.Ordinal);
        Assert.Contains("d2d::render_scene_target(", nativeTest, StringComparison.Ordinal);
        Assert.Contains("CreateLinearGradientBrush(", nativeTest, StringComparison.Ordinal);
        Assert.Contains("CreateRadialGradientBrush(", nativeTest, StringComparison.Ordinal);
        Assert.Contains("pixel(46U, 14U)", nativeTest, StringComparison.Ordinal);
        Assert.Contains("frame_metrics.submission_count == 4U", nativeTest, StringComparison.Ordinal);
        Assert.Contains("progpu_native_direct2d_webgpu_tests", cmake, StringComparison.Ordinal);
        Assert.Contains("progpu-direct2d-metal.ppm", unixBuild, StringComparison.Ordinal);
        Assert.Contains("progpu-direct2d-vulkan.ppm", unixBuild, StringComparison.Ordinal);
        Assert.Contains("progpu-direct2d-d3d12.ppm", windowsBuild, StringComparison.Ordinal);
        Assert.Contains("MaximumChannelDifference\": 1", comparator, StringComparison.Ordinal);
        Assert.Contains("linear-gradient rectangle", comparator, StringComparison.Ordinal);
        Assert.Contains("(46, 14)", comparator, StringComparison.Ordinal);
        Assert.Contains("native-direct2d-webgpu-parity", workflow, StringComparison.Ordinal);
        Assert.Contains("progpu-compare-direct2d-webgpu.py", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableComFoundationPreservesIdentityLifetimeAndInstallContract()
    {
        string header = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "include",
            "progpu_native_com.hpp");
        string cmake = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "CMakeLists.txt");
        string nativeTest = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "tests",
            "progpu_native_com_tests.cpp");
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d.cpp");

        Assert.Contains("using unknown = IUnknown;", header, StringComparison.Ordinal);
        Assert.Contains("struct unknown", header, StringComparison.Ordinal);
        Assert.Contains("atomic_reference_count", header, StringComparison.Ordinal);
        Assert.Contains("class pointer final", header, StringComparison.Ordinal);
        Assert.Contains("unknown_interface_id()", header, StringComparison.Ordinal);
        Assert.Contains(
            "include/progpu_native_com.hpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "add_executable(progpu_native_com_tests",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "identity.get() != static_cast<com::unknown*>(raw)",
            nativeTest,
            StringComparison.Ordinal);
        Assert.Contains("return destroyed ? 0 : 9;", nativeTest, StringComparison.Ordinal);
        Assert.Contains(
            "using ComPtr = progpu::native::com::pointer<Interface>;",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<wrl/client.h>", provider, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDirect2DHeaderExposesPortableLayoutsAndExplicitProviderCapability()
    {
        string header = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "include",
            "progpu_native_direct2d.h");
        string cmake = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "CMakeLists.txt");
        string nativeTest = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "tests",
            "progpu_native_direct2d_header_tests.cpp");

        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_HAS_WINDOWS_PROVIDER 1",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_HAS_WINDOWS_PROVIDER 0",
            header,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "progpu_native_direct2d.h is a Windows-only",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "add_executable(progpu_native_direct2d_header_tests",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "install(FILES include/progpu_native_direct2d.h",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "sizeof(progpu_native_direct2d_guid) == 16U",
            nativeTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "sizeof(progpu_native_direct2d_scene_stream_result) == 80U",
            nativeTest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PortableRectangleCoreIsSharedByWindowsComAdapter()
    {
        string coreHeader = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "include",
            "progpu_native_direct2d_core.hpp");
        string coreSource = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d_core.cpp");
        string nativeTest = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "tests",
            "progpu_native_direct2d_core_tests.cpp");
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d.cpp");
        string cmake = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "CMakeLists.txt");

        Assert.Contains("class rectangle_geometry final", coreHeader, StringComparison.Ordinal);
        Assert.Contains("rectangle_geometry::tessellate", coreSource, StringComparison.Ordinal);
        Assert.Contains("rectangle_geometry::point_at_length", coreSource, StringComparison.Ordinal);
        Assert.Contains("progpu_native_direct2d_core_tests", cmake, StringComparison.Ordinal);
        Assert.Contains("include/progpu_native_direct2d_core.hpp", cmake, StringComparison.Ordinal);
        Assert.Contains("direct2d_core::rectangle_geometry geometry", provider, StringComparison.Ordinal);
        Assert.DoesNotContain("compat_transform_rectangle", provider, StringComparison.Ordinal);
        Assert.Contains("degenerate.point_at_length", nativeTest, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableDirect2DCompatFactoryPreservesComShapeAndFailsClosed()
    {
        string header = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "include",
            "progpu_native_direct2d_compat.hpp");
        string source = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d_compat.cpp");
        string pathSource = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d_path.cpp");
        string ellipseSource = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d_ellipse.cpp");
        string roundedRectangleSource = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d_rounded_rectangle.cpp");
        string geometryGroupSource = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d_geometry_group.cpp");
        string strokeStyleSource = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d_stroke_style.cpp");
        string drawingStateSource = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d_drawing_state.cpp");
        string renderTargetSource = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d_render_target.cpp");
        string submissionHeader = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "include",
            "progpu_native_direct2d_scene_submission.hpp");
        string webSceneTest = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "tests",
            "progpu_native_webscene_provider_tests.cpp");
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d.cpp");
        string nativeTest = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "tests",
            "progpu_native_direct2d_compat_tests.cpp");
        string cmake = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "CMakeLists.txt");

        Assert.Contains("struct factory : com::unknown", header, StringComparison.Ordinal);
        Assert.Contains("struct rectangle_geometry : geometry", header, StringComparison.Ordinal);
        Assert.Contains("0x06152247U", header, StringComparison.Ordinal);
        Assert.Contains("0x2CD906A2U", header, StringComparison.Ordinal);
        Assert.Contains("0x2CD906BBU", header, StringComparison.Ordinal);
        Assert.Contains("struct transformed_geometry : geometry", header, StringComparison.Ordinal);
        Assert.Contains("struct path_geometry : geometry", header, StringComparison.Ordinal);
        Assert.Contains("struct ellipse_geometry : geometry", header, StringComparison.Ordinal);
        Assert.Contains("struct rounded_rectangle_geometry : geometry", header, StringComparison.Ordinal);
        Assert.Contains("struct geometry_group : geometry", header, StringComparison.Ordinal);
        Assert.Contains("struct stroke_style : resource", header, StringComparison.Ordinal);
        Assert.Contains("struct drawing_state_block : resource", header, StringComparison.Ordinal);
        Assert.Contains("struct render_target : resource", header, StringComparison.Ordinal);
        Assert.Contains("struct wic_bitmap_source : com::unknown", header, StringComparison.Ordinal);
        Assert.Contains("wic_bitmap_source_interface_id", header, StringComparison.Ordinal);
        Assert.Contains("wic_pixel_format_32bpp_pbgra", header, StringComparison.Ordinal);
        Assert.Contains("wic_pixel_format_32bpp_prgba", header, StringComparison.Ordinal);
        Assert.Contains("struct glyph_run final", header, StringComparison.Ordinal);
        Assert.Contains("struct font_face : com::unknown", header, StringComparison.Ordinal);
        Assert.Contains("font_face_interface_id", header, StringComparison.Ordinal);
        Assert.Contains("rendering_parameters_interface_id", header, StringComparison.Ordinal);
        Assert.Contains("struct rendering_parameters : com::unknown", header, StringComparison.Ordinal);
        Assert.Contains("struct text_renderer : com::unknown", header, StringComparison.Ordinal);
        Assert.Contains("text_renderer_interface_id", header, StringComparison.Ordinal);
        Assert.Contains("text_layout_interface_id", header, StringComparison.Ordinal);
        Assert.Contains("portable_text_layout_factory_interface_id", header, StringComparison.Ordinal);
        Assert.Contains("struct portable_text_layout_factory", header, StringComparison.Ordinal);
        Assert.Contains("struct underline final", header, StringComparison.Ordinal);
        Assert.Contains("struct strikethrough final", header, StringComparison.Ordinal);
        Assert.Contains("struct gradient_stop_collection : resource", header, StringComparison.Ordinal);
        Assert.Contains("struct linear_gradient_brush : brush", header, StringComparison.Ordinal);
        Assert.Contains("struct radial_gradient_brush : brush", header, StringComparison.Ordinal);
        Assert.Contains("struct scene_render_target_native : com::unknown", header, StringComparison.Ordinal);
        Assert.Contains("struct geometry_sink : simplified_geometry_sink", header, StringComparison.Ordinal);
        Assert.Contains("class portable_factory final", source, StringComparison.Ordinal);
        Assert.Contains("class portable_transformed_geometry final", source, StringComparison.Ordinal);
        Assert.Contains("class portable_path_geometry final", pathSource, StringComparison.Ordinal);
        Assert.Contains("class portable_geometry_sink final", pathSource, StringComparison.Ordinal);
        Assert.Contains("class portable_ellipse_geometry final", ellipseSource, StringComparison.Ordinal);
        Assert.Contains("class portable_rounded_rectangle_geometry final", roundedRectangleSource, StringComparison.Ordinal);
        Assert.Contains("class portable_geometry_group final", geometryGroupSource, StringComparison.Ordinal);
        Assert.Contains("class portable_stroke_style final", strokeStyleSource, StringComparison.Ordinal);
        Assert.Contains("core::valid_stroke_style", strokeStyleSource, StringComparison.Ordinal);
        Assert.Contains("class portable_drawing_state_block final", drawingStateSource, StringComparison.Ordinal);
        Assert.Contains("class portable_scene_render_target final", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("class portable_gradient_stop_collection final", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("class portable_linear_gradient_brush final", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("class portable_radial_gradient_brush final", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("builder_.draw_analytic", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("builder_.draw_geometry", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("wic_source->CopyPixels(", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("font_face_value->GetGlyphRunOutline(", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("transformed.get(), foreground, nullptr, text_sample_grid", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("text_antialias_mode_ == text_antialias_mode::aliased", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("class portable_text_renderer final", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("offsetof(text_layout_vtable, draw) == 58U * sizeof(void*)", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("layout_vtable->draw(", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("layout_factory->CreateTextLayout(", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("text_rendering_parameters_ =", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("class portable_shared_bitmap final", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("class portable_shared_render_target_bitmap final", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("GetStorageIdentity()", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("new (std::nothrow) portable_shared_bitmap(", renderTargetSource, StringComparison.Ordinal);
        Assert.Contains("class fake_wic_bitmap_source final", nativeTest, StringComparison.Ordinal);
        Assert.Contains("__uuidof(IWICBitmapSource)", nativeTest, StringComparison.Ordinal);
        Assert.Contains("native_target->CreateSharedBitmap(", nativeTest, StringComparison.Ordinal);
        Assert.Contains("class fake_font_face final", nativeTest, StringComparison.Ordinal);
        Assert.Contains("class fake_rendering_parameters final", nativeTest, StringComparison.Ordinal);
        Assert.Contains("native_target->SetTextRenderingParams(", nativeTest, StringComparison.Ordinal);
        Assert.Contains("native_target->DrawGlyphRun(", nativeTest, StringComparison.Ordinal);
        Assert.Contains("sizeof(compat::glyph_run) == sizeof(DWRITE_GLYPH_RUN)", nativeTest, StringComparison.Ordinal);
        Assert.Contains("struct fake_text_layout final", nativeTest, StringComparison.Ordinal);
        Assert.Contains("class fake_text_format final", nativeTest, StringComparison.Ordinal);
        Assert.Contains("native_target->DrawTextLayout(", nativeTest, StringComparison.Ordinal);
        Assert.Contains("native_target->DrawText(", nativeTest, StringComparison.Ordinal);
        Assert.Contains("draw_text_options::disable_color_bitmap_snapping", nativeTest, StringComparison.Ordinal);
        Assert.Contains("sizeof(compat::underline) == sizeof(DWRITE_UNDERLINE)", nativeTest, StringComparison.Ordinal);
        Assert.Contains("render_scene_target(", submissionHeader, StringComparison.Ordinal);
        Assert.Contains("progpu_native_engine_update_scene", submissionHeader, StringComparison.Ordinal);
        Assert.Contains("progpu_native_engine_render_scene", submissionHeader, StringComparison.Ordinal);
        Assert.Contains("verify_direct2d_scene", webSceneTest, StringComparison.Ordinal);
        Assert.Contains("path_state::fresh", pathSource, StringComparison.Ordinal);
        Assert.Contains("path_state::closed", pathSource, StringComparison.Ordinal);
        Assert.Contains("core::arc_to_cubics", pathSource, StringComparison.Ordinal);
        Assert.Contains("Multiple contours need fill-rule-aware union", pathSource, StringComparison.Ordinal);
        Assert.Contains("system_outline_rectangle->Outline(", nativeTest, StringComparison.Ordinal);
        Assert.Contains("direct2d_core::arc_to_cubics", provider, StringComparison.Ordinal);
        Assert.Contains("direct2d_core::ellipse_to_cubics", provider, StringComparison.Ordinal);
        Assert.Contains("direct2d_core::rounded_rectangle_to_path", provider, StringComparison.Ordinal);
        Assert.Contains("direct2d_core::valid_stroke_style", provider, StringComparison.Ordinal);
        Assert.Contains("core::rectangle_geometry geometry_", source, StringComparison.Ordinal);
        Assert.Contains("style->GetDashStyle() != dash_style::solid", source, StringComparison.Ordinal);
        Assert.Contains("rectangle_stroke_contains_point(", source, StringComparison.Ordinal);
        Assert.Contains("core::compose_transform", source, StringComparison.Ordinal);
        Assert.Contains("return not_implemented;", source, StringComparison.Ordinal);
        Assert.Contains("progpu_native_direct2d_compat_tests", cmake, StringComparison.Ordinal);
        Assert.Contains("include/progpu_native_direct2d_compat.hpp", cmake, StringComparison.Ordinal);
        Assert.Contains("include/progpu_native_direct2d_scene_submission.hpp", cmake, StringComparison.Ordinal);
        Assert.Contains("reinterpret_cast<ID2D1Factory*>", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1TransformedGeometry*", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1PathGeometry*", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1EllipseGeometry*", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1RoundedRectangleGeometry*", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1GeometryGroup*", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1StrokeStyle*", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1DrawingStateBlock*", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1RenderTarget*", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1GradientStopCollection", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1LinearGradientBrush", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1RadialGradientBrush", nativeTest, StringComparison.Ordinal);
        Assert.Contains("ID2D1GeometrySink*", nativeTest, StringComparison.Ordinal);
        Assert.Contains("native_path->Stream", nativeTest, StringComparison.Ordinal);
        Assert.Contains("factory.Reset();", nativeTest, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedProviderUsesTypedAotSafeNativeAbi()
    {
        string project = ReadRepoFile(
            "src",
            "ProGPU.Direct2D",
            "ProGPU.Direct2D.csproj");
        string native = ReadRepoFile(
            "src",
            "ProGPU.Direct2D",
            "ProGpuDirect2DNative.cs");
        string d3dImage = ReadRepoFile(
            "src",
            "ProGPU.Direct2D",
            "ProGpuDirect2DD3DImageSource.cs");
        string exports = ReadRepoFile(
            "eng",
            "progpu-native-direct2d-exports.txt");

        Assert.Contains(
            "<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<IsAotCompatible>true</IsAotCompatible>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Backend.Native.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Wpf.Interop.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "[LibraryImport(",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal const uint AbiVersion = 54U;",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_get_device_loss_state",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_com_query_interface",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_try_get_win2d_canvas_device",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_try_get_win2d_canvas_render_target",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_try_get_win2d_native_resource",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_solid_color_brush",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_brush_set_properties",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_solid_color_brush_get_color",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_linear_gradient_brush_set_properties",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_radial_gradient_brush_get_properties",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_geometry_compute_point_at_length",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_geometry_tessellate",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_geometry_realization",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_set_transform",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_line",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_fill_geometry",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_push_axis_aligned_clip",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_bitmap",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_image",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_set_antialias_mode",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_get_text_antialias_mode",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_set_primitive_blend",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_get_unit_mode",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_set_tags",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_get_dpi",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_gradient_stop_collection",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_linear_gradient_brush",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_radial_gradient_brush",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_bitmap",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_bitmap_get_descriptor",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_bitmap_copy_from_memory",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_bitmap_copy_from_bitmap",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_bitmap_brush",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_bitmap_brush_set_properties",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_bitmap_brush_get_bitmap",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_image_brush",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_image_brush_set_properties",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_image_brush_get_image",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_command_list",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_command_list_get_stream_summary",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_command_list_build_scene_stream",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_begin_command_list_draw",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_end_command_list_draw",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_effect",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_effect_set_input",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_effect_set_input_effect",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_effect_set_value",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_effect_get_output",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_layer",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_drawing_state_block",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_save_drawing_state",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_restore_drawing_state",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_push_layer",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_pop_layer",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_text_format",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_text",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_text_layout",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_text_layout",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_text_layout_set_range_format",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_typography",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_text_layout_set_typography",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_system_font_face_reference",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_font_face_reference_create_font_face",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_glyph_run",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_color_glyph_run",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_rectangle_geometry",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_rounded_rectangle_geometry",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_ellipse_geometry",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_path_geometry",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_transformed_geometry",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_combine_geometry",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_stroke_style",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_acquire",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "10A72A66-E91C-43F4-993F-DDF4B82B0B4A",
            ReadRepoFile(
                "src",
                "ProGPU.Direct2D",
                "ProGpuDirect2DSurface.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "41343A53-E41A-49A2-91CD-21793BBB62E5",
            ReadRepoFile(
                "src",
                "ProGPU.Direct2D",
                "ProGpuDirect2DSurface.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "FE9E984D-3F95-407C-B5DB-CB94D4E8F87C",
            ReadRepoFile(
                "src",
                "ProGPU.Direct2D",
                "ProGpuDirect2DSurface.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "B4F34A19-2383-4D76-94F6-EC343657C3DC",
            ReadRepoFile(
                "src",
                "ProGPU.Direct2D",
                "ProGpuDirect2DSurface.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "28211A43-7D89-476F-8181-2D6159B220AD",
            ReadRepoFile(
                "src",
                "ProGPU.Direct2D",
                "ProGpuDirect2DSurface.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "65019F75-8DA2-497C-B32C-DFA34E48EDE6",
            ReadRepoFile(
                "src",
                "ProGPU.Direct2D",
                "ProGpuDirect2DSurface.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "5F174B49-0D8B-4CFB-8BCA-F1CCE9D06C67",
            ReadRepoFile(
                "src",
                "ProGPU.Direct2D",
                "ProGpuDirect2DSurface.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "05A9BF42-223F-4441-B5FB-8263685F55E9",
            ReadRepoFile(
                "src",
                "ProGPU.Direct2D",
                "ProGpuDirect2DSurface.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "5E7FA7CA-DDE3-424C-89F0-9FCD6FED58CD",
            ReadRepoFile(
                "src",
                "ProGPU.Direct2D",
                "ProGpuDirect2DSurface.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_release",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.GetDelegateForFunctionPointer",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeLibrary.Load",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_try_get_win2d_native_resource",
            exports,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_scene_recorder_get_command_sink",
            exports,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_scene_recorder_build_stream",
            exports,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_compat_factory_create",
            exports,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_compat_factory_create_solid_color_brush",
            exports,
            StringComparison.Ordinal);
        Assert.Equal(
            129,
            exports.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries).Length);
        Assert.Contains(
            "IPortableD3DImageSource",
            d3dImage,
            StringComparison.Ordinal);
        Assert.Contains(
            "IPortableInvalidationSource",
            d3dImage,
            StringComparison.Ordinal);
        Assert.Contains(
            "contentVersion == 0U",
            d3dImage,
            StringComparison.Ordinal);
        Assert.Contains(
            "_surface.TextureChanged += handler",
            d3dImage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            d3dImage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDrawScopeOwnsComAndKeyedMutexTransaction()
    {
        string header = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "include",
            "progpu_native_direct2d.h");
        string source = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d.cpp");
        string semanticStroke = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Scene",
            "progpu_native_semantic_path_stroke.hpp");
        string curveDash = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Mil",
            "progpu_native_mil_curve_dash.hpp");
        string test = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "tests",
            "progpu_native_direct2d_tests.cpp");

        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_ABI_VERSION = 54U",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_device_loss_state",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "RegisterDeviceRemovedEvent(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetDeviceRemovedReason()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_begin_draw",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_end_draw",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_scene_recorder_get_command_sink",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "class CommandSceneStreamSink final : public ID2D1CommandSink1",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class CommandSceneStrokeSink final : public ID2D1SimplifiedGeometrySink",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "semantic_path_stroke::compile(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "mil::curve_dash::try_create_runs(",
            semanticStroke,
            StringComparison.Ordinal);
        Assert.Contains(
            "hairline || fixed_device ? &stroke.transform : nullptr",
            semanticStroke,
            StringComparison.Ordinal);
        Assert.Contains(
            "metric_point(",
            curveDash,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_GEOMETRY_PATH_JOIN",
            semanticStroke,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU Direct2D COM recorder omitted a curved stroke transform policy",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DFactory final",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DRectangleGeometry final",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DEllipseGeometry final : public ID2D1EllipseGeometry",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DSolidColorBrush final",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DStrokeStyle final : public ID2D1StrokeStyle1",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DPathGeometry final : public ID2D1PathGeometry1",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DRoundedRectangleGeometry final :",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DTransformedGeometry final :",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DGeometryGroup final : public ID2D1GeometryGroup",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class CompatGroupGeometrySink final :",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class ProGpuD2DGeometrySink final : public ID2D1GeometrySink",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_compat_factory_create",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU ID2D1RectangleGeometry creation failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU ID2D1EllipseGeometry creation failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU ID2D1RoundedRectangleGeometry creation failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU ID2D1TransformedGeometry creation failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU transformed geometry diverged from system Direct2D",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU Direct2D COM transformed FillGeometry callback failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU ID2D1GeometryGroup creation failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU geometry group diverged from system Direct2D",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU Direct2D COM geometry-group FillGeometry callback failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU Direct2D COM rounded-rectangle FillGeometry callback failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU rounded-rectangle bounds diverged from system Direct2D",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU rounded-rectangle area diverged from system Direct2D",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU rounded-rectangle length diverged from system Direct2D",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU Direct2D COM ellipse FillGeometry callback failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU ellipse bounds diverged from system Direct2D",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU ellipse area diverged from system Direct2D",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU ellipse length diverged from system Direct2D",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "flattening_tolerance * 0.5F",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU ID2D1StrokeStyle1 creation failed",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU Direct2D COM recorder omitted the retained stroke batch",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "direct_write.brush_count == 1U",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "translated Direct2D scene omitted its fill path or analytic stroke",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU path bounds diverged from system Direct2D",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "compat_path.Get(),",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU command sink did not expose genuine Direct2D COM identity",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_com_release",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_com_query_interface",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_INTERFACE_WINRT_DIRECT3D11_DEVICE",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateDirect3D11DeviceFromDXGIDevice(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RoGetActivationFactory(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IProGpuWin2DCanvasFactoryNative",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IProGpuWin2DCanvasResourceWrapperNative",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface.win2d_factory->GetOrCreate(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface.d2d_device.Get()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface.d2d_bitmap.Get()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DWriteCreateFactory(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->DrawText(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->DrawTextLayout(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_INTERFACE_DWRITE_TEXT_FORMAT1",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_INTERFACE_DWRITE_TEXT_LAYOUT4",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_text_format(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_text_layout(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_text_layout_set_range_format(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_typography(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_text_layout_set_typography(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_system_font_face_reference(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_font_face_reference_create_font_face(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_glyph_run(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_color_glyph_run(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->DrawGlyphRun(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DrawGlyphRunWithColorSupport(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TranslateColorGlyphRun(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_svg_document(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_draw_svg_document(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "context5->CreateSvgDocument(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "context5->DrawSvgDocument(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "BorrowedMemoryStream",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_SVG_DOCUMENT",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "resource_wrapper->GetNativeResource(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_RENDER_TARGET",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "has_same_com_identity(bitmap.Get(), wrapped_bitmap.Get())",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_create_solid_color_brush(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "get_win2d_wrapper_native_resource(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "has_same_com_identity(\n                solid_brush.Get(),\n                unwrapped_solid_brush.Get())",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ID2D1GradientStopCollection1",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateLinearGradientBrush(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateRadialGradientBrush(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "unwrapped_linear_brush.Get())",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "unwrapped_radial_brush.Get())",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ID2D1PathGeometry1",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateTransformedGeometry(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CombineWithGeometry(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "has_same_com_identity(\n                transformed_geometry.Get(),\n                unwrapped_geometry.Get())",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ID2D1StrokeStyle1",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "factory->CreateStrokeStyle(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "unwrapped_stroke_style.Get())",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "ID2D1BitmapBrush1",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->CreateBitmap(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->CreateBitmapBrush(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ID2D1ImageBrush",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->CreateImageBrush(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->CreateCommandList(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class CommandStreamSummarySink final : public ID2D1CommandSink1",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "native_command_list->Stream(sink)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class CommandSceneStreamSink final : public ID2D1CommandSink1",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "collection->GetGradientStops1(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_GRADIENT_BRUSHES",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "class CommandScenePathSink final : public ID2D1SimplifiedGeometrySink",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_PATH_GEOMETRY",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "geometry->Widen(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_STROKED_PATH_GEOMETRY",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_HAS_UNSUPPORTED_OPERATIONS",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->SetTarget(surface->active_command_list.Get());",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->SetTarget(surface->d2d_bitmap.Get());",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->active_command_list->Close()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->CreateEffect(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "native_effect->SetInput(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "native_effect->SetInputEffect(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "native_effect->SetValue(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "native_effect->GetOutput(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->CreateLayer(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "factory->CreateDrawingStateBlock(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->SaveDrawingState(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->RestoreDrawingState(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->PushLayer(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->PopLayer();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "provider drawing-state restore changed the transform",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "unbalanced ID2D1Layer scope did not fail closed and unwind",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "to_portable_guid(gaussian_blur_effect_id)",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "to_portable_guid(shadow_effect_id)",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "effect image brush changed output COM identity",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win2D effect-output CanvasImageBrush changed COM identity",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "unwrapped_bitmap_brush.Get())",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "unwrapped_image_brush.Get())",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "unwrapped_command_list.Get())",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "command_descriptor.content_version == descriptor.content_version",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "context->FillGeometry(path_geometry.Get(), solid_brush.Get());",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "context->DrawGeometry(",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "stroke_style.Get());",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "Microsoft.Graphics.Canvas.CanvasDevice",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LoadLibrary",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IDirect3DDxgiInterfaceAccess",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->BeginDraw();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->EndDraw(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "release_locked(*surface, release_key, SUCCEEDED(draw_hr))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE",
            test,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DawnBridgeAlternatesGpuOwnershipWithoutCpuCopies()
    {
        string dawn = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnExplicitSharedTextureAccess.cs");
        string surface = ReadRepoFile(
            "src",
            "ProGPU.Direct2D",
            "ProGpuDirect2DSurface.cs");

        Assert.Contains(
            "public bool TryImportDxgiSharedTexture(",
            dawn,
            StringComparison.Ordinal);
        Assert.Contains(
            "SharedTextureMemory.ImportDXGISharedHandle(",
            dawn,
            StringComparison.Ordinal);
        Assert.Contains(
            "_access.EndAccess();",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "SurfaceBeginDraw(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "SurfaceEndDraw(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_access.BeginAccess(initialized: true);",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "IProGpuContextTextureLeaseSource",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_leaseCount != 0",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_producer = ProducerKind.Direct2D;",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_typedDrawScopeDepth = 0U;",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "public event EventHandler<ProGpuDirect2DDeviceLostEventArgs>? DeviceLost;",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "public ulong ResourceGeneration { get; }",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_resourceDomain.MarkDeviceLost(state.ReasonHResult);",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dawn.Context.ReportDeviceLost(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "ValidateResourceDomain(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "nativeSurface = _nativeSurface;\n        }\n\n        ulong tag1",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryBeginMicrosoftWin2DProducerAccess(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "SurfaceTryGetWin2DCanvasRenderTarget(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "SurfaceAcquire(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "SurfaceRelease(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGpuMicrosoftWin2DProducerAccess",
            surface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CopyPixels",
            surface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadPixels",
            surface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ToArray()",
            surface,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublicContractKeepsComWindowsOnlyAndTyped()
    {
        var options = new ProGpuDirect2DSurfaceOptions(
            Width: 640U,
            Height: 480U,
            DpiX: 120.0F,
            DpiY: 120.0F,
            Flags: ProGpuDirect2DSurfaceFlags.AllowWarpFallback,
            AdapterLuid: 0x1234L);

        Assert.Equal(640U, options.Width);
        Assert.Equal(480U, options.Height);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1DeviceContext1, (ProGpuDirect2DInterfaceKind)13);
        Assert.Equal(ProGpuDirect2DInterfaceKind.Win2DCanvasRenderTarget, (ProGpuDirect2DInterfaceKind)18);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1SolidColorBrush, (ProGpuDirect2DInterfaceKind)19);
        Assert.Equal(ProGpuDirect2DInterfaceKind.Win2DCanvasSolidColorBrush, (ProGpuDirect2DInterfaceKind)20);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1GradientStopCollection1, (ProGpuDirect2DInterfaceKind)21);
        Assert.Equal(ProGpuDirect2DInterfaceKind.Win2DCanvasLinearGradientBrush, (ProGpuDirect2DInterfaceKind)23);
        Assert.Equal(ProGpuDirect2DInterfaceKind.Win2DCanvasRadialGradientBrush, (ProGpuDirect2DInterfaceKind)25);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1Geometry, (ProGpuDirect2DInterfaceKind)26);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1PathGeometry1, (ProGpuDirect2DInterfaceKind)30);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1TransformedGeometry, (ProGpuDirect2DInterfaceKind)31);
        Assert.Equal(ProGpuDirect2DInterfaceKind.Win2DCanvasGeometry, (ProGpuDirect2DInterfaceKind)32);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1ImageBrush, (ProGpuDirect2DInterfaceKind)38);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1CommandList, (ProGpuDirect2DInterfaceKind)39);
        Assert.Equal(ProGpuDirect2DInterfaceKind.Win2DCanvasCommandList, (ProGpuDirect2DInterfaceKind)40);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1Effect, (ProGpuDirect2DInterfaceKind)41);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1Image, (ProGpuDirect2DInterfaceKind)42);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1Layer, (ProGpuDirect2DInterfaceKind)43);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1DrawingStateBlock1, (ProGpuDirect2DInterfaceKind)44);
        Assert.Equal(
            new Guid("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5"),
            ProGpuDirect2DBuiltInEffects.GaussianBlur);
        Assert.Equal(
            new Guid("C67EA361-1863-4E69-89DB-695D3E9A5B6B"),
            ProGpuDirect2DBuiltInEffects.Shadow);
        Assert.Equal(ProGpuDirect2DStatus.DrawFailed, (ProGpuDirect2DStatus)12);
        Assert.Equal(ProGpuDirect2DStatus.DrawingStateMismatch, (ProGpuDirect2DStatus)16);
        Assert.Equal(ProGpuDirect2DLayerOptions.IgnoreAlpha, (ProGpuDirect2DLayerOptions)2);
        Assert.Equal(
            ProGpuDirect2DSceneStreamFlags.HasGradientBrushes,
            (ProGpuDirect2DSceneStreamFlags)(1U << 3));
        Assert.Equal(
            ProGpuDirect2DSceneStreamFlags.HasOpacityLayers,
            (ProGpuDirect2DSceneStreamFlags)(1U << 6));
        Assert.Equal(
            ProGpuDirect2DSceneStreamFlags.HasGeometricLayerMasks,
            (ProGpuDirect2DSceneStreamFlags)(1U << 7));
        Assert.Equal(
            ProGpuDirect2DSceneStreamFlags.HasOpacityBrushLayerMasks,
            (ProGpuDirect2DSceneStreamFlags)(1U << 8));
        Assert.Equal(
            ProGpuDirect2DSceneStreamFlags.HasCompositeLayerMasks,
            (ProGpuDirect2DSceneStreamFlags)(1U << 9));
        Assert.Equal(
            ProGpuDirect2DSceneStreamFailureReason.CapacityExceeded,
            (ProGpuDirect2DSceneStreamFailureReason)7U);
        Assert.Equal(
            16,
            System.Runtime.InteropServices.Marshal.SizeOf<
                ProGpuDirect2DColor>());
        Assert.Equal(
            20,
            System.Runtime.InteropServices.Marshal.SizeOf<
                ProGpuDirect2DGradientStop>());
        Assert.Equal(
            16,
            System.Runtime.InteropServices.Marshal.SizeOf<
                ProGpuDirect2DRect>());
        Assert.Equal(
            28,
            System.Runtime.InteropServices.Marshal.SizeOf<
                ProGpuDirect2DImageBrushProperties>());
        Assert.Equal(
            24,
            System.Runtime.InteropServices.Marshal.SizeOf<
                ProGpuDirect2DPathFigure>());
        Assert.Equal(
            48,
            System.Runtime.InteropServices.Marshal.SizeOf<
                ProGpuDirect2DPathSegment>());

        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<ArgumentNullException>(() =>
                ProGpuDirect2DSurface.Create(null!, options));
        }
    }

    [Fact]
    public void PackagedWin2DGateUsesRealProjectedObjectsAndPretrustedSigning()
    {
        string project = ReadRepoFile(
            "tests",
            "ProGPU.Direct2D.Win2D.Integration",
            "ProGPU.Direct2D.Win2D.Integration.csproj");
        string program = ReadRepoFile(
            "tests",
            "ProGPU.Direct2D.Win2D.Integration",
            "Program.cs");
        string manifest = ReadRepoFile(
            "tests",
            "ProGPU.Direct2D.Win2D.Integration",
            "Package.appxmanifest");
        string gate = ReadRepoFile(
            "eng",
            "progpu-run-direct2d-win2d-integration.ps1");
        string windowsBuild = ReadRepoFile(
            "eng",
            "build-progpu-native-windows.ps1");

        Assert.Contains("Microsoft.Graphics.Win2D", project, StringComparison.Ordinal);
        Assert.Contains("CanvasRenderTarget.FromAbi(", program, StringComparison.Ordinal);
        Assert.Contains("target.CreateDrawingSession()", program, StringComparison.Ordinal);
        Assert.Contains("drawingSession.FillRectangle(", program, StringComparison.Ordinal);
        Assert.Contains("target.GetPixelColors()", program, StringComparison.Ordinal);
        Assert.Contains("contentVersionAfter <= contentVersionBefore", program, StringComparison.Ordinal);
        Assert.Contains("TryAcquireMicrosoftWin2DNativeDevice(", program, StringComparison.Ordinal);
        Assert.Contains("TryAcquireMicrosoftWin2DNativeBitmap(", program, StringComparison.Ordinal);
        Assert.Contains("surface.CreateImageBrush(", program, StringComparison.Ordinal);
        Assert.Contains("ProGpuDirect2DInterfaceKind.D2D1ImageBrush", program, StringComparison.Ordinal);
        Assert.Contains("canvasGeneralImageBrush.SourceRectangle", program, StringComparison.Ordinal);
        Assert.Contains("surface.CreateCommandList()", program, StringComparison.Ordinal);
        Assert.Contains("CanvasCommandList.FromAbi(", program, StringComparison.Ordinal);
        Assert.Contains("canvasCommandList.CreateDrawingSession()", program, StringComparison.Ordinal);
        Assert.Contains("TryAcquireMicrosoftWin2DNativeCommandList(", program, StringComparison.Ordinal);
        Assert.Contains("canvasCommandListImageBrush.SourceRectangle", program, StringComparison.Ordinal);
        Assert.Contains("surface.CreateEffect(", program, StringComparison.Ordinal);
        Assert.Contains("surface.SetEffectInput(", program, StringComparison.Ordinal);
        Assert.Contains("surface.SetEffectFloat(", program, StringComparison.Ordinal);
        Assert.Contains("surface.GetEffectOutput(", program, StringComparison.Ordinal);
        Assert.Contains("canvasEffectImageBrush.SourceRectangle", program, StringComparison.Ordinal);
        Assert.Contains("surface.CreateLayer(", program, StringComparison.Ordinal);
        Assert.Contains("surface.CreateDrawingStateBlock()", program, StringComparison.Ordinal);
        Assert.Contains("surface.BeginCommandListDrawing(", program, StringComparison.Ordinal);
        Assert.Contains("layerSession.SaveDrawingState(", program, StringComparison.Ordinal);
        Assert.Contains("layerSession.RestoreDrawingState(", program, StringComparison.Ordinal);
        Assert.Contains("layerSession.PushLayer(", program, StringComparison.Ordinal);
        Assert.Contains("TypedLayerStateScopePassed: true", program, StringComparison.Ordinal);
        Assert.Contains("CreateSolidColorBrush(", program, StringComparison.Ordinal);
        Assert.Contains("TryAcquireMicrosoftWin2DSolidColorBrush(", program, StringComparison.Ordinal);
        Assert.Contains("TryAcquireMicrosoftWin2DNativeSolidColorBrush(", program, StringComparison.Ordinal);
        Assert.Contains("CanvasSolidColorBrush.FromAbi(", program, StringComparison.Ordinal);
        Assert.Contains("CanvasLinearGradientBrush.FromAbi(", program, StringComparison.Ordinal);
        Assert.Contains("CanvasRadialGradientBrush.FromAbi(", program, StringComparison.Ordinal);
        Assert.Contains("CanvasGeometry.FromAbi(", program, StringComparison.Ordinal);
        Assert.Contains("TryAcquireMicrosoftWin2DLinearGradientBrush(", program, StringComparison.Ordinal);
        Assert.Contains("TryAcquireMicrosoftWin2DRadialGradientBrush(", program, StringComparison.Ordinal);
        Assert.Contains("surface.CreateGeometry(combinedGeometry)", program, StringComparison.Ordinal);
        Assert.Contains("TryAcquireMicrosoftWin2DGeometry(", program, StringComparison.Ordinal);
        Assert.Contains("TryAcquireMicrosoftWin2DNativeGeometry(", program, StringComparison.Ordinal);
        Assert.Contains("drawingSession.FillGeometry(", program, StringComparison.Ordinal);
        Assert.Contains("HasSameComIdentity(originalDevice, wrappedDevice)", program, StringComparison.Ordinal);
        Assert.Contains("HasSameComIdentity(originalBitmap, wrappedBitmap)", program, StringComparison.Ordinal);
        Assert.Contains("NativeSolidColorBrushIdentityMatches: true", program, StringComparison.Ordinal);
        Assert.Contains("NativeLinearGradientBrushIdentityMatches: true", program, StringComparison.Ordinal);
        Assert.Contains("NativeRadialGradientBrushIdentityMatches: true", program, StringComparison.Ordinal);
        Assert.Contains("NativeGeneralImageBrushIdentityMatches: true", program, StringComparison.Ordinal);
        Assert.Contains("NativeCommandListIdentityMatches: true", program, StringComparison.Ordinal);
        Assert.Contains("NativeCommandListImageBrushIdentityMatches: true", program, StringComparison.Ordinal);
        Assert.Contains("NativeEffectImageBrushIdentityMatches: true", program, StringComparison.Ordinal);
        Assert.Contains("NativeGeometryIdentityMatches: true", program, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Graphics.Canvas.CanvasDevice", manifest, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Graphics.Canvas.dll", manifest, StringComparison.Ordinal);
        Assert.Contains("runFullTrust", manifest, StringComparison.Ordinal);

        Assert.Contains("PROGPU_WIN2D_SIGNING_CERTIFICATE_THUMBPRINT", gate, StringComparison.Ordinal);
        Assert.Contains("Cert:\\CurrentUser\\My\\", gate, StringComparison.Ordinal);
        Assert.Contains("/sha1 $SigningCertificateThumbprint", gate, StringComparison.Ordinal);
        Assert.Contains("Add-AppxPackage -Path $SignedPackagePath", gate, StringComparison.Ordinal);
        Assert.Contains("direct2d-win2d-result.json", gate, StringComparison.Ordinal);
        Assert.Contains("$FallbackResultPath", gate, StringComparison.Ordinal);
        Assert.Contains("FallbackResultDirectoryName", program, StringComparison.Ordinal);
        Assert.Contains("WriteProgress(\"main-entered\")", program, StringComparison.Ordinal);
        Assert.Contains("WriteProgress(\"geometry-created\")", program, StringComparison.Ordinal);
        Assert.Contains("$PackageProgressPath", gate, StringComparison.Ordinal);
        Assert.Contains("$FallbackProgressPath", gate, StringComparison.Ordinal);
        Assert.Contains("$ObservedProcess", gate, StringComparison.Ordinal);
        Assert.Contains("$StaleProcessDeadline", gate, StringComparison.Ordinal);
        Assert.Contains(
            "exited before producing evidence; last stage:",
            gate,
            StringComparison.Ordinal);
        Assert.Contains("return WriteEvidence(evidence) ? 0 : 2;", program, StringComparison.Ordinal);
        Assert.Contains("WriteProgress(\"evidence-write-failed\")", program, StringComparison.Ordinal);
        Assert.Contains("artifacts/direct2d-win2d", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("New-SelfSignedCertificate", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Export-PfxCertificate", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("RootStore.Add", gate, StringComparison.Ordinal);
        Assert.Contains("PROGPU_RUN_REAL_WIN2D_INTEGRATION", windowsBuild, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException(
            $"Could not find repository file {Path.Combine(pathParts)}.");
    }
}
