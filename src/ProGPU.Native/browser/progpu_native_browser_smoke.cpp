#include "progpu_native_browser.h"
#include "progpu_native_browser_evidence.hpp"
#include "progpu_native_scene_builder.hpp"
#include "progpu_native_semantic_backdrop_scene.hpp"
#include "progpu_native_semantic_color_glyph_scene.hpp"
#include "progpu_native_semantic_coverage_mask_scene.hpp"
#include "progpu_native_semantic_geometry_scene.hpp"
#include "progpu_native_semantic_image_scene.hpp"
#include "progpu_native_semantic_rounded_mask_scene.hpp"
#include "progpu_native_semantic_state_mask_scene.hpp"
#include "progpu_native_semantic_text_scene.hpp"

#include <emscripten.h>
#include <emscripten/html5.h>
#include <webgpu/webgpu.h>

#include <array>
#include <cinttypes>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <span>
#include <vector>

namespace {

constexpr std::uint32_t width = 640U;
constexpr std::uint32_t height = 360U;

struct browser_resources final {
    progpu_native_engine* engine = nullptr;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    WGPUTexture render_texture = nullptr;
    WGPUTextureView render_view = nullptr;
};

browser_resources resources{};

[[noreturn]] void fail(const char* message) {
    std::fprintf(stderr, "ProGPU browser smoke failed: %s\n", message);
    EM_ASM({
        document.body.dataset.progpuNative = "failed";
        document.body.dataset.progpuNativeError =
            "The native C++ browser smoke failed; see the console.";
    });
    emscripten_force_exit(1);
}

[[noreturn]] void fail_engine(const char* message) {
    char detail[512]{};
    if (resources.engine != nullptr) {
        progpu_native_engine_get_last_error(
            resources.engine,
            detail,
            sizeof(detail));
    }
    std::fprintf(
        stderr,
        "ProGPU browser engine detail: %s\n",
        detail[0] == '\0' ? "unavailable" : detail);
    fail(message);
}

bool finish_evidence_frame(double, void*) {
    EM_ASM({
        document.body.dataset.progpuNative = "passed";
        document.body.dataset.progpuNativeSemanticCommands = "2";
        document.body.dataset.progpuNativeSemanticResources = "4";
        document.body.dataset.progpuNativeSemanticDraws = "2";
        document.body.dataset.progpuNativeRendererSubmissions = "1";
        document.body.dataset.progpuNativeRetainedTextStyles = "passed";
        document.body.dataset.progpuNativeColorGlyphAtlas = "passed";
        document.body.dataset.progpuNativeCubicImages = "passed";
        document.body.dataset.progpuNativeCoverageMasks = "passed";
        document.body.dataset.progpuNativeRoundedMasks = "passed";
        document.body.dataset.progpuNativeStateMasks = "passed";
        document.body.dataset.progpuNativeStateMaskMedia = "passed";
        document.body.dataset.progpuNativeSemanticGeometry = "passed";
        document.body.dataset.progpuNativeSceneBuilder = "passed";
        document.body.dataset.progpuNativeDeviceRecovery = "passed";
        document.body.dataset.progpuNativeEvidenceTarget =
            "offscreen-texture-readback";
        document.body.dataset.progpuNativeBackendAbi = "3";
        document.body.dataset.progpuNativeExplicitTimeline = "0";
        document.getElementById("native-status").textContent =
            "C++ / WebGPU semantic backend active — exact vector, glyph, and image masks verified";
    });
    // The test page owns the offscreen texture until navigation releases the
    // WebAssembly instance and its Emdawnwebgpu handle table. The visible
    // canvas is populated only from the mapped test evidence.
    return false;
}

void finish_browser_evidence(bool success) {
    if (!success) {
        fail("The browser WebGPU evidence readback failed.");
    }
    if (emscripten_request_animation_frame(
            finish_evidence_frame,
            nullptr) < 0) {
        fail("The browser evidence completion frame could not be scheduled.");
    }
}

bool render_browser_frame(double, void*) {
    WGPUTexture render_texture = nullptr;
    WGPUTextureView render_view = nullptr;
    if (!progpu::native::browser::create_evidence_target(
            resources.device,
            WGPUTextureFormat_BGRA8Unorm,
            width,
            height,
            &render_texture,
            &render_view)) {
        fail("The browser WebGPU render target could not be created.");
    }

    progpu_native_scene_frame semantic_frame{};
    semantic_frame.struct_size = sizeof(semantic_frame);
    semantic_frame.width = width;
    semantic_frame.height = height;
    semantic_frame.dpi_scale = 1.0F;
    semantic_frame.target_view = reinterpret_cast<std::uintptr_t>(render_view);
    semantic_frame.clear_color = {0.01F, 0.015F, 0.03F, 1.0F};

    progpu::native::semantic_scene_builder native_builder(700U, 1U);
    std::uint32_t builder_brush = PROGPU_NATIVE_SCENE_NO_INDEX;
    auto builder_state =
        progpu::native::semantic_scene_builder::identity_state();
    builder_state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
    builder_state.clip_rect = {32.0F, 32.0F, 192.0F, 112.0F};
    std::uint32_t builder_state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    const auto builder_identity =
        progpu::native::semantic_scene_builder::identity_transform();
    const std::array builder_primitives{
        progpu_native_analytic_primitive{
            PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE,
            0U,
            24.0F,
            24.0F,
            216.0F,
            128.0F,
            12.0F,
            0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F},
            builder_identity}};
    progpu_native_geometry_primitive builder_line{};
    builder_line.kind = PROGPU_NATIVE_GEOMETRY_LINE;
    builder_line.p0 = {40.0F, 100.0F};
    builder_line.p1 = {210.0F, 100.0F};
    builder_line.stroke_thickness = 3.0F;
    builder_line.color = {1.0F, 1.0F, 1.0F, 1.0F};
    builder_line.transform = builder_identity;
    const std::array builder_stroke_points{
        progpu_native_point{40.0F, 120.0F},
        progpu_native_point{100.0F, 132.0F},
        progpu_native_point{200.0F, 112.0F}};
    progpu_native_scene_stroke builder_stroke{};
    builder_stroke.struct_size = sizeof(builder_stroke);
    builder_stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
    builder_stroke.point_count = builder_stroke_points.size();
    builder_stroke.color = {1.0F, 1.0F, 1.0F, 1.0F};
    builder_stroke.transform = builder_identity;
    builder_stroke.stroke_thickness = 3.0F;
    builder_stroke.miter_limit = 10.0F;
    builder_stroke.start_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    builder_stroke.end_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    builder_stroke.line_join = PROGPU_NATIVE_STROKE_JOIN_ROUND;
    builder_stroke.dash_cap = PROGPU_NATIVE_STROKE_CAP_FLAT;
    const std::array builder_path_segments{
        progpu_native_path_segment{
            {150.0F, 44.0F}, {206.0F, 62.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {206.0F, 62.0F}, {156.0F, 88.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {156.0F, 88.0F}, {150.0F, 44.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const progpu_native_scene_path_fill builder_path{
        0U,
        builder_path_segments.size(),
        150.0F,
        44.0F,
        206.0F,
        88.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        builder_identity,
        PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        8U};
    constexpr std::array<std::byte, 16U> builder_image_pixels{
        std::byte{0xff}, std::byte{0x40}, std::byte{0x80}, std::byte{0xff},
        std::byte{0x20}, std::byte{0xe0}, std::byte{0xff}, std::byte{0xff},
        std::byte{0x20}, std::byte{0xe0}, std::byte{0xff}, std::byte{0xff},
        std::byte{0xff}, std::byte{0x40}, std::byte{0x80}, std::byte{0xff}};
    std::uint32_t builder_image_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_scene_image_draw builder_image{};
    builder_image.image_width = 2U;
    builder_image.image_height = 2U;
    builder_image.row_bytes = 8U;
    builder_image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    builder_image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    builder_image.destination_rect = {104.0F, 48.0F, 36.0F, 36.0F};
    builder_image.transform = builder_identity;
    builder_image.opacity = 1.0F;
    const std::array builder_glyph_segments{
        progpu_native_path_segment{
            {0.0F, 0.0F}, {18.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {18.0F, 0.0F}, {18.0F, 22.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {18.0F, 22.0F}, {0.0F, 22.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {0.0F, 22.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const progpu_native_scene_glyph_outline builder_glyph_outline{
        0U,
        builder_glyph_segments.size(),
        0.0F,
        0.0F,
        18.0F,
        22.0F,
        1.0F,
        0.25F};
    const progpu_native_positioned_glyph builder_glyph{
        0U,
        0U,
        {64.0F, 60.0F},
        {1.0F, 0.0F},
        {0.0F, 1.0F},
        {1.0F, 1.0F, 1.0F, 1.0F},
        1.0F,
        0.0F,
        0.0F,
        0.0F};
    const progpu_native_scene_text_style builder_text_style{
        {1.0F, 0.82F, 0.12F, 1.0F},
        PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE,
        0U,
        0U,
        0U};
    const progpu_native_scene_color_glyph_bitmap builder_color_bitmap{
        0U, 2U, 2U, 8U, 0U,
        0.0F, 0.0F, 18.0F, 22.0F, 0U, 0U};
    const progpu_native_positioned_glyph builder_color_glyph{
        0U,
        0U,
        {214.0F, 60.0F},
        {1.0F, 0.0F},
        {0.0F, 1.0F},
        {1.0F, 1.0F, 1.0F, 1.0F},
        1.0F,
        0.0F,
        0.0F,
        0.0F};
    progpu_native_scene_layer_mask builder_layer_mask{};
    builder_layer_mask.bounds = {20.0F, 20.0F, 224.0F, 136.0F};
    builder_layer_mask.transform = builder_identity;
    for (std::size_t index = 0U; index < 4U; ++index) {
        builder_layer_mask.corner_radii_x[index] = 14.0F;
        builder_layer_mask.corner_radii_y[index] = 14.0F;
    }
    builder_layer_mask.opacity = 1.0F;
    progpu_native_group_effect builder_blur{};
    builder_blur.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
    builder_blur.revision = 1U;
    builder_blur.sigma_x = 0.75F;
    builder_blur.sigma_y = 0.75F;
    std::uint32_t builder_layer_mask_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t builder_effect_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_scene_layer builder_layer{};
    builder_layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
    builder_layer.bounds = {16.0F, 16.0F, 232.0F, 144.0F};
    builder_layer.opacity = 1.0F;
    builder_layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    builder_layer.content_revision = 1U;
    builder_layer.composite_revision = 1U;
    std::uint32_t builder_glyph_resource = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t builder_color_glyph_resource =
        PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t builder_text_style_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!native_builder.reserve(11U, 12U, 6144U) ||
        !native_builder.add_solid_brush(
            {0.2F, 0.55F, 1.0F, 1.0F}, 0.9F, builder_brush) ||
        !native_builder.add_rounded_rectangle_mask(
            builder_layer_mask,
            builder_layer_mask_index) ||
        !native_builder.add_effect_chain(
            std::span<const progpu_native_group_effect>(&builder_blur, 1U),
            1U,
            builder_effect_index)) {
        fail("The browser native C++ scene builder could not add resources.");
    }
    builder_layer.mask_resource_index = builder_layer_mask_index;
    builder_layer.effect_resource_index = builder_effect_index;
    if (!native_builder.add_state(builder_state, builder_state_index) ||
        !native_builder.push_layer(builder_layer) ||
        !native_builder.save(builder_state_index) ||
        !native_builder.draw_analytic(
            builder_primitives,
            std::span<const std::uint32_t>(&builder_brush, 1U),
            {24.0F, 24.0F, 216.0F, 128.0F}) ||
        !native_builder.draw_geometry(
            std::span<const progpu_native_geometry_primitive>(
                &builder_line,
                1U),
            std::span<const std::uint32_t>(&builder_brush, 1U),
            {38.0F, 96.0F, 174.0F, 8.0F}) ||
        !native_builder.draw_strokes(
            std::span<const progpu_native_scene_stroke>(
                &builder_stroke,
                1U),
            builder_stroke_points,
            std::span<const double>(),
            std::span<const std::uint32_t>(&builder_brush, 1U),
            {38.0F, 108.0F, 164.0F, 28.0F}) ||
        !native_builder.draw_paths(
            std::span<const progpu_native_scene_path_fill>(
                &builder_path,
                1U),
            builder_path_segments,
            std::span<const std::uint32_t>(&builder_brush, 1U),
            {146.0F, 40.0F, 64.0F, 52.0F}) ||
        !native_builder.add_rgba8_image(
            2U,
            2U,
            8U,
            builder_image_pixels,
            builder_image_index) ||
        !native_builder.draw_image(
            builder_image_index,
            builder_image,
            {104.0F, 48.0F, 36.0F, 36.0F}) ||
        !native_builder.add_glyph_outlines(
            std::span<const progpu_native_scene_glyph_outline>(
                &builder_glyph_outline,
                1U),
            builder_glyph_segments,
            builder_glyph_resource) ||
        !native_builder.add_text_style(
            builder_text_style,
            builder_text_style_index) ||
        !native_builder.draw_glyph_run(
            builder_glyph_resource,
            std::span<const progpu_native_positioned_glyph>(
                &builder_glyph,
                1U),
            {64.0F, 60.0F, 18.0F, 22.0F},
            PROGPU_NATIVE_SCENE_NO_INDEX,
            builder_text_style_index) ||
        !native_builder.add_color_glyph_bitmaps(
            std::span<const progpu_native_scene_color_glyph_bitmap>(
                &builder_color_bitmap,
                1U),
            builder_image_pixels,
            builder_color_glyph_resource) ||
        !native_builder.draw_glyph_run(
            builder_color_glyph_resource,
            std::span<const progpu_native_positioned_glyph>(
                &builder_color_glyph,
                1U),
            {214.0F, 60.0F, 18.0F, 22.0F}) ||
        !native_builder.restore() ||
        !native_builder.pop_layer()) {
        fail("The browser native C++ scene builder could not record.");
    }
    std::vector<std::byte> builder_scene;
    if (!native_builder.build(builder_scene)) {
        fail("The browser native C++ scene builder could not compile.");
    }
    progpu_native_scene_metrics builder_update_metrics{};
    builder_update_metrics.struct_size = sizeof(builder_update_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            builder_scene.data(),
            builder_scene.size(),
            &builder_update_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        builder_update_metrics.command_count != 11U ||
        builder_update_metrics.resource_count != 12U ||
        builder_update_metrics.draw_count != 7U) {
        fail_engine("The browser native C++ scene update failed.");
    }
    semantic_frame.scene_id = native_builder.scene_id();
    semantic_frame.generation = native_builder.generation();
    progpu_native_scene_frame_metrics builder_frame_metrics{};
    builder_frame_metrics.struct_size = sizeof(builder_frame_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &builder_frame_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        builder_frame_metrics.command_count != 11U ||
        builder_frame_metrics.draw_call_count != 8U ||
        builder_frame_metrics.submission_count != 1U ||
        builder_frame_metrics.brush_upload_bytes == 0U ||
        builder_frame_metrics.vertex_upload_bytes == 0U ||
        builder_frame_metrics.texture_upload_bytes != 32U ||
        builder_frame_metrics.color_glyph_upload_bytes != 16U ||
        builder_frame_metrics.text_style_upload_bytes == 0U ||
        builder_frame_metrics.coverage_staging_bytes == 0U) {
        std::fprintf(
            stderr,
            "C++ builder metrics: commands=%" PRIu32
            " draws=%" PRIu32 " submissions=%" PRIu64
            " brush=%" PRIu64 " vertex=%" PRIu64
            " texture=%" PRIu64 " color_glyph=%" PRIu64
            " text=%" PRIu64
            " coverage=%" PRIu64 "\n",
            builder_frame_metrics.command_count,
            builder_frame_metrics.draw_call_count,
            builder_frame_metrics.submission_count,
            builder_frame_metrics.brush_upload_bytes,
            builder_frame_metrics.vertex_upload_bytes,
            builder_frame_metrics.texture_upload_bytes,
            builder_frame_metrics.color_glyph_upload_bytes,
            builder_frame_metrics.text_style_upload_bytes,
            builder_frame_metrics.coverage_staging_bytes);
        fail_engine("The browser native C++ scene render failed.");
    }
    progpu_native_layer_metrics builder_layer_metrics{};
    builder_layer_metrics.struct_size = sizeof(builder_layer_metrics);
    if (progpu_native_engine_get_layer_metrics(
            resources.engine,
            &builder_layer_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        builder_layer_metrics.mask_kind !=
            PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE ||
        builder_layer_metrics.content_pass_count != 1U ||
        builder_layer_metrics.composite_pass_count != 1U ||
        builder_layer_metrics.effect_count != 1U ||
        builder_layer_metrics.effect_pass_count != 2U) {
        fail("The browser C++ builder layer metrics are invalid.");
    }
    builder_frame_metrics = {};
    builder_frame_metrics.struct_size = sizeof(builder_frame_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &builder_frame_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        builder_frame_metrics.brush_upload_bytes != 0U ||
        builder_frame_metrics.vertex_upload_bytes != 0U ||
        builder_frame_metrics.index_upload_bytes != 0U ||
        builder_frame_metrics.texture_upload_bytes != 0U ||
        builder_frame_metrics.color_glyph_upload_bytes != 0U ||
        builder_frame_metrics.text_style_upload_bytes != 0U ||
        builder_frame_metrics.coverage_staging_bytes != 0U) {
        fail_engine("The stable browser native C++ scene was rebuilt.");
    }

    auto geometry_scene =
        progpu::native::tests::create_semantic_geometry_scene_stream(
            width,
            height);
    progpu_native_scene_metrics geometry_scene_metrics{};
    geometry_scene_metrics.struct_size = sizeof(geometry_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            geometry_scene.data(),
            geometry_scene.size(),
            &geometry_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        geometry_scene_metrics.command_count != 4U ||
        geometry_scene_metrics.resource_count != 6U ||
        geometry_scene_metrics.draw_count != 4U) {
        fail_engine("The browser retained geometry scene update failed.");
    }
    semantic_frame.scene_id = 101U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics geometry_metrics{};
    geometry_metrics.struct_size = sizeof(geometry_metrics);
    const auto geometry_status = progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &geometry_metrics);
    if (geometry_status != PROGPU_NATIVE_STATUS_SUCCESS ||
        geometry_metrics.command_count != 4U ||
        geometry_metrics.draw_call_count != 4U ||
        geometry_metrics.submission_count != 1U ||
        geometry_metrics.brush_upload_bytes !=
            3U * sizeof(progpu_native_scene_brush) ||
        geometry_metrics.vertex_upload_bytes == 0U) {
        std::fprintf(
            stderr,
            "ProGPU browser geometry metrics: status=%u commands=%u "
            "draws=%u submissions=%" PRIu64 " brushes=%" PRIu64
            " vertices=%" PRIu64 "\n",
            static_cast<unsigned>(geometry_status),
            geometry_metrics.command_count,
            geometry_metrics.draw_call_count,
            geometry_metrics.submission_count,
            geometry_metrics.brush_upload_bytes,
            geometry_metrics.vertex_upload_bytes);
        fail_engine("The browser retained geometry render failed.");
    }
    geometry_metrics = {};
    geometry_metrics.struct_size = sizeof(geometry_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &geometry_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        geometry_metrics.brush_upload_bytes != 0U ||
        geometry_metrics.vertex_upload_bytes != 0U ||
        geometry_metrics.index_upload_bytes != 0U) {
        fail_engine("The stable browser geometry page was rebuilt.");
    }

    auto color_glyph_scene =
        progpu::native::tests::create_semantic_color_glyph_scene_stream(
            width,
            height);
    progpu_native_scene_metrics color_scene_metrics{};
    color_scene_metrics.struct_size = sizeof(color_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            color_glyph_scene.data(),
            color_glyph_scene.size(),
            &color_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        color_scene_metrics.command_count != 3U ||
        color_scene_metrics.resource_count != 5U ||
        color_scene_metrics.draw_count != 3U) {
        fail_engine(
            "The ProGPU C++ browser color-glyph scene update failed.");
    }
    semantic_frame.scene_id = 97U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics color_metrics{};
    color_metrics.struct_size = sizeof(color_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &color_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        color_metrics.command_count != 3U ||
        color_metrics.draw_call_count != 3U ||
        color_metrics.submission_count != 1U ||
        color_metrics.color_glyph_upload_bytes != 16U) {
        fail_engine(
            "The ProGPU C++ browser color-glyph render failed.");
    }
    color_metrics = {};
    color_metrics.struct_size = sizeof(color_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &color_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        color_metrics.color_glyph_upload_bytes != 0U ||
        color_metrics.vertex_upload_bytes != 0U ||
        color_metrics.coverage_staging_bytes != 0U) {
        fail_engine(
            "The stable browser color-glyph page was rebuilt.");
    }

    auto cubic_image_scene =
        progpu::native::tests::create_semantic_cubic_image_scene_stream(
            width,
            height);
    progpu_native_scene_metrics image_scene_metrics{};
    image_scene_metrics.struct_size = sizeof(image_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            cubic_image_scene.data(),
            cubic_image_scene.size(),
            &image_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        image_scene_metrics.command_count != 1U ||
        image_scene_metrics.resource_count != 1U ||
        image_scene_metrics.draw_count != 1U) {
        fail_engine("The browser cubic image scene update failed.");
    }
    semantic_frame.scene_id = 96U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics image_metrics{};
    image_metrics.struct_size = sizeof(image_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &image_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        image_metrics.draw_call_count != 1U ||
        image_metrics.submission_count != 1U ||
        image_metrics.texture_upload_bytes != 16U ||
        image_metrics.uniform_upload_bytes <
            sizeof(progpu_native_scene_image_color_matrix)) {
        fail_engine("The browser cubic image render failed.");
    }
    image_metrics = {};
    image_metrics.struct_size = sizeof(image_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &image_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        image_metrics.texture_upload_bytes != 0U ||
        image_metrics.vertex_upload_bytes != 0U ||
        image_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser cubic image page was rebuilt.");
    }

    auto text_scene =
        progpu::native::tests::create_semantic_text_scene_stream(
            width,
            height);
    progpu_native_scene_metrics text_scene_metrics{};
    text_scene_metrics.struct_size = sizeof(text_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            text_scene.data(),
            text_scene.size(),
            &text_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        text_scene_metrics.command_count != 1U ||
        text_scene_metrics.resource_count != 2U ||
        text_scene_metrics.draw_count != 1U) {
        fail_engine(
            "The ProGPU C++ browser retained text scene update failed.");
    }
    semantic_frame.scene_id = 99U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics text_metrics{};
    text_metrics.struct_size = sizeof(text_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &text_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        text_metrics.command_count != 1U ||
        text_metrics.draw_call_count != 1U ||
        text_metrics.submission_count != 1U ||
        text_metrics.text_style_upload_bytes !=
            2U * sizeof(progpu_native_scene_text_style)) {
        fail_engine(
            "The ProGPU C++ browser retained text render failed.");
    }
    text_metrics = {};
    text_metrics.struct_size = sizeof(text_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &text_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        text_metrics.text_style_upload_bytes != 0U ||
        text_metrics.vertex_upload_bytes != 0U ||
        text_metrics.coverage_staging_bytes != 0U) {
        fail_engine(
            "The stable browser retained text page was rebuilt.");
    }

    auto backdrop_scene =
        progpu::native::tests::create_semantic_backdrop_scene_stream(
            width,
            height);
    progpu_native_scene_metrics backdrop_scene_metrics{};
    backdrop_scene_metrics.struct_size = sizeof(backdrop_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            backdrop_scene.data(),
            backdrop_scene.size(),
            &backdrop_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS) {
        fail_engine(
            "The ProGPU C++ browser backdrop scene restore failed.");
    }
    semantic_frame.scene_id = 98U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics semantic_metrics{};
    semantic_metrics.struct_size = sizeof(semantic_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &semantic_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        semantic_metrics.command_count != 6U ||
        semantic_metrics.draw_call_count != 6U ||
        semantic_metrics.submission_count != 1U ||
        semantic_metrics.brush_upload_bytes !=
            4U * sizeof(progpu_native_scene_brush) ||
        semantic_metrics.gradient_stop_upload_bytes !=
            3U * sizeof(progpu_native_scene_gradient_stop)) {
        fail_engine(
            "The ProGPU C++ browser semantic backdrop render failed.");
    }
    progpu_native_layer_metrics layer_metrics{};
    layer_metrics.struct_size = sizeof(layer_metrics);
    if (progpu_native_engine_get_layer_metrics(
            resources.engine,
            &layer_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        layer_metrics.content_pass_count != 2U ||
        layer_metrics.composite_pass_count != 2U ||
        layer_metrics.effect_count != 1U ||
        layer_metrics.effect_pass_count != 2U ||
        layer_metrics.effect_cache_hit != 0U ||
        layer_metrics.texture_bytes == 0U) {
        fail("The ProGPU C++ browser semantic layer metrics are invalid.");
    }
    progpu_native_scene_frame_metrics stable_metrics{};
    stable_metrics.struct_size = sizeof(stable_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &stable_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        stable_metrics.brush_upload_bytes != 0U ||
        stable_metrics.gradient_stop_upload_bytes != 0U) {
        fail_engine(
            "The stable browser semantic brush page was uploaded again.");
    }

    auto rounded_scene =
        progpu::native::tests::create_semantic_rounded_mask_scene_stream(
            width,
            height);
    progpu_native_scene_metrics rounded_scene_metrics{};
    rounded_scene_metrics.struct_size = sizeof(rounded_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            rounded_scene.data(),
            rounded_scene.size(),
            &rounded_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        rounded_scene_metrics.command_count != 3U ||
        rounded_scene_metrics.resource_count != 2U ||
        rounded_scene_metrics.draw_count != 1U) {
        fail_engine("The browser rounded-mask scene update failed.");
    }
    semantic_frame.scene_id = 101U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics rounded_metrics{};
    rounded_metrics.struct_size = sizeof(rounded_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &rounded_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        rounded_metrics.command_count != 3U ||
        rounded_metrics.draw_call_count != 2U ||
        rounded_metrics.submission_count != 1U ||
        rounded_metrics.texture_upload_bytes != 0U ||
        rounded_metrics.uniform_upload_bytes < 24U * sizeof(float)) {
        fail_engine("The browser rounded-mask render failed.");
    }
    progpu_native_layer_metrics rounded_layer_metrics{};
    rounded_layer_metrics.struct_size = sizeof(rounded_layer_metrics);
    if (progpu_native_engine_get_layer_metrics(
            resources.engine,
            &rounded_layer_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        rounded_layer_metrics.mask_kind !=
            PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE ||
        rounded_layer_metrics.content_pass_count != 1U ||
        rounded_layer_metrics.composite_pass_count != 1U ||
        rounded_layer_metrics.mask_uniform_upload_bytes == 0U) {
        fail_engine("The browser rounded-mask metrics are invalid.");
    }
    rounded_metrics = {};
    rounded_metrics.struct_size = sizeof(rounded_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &rounded_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        rounded_metrics.texture_upload_bytes != 0U ||
        rounded_metrics.vertex_upload_bytes != 0U ||
        rounded_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser rounded mask was rebuilt.");
    }

    auto state_mask_scene =
        progpu::native::tests::create_semantic_state_mask_scene_stream(
            width,
            height);
    progpu_native_scene_metrics state_mask_scene_metrics{};
    state_mask_scene_metrics.struct_size = sizeof(state_mask_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            state_mask_scene.data(),
            state_mask_scene.size(),
            &state_mask_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_scene_metrics.command_count != 3U ||
        state_mask_scene_metrics.resource_count != 3U ||
        state_mask_scene_metrics.draw_count != 1U) {
        fail_engine("The browser per-draw mask scene update failed.");
    }
    semantic_frame.scene_id = 102U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics state_mask_metrics{};
    state_mask_metrics.struct_size = sizeof(state_mask_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &state_mask_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_metrics.command_count != 3U ||
        state_mask_metrics.draw_call_count != 1U ||
        state_mask_metrics.submission_count != 1U ||
        state_mask_metrics.texture_upload_bytes != 0U ||
        state_mask_metrics.uniform_upload_bytes <
            24U * sizeof(float)) {
        fail_engine("The browser per-draw mask render failed.");
    }
    progpu_native_layer_metrics state_mask_layer_metrics{};
    state_mask_layer_metrics.struct_size =
        sizeof(state_mask_layer_metrics);
    if (progpu_native_engine_get_layer_metrics(
            resources.engine,
            &state_mask_layer_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_layer_metrics.mask_kind !=
            PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE ||
        state_mask_layer_metrics.content_pass_count != 0U ||
        state_mask_layer_metrics.composite_pass_count != 0U ||
        state_mask_layer_metrics.mask_uniform_upload_bytes !=
            4U * 24U * sizeof(float)) {
        fail_engine("The browser per-draw mask metrics are invalid.");
    }
    state_mask_metrics = {};
    state_mask_metrics.struct_size = sizeof(state_mask_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &state_mask_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_metrics.texture_upload_bytes != 0U ||
        state_mask_metrics.vertex_upload_bytes != 0U ||
        state_mask_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser per-draw mask was rebuilt.");
    }

    auto state_mask_media_scene =
        progpu::native::tests::create_semantic_state_mask_media_scene_stream(
            width,
            height);
    progpu_native_scene_metrics state_mask_media_scene_metrics{};
    state_mask_media_scene_metrics.struct_size =
        sizeof(state_mask_media_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            state_mask_media_scene.data(),
            state_mask_media_scene.size(),
            &state_mask_media_scene_metrics) !=
                PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_media_scene_metrics.command_count != 2U ||
        state_mask_media_scene_metrics.resource_count != 4U ||
        state_mask_media_scene_metrics.draw_count != 2U) {
        fail_engine("The browser masked glyph/image scene update failed.");
    }
    semantic_frame.scene_id = 103U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics state_mask_media_metrics{};
    state_mask_media_metrics.struct_size =
        sizeof(state_mask_media_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &state_mask_media_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_media_metrics.command_count != 2U ||
        state_mask_media_metrics.draw_call_count != 2U ||
        state_mask_media_metrics.submission_count != 1U ||
        state_mask_media_metrics.texture_upload_bytes != 96U ||
        state_mask_media_metrics.color_glyph_upload_bytes != 16U ||
        state_mask_media_metrics.uniform_upload_bytes <
            24U * sizeof(float)) {
        fail_engine("The browser masked glyph/image render failed.");
    }
    progpu_native_layer_metrics state_mask_media_layer_metrics{};
    state_mask_media_layer_metrics.struct_size =
        sizeof(state_mask_media_layer_metrics);
    if (progpu_native_engine_get_layer_metrics(
            resources.engine,
            &state_mask_media_layer_metrics) !=
                PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_media_layer_metrics.mask_kind !=
            PROGPU_NATIVE_GROUP_MASK_TEXTURE ||
        state_mask_media_layer_metrics.content_pass_count != 0U ||
        state_mask_media_layer_metrics.composite_pass_count != 0U ||
        state_mask_media_layer_metrics.mask_uniform_upload_bytes !=
            24U * sizeof(float)) {
        fail_engine("The browser masked glyph/image metrics are invalid.");
    }
    state_mask_media_metrics = {};
    state_mask_media_metrics.struct_size =
        sizeof(state_mask_media_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &state_mask_media_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_media_metrics.texture_upload_bytes != 0U ||
        state_mask_media_metrics.vertex_upload_bytes != 0U ||
        state_mask_media_metrics.uniform_upload_bytes != 0U ||
        state_mask_media_metrics.color_glyph_upload_bytes != 0U) {
        fail_engine("The stable browser masked glyph/image page was rebuilt.");
    }

    auto mask_chain_media_scene = progpu::native::tests::
        create_semantic_state_mask_chain_media_scene_stream(width, height);
    state_mask_media_scene_metrics = {};
    state_mask_media_scene_metrics.struct_size =
        sizeof(state_mask_media_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            mask_chain_media_scene.data(),
            mask_chain_media_scene.size(),
            &state_mask_media_scene_metrics) !=
                PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_media_scene_metrics.command_count != 2U ||
        state_mask_media_scene_metrics.resource_count != 4U ||
        state_mask_media_scene_metrics.draw_count != 2U) {
        fail_engine("The browser mask-chain glyph/image scene update failed.");
    }
    semantic_frame.scene_id = 104U;
    state_mask_media_metrics = {};
    state_mask_media_metrics.struct_size =
        sizeof(state_mask_media_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &state_mask_media_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_media_metrics.draw_call_count != 2U ||
        state_mask_media_metrics.texture_upload_bytes != 32U ||
        state_mask_media_metrics.color_glyph_upload_bytes != 16U ||
        state_mask_media_metrics.uniform_upload_bytes <
            4U * 24U * sizeof(float)) {
        fail_engine("The browser mask-chain glyph/image render failed.");
    }
    state_mask_media_layer_metrics = {};
    state_mask_media_layer_metrics.struct_size =
        sizeof(state_mask_media_layer_metrics);
    if (progpu_native_engine_get_layer_metrics(
            resources.engine,
            &state_mask_media_layer_metrics) !=
                PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_media_layer_metrics.mask_kind !=
            PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE ||
        state_mask_media_layer_metrics.mask_uniform_upload_bytes !=
            4U * 24U * sizeof(float)) {
        fail_engine("The browser mask-chain glyph/image metrics are invalid.");
    }
    state_mask_media_metrics = {};
    state_mask_media_metrics.struct_size =
        sizeof(state_mask_media_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &state_mask_media_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_media_metrics.texture_upload_bytes != 0U ||
        state_mask_media_metrics.color_glyph_upload_bytes != 0U ||
        state_mask_media_metrics.vertex_upload_bytes != 0U ||
        state_mask_media_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser mask-chain glyph/image page was rebuilt.");
    }

    auto coverage_scene =
        progpu::native::tests::create_semantic_coverage_mask_scene_stream(
            width,
            height);
    progpu_native_scene_metrics coverage_scene_metrics{};
    coverage_scene_metrics.struct_size = sizeof(coverage_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            coverage_scene.data(),
            coverage_scene.size(),
            &coverage_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_scene_metrics.command_count != 3U ||
        coverage_scene_metrics.resource_count != 2U ||
        coverage_scene_metrics.draw_count != 1U) {
        fail_engine("The browser coverage-mask scene update failed.");
    }
    semantic_frame.scene_id = 100U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics coverage_metrics{};
    coverage_metrics.struct_size = sizeof(coverage_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_metrics.command_count != 3U ||
        coverage_metrics.draw_call_count != 2U ||
        coverage_metrics.submission_count != 1U ||
        coverage_metrics.texture_upload_bytes != 64U ||
        coverage_metrics.uniform_upload_bytes <
            24U * sizeof(float)) {
        fail_engine("The browser coverage-mask render failed.");
    }
    coverage_metrics = {};
    coverage_metrics.struct_size = sizeof(coverage_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_metrics.texture_upload_bytes != 0U ||
        coverage_metrics.vertex_upload_bytes != 0U ||
        coverage_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser coverage mask was rebuilt.");
    }

    if (progpu_native_engine_mark_device_lost(resources.engine) !=
            PROGPU_NATIVE_STATUS_SUCCESS ||
        progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_DEVICE_LOST) {
        fail_engine("The browser device-loss gate did not fail closed.");
    }
    progpu_native_browser_engine_options replacement_options{};
    replacement_options.struct_size = sizeof(replacement_options);
    replacement_options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    replacement_options.adapter_abi_version =
        PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION;
    replacement_options.target_format =
        PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    replacement_options.device =
        reinterpret_cast<std::uintptr_t>(resources.device);
    replacement_options.queue =
        reinterpret_cast<std::uintptr_t>(resources.queue);
    progpu_native_engine* replacement = nullptr;
    if (progpu_native_browser_engine_recreate(
            resources.engine,
            &replacement_options,
            &replacement) != PROGPU_NATIVE_STATUS_SUCCESS ||
        replacement == nullptr) {
        fail_engine("The browser engine could not be recreated.");
    }
    progpu_native_engine_destroy(resources.engine);
    resources.engine = replacement;
    coverage_metrics = {};
    coverage_metrics.struct_size = sizeof(coverage_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_metrics.texture_upload_bytes != 64U ||
        coverage_metrics.vertex_upload_bytes == 0U ||
        coverage_metrics.uniform_upload_bytes == 0U) {
        fail_engine("The recreated browser engine did not rebuild the retained scene.");
    }
    coverage_metrics = {};
    coverage_metrics.struct_size = sizeof(coverage_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &coverage_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        coverage_metrics.texture_upload_bytes != 0U ||
        coverage_metrics.vertex_upload_bytes != 0U ||
        coverage_metrics.uniform_upload_bytes != 0U) {
        fail_engine("Stable browser replay after device recovery rebuilt resources.");
    }
    if (progpu_native_engine_update_scene(
            resources.engine,
            state_mask_media_scene.data(),
            state_mask_media_scene.size(),
            &state_mask_media_scene_metrics) !=
                PROGPU_NATIVE_STATUS_SUCCESS) {
        fail_engine("The browser evidence glyph/image scene could not be restored.");
    }
    semantic_frame.scene_id = 103U;
    semantic_frame.generation = 1U;
    state_mask_media_metrics = {};
    state_mask_media_metrics.struct_size =
        sizeof(state_mask_media_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &state_mask_media_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        state_mask_media_metrics.draw_call_count != 2U ||
        state_mask_media_metrics.texture_upload_bytes != 96U) {
        fail_engine("The browser evidence glyph/image scene did not render.");
    }
    resources.render_texture = render_texture;
    resources.render_view = render_view;
    if (!progpu::native::browser::begin_evidence_readback(
            resources.device,
            resources.queue,
            resources.render_texture,
            width,
            height,
            finish_browser_evidence)) {
        fail("The browser WebGPU evidence copy could not be scheduled.");
    }
    return false;
}

} // namespace

int main() {
    progpu_native_engine_info info{};
    info.struct_size = sizeof(info);
    if (progpu_native_get_info(&info) == 0U ||
        info.abi_version != PROGPU_NATIVE_ABI_VERSION ||
        info.backend_abi !=
            PROGPU_NATIVE_BACKEND_ABI_BROWSER_WEBGPU_2025_10 ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_SEMANTIC_RETAINED_TEXT_STYLES) == 0U ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_SEMANTIC_GEOMETRY_BATCH) == 0U ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_DEVICE_LOSS_RECREATION) == 0U ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_EXPLICIT_QUEUE_TIMELINE) != 0U) {
        fail("The ProGPU browser ABI/capability contract is invalid.");
    }

    WGPUDevice device = emscripten_webgpu_get_device();
    if (device == nullptr) {
        fail("The browser did not provide a WebGPU device.");
    }
    WGPUQueue queue = wgpuDeviceGetQueue(device);
    if (queue == nullptr) {
        fail("The Emdawnwebgpu queue is unavailable.");
    }

    progpu_native_browser_engine_options options{};
    options.struct_size = sizeof(options);
    options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    options.adapter_abi_version =
        PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION;
    options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    options.device = reinterpret_cast<std::uintptr_t>(device);
    options.queue = reinterpret_cast<std::uintptr_t>(queue);
    progpu_native_engine* engine = nullptr;
    if (progpu_native_browser_engine_create(&options, &engine) !=
            PROGPU_NATIVE_STATUS_SUCCESS ||
        engine == nullptr) {
        fail("The ProGPU C++ browser engine could not be created.");
    }

    auto semantic_scene =
        progpu::native::tests::create_semantic_backdrop_scene_stream(
            width,
            height);
    progpu_native_scene_metrics scene_metrics{};
    scene_metrics.struct_size = sizeof(scene_metrics);
    if (progpu_native_engine_update_scene(
            engine,
            semantic_scene.data(),
            semantic_scene.size(),
            &scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        scene_metrics.command_count != 6U ||
        scene_metrics.resource_count != 4U ||
        scene_metrics.draw_count != 2U ||
        scene_metrics.maximum_stack_depth != 1U) {
        fail("The ProGPU C++ browser semantic scene update failed.");
    }
    resources = {
        engine,
        device,
        queue,
        nullptr,
        nullptr};
    if (emscripten_request_animation_frame(
            render_browser_frame,
            nullptr) < 0) {
        fail("The browser render frame could not be scheduled.");
    }
    return 0;
}
