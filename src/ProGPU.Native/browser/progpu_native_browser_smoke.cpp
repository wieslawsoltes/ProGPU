#include "progpu_native_browser.h"
#include "progpu_native_browser_evidence.hpp"
#include "progpu_native_geometry_base.hpp"
#include "progpu_native_image.hpp"
#include "progpu_native_scene_builder.hpp"
#include "progpu_native_semantic_backdrop_scene.hpp"
#include "progpu_native_semantic_color_glyph_scene.hpp"
#include "progpu_native_semantic_coverage_mask_scene.hpp"
#include "progpu_native_semantic_brush_mask_scene.hpp"
#include "progpu_native_semantic_geometry_scene.hpp"
#include "progpu_native_semantic_image_scene.hpp"
#include "progpu_native_semantic_rounded_mask_scene.hpp"
#include "progpu_native_semantic_state_mask_scene.hpp"
#include "progpu_native_semantic_text_scene.hpp"
#include "progpu_native_text.hpp"

#include <emscripten.h>
#include <emscripten/html5.h>
#include <webgpu/webgpu.h>

#include <array>
#include <cinttypes>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <span>
#include <vector>

namespace {

constexpr std::uint32_t width = 640U;
constexpr std::uint32_t height = 360U;
std::uint32_t physical_width = width;
std::uint32_t physical_height = height;
float display_scale = 1.0F;

struct browser_resources final {
    progpu_native_engine* engine = nullptr;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    WGPUTexture render_texture = nullptr;
    WGPUTextureView render_view = nullptr;
};

browser_resources resources{};

void initialize_display_metrics() {
    const double requested_scale = EM_ASM_DOUBLE({
        const scale = Number(globalThis.devicePixelRatio) || 1;
        return Math.min(4, Math.max(1, scale));
    });
    display_scale = static_cast<float>(requested_scale);
    physical_width = static_cast<std::uint32_t>(std::lround(
        static_cast<double>(width) * requested_scale));
    physical_height = static_cast<std::uint32_t>(std::lround(
        static_cast<double>(height) * requested_scale));
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdollar-in-identifier-extension"
    EM_ASM({
        const width = $0;
        const height = $1;
        const scale = $2;
        for (const id of [
            "progpu-native-canvas",
            "progpu-native-evidence"
        ]) {
            const canvas = document.getElementById(id);
            canvas.width = width;
            canvas.height = height;
        }
        document.body.dataset.progpuNativeBackingWidth = String(width);
        document.body.dataset.progpuNativeBackingHeight = String(height);
        document.body.dataset.progpuNativeDpiScale = String(scale);
    }, physical_width, physical_height, requested_scale);
#pragma clang diagnostic pop
}

bool verify_native_png_decode() {
    constexpr std::array png{
        std::byte{0x89U}, std::byte{0x50U}, std::byte{0x4eU}, std::byte{0x47U},
        std::byte{0x0dU}, std::byte{0x0aU}, std::byte{0x1aU}, std::byte{0x0aU},
        std::byte{0x00U}, std::byte{0x00U}, std::byte{0x00U}, std::byte{0x0dU},
        std::byte{0x49U}, std::byte{0x48U}, std::byte{0x44U}, std::byte{0x52U},
        std::byte{0x00U}, std::byte{0x00U}, std::byte{0x00U}, std::byte{0x01U},
        std::byte{0x00U}, std::byte{0x00U}, std::byte{0x00U}, std::byte{0x01U},
        std::byte{0x08U}, std::byte{0x06U}, std::byte{0x00U}, std::byte{0x00U},
        std::byte{0x00U}, std::byte{0x1fU}, std::byte{0x15U}, std::byte{0xc4U},
        std::byte{0x89U}, std::byte{0x00U}, std::byte{0x00U}, std::byte{0x00U},
        std::byte{0x0dU}, std::byte{0x49U}, std::byte{0x44U}, std::byte{0x41U},
        std::byte{0x54U}, std::byte{0x78U}, std::byte{0x01U}, std::byte{0x63U},
        std::byte{0xe0U}, std::byte{0x12U}, std::byte{0x91U}, std::byte{0xd3U},
        std::byte{0x00U}, std::byte{0x00U}, std::byte{0x00U}, std::byte{0xcdU},
        std::byte{0x00U}, std::byte{0x65U}, std::byte{0x98U}, std::byte{0xe9U},
        std::byte{0x07U}, std::byte{0xb0U}, std::byte{0x00U}, std::byte{0x00U},
        std::byte{0x00U}, std::byte{0x00U}, std::byte{0x49U}, std::byte{0x45U},
        std::byte{0x4eU}, std::byte{0x44U}, std::byte{0xaeU}, std::byte{0x42U},
        std::byte{0x60U}, std::byte{0x82U}};
    std::array<std::byte, 13U> compressed{};
    std::array<std::byte, 5U> filtered{};
    std::array<std::byte, 4U> rgba{};
    progpu::native::image::png_decode_requirements requirements{};
    progpu::native::image::image_error error{};
    return progpu::native::image::try_decode_png_rgba(
            png, compressed, filtered, rgba, requirements, &error) &&
        requirements.width == 1U && requirements.height == 1U &&
        requirements.color_type == 6U &&
        rgba == std::array{
            std::byte{10U}, std::byte{20U},
            std::byte{30U}, std::byte{40U}};
}

bool verify_native_text_feature_plan() {
    using namespace progpu::native::text;
    const auto defaults = get_default_open_type_feature_settings();
    const open_type_shaping_route route{
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        open_type_tag::from_chars('l', 'a', 't', 'n'),
        shaping_direction::left_to_right};
    open_type_feature_plan_requirements requirements{};
    font_error error{};
    if (!try_get_open_type_feature_plan_requirements(
            route, defaults, requirements, &error) ||
        requirements.requested_feature_capacity != 28U) {
        return false;
    }
    std::array<open_type_tag, 28U> requested{};
    std::array<shaping_feature, 28U> settings{};
    std::uint32_t requested_written = 0U;
    std::uint32_t settings_written = 0U;
    return try_resolve_open_type_feature_plan(
            route,
            defaults,
            {},
            requested,
            settings,
            requested_written,
            settings_written,
            &error) &&
        requested_written == 28U && settings_written == 1U &&
        requested[0U] == open_type_tag::from_chars('l', 't', 'r', 'a') &&
        requested[1U] == open_type_tag::from_chars('l', 't', 'r', 'm') &&
        settings[0U].tag == open_type_tag::from_chars('r', 'a', 'n', 'd') &&
        settings[0U].value == 0xFFFFU;
}

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
        document.body.dataset.progpuNativeDirectImageSampling = "passed";
        document.body.dataset.progpuNativeCoverageMasks = "passed";
        document.body.dataset.progpuNativeRoundedMasks = "passed";
        document.body.dataset.progpuNativeStateMasks = "passed";
        document.body.dataset.progpuNativeStateMaskMedia = "passed";
        document.body.dataset.progpuNativeVectorClipMasks = "passed";
        document.body.dataset.progpuNativeCompositeGeometryMasks = "passed";
        document.body.dataset.progpuNativeSemanticGeometry = "passed";
        document.body.dataset.progpuNativeSceneBuilder = "passed";
        document.body.dataset.progpuNativePngDecode = "passed";
        document.body.dataset.progpuNativeIncrementalUpdate = "passed";
        document.body.dataset.progpuNativeDeviceRecovery = "passed";
        document.body.dataset.progpuNativeEvidenceTarget =
            "offscreen-texture-readback";
        document.body.dataset.progpuNativeBackendAbi = "3";
        document.body.dataset.progpuNativeExplicitTimeline = "0";
        document.getElementById("native-status").textContent =
            "C++ / WebGPU backend active — retained scenes, GPU vector masks, and direct image sampling verified";
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

bool render_browser_frame(double, void*);

std::uint64_t browser_hit_test_token = 0U;

bool poll_browser_hit_test(double, void*) {
    EM_ASM({ document.body.dataset.progpuNativeStage = "hit-test-poll"; });
    std::array<progpu_native_hit_test_result, 1U> hits{};
    progpu_native_hit_test_result summary{};
    std::uint32_t hit_count = 0U;
    std::uint8_t complete = 0U;
    if (progpu_native_engine_poll_hit_test(
            resources.engine,
            browser_hit_test_token,
            hits.data(),
            hits.size(),
            &hit_count,
            &summary,
            &complete) != PROGPU_NATIVE_STATUS_SUCCESS) {
        fail_engine("The browser retained GPU hit-test poll failed.");
    }
    if (complete == 0U) {
        if (emscripten_request_animation_frame(
                poll_browser_hit_test,
                nullptr) < 0) {
            fail("The next browser hit-test poll could not be scheduled.");
        }
        return false;
    }
    if (summary.hit != 1U || summary.candidate_count != 1U ||
        summary.nodes_visited != 1U || summary.precise_tests != 1U ||
        hit_count != 1U || hits[0U].hit == 0U ||
        hits[0U].id != 501 || hits[0U].primitive_index != 0U) {
        fail("The browser retained GPU hit-test result diverged.");
    }
    EM_ASM({
        document.body.dataset.progpuNativeGpuHitTesting = "passed";
        document.body.dataset.progpuNativeStage = "render-workload";
    });
    if (emscripten_request_animation_frame(
            render_browser_frame,
            nullptr) < 0) {
        fail("The browser render frame could not be scheduled after hit testing.");
    }
    return false;
}

bool begin_browser_hit_test() {
    EM_ASM({ document.body.dataset.progpuNativeStage = "hit-test-build"; });
    progpu_native_hit_test_primitive primitive{};
    primitive.bounds_min = {48.0F, 48.0F};
    primitive.bounds_max = {228.0F, 168.0F};
    primitive.data0 = {48.0F, 48.0F, 228.0F, 168.0F};
    primitive.inverse_transform0 = {1.0F, 0.0F, 0.0F, 0.0F};
    primitive.inverse_transform1 = {0.0F, 1.0F, 0.0F, 0.0F};
    primitive.kind = PROGPU_NATIVE_HIT_TEST_RECTANGLE_FILL;
    primitive.flags = PROGPU_NATIVE_HIT_TEST_VISIBLE |
        PROGPU_NATIVE_HIT_TEST_VISIBLE_TO_INPUT;
    primitive.id = 501;
    primitive.z_index = 1.0F;
    const progpu_native_hit_test_node node{
        {48.0F, 48.0F},
        {228.0F, 168.0F},
        0U,
        0U,
        0U,
        1U};
    constexpr std::uint32_t primitive_index = 0U;
    progpu::native::semantic_scene_builder builder(711U, 1U);
    std::uint32_t hit_test_resource = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_hit_test_index(
            std::span<const progpu_native_hit_test_primitive>(&primitive, 1U),
            std::span<const progpu_native_hit_test_node>(&node, 1U),
            std::span<const std::uint32_t>(&primitive_index, 1U),
            {},
            hit_test_resource)) {
        return false;
    }
    std::vector<std::byte> stream(builder.required_stream_size());
    std::size_t bytes_written = 0U;
    if (stream.empty() ||
        !builder.build_into(stream, bytes_written) ||
        bytes_written != stream.size()) {
        return false;
    }
    progpu_native_scene_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            stream.data(),
            stream.size(),
            &metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        metrics.command_count != 0U || metrics.resource_count != 1U) {
        return false;
    }
    EM_ASM({ document.body.dataset.progpuNativeStage = "hit-test-begin"; });
    progpu_native_hit_test_query query{};
    query.point = {80.0F, 80.0F};
    query.region_max = query.point;
    query.flags = 1U;
    if (progpu_native_engine_begin_hit_test(
            resources.engine,
            &query,
            &browser_hit_test_token) != PROGPU_NATIVE_STATUS_SUCCESS ||
        browser_hit_test_token == 0U) {
        return false;
    }
    EM_ASM({ document.body.dataset.progpuNativeStage = "hit-test-wait"; });
    if (emscripten_request_animation_frame(
            poll_browser_hit_test,
            nullptr) < 0) {
        return false;
    }
    return true;
}

bool render_browser_frame(double, void*) {
    WGPUTexture render_texture = nullptr;
    WGPUTextureView render_view = nullptr;
    if (!progpu::native::browser::create_evidence_target(
            resources.device,
            WGPUTextureFormat_BGRA8Unorm,
            physical_width,
            physical_height,
            &render_texture,
            &render_view)) {
        fail("The browser WebGPU render target could not be created.");
    }

    constexpr std::array<std::uint8_t, 16U> direct_image_pixels{
        255U, 0U, 0U, 255U,
        0U, 255U, 0U, 255U,
        0U, 0U, 255U, 255U,
        255U, 255U, 255U, 255U};
    progpu_native_image_frame direct_image{};
    direct_image.struct_size = sizeof(direct_image);
    direct_image.width = physical_width;
    direct_image.height = physical_height;
    direct_image.dpi_scale = display_scale;
    direct_image.target_view =
        reinterpret_cast<std::uintptr_t>(render_view);
    direct_image.clear_color = {0.01F, 0.015F, 0.03F, 1.0F};
    direct_image.rgba_pixels = direct_image_pixels.data();
    direct_image.pixel_bytes = direct_image_pixels.size();
    direct_image.image_width = 2U;
    direct_image.image_height = 2U;
    direct_image.row_bytes = 8U;
    direct_image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP;
    direct_image.image_revision = 1U;
    direct_image.content_revision = 1U;
    direct_image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    direct_image.destination_rect =
        {16.0F, 16.0F, 224.0F, 128.0F};
    direct_image.transform =
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    direct_image.opacity = 1.0F;
    direct_image.cubic_c = 0.5F;
    direct_image.max_anisotropy = 8U;
    progpu_native_image_frame_metrics direct_image_metrics{};
    direct_image_metrics.struct_size = sizeof(direct_image_metrics);
    if (progpu_native_engine_render_image(
            resources.engine,
            &direct_image,
            &direct_image_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        direct_image_metrics.draw_call_count != 1U ||
        direct_image_metrics.texture_upload_bytes !=
            direct_image_pixels.size() ||
        direct_image_metrics.vertex_upload_bytes == 0U) {
        fail_engine(
            "The browser direct image mipmap sampler render failed.");
    }
    direct_image_metrics = {};
    direct_image_metrics.struct_size = sizeof(direct_image_metrics);
    if (progpu_native_engine_render_image(
            resources.engine,
            &direct_image,
            &direct_image_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        direct_image_metrics.texture_upload_bytes != 0U ||
        direct_image_metrics.vertex_upload_bytes != 0U) {
        fail_engine(
            "The browser stable direct image replay rebuilt resources.");
    }

    progpu_native_scene_frame semantic_frame{};
    semantic_frame.struct_size = sizeof(semantic_frame);
    semantic_frame.width = physical_width;
    semantic_frame.height = physical_height;
    semantic_frame.dpi_scale = display_scale;
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
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {163.0F, 58.0F}, {187.0F, 64.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {187.0F, 64.0F}, {166.0F, 75.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {166.0F, 75.0F}, {163.0F, 58.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const std::array builder_path_boolean_nodes{
        progpu_native_scene_path_boolean_node{
            0U, 3U, 150.0F, 44.0F, 206.0F, 88.0F,
            PROGPU_NATIVE_FILL_RULE_NON_ZERO,
            PROGPU_NATIVE_PATH_BOOLEAN_LEAF, 0U, 0U},
        progpu_native_scene_path_boolean_node{
            3U, 3U, 163.0F, 58.0F, 187.0F, 75.0F,
            PROGPU_NATIVE_FILL_RULE_NON_ZERO,
            PROGPU_NATIVE_PATH_BOOLEAN_LEAF, 0U, 0U},
        progpu_native_scene_path_boolean_node{
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F,
            PROGPU_NATIVE_FILL_RULE_NON_ZERO,
            PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE, 0U, 0U}};
    const progpu_native_scene_path_fill builder_path{
        0U,
        builder_path_segments.size(),
        0U,
        builder_path_boolean_nodes.size(),
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
            {146.0F, 40.0F, 64.0F, 52.0F},
            PROGPU_NATIVE_SCENE_NO_INDEX,
            builder_path_boolean_nodes) ||
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
    for (std::uint32_t index = 0U; index < 12U; ++index) {
        if (!native_builder.set_resource_identity(
                index,
                100U + index,
                1U)) {
            fail("The browser native C++ scene builder identity failed.");
        }
    }
    std::vector<std::byte> builder_scene;
    const std::size_t builder_scene_size =
        native_builder.required_stream_size();
    builder_scene.resize(builder_scene_size);
    std::size_t builder_scene_bytes = 0U;
    if (builder_scene_size == 0U ||
        !native_builder.build_into(
            builder_scene,
            builder_scene_bytes) ||
        builder_scene_bytes != builder_scene.size()) {
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
        builder_frame_metrics.draw_call_count != 5U ||
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

    constexpr std::array<std::byte, 16U> updated_builder_image_pixels{
        std::byte{0xff}, std::byte{0xd0}, std::byte{0x20}, std::byte{0xff},
        std::byte{0x20}, std::byte{0xff}, std::byte{0xb0}, std::byte{0xff},
        std::byte{0x20}, std::byte{0xff}, std::byte{0xb0}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xd0}, std::byte{0x20}, std::byte{0xff}};
    if (!native_builder.advance_generation(2U) ||
        !native_builder.update_rgba8_image(
            builder_image_index,
            2U,
            2U,
            8U,
            updated_builder_image_pixels,
            2U)) {
        fail("The browser retained image range update could not compile.");
    }
    const std::size_t updated_builder_scene_size =
        native_builder.required_stream_size();
    builder_scene.resize(updated_builder_scene_size);
    builder_scene_bytes = 0U;
    if (updated_builder_scene_size == 0U ||
        !native_builder.build_into(
            builder_scene,
            builder_scene_bytes) ||
        builder_scene_bytes != builder_scene.size()) {
        fail("The browser retained image range update could not serialize.");
    }
    builder_update_metrics = {};
    builder_update_metrics.struct_size = sizeof(builder_update_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            builder_scene.data(),
            builder_scene.size(),
            &builder_update_metrics) != PROGPU_NATIVE_STATUS_SUCCESS) {
        fail_engine("The browser retained image range update failed.");
    }
    semantic_frame.generation = native_builder.generation();
    builder_frame_metrics = {};
    builder_frame_metrics.struct_size = sizeof(builder_frame_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &builder_frame_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        builder_frame_metrics.brush_upload_bytes != 0U ||
        builder_frame_metrics.gradient_stop_upload_bytes != 0U ||
        builder_frame_metrics.index_upload_bytes != 0U ||
        builder_frame_metrics.texture_upload_bytes != 16U ||
        builder_frame_metrics.color_glyph_upload_bytes != 0U ||
        builder_frame_metrics.text_style_upload_bytes != 0U ||
        builder_frame_metrics.coverage_staging_bytes != 0U ||
        builder_frame_metrics.vertex_upload_bytes !=
            4U * sizeof(progpu::native::vector_vertex)) {
        std::fprintf(
            stderr,
            "C++ incremental image metrics: brush=%" PRIu64
            " gradient=%" PRIu64 " vertex=%" PRIu64
            " index=%" PRIu64 " texture=%" PRIu64
            " color_glyph=%" PRIu64 " text=%" PRIu64
            " coverage=%" PRIu64 "\n",
            builder_frame_metrics.brush_upload_bytes,
            builder_frame_metrics.gradient_stop_upload_bytes,
            builder_frame_metrics.vertex_upload_bytes,
            builder_frame_metrics.index_upload_bytes,
            builder_frame_metrics.texture_upload_bytes,
            builder_frame_metrics.color_glyph_upload_bytes,
            builder_frame_metrics.text_style_upload_bytes,
            builder_frame_metrics.coverage_staging_bytes);
        fail_engine(
            "The browser retained image update rebuilt unrelated GPU pages.");
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
        fail_engine("The updated browser retained scene was rebuilt.");
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
        geometry_metrics.draw_call_count != 1U ||
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

    auto patch_image_scene =
        progpu::native::tests::create_semantic_image_patch_scene_stream(
            width,
            height);
    progpu_native_scene_metrics patch_scene_metrics{};
    patch_scene_metrics.struct_size = sizeof(patch_scene_metrics);
    if (patch_image_scene.empty() ||
        progpu_native_engine_update_scene(
            resources.engine,
            patch_image_scene.data(),
            patch_image_scene.size(),
            &patch_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        patch_scene_metrics.command_count != 1U ||
        patch_scene_metrics.resource_count != 1U ||
        patch_scene_metrics.draw_count != 1U) {
        fail_engine("The browser image patch scene update failed.");
    }
    semantic_frame.scene_id = 95U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics patch_metrics{};
    patch_metrics.struct_size = sizeof(patch_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &patch_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        patch_metrics.command_count != 1U ||
        patch_metrics.draw_call_count != 1U ||
        patch_metrics.submission_count != 1U ||
        patch_metrics.vertex_upload_bytes != 18U * 56U ||
        patch_metrics.index_upload_bytes != 0U ||
        patch_metrics.texture_upload_bytes != 16U) {
        fail_engine("The browser image patch batch render failed.");
    }
    patch_metrics = {};
    patch_metrics.struct_size = sizeof(patch_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &patch_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        patch_metrics.draw_call_count != 1U ||
        patch_metrics.vertex_upload_bytes != 0U ||
        patch_metrics.index_upload_bytes != 0U ||
        patch_metrics.texture_upload_bytes != 0U) {
        fail_engine("The stable browser image patch page was rebuilt.");
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

    progpu::native::semantic_scene_builder vector_mask_builder(105U, 1U);
    const std::array vector_mask_segments{
        progpu_native_path_segment{
            {16.0F, 24.0F}, {128.0F, 8.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {128.0F, 8.0F}, {240.0F, 24.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {240.0F, 24.0F}, {220.0F, 144.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {220.0F, 144.0F}, {36.0F, 144.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {36.0F, 144.0F}, {16.0F, 24.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {92.0F, 56.0F}, {164.0F, 56.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {164.0F, 56.0F}, {164.0F, 112.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {164.0F, 112.0F}, {92.0F, 112.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{
            {92.0F, 112.0F}, {92.0F, 56.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    std::array<progpu_native_scene_path_boolean_node, 3U>
        vector_mask_boolean_nodes{};
    vector_mask_boolean_nodes[0].segment_count = 5U;
    vector_mask_boolean_nodes[0].min_x = 16.0F;
    vector_mask_boolean_nodes[0].min_y = 8.0F;
    vector_mask_boolean_nodes[0].max_x = 240.0F;
    vector_mask_boolean_nodes[0].max_y = 144.0F;
    vector_mask_boolean_nodes[0].kind = PROGPU_NATIVE_PATH_BOOLEAN_LEAF;
    vector_mask_boolean_nodes[1].segment_offset = 5U;
    vector_mask_boolean_nodes[1].segment_count = 4U;
    vector_mask_boolean_nodes[1].min_x = 92.0F;
    vector_mask_boolean_nodes[1].min_y = 56.0F;
    vector_mask_boolean_nodes[1].max_x = 164.0F;
    vector_mask_boolean_nodes[1].max_y = 112.0F;
    vector_mask_boolean_nodes[1].kind = PROGPU_NATIVE_PATH_BOOLEAN_LEAF;
    vector_mask_boolean_nodes[2].kind =
        PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE;
    const progpu_native_scene_clip_path vector_mask_path{
        0U,
        vector_mask_segments.size(),
        0U,
        vector_mask_boolean_nodes.size(),
        16.0F,
        8.0F,
        240.0F,
        144.0F,
        progpu::native::semantic_scene_builder::identity_transform(),
        PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        4U,
        PROGPU_NATIVE_CLIP_INTERSECT,
        0U};
    std::uint32_t vector_mask_brush = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t vector_mask_resource = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t vector_mask_state = PROGPU_NATIVE_SCENE_NO_INDEX;
    auto vector_state =
        progpu::native::semantic_scene_builder::identity_state();
    const progpu_native_analytic_primitive vector_mask_fill{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
        0U,
        0.0F,
        0.0F,
        static_cast<float>(width),
        static_cast<float>(height),
        0.0F,
        0.0F,
        {0.0F, 0.85F, 0.55F, 1.0F},
        progpu::native::semantic_scene_builder::identity_transform()};
    if (!vector_mask_builder.reserve(3U, 4U, 2048U) ||
        !vector_mask_builder.add_solid_brush(
            {0.0F, 0.85F, 0.55F, 1.0F},
            1.0F,
            vector_mask_brush) ||
        !vector_mask_builder.add_vector_clip_mask(
            std::span<const progpu_native_scene_clip_path>(
                &vector_mask_path,
                1U),
            vector_mask_segments,
            vector_mask_boolean_nodes,
            1.0F,
            vector_mask_resource)) {
        fail("The browser vector-mask scene resources could not be built.");
    }
    vector_state.flags = PROGPU_NATIVE_SCENE_STATE_MASK;
    vector_state.mask_resource_index = vector_mask_resource;
    if (!vector_mask_builder.add_state(vector_state, vector_mask_state) ||
        !vector_mask_builder.save(vector_mask_state) ||
        !vector_mask_builder.draw_analytic(
            std::span<const progpu_native_analytic_primitive>(
                &vector_mask_fill,
                1U),
            std::span<const std::uint32_t>(&vector_mask_brush, 1U),
            {0.0F, 0.0F, static_cast<float>(width),
                static_cast<float>(height)}) ||
        !vector_mask_builder.restore()) {
        fail("The browser vector-mask scene commands could not be built.");
    }
    std::vector<std::byte> vector_mask_scene;
    const std::size_t vector_mask_scene_size =
        vector_mask_builder.required_stream_size();
    vector_mask_scene.resize(vector_mask_scene_size);
    std::size_t vector_mask_scene_bytes = 0U;
    if (vector_mask_scene_size == 0U ||
        !vector_mask_builder.build_into(
            vector_mask_scene,
            vector_mask_scene_bytes) ||
        vector_mask_scene_bytes != vector_mask_scene.size()) {
        fail("The browser vector-mask scene stream could not be built.");
    }
    progpu_native_scene_metrics vector_mask_scene_metrics{};
    vector_mask_scene_metrics.struct_size =
        sizeof(vector_mask_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            vector_mask_scene.data(),
            vector_mask_scene.size(),
            &vector_mask_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        vector_mask_scene_metrics.command_count != 3U ||
        vector_mask_scene_metrics.resource_count != 4U ||
        vector_mask_scene_metrics.draw_count != 1U) {
        fail_engine("The browser vector-mask scene update failed.");
    }
    semantic_frame.scene_id = 105U;
    progpu_native_scene_frame_metrics vector_mask_metrics{};
    vector_mask_metrics.struct_size = sizeof(vector_mask_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &vector_mask_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        vector_mask_metrics.draw_call_count != 1U ||
        vector_mask_metrics.texture_upload_bytes != 0U ||
        vector_mask_metrics.uniform_upload_bytes < 24U * sizeof(float)) {
        fail_engine("The browser GPU vector-mask render failed.");
    }
    vector_mask_metrics = {};
    vector_mask_metrics.struct_size = sizeof(vector_mask_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &vector_mask_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        vector_mask_metrics.texture_upload_bytes != 0U ||
        vector_mask_metrics.vertex_upload_bytes != 0U ||
        vector_mask_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser GPU vector mask was rebuilt.");
    }

    auto brush_mask_scene =
        progpu::native::tests::create_semantic_composite_geometry_mask_scene_stream(
            width,
            height);
    progpu_native_scene_metrics brush_mask_scene_metrics{};
    brush_mask_scene_metrics.struct_size =
        sizeof(brush_mask_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            brush_mask_scene.data(),
            brush_mask_scene.size(),
            &brush_mask_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        brush_mask_scene_metrics.command_count != 3U ||
        brush_mask_scene_metrics.resource_count != 2U ||
        brush_mask_scene_metrics.draw_count != 1U) {
        fail_engine("The browser brush-mask scene update failed.");
    }
    semantic_frame.scene_id = 107U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics brush_mask_metrics{};
    brush_mask_metrics.struct_size = sizeof(brush_mask_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &brush_mask_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        brush_mask_metrics.command_count != 3U ||
        brush_mask_metrics.draw_call_count != 2U ||
        brush_mask_metrics.submission_count != 1U ||
        brush_mask_metrics.texture_upload_bytes != 0U ||
        brush_mask_metrics.uniform_upload_bytes <
            24U * sizeof(float)) {
        fail_engine("The browser GPU-generated brush-mask render failed.");
    }
    brush_mask_metrics = {};
    brush_mask_metrics.struct_size = sizeof(brush_mask_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &brush_mask_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        brush_mask_metrics.submission_count != 1U ||
        brush_mask_metrics.texture_upload_bytes != 0U ||
        brush_mask_metrics.vertex_upload_bytes != 0U ||
        brush_mask_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser GPU brush mask was rebuilt.");
    }

    auto picture_mask_scene =
        progpu::native::tests::create_semantic_picture_mask_scene_stream(
            width,
            height);
    progpu_native_scene_metrics picture_mask_scene_metrics{};
    picture_mask_scene_metrics.struct_size =
        sizeof(picture_mask_scene_metrics);
    if (progpu_native_engine_update_scene(
            resources.engine,
            picture_mask_scene.data(),
            picture_mask_scene.size(),
            &picture_mask_scene_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        picture_mask_scene_metrics.command_count != 3U ||
        picture_mask_scene_metrics.resource_count != 2U ||
        picture_mask_scene_metrics.draw_count != 1U) {
        fail_engine("The browser picture-mask scene update failed.");
    }
    semantic_frame.scene_id = 108U;
    semantic_frame.generation = 1U;
    progpu_native_scene_frame_metrics picture_mask_metrics{};
    picture_mask_metrics.struct_size = sizeof(picture_mask_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &picture_mask_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        picture_mask_metrics.command_count != 3U ||
        picture_mask_metrics.draw_call_count != 2U ||
        picture_mask_metrics.submission_count != 2U ||
        picture_mask_metrics.texture_upload_bytes != 0U ||
        picture_mask_metrics.uniform_upload_bytes <
            28U * sizeof(float)) {
        fail_engine("The browser retained picture-mask render failed.");
    }
    picture_mask_metrics = {};
    picture_mask_metrics.struct_size = sizeof(picture_mask_metrics);
    if (progpu_native_engine_render_scene(
            resources.engine,
            &semantic_frame,
            &picture_mask_metrics) != PROGPU_NATIVE_STATUS_SUCCESS ||
        picture_mask_metrics.submission_count != 1U ||
        picture_mask_metrics.texture_upload_bytes != 0U ||
        picture_mask_metrics.vertex_upload_bytes != 0U ||
        picture_mask_metrics.uniform_upload_bytes != 0U) {
        fail_engine("The stable browser retained picture mask was rebuilt.");
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
            physical_width,
            physical_height,
            finish_browser_evidence)) {
        fail("The browser WebGPU evidence copy could not be scheduled.");
    }
    return false;
}

} // namespace

int main() {
    initialize_display_metrics();
    if (!verify_native_png_decode()) {
        fail("The dependency-free native PNG decoder contract is invalid.");
    }
    if (!verify_native_text_feature_plan()) {
        fail("The native text feature-plan contract is invalid.");
    }
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
            PROGPU_NATIVE_CAPABILITY_IMAGE_FRAME_MIPMAP_SAMPLING) == 0U ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_SEMANTIC_VECTOR_CLIP_MASK) == 0U ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_DEVICE_LOSS_RECREATION) == 0U ||
        (info.capabilities &
            PROGPU_NATIVE_CAPABILITY_RETAINED_GPU_HIT_TESTING) == 0U ||
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
    const bool defer_hit_test = EM_ASM_INT({
        return new URLSearchParams(window.location.search)
            .get("progpuNativeGpuHitTesting") === "0";
    }) != 0;
    if (defer_hit_test) {
        // SwiftShader currently spends minutes compiling the complete shared
        // retained hit-test shader. The deterministic software lane exercises
        // the rest of the browser renderer while hardware qualification runs
        // the exact shader and readback path without a reduced approximation.
        EM_ASM({
            document.body.dataset.progpuNativeGpuHitTesting =
                "deferred-software-adapter";
            document.body.dataset.progpuNativeStage = "render-workload";
        });
        if (emscripten_request_animation_frame(
                render_browser_frame,
                nullptr) < 0) {
            fail("The browser render frame could not be scheduled.");
        }
    } else if (!begin_browser_hit_test()) {
        fail_engine("The browser retained GPU hit test could not be scheduled.");
    }
    return 0;
}
