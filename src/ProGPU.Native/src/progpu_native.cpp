#include "progpu_native.h"
#include "progpu_native_draw_state.hpp"
#include "progpu_native_effect_plan.hpp"
#include "progpu_native_geometry.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_scene.hpp"
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_effect_cache.hpp"
#include "progpu_native_semantic_state.hpp"
#include "progpu_native_semantic_validation.hpp"
#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_webgpu_compat.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_replay_execution.hpp"
#include "progpu_native_webgpu_resources.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace {

using semantic_scissor = progpu::native::semantic::scissor;
using semantic_compilation_budget =
    progpu::native::semantic::compilation_budget;
using semantic_layer_budget = progpu::native::semantic::layer_budget;
using progpu::native::semantic::apply_semantic_state;
using progpu::native::semantic::intersect_semantic_scissors;
using progpu::native::semantic::is_valid_semantic_analytic;
using progpu::native::semantic::is_valid_semantic_glyph_outline;
using progpu::native::semantic::is_valid_semantic_image;
using progpu::native::semantic::is_valid_semantic_path;
using progpu::native::semantic::is_valid_semantic_positioned_glyph;
using progpu::native::semantic::is_valid_semantic_segment;
using progpu::native::semantic::localize_semantic_state;
using progpu::native::semantic::resolve_semantic_layer_scissor;
using progpu::native::semantic::resolve_semantic_scissor;
using progpu::native::semantic::resolve_semantic_target_scissor;
using progpu::native::semantic::semantic_default_layer;
using progpu::native::semantic::semantic_layer_target_cursor;
using progpu::native::semantic::semantic_state_cursor;
using progpu::native::align_up;
using progpu::native::antialias_padding_pixels;
using progpu::native::gpu_brush_size;
using progpu::native::gpu_clip_compose_uniforms;
using progpu::native::gpu_clip_vertex;
using progpu::native::gpu_drop_shadow_params;
using progpu::native::gpu_gaussian_blur_params;
using progpu::native::gpu_glyph_instance;
using progpu::native::gpu_glyph_record;
using progpu::native::gpu_glyph_uniforms;
using progpu::native::gpu_group_blend_uniforms;
using progpu::native::gpu_mask_sampling_uniforms;
using progpu::native::gpu_path_record;
using progpu::native::gpu_path_uniforms;
using progpu::native::gpu_uniforms;
using progpu::native::initial_brush_buffer_size;
using progpu::native::initial_index_buffer_size;
using progpu::native::initial_vertex_buffer_size;
using progpu::native::layer_family;
using progpu::native::native_glyph_raster;
using progpu::native::native_initial_atlas_size;
using progpu::native::native_max_atlas_size;
using progpu::native::native_path_cache_key;
using progpu::native::native_path_cache_key_hash;
using progpu::native::native_path_raster;
using progpu::native::path_padding;
using progpu::native::path_raster_resources;
using progpu::native::quantize_subpixel_phase;
using progpu::native::webgpu_copy_row_alignment;
using progpu::native::execution::append_semantic_layer_quad;
using progpu::native::execution::create_drop_shadow_effect_resources;
using progpu::native::execution::create_gaussian_effect_resources;
using progpu::native::execution::create_image_mask_resources;
using progpu::native::execution::create_semantic_layer_mask_binding;
using progpu::native::execution::encode_group_effect;
using progpu::native::execution::encode_layer_composite;
using progpu::native::execution::encode_semantic_effect_chain;
using progpu::native::execution::encode_semantic_layer_composite;
using progpu::native::execution::ensure_semantic_effect_uniform_buffer;
using progpu::native::execution::prepare_group_layer;
using progpu::native::execution::prepare_semantic_layer_resources;
using progpu::native::execution::reset_layer_metrics;
using progpu::native::execution::retain_group_layer_content;
using progpu::native::execution::update_image_mask;
using progpu::native::execution::upload_image_texture;
inline constexpr std::uint32_t semantic_max_draw_passes =
    progpu::native::semantic::max_draw_passes;
inline constexpr std::uint32_t semantic_max_effect_passes =
    progpu::native::semantic::max_effect_passes;
inline constexpr std::uint32_t semantic_effect_uniform_alignment =
    progpu::native::semantic::effect_uniform_alignment;
inline constexpr std::uint64_t semantic_max_total_compiled_bytes =
    progpu::native::semantic::max_total_compiled_bytes;
inline constexpr std::uint64_t semantic_max_coverage_bytes =
    progpu::native::semantic::max_coverage_bytes;

void apply_scissor(
    WGPURenderPassEncoder pass,
    const resolved_draw_state& state) noexcept {
    if (state.has_clip && state.has_drawable_clip) {
        wgpuRenderPassEncoderSetScissorRect(
            pass,
            state.clip_x,
            state.clip_y,
            state.clip_width,
            state.clip_height);
    }
}

void multiply_vertex_alpha(
    std::vector<progpu::native::vector_vertex>& vertices,
    float opacity) noexcept {
    if (opacity == 1.0F) {
        return;
    }
    for (auto& vertex : vertices) {
        vertex.color[3] *= opacity;
    }
}

void set_brush_opacity(
    std::vector<std::byte>& brushes,
    float opacity) noexcept {
    for (std::size_t offset = 4U; offset < brushes.size();
         offset += gpu_brush_size) {
        std::memcpy(brushes.data() + offset, &opacity, sizeof(opacity));
    }
}

void clear_metrics(progpu_native_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_analytic_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_analytic_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_geometry_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_geometry_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_path_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_path_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_glyph_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_glyph_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

void clear_metrics(progpu_native_image_frame_metrics* metrics) noexcept {
    if (metrics == nullptr ||
        metrics->struct_size < sizeof(progpu_native_image_frame_metrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

struct semantic_render_bundle_commands final {
    using encoder_type = WGPURenderBundleEncoder;

    static void set_pipeline(
        encoder_type encoder,
        WGPURenderPipeline pipeline) noexcept {
        wgpuRenderBundleEncoderSetPipeline(encoder, pipeline);
    }

    static void set_bind_group(
        encoder_type encoder,
        std::uint32_t index,
        WGPUBindGroup bind_group) noexcept {
        wgpuRenderBundleEncoderSetBindGroup(
            encoder, index, bind_group, 0U, nullptr);
    }

    static void set_vertex_buffer(
        encoder_type encoder,
        WGPUBuffer buffer,
        std::uint64_t size) noexcept {
        wgpuRenderBundleEncoderSetVertexBuffer(
            encoder, 0U, buffer, 0U, size);
    }

    static void set_index_buffer(
        encoder_type encoder,
        WGPUBuffer buffer,
        std::uint64_t size) noexcept {
        wgpuRenderBundleEncoderSetIndexBuffer(
            encoder, buffer, WGPUIndexFormat_Uint32, 0U, size);
    }

    static void draw(
        encoder_type encoder,
        std::uint32_t vertex_count,
        std::uint32_t instance_count,
        std::uint32_t first_vertex,
        std::uint32_t first_instance) noexcept {
        wgpuRenderBundleEncoderDraw(
            encoder,
            vertex_count,
            instance_count,
            first_vertex,
            first_instance);
    }

    static void draw_indexed(
        encoder_type encoder,
        std::uint32_t index_count,
        std::uint32_t first_index,
        std::int32_t base_vertex) noexcept {
        wgpuRenderBundleEncoderDrawIndexed(
            encoder, index_count, 1U, first_index, base_vertex, 0U);
    }
};

WGPUBindGroup select_semantic_analytic_uniform_bind_group(
    progpu_native_engine& engine,
    std::uint32_t target_layer) noexcept {
    return target_layer == PROGPU_NATIVE_SCENE_NO_INDEX
        ? engine.analytic_uniform_bind_group
        : target_layer < engine.semantic_layer_slots.size()
            ? engine.semantic_layer_slots[target_layer]
                .analytic_uniform_bind_group
            : nullptr;
}

WGPUBindGroup select_semantic_text_uniform_bind_group(
    progpu_native_engine& engine,
    std::uint32_t target_layer) noexcept {
    return target_layer == PROGPU_NATIVE_SCENE_NO_INDEX
        ? engine.text_uniform_bind_group
        : target_layer < engine.semantic_layer_slots.size()
            ? engine.semantic_layer_slots[target_layer]
                .text_uniform_bind_group
            : nullptr;
}

WGPUBindGroup select_semantic_image_uniform_bind_group(
    progpu_native_engine& engine,
    std::uint32_t target_layer) noexcept {
    return target_layer == PROGPU_NATIVE_SCENE_NO_INDEX
        ? engine.image_uniform_bind_group
        : target_layer < engine.semantic_layer_slots.size()
            ? engine.semantic_layer_slots[target_layer]
                .image_uniform_bind_group
            : nullptr;
}

template<typename Commands>
progpu_native_status encode_semantic_analytic_draw(
    progpu_native_engine& engine,
    typename Commands::encoder_type encoder,
    const semantic_analytic_draw& draw,
    std::uint32_t target_layer) {
    auto& page = engine.semantic_analytic_cache;
    WGPUBindGroup uniform_group =
        select_semantic_analytic_uniform_bind_group(
            engine,
            target_layer);
    if (!page.cache_valid || page.vertex_buffer == nullptr ||
        page.index_buffer == nullptr || encoder == nullptr ||
        uniform_group == nullptr || draw.vertex_count == 0U ||
        draw.index_count == 0U ||
        draw.vertex_offset_bytes >= page.vertex_bytes ||
        draw.index_offset_bytes >= page.index_bytes ||
        draw.vertex_count >
            (page.vertex_bytes - draw.vertex_offset_bytes) /
                sizeof(progpu::native::vector_vertex) ||
        draw.index_count >
            (page.index_bytes - draw.index_offset_bytes) /
                sizeof(std::uint32_t)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic analytic packed page is incomplete.");
    }
    if (engine.analytic_pipeline == nullptr &&
        !create_analytic_pipeline(engine)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic analytic WebGPU pipeline could not be created.");
    }

    Commands::set_pipeline(encoder, engine.analytic_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(
        encoder, 1U, engine.analytic_atlas_bind_group);
    Commands::set_vertex_buffer(
        encoder, page.vertex_buffer, page.vertex_bytes);
    Commands::set_index_buffer(
        encoder, page.index_buffer, page.index_bytes);
    Commands::draw_indexed(
        encoder,
        draw.index_count,
        static_cast<std::uint32_t>(
            draw.index_offset_bytes / sizeof(std::uint32_t)),
        0);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

template<typename Commands>
progpu_native_status encode_semantic_path_draw(
    progpu_native_engine& engine,
    typename Commands::encoder_type encoder,
    const semantic_path_draw& draw,
    std::uint32_t target_layer) {
    const std::uint64_t vertex_bytes = engine.path_vertices.size() *
        sizeof(progpu::native::vector_vertex);
    const std::uint64_t index_bytes = engine.path_indices.size() *
        sizeof(std::uint32_t);
    WGPUBindGroup uniform_group =
        select_semantic_analytic_uniform_bind_group(
            engine,
            target_layer);
    if (!engine.semantic_path_cache.cache_valid ||
        !engine.path_cache_valid || !engine.path_gpu_cache_valid ||
        engine.path_vertex_buffer == nullptr ||
        engine.path_index_buffer == nullptr ||
        engine.path_atlas_bind_group == nullptr ||
        encoder == nullptr || uniform_group == nullptr ||
        draw.index_count == 0U ||
        draw.first_index > engine.path_indices.size() ||
        draw.index_count >
            engine.path_indices.size() - draw.first_index) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic path packed page is incomplete.");
    }
    Commands::set_pipeline(encoder, engine.analytic_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(
        encoder, 1U, engine.path_atlas_bind_group);
    Commands::set_vertex_buffer(
        encoder, engine.path_vertex_buffer, vertex_bytes);
    Commands::set_index_buffer(
        encoder, engine.path_index_buffer, index_bytes);
    Commands::draw_indexed(
        encoder, draw.index_count, draw.first_index, 0);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

template<typename Commands>
progpu_native_status encode_semantic_glyph_draw(
    progpu_native_engine& engine,
    typename Commands::encoder_type encoder,
    const semantic_glyph_draw& draw,
    std::uint32_t target_layer) {
    const std::uint64_t instance_bytes = engine.glyph_instances.size() *
        sizeof(gpu_glyph_instance);
    WGPUBindGroup uniform_group =
        select_semantic_text_uniform_bind_group(
            engine,
            target_layer);
    if (!engine.semantic_glyph_cache.cache_valid ||
        !engine.glyph_cache_valid || !engine.glyph_gpu_cache_valid ||
        engine.text_vertex_buffer == nullptr ||
        engine.text_atlas_bind_group == nullptr ||
        encoder == nullptr || uniform_group == nullptr ||
        draw.instance_count == 0U ||
        draw.first_instance > engine.glyph_instances.size() ||
        draw.instance_count >
            engine.glyph_instances.size() - draw.first_instance) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic glyph packed page is incomplete.");
    }
    Commands::set_pipeline(encoder, engine.text_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(
        encoder, 1U, engine.text_atlas_bind_group);
    Commands::set_vertex_buffer(
        encoder, engine.text_vertex_buffer, instance_bytes);
    Commands::draw(
        encoder, 6U, draw.instance_count, 0U, draw.first_instance);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

template<typename Commands>
progpu_native_status encode_semantic_image_draw(
    progpu_native_engine& engine,
    typename Commands::encoder_type encoder,
    const semantic_image_draw& draw,
    std::uint32_t target_layer) {
    auto& page = engine.semantic_image_cache;
    WGPUBindGroup texture_group =
        draw.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
        ? draw.nearest_bind_group
        : draw.linear_bind_group;
    WGPUBindGroup uniform_group =
        select_semantic_image_uniform_bind_group(
            engine,
            target_layer);
    if (!page.cache_valid || page.vertex_buffer == nullptr ||
        page.vertex_bytes == 0U || texture_group == nullptr ||
        engine.image_index_buffer == nullptr || uniform_group == nullptr ||
        encoder == nullptr ||
        draw.first_vertex >
            std::numeric_limits<std::uint32_t>::max() - 4U ||
        static_cast<std::uint64_t>(draw.first_vertex + 4U) *
                sizeof(progpu::native::vector_vertex) >
            page.vertex_bytes) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic image packed page is incomplete.");
    }
    Commands::set_pipeline(encoder, engine.image_pipeline);
    Commands::set_bind_group(
        encoder, 0U, uniform_group);
    Commands::set_bind_group(encoder, 1U, texture_group);
    Commands::set_vertex_buffer(
        encoder, page.vertex_buffer, page.vertex_bytes);
    Commands::set_index_buffer(
        encoder, engine.image_index_buffer, 6U * sizeof(std::uint32_t));
    Commands::draw_indexed(
        encoder, 6U, 0U, static_cast<std::int32_t>(draw.first_vertex));
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status create_engine(
    WGPUInstance instance,
    WGPUDevice device,
    WGPUQueue queue,
    WGPUTextureFormat target_format,
    const progpu::native::webgpu::dispatch& webgpu_dispatch,
    progpu_native_engine** engine) {
    try {
        auto result = std::make_unique<progpu_native_engine>();
        result->owner_thread = std::this_thread::get_id();
        result->webgpu_dispatch = webgpu_dispatch;
        result->instance = instance;
        result->device = device;
        result->queue = queue;
        result->target_format = target_format;
        const progpu::native::webgpu::dispatch_scope dispatch_scope(
            &result->webgpu_dispatch);
        if (result->instance != nullptr) {
            progpu::native::webgpu::instance_add_ref(result->instance);
        }
        progpu::native::webgpu::device_add_ref(result->device);
        progpu::native::webgpu::queue_add_ref(result->queue);
        if (!create_pipeline(*result) ||
            !result->ensure_vertex_buffer(initial_vertex_buffer_size)) {
            result->last_error =
                "The shared vector shader or native WebGPU pipeline could not be created.";
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        }
        *engine = result.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

} // namespace

extern "C" {

uint32_t progpu_native_get_abi_version(void) {
    return PROGPU_NATIVE_ABI_VERSION;
}

uint8_t progpu_native_get_info(progpu_native_engine_info* info) {
    if (info == nullptr || info->struct_size < sizeof(progpu_native_engine_info)) {
        return 0U;
    }
    *info = {};
    info->struct_size = sizeof(progpu_native_engine_info);
    info->abi_version = PROGPU_NATIVE_ABI_VERSION;
#if defined(PROGPU_NATIVE_DAWN_ABI)
    info->backend_abi = PROGPU_NATIVE_BACKEND_ABI_DAWN_WEBSCENE_2026_07;
#else
    info->backend_abi = PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05;
#endif
    info->capabilities =
        PROGPU_NATIVE_CAPABILITY_SOLID_RECT_BATCH |
        PROGPU_NATIVE_CAPABILITY_SHARED_VECTOR_SHADER |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_TARGET |
        PROGPU_NATIVE_CAPABILITY_INDEXED_ANALYTIC_BATCH |
        PROGPU_NATIVE_CAPABILITY_AFFINE_2D |
        PROGPU_NATIVE_CAPABILITY_INDEXED_GEOMETRY_BATCH |
        PROGPU_NATIVE_CAPABILITY_DEVICE_STROKES |
        PROGPU_NATIVE_CAPABILITY_BEZIER_STROKES |
        PROGPU_NATIVE_CAPABILITY_STROKE_CAPS |
        PROGPU_NATIVE_CAPABILITY_CONNECTED_STROKES |
        PROGPU_NATIVE_CAPABILITY_SPLINE_STROKES |
        PROGPU_NATIVE_CAPABILITY_DASHED_STROKES |
        PROGPU_NATIVE_CAPABILITY_RETAINED_GEOMETRY_REPLAY |
        PROGPU_NATIVE_CAPABILITY_PATH_FILL_ATLAS |
        PROGPU_NATIVE_CAPABILITY_POSITIONED_GLYPH_ATLAS |
        PROGPU_NATIVE_CAPABILITY_RESIZABLE_ATLASES |
        PROGPU_NATIVE_CAPABILITY_RETAINED_RGBA_IMAGE |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_RGBA_VIEW |
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_IMAGE_MASK |
        PROGPU_NATIVE_CAPABILITY_EXPLICIT_QUEUE_TIMELINE |
        PROGPU_NATIVE_CAPABILITY_FRAME_DRAW_STATE |
        PROGPU_NATIVE_CAPABILITY_GROUP_OPACITY |
        PROGPU_NATIVE_CAPABILITY_COMMON_GROUP_MASK |
        PROGPU_NATIVE_CAPABILITY_ANALYTIC_ROUNDED_GROUP_MASK |
        PROGPU_NATIVE_CAPABILITY_RETAINED_VECTOR_CLIP_CHAIN |
        PROGPU_NATIVE_CAPABILITY_GROUP_GAUSSIAN_BLUR |
        PROGPU_NATIVE_CAPABILITY_GROUP_DROP_SHADOW |
        PROGPU_NATIVE_CAPABILITY_BOUNDED_GROUP_EFFECT_CHAIN |
        PROGPU_NATIVE_CAPABILITY_GROUP_BLEND_MODES |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_SCENE_SNAPSHOTS |
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_SCENE_RENDERING;
#if defined(PROGPU_NATIVE_DAWN_ABI)
    constexpr char name[] = "ProGPU C++ core renderer / Dawn provider";
#else
    constexpr char name[] = "ProGPU C++ core renderer / wgpu-native";
#endif
    std::memcpy(info->name, name, sizeof(name));
    return 1U;
}

progpu_native_status progpu_native_scene_validate(
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics) {
    const auto result = progpu::native::scene::validate(stream, stream_size);
    progpu::native::scene::write_metrics(result, metrics);
    return result.status;
}

progpu_native_status progpu_native_engine_create(
    const progpu_native_engine_options* options,
    progpu_native_engine** engine) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *engine = nullptr;
#if defined(PROGPU_NATIVE_DAWN_ABI)
    (void)options;
    return PROGPU_NATIVE_STATUS_UNSUPPORTED;
#else
    if (options == nullptr ||
        options->struct_size < sizeof(progpu_native_engine_options) ||
        options->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        options->backend_abi !=
            PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05 ||
        options->device == 0U || options->queue == 0U ||
        texture_format(options->target_format) == WGPUTextureFormat_Undefined) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const progpu::native::webgpu::dispatch webgpu_dispatch{};
    return create_engine(
        nullptr,
        reinterpret_cast<WGPUDevice>(options->device),
        reinterpret_cast<WGPUQueue>(options->queue),
        texture_format(options->target_format),
        webgpu_dispatch,
        engine);
#endif
}

#if defined(PROGPU_NATIVE_DAWN_ABI)
static_assert(sizeof(progpu_native_dawn_engine_options) == 72U);

uint32_t progpu_native_dawn_get_adapter_abi_version(void) {
    return PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION;
}

progpu_native_status progpu_native_dawn_engine_create(
    const progpu_native_dawn_engine_options* options,
    progpu_native_engine** engine) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *engine = nullptr;
    if (options == nullptr ||
        options->struct_size < sizeof(progpu_native_dawn_engine_options) ||
        options->native_abi_version != PROGPU_NATIVE_ABI_VERSION ||
        options->adapter_abi_version !=
            PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION ||
        options->provider_abi_version !=
            PROGPU_NATIVE_DAWN_REQUIRED_PROVIDER_ABI_VERSION ||
        options->reserved != 0U || options->flags != 0U ||
        options->resolver_context == nullptr ||
        options->resolve_proc == nullptr ||
        options->instance == 0U || options->device == 0U ||
        options->queue == 0U ||
        texture_format(options->target_format) ==
            WGPUTextureFormat_Undefined) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }

    progpu::native::webgpu::dispatch webgpu_dispatch{};
    if (!webgpu_dispatch.load(
            options->resolver_context,
            options->resolve_proc)) {
        return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    }
    return create_engine(
        reinterpret_cast<WGPUInstance>(options->instance),
        reinterpret_cast<WGPUDevice>(options->device),
        reinterpret_cast<WGPUQueue>(options->queue),
        texture_format(options->target_format),
        webgpu_dispatch,
        engine);
}
#endif

void progpu_native_engine_destroy(progpu_native_engine* engine) {
    delete engine;
}

progpu_native_status progpu_native_engine_update_scene(
    progpu_native_engine* engine,
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics) {
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        &engine->webgpu_dispatch);
    if (std::this_thread::get_id() != engine->owner_thread) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Native scene updates are owner-thread affine.");
    }
    if (stream != nullptr && !engine->semantic_scene_snapshot.empty() &&
        engine->semantic_scene_snapshot.size() == stream_size &&
        std::memcmp(
            engine->semantic_scene_snapshot.data(),
            stream,
            stream_size) == 0) {
        if (metrics != nullptr &&
            metrics->struct_size >= sizeof(progpu_native_scene_metrics)) {
            const std::uint32_t struct_size = metrics->struct_size;
            *metrics = engine->semantic_scene_metrics;
            metrics->struct_size = struct_size;
            metrics->flags |= PROGPU_NATIVE_SCENE_METRICS_SNAPSHOT_REUSED;
        }
        engine->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    const auto validation =
        progpu::native::scene::validate(stream, stream_size);
    progpu::native::scene::write_metrics(validation, metrics);
    if (validation.status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return engine->fail(
            validation.status,
            "The semantic scene stream failed transactional validation.");
    }

    if (validation.header.scene_id == engine->semantic_scene_id) {
        if (validation.header.generation <
            engine->semantic_scene_generation) {
            if (metrics != nullptr &&
                metrics->struct_size >= sizeof(progpu_native_scene_metrics)) {
                metrics->validation_error =
                    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION;
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The semantic scene generation regressed.");
        }
        if (validation.header.generation ==
            engine->semantic_scene_generation) {
            if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_scene_metrics)) {
                metrics->validation_error =
                    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION;
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "One semantic scene generation must be immutable.");
        }

        std::uint32_t error_offset = 0U;
        if (!progpu::native::scene::generations_do_not_regress(
                engine->semantic_scene_snapshot.data(),
                engine->semantic_scene_header,
                stream,
                validation.header,
                error_offset)) {
            if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_scene_metrics)) {
                metrics->validation_error =
                    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION;
                metrics->error_offset = error_offset;
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "A retained semantic resource generation regressed.");
        }
    }

    try {
        std::vector<std::byte> next(stream_size);
        std::memcpy(next.data(), stream, stream_size);
        const std::uint64_t next_hash = append_fnv1a64(
            14695981039346656037ULL,
            stream,
            stream_size);
        engine->release_semantic_render_bundle();
        engine->semantic_scene_snapshot.swap(next);
        engine->semantic_scene_id = validation.header.scene_id;
        engine->semantic_scene_generation = validation.header.generation;
        engine->semantic_scene_hash = next_hash;
        engine->semantic_scene_header = validation.header;
        engine->semantic_scene_metrics = {};
        engine->semantic_scene_metrics.struct_size =
            sizeof(progpu_native_scene_metrics);
        progpu::native::scene::write_metrics(
            validation,
            &engine->semantic_scene_metrics);
        engine->last_error.clear();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The immutable semantic scene snapshot could not be allocated.");
    } catch (...) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The immutable semantic scene snapshot could not be committed.");
    }
}

progpu_native_status progpu_native_engine_render(
    progpu_native_engine* engine,
    const progpu_native_frame* frame,
    progpu_native_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->rect_count != 0U && frame->rects == nullptr) ||
        !std::isfinite(frame->clear_color.r) ||
        !std::isfinite(frame->clear_color.g) ||
        !std::isfinite(frame->clear_color.b) ||
        !std::isfinite(frame->clear_color.a)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_frame)
            ? frame->draw_state
            : nullptr;
    if (!resolve_draw_state(
            requested_draw_state,
            frame->target_view,
            frame->width,
            frame->height,
            frame->dpi_scale,
            draw_state)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    engine->release_semantic_render_bundle();
    reset_layer_metrics(*engine);
    engine->geometry_cache_valid = false;
    engine->geometry_gpu_cache_valid = false;
    if (frame->rect_count >
            std::numeric_limits<std::size_t>::max() / 6U ||
        frame->rect_count >
            std::numeric_limits<std::uint32_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The rectangle batch is too large.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::solid,
        frame->width,
        frame->height,
        frame->dpi_scale,
        reinterpret_cast<WGPUTextureView>(frame->target_view),
        frame->clear_color,
        draw_state,
        use_group_layer,
        group_cache_hit);
    if (group_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return group_status;
    }
    if (group_cache_hit) {
        if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    try {
        engine->vertices.clear();
        engine->vertices.reserve(frame->rect_count * 6U);
        const float local_padding =
            antialias_padding_pixels / frame->dpi_scale;
        for (std::size_t index = 0; index < frame->rect_count; ++index) {
            if (!progpu::native::append_solid_rect(
                    frame->rects[index],
                    local_padding,
                    engine->vertices)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A rectangle contains invalid geometry or color values.");
            }
        }
        multiply_vertex_alpha(engine->vertices, draw_state.opacity);
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native rectangle batch could not be allocated.");
    }

    const std::uint64_t vertex_bytes =
        engine->vertices.size() * sizeof(progpu::native::vector_vertex);
    if (!engine->ensure_vertex_buffer(vertex_bytes)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native WebGPU vertex buffer could not be allocated.");
    }

    const gpu_uniforms uniforms = create_uniforms(
        frame->width,
        frame->height,
        frame->dpi_scale);
    const bool uploaded_uniforms = engine->upload_uniform_if_changed(
        engine->uniform_buffer,
        uniforms,
        engine->cached_uniforms,
        engine->uniform_cache_valid);
    if (vertex_bytes != 0U) {
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->vertex_buffer,
            0U,
            engine->vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
    }

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native frame encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native frame command encoder could not be created.");
    }

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = !use_group_layer &&
            engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native solid rectangle pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native render pass could not be created.");
    }

    if (!engine->vertices.empty() && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            0U,
            engine->uniform_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->vertex_buffer,
            0U,
            vertex_bytes);
        wgpuRenderPassEncoderDraw(
            pass,
            static_cast<std::uint32_t>(engine->vertices.size()),
            1U,
            0U,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (use_group_layer) {
        engine->last_layer_metrics.content_pass_count = 1U;
        if (!encode_group_effect(
                *engine,
                encoder,
                draw_state,
                frame->dpi_scale) ||
            !encode_layer_composite(
                *engine,
                encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                frame->clear_color,
                draw_state)) {
            wgpuCommandEncoderRelease(encoder);
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The group composite pass could not be created.");
        }
    }

    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native frame commands");
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (command == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native frame command buffer could not be finished.");
    }

    engine->submit(command);
    wgpuCommandBufferRelease(command);
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::solid,
            frame->dpi_scale,
            draw_state);
    }
    engine->last_error.clear();

    if (metrics != nullptr &&
        metrics->struct_size >= sizeof(progpu_native_frame_metrics)) {
        metrics->draw_call_count = engine->vertices.empty() ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count =
            static_cast<std::uint32_t>(engine->vertices.size());
        metrics->vertex_upload_bytes = vertex_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_render_analytic(
    progpu_native_engine* engine,
    const progpu_native_analytic_frame* frame,
    progpu_native_analytic_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_analytic_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->primitive_count != 0U && frame->primitives == nullptr) ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The analytic frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_analytic_frame)
            ? frame->draw_state
            : nullptr;
    if (!resolve_draw_state(
            requested_draw_state,
            frame->target_view,
            frame->width,
            frame->height,
            frame->dpi_scale,
            draw_state)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The analytic frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    engine->release_semantic_render_bundle();
    reset_layer_metrics(*engine);
    engine->geometry_cache_valid = false;
    engine->geometry_gpu_cache_valid = false;
    if (frame->primitive_count >
            std::numeric_limits<std::uint32_t>::max() / 6U ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The analytic primitive batch is too large.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::analytic,
        frame->width,
        frame->height,
        frame->dpi_scale,
        reinterpret_cast<WGPUTextureView>(frame->target_view),
        frame->clear_color,
        draw_state,
        use_group_layer,
        group_cache_hit);
    if (group_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return group_status;
    }
    if (group_cache_hit) {
        if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_analytic_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    try {
        engine->vertices.clear();
        engine->indices.clear();
        engine->vertices.reserve(frame->primitive_count * 4U);
        engine->indices.reserve(frame->primitive_count * 6U);
        for (std::size_t index = 0;
             index < frame->primitive_count;
             ++index) {
            float minimum_scale = 0.0F;
            if (!progpu::native::try_get_minimum_scale(
                    frame->primitives[index].transform,
                    minimum_scale)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "An analytic primitive has a non-invertible affine transform.");
            }
            const float local_padding =
                antialias_padding_pixels / minimum_scale;
            if (!progpu::native::append_analytic_primitive(
                    frame->primitives[index],
                    local_padding,
                    engine->vertices,
                    engine->indices)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "An analytic primitive contains invalid geometry, color, or flags.");
            }
        }
        multiply_vertex_alpha(engine->vertices, draw_state.opacity);
    } catch (const std::bad_alloc&) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native analytic batch could not be allocated.");
    }

    const std::uint64_t vertex_bytes =
        engine->vertices.size() * sizeof(progpu::native::vector_vertex);
    const std::uint64_t index_bytes =
        engine->indices.size() * sizeof(std::uint32_t);
    bool uploaded_uniforms = false;
    if (vertex_bytes != 0U) {
        if (engine->analytic_pipeline == nullptr &&
            !create_analytic_pipeline(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native analytic WebGPU pipeline could not be created.");
        }
        if (!engine->ensure_vertex_buffer(vertex_bytes) ||
            !engine->ensure_index_buffer(index_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native analytic WebGPU buffers could not be allocated.");
        }

        const gpu_uniforms uniforms = create_uniforms(
            frame->width,
            frame->height,
            frame->dpi_scale);
        uploaded_uniforms = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->vertex_buffer,
            0U,
            engine->vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->index_buffer,
            0U,
            engine->indices.data(),
            static_cast<std::size_t>(index_bytes));
    }

    const bool owns_encoder = engine->semantic_encoder == nullptr;
    WGPUCommandEncoder encoder = engine->semantic_encoder;
    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic frame encoder");
    if (owns_encoder) {
        encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
    }
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native analytic command encoder could not be created.");
    }

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = !use_group_layer &&
            engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native indexed analytic primitive pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native analytic render pass could not be created.");
    }

    if (!engine->indices.empty() && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->analytic_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            0U,
            engine->analytic_uniform_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            1U,
            engine->analytic_atlas_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->vertex_buffer,
            0U,
            vertex_bytes);
        wgpuRenderPassEncoderSetIndexBuffer(
            pass,
            engine->index_buffer,
            WGPUIndexFormat_Uint32,
            0U,
            index_bytes);
        wgpuRenderPassEncoderDrawIndexed(
            pass,
            static_cast<std::uint32_t>(engine->indices.size()),
            1U,
            0U,
            0,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (use_group_layer) {
        engine->last_layer_metrics.content_pass_count = 1U;
        if (!encode_group_effect(
                *engine,
                encoder,
                draw_state,
                frame->dpi_scale) ||
            !encode_layer_composite(
                *engine,
                encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                frame->clear_color,
                draw_state)) {
            if (owns_encoder) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The analytic group composite pass could not be created.");
        }
    }

    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic frame commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native analytic command buffer could not be finished.");
        }

        engine->submit(command);
        wgpuCommandBufferRelease(command);
    }
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::analytic,
            frame->dpi_scale,
            draw_state);
    }
    engine->last_error.clear();

    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_analytic_frame_metrics)) {
        metrics->draw_call_count = engine->indices.empty() ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count =
            static_cast<std::uint32_t>(engine->vertices.size());
        metrics->index_count =
            static_cast<std::uint32_t>(engine->indices.size());
        metrics->vertex_upload_bytes = vertex_bytes;
        metrics->index_upload_bytes = index_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(gpu_uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_render_geometry(
    progpu_native_engine* engine,
    const progpu_native_geometry_frame* frame,
    progpu_native_geometry_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_geometry_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->primitive_count != 0U && frame->primitives == nullptr) ||
        (frame->point_count != 0U && frame->points == nullptr) ||
        (frame->polyline_count != 0U && frame->polylines == nullptr) ||
        (frame->spline_count != 0U && frame->points == nullptr) ||
        (frame->double_count != 0U && frame->doubles == nullptr) ||
        (frame->dash_style_count != 0U && frame->dash_styles == nullptr) ||
        (frame->spline_count != 0U && frame->splines == nullptr) ||
        (frame->flags &
            ~(PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH |
              PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD)) != 0U ||
        (((frame->flags &
                PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U) !=
            (frame->reserved != 0U)) ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The geometry frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_geometry_frame)
            ? frame->draw_state
            : nullptr;
    if (!resolve_draw_state(
            requested_draw_state,
            frame->target_view,
            frame->width,
            frame->height,
            frame->dpi_scale,
            draw_state)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The geometry frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    engine->release_semantic_render_bundle();
    reset_layer_metrics(*engine);
    engine->path_gpu_cache_valid = false;
    if (frame->primitive_count > (1U << 24U) ||
        frame->polyline_count > (1U << 24U) ||
        frame->spline_count > (1U << 24U) ||
        frame->dash_style_count > (1U << 24U) ||
        frame->point_count > (1U << 28U) ||
        frame->double_count > (1U << 28U) ||
        frame->primitive_count >
            std::numeric_limits<std::uint32_t>::max() / 6U ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() / 6U ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() / gpu_brush_size ||
        frame->polyline_count >
            std::numeric_limits<std::size_t>::max() / gpu_brush_size ||
        frame->spline_count >
            std::numeric_limits<std::size_t>::max() / gpu_brush_size ||
        frame->primitive_count >
            std::numeric_limits<std::size_t>::max() - frame->polyline_count ||
        frame->primitive_count + frame->polyline_count >
            std::numeric_limits<std::size_t>::max() - frame->spline_count ||
        frame->primitive_count + frame->polyline_count +
            frame->spline_count > (1U << 24U)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The geometry primitive batch is too large.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::geometry,
        frame->width,
        frame->height,
        frame->dpi_scale,
        reinterpret_cast<WGPUTextureView>(frame->target_view),
        frame->clear_color,
        draw_state,
        use_group_layer,
        group_cache_hit);
    if (group_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return group_status;
    }
    if (group_cache_hit) {
        if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_geometry_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    const bool retain_compiled_payload =
        (frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U;
    const bool compiled_payload_hit = retain_compiled_payload &&
        engine->geometry_cache_valid &&
        engine->geometry_content_revision == frame->reserved;
    if (!compiled_payload_hit) {
        engine->geometry_cache_valid = false;
        engine->geometry_gpu_cache_valid = false;
        try {
        engine->vertices.clear();
        engine->indices.clear();
        engine->primitive_brush_indices.clear();
        engine->polyline_brush_indices.clear();
        engine->spline_brush_indices.clear();
        engine->spline_segment_counts.clear();
        for (std::size_t index = 0U;
             index < frame->dash_style_count;
             ++index) {
            const auto& style = frame->dash_styles[index];
            if (style.interval_count == 0U ||
                style.interval_offset > frame->double_count ||
                style.interval_count >
                    frame->double_count - style.interval_offset ||
                !std::isfinite(style.offset) ||
                style.cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
                style.reserved != 0U) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A dash style range, offset, cap, or reserved field is invalid.");
            }
            for (std::size_t interval = 0U;
                 interval < style.interval_count;
                 ++interval) {
                const double value =
                    frame->doubles[style.interval_offset + interval];
                if (!std::isfinite(value) || value < 0.0) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A dash interval is negative or not finite.");
                }
            }
        }
        std::size_t vertex_capacity = 0U;
        std::size_t index_capacity = 0U;
        for (std::size_t index = 0; index < frame->primitive_count; ++index) {
            std::size_t vertices_to_add = 0U;
            std::size_t indices_to_add = 0U;
            if (!progpu::native::geometry_primitive_capacity(
                    frame->primitives[index],
                    vertices_to_add,
                    indices_to_add) ||
                vertex_capacity >
                    std::numeric_limits<std::uint32_t>::max() - vertices_to_add ||
                vertex_capacity >
                    std::numeric_limits<std::size_t>::max() - vertices_to_add ||
                index_capacity >
                    std::numeric_limits<std::size_t>::max() - indices_to_add) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "The geometry primitive batch exceeds the indexed upload limits.");
            }
            vertex_capacity += vertices_to_add;
            index_capacity += indices_to_add;
        }
        for (std::size_t index = 0; index < frame->polyline_count; ++index) {
            const auto& polyline = frame->polylines[index];
            std::size_t vertices_to_add = 0U;
            std::size_t indices_to_add = 0U;
            if (polyline.point_offset > frame->point_count ||
                polyline.point_count >
                    frame->point_count - polyline.point_offset ||
                !progpu::native::polyline_capacity(
                    polyline,
                    frame->points + polyline.point_offset,
                    frame->dash_styles,
                    frame->dash_style_count,
                    frame->doubles,
                    frame->double_count,
                    vertices_to_add,
                    indices_to_add) ||
                vertex_capacity >
                    std::numeric_limits<std::uint32_t>::max() -
                        vertices_to_add ||
                vertex_capacity >
                    std::numeric_limits<std::size_t>::max() -
                        vertices_to_add ||
                index_capacity >
                    std::numeric_limits<std::size_t>::max() -
                        indices_to_add) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A connected stroke range exceeds the point arena or indexed upload limits.");
            }
            vertex_capacity += vertices_to_add;
            index_capacity += indices_to_add;
        }
        std::size_t maximum_spline_degree = 0U;
        for (std::size_t index = 0U; index < frame->spline_count; ++index) {
            if (frame->splines[index].degree > (1U << 20U)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A spline degree exceeds the native safety bound.");
            }
            maximum_spline_degree = std::max(
                maximum_spline_degree,
                static_cast<std::size_t>(frame->splines[index].degree));
        }
        engine->spline_work.reserve(maximum_spline_degree + 1U);
        engine->spline_segment_counts.resize(frame->spline_count);
        for (std::size_t index = 0; index < frame->spline_count; ++index) {
            const auto& spline = frame->splines[index];
            const auto& stroke = spline.stroke;
            std::size_t segment_count = 0U;
            std::size_t vertices_to_add = 0U;
            std::size_t indices_to_add = 0U;
            if (stroke.point_offset > frame->point_count ||
                stroke.point_count >
                    frame->point_count - stroke.point_offset ||
                spline.knot_offset > frame->double_count ||
                spline.knot_count >
                    frame->double_count - spline.knot_offset ||
                spline.weight_offset > frame->double_count ||
                spline.weight_count >
                    frame->double_count - spline.weight_offset ||
                !progpu::native::spline_capacity(
                    spline,
                    frame->points + stroke.point_offset,
                    spline.knot_count == 0U
                        ? nullptr
                        : frame->doubles + spline.knot_offset,
                    spline.weight_count == 0U
                        ? nullptr
                        : frame->doubles + spline.weight_offset,
                    frame->dash_styles,
                    frame->dash_style_count,
                    frame->doubles,
                    frame->double_count,
                    segment_count,
                    engine->spline_sampled_points,
                    engine->spline_work,
                    vertices_to_add,
                    indices_to_add) ||
                vertex_capacity >
                    std::numeric_limits<std::uint32_t>::max() -
                        vertices_to_add ||
                vertex_capacity >
                    std::numeric_limits<std::size_t>::max() -
                        vertices_to_add ||
                index_capacity >
                    std::numeric_limits<std::size_t>::max() -
                        indices_to_add) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A spline range, degree, or indexed upload bound is invalid.");
            }
            for (std::size_t knot = 0U; knot < spline.knot_count; ++knot) {
                if (!std::isfinite(frame->doubles[spline.knot_offset + knot])) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A spline knot is not finite.");
                }
            }
            for (std::size_t weight = 0U;
                 weight < spline.weight_count;
                 ++weight) {
                if (!std::isfinite(
                        frame->doubles[spline.weight_offset + weight])) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A spline weight is not finite.");
                }
            }
            engine->spline_segment_counts[index] = segment_count;
            vertex_capacity += vertices_to_add;
            index_capacity += indices_to_add;
        }
        engine->vertices.reserve(vertex_capacity);
        engine->indices.reserve(index_capacity);
        engine->primitive_brush_indices.resize(frame->primitive_count);
        engine->polyline_brush_indices.resize(frame->polyline_count);
        engine->spline_brush_indices.resize(frame->spline_count);
        std::uint32_t brush_count = 1U;
        for (std::size_t index = 0; index < frame->primitive_count; ++index) {
            const std::uint32_t brush_index =
                progpu::native::geometry_uses_payload_brush(
                    frame->primitives[index])
                ? brush_count++
                : 0U;
            engine->primitive_brush_indices[index] = brush_index;
            if (!progpu::native::append_geometry_primitive(
                    frame->primitives[index],
                    static_cast<float>(brush_index),
                    engine->vertices,
                    engine->indices)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A geometry primitive contains invalid points, stroke state, color, transform, or flags.");
            }
        }
        for (std::size_t index = 0; index < frame->polyline_count; ++index) {
            const std::uint32_t brush_index = brush_count++;
            engine->polyline_brush_indices[index] = brush_index;
            const auto& polyline = frame->polylines[index];
            if (!progpu::native::append_polyline(
                    polyline,
                    frame->points + polyline.point_offset,
                    static_cast<float>(brush_index),
                    engine->vertices,
                    engine->indices,
                    frame->dash_styles,
                    frame->dash_style_count,
                    frame->doubles,
                    frame->double_count,
                    true)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A connected stroke contains invalid points, stroke state, transform, join, or flags.");
            }
        }
        for (std::size_t index = 0; index < frame->spline_count; ++index) {
            const auto& spline = frame->splines[index];
            const std::size_t segment_count =
                engine->spline_segment_counts[index];
            const std::uint32_t brush_index = segment_count == 0U
                ? 0U
                : brush_count++;
            engine->spline_brush_indices[index] = brush_index;
            if (!progpu::native::append_spline(
                    spline,
                    frame->points + spline.stroke.point_offset,
                    spline.knot_count == 0U
                        ? nullptr
                        : frame->doubles + spline.knot_offset,
                    spline.weight_count == 0U
                        ? nullptr
                        : frame->doubles + spline.weight_offset,
                    segment_count,
                    static_cast<float>(brush_index),
                    engine->spline_sampled_points,
                    engine->spline_work,
                    engine->vertices,
                    engine->indices,
                    frame->dash_styles,
                    frame->dash_style_count,
                    frame->doubles,
                    frame->double_count)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                    "A spline contains invalid control points, knots, weights, stroke state, or transform.");
            }
        }

        engine->brush_bytes.clear();
        engine->brush_bytes.resize(
            static_cast<std::size_t>(brush_count) * gpu_brush_size);
        set_brush_opacity(engine->brush_bytes, draw_state.opacity);
        for (std::size_t index = 0; index < frame->primitive_count; ++index) {
            const std::uint32_t brush_index =
                engine->primitive_brush_indices[index];
            if (brush_index == 0U) {
                continue;
            }
            std::byte* brush = engine->brush_bytes.data() +
                static_cast<std::size_t>(brush_index) * gpu_brush_size;
            std::memcpy(
                brush + 64U,
                &frame->primitives[index].color,
                sizeof(progpu_native_color));
        }
        for (std::size_t index = 0; index < frame->polyline_count; ++index) {
            const std::uint32_t brush_index =
                engine->polyline_brush_indices[index];
            std::byte* brush = engine->brush_bytes.data() +
                static_cast<std::size_t>(brush_index) * gpu_brush_size;
            std::memcpy(
                brush + 64U,
                &frame->polylines[index].color,
                sizeof(progpu_native_color));
        }
        for (std::size_t index = 0; index < frame->spline_count; ++index) {
            const std::uint32_t brush_index =
                engine->spline_brush_indices[index];
            if (brush_index == 0U) {
                continue;
            }
            std::byte* brush = engine->brush_bytes.data() +
                static_cast<std::size_t>(brush_index) * gpu_brush_size;
            std::memcpy(
                brush + 64U,
                &frame->splines[index].stroke.color,
                sizeof(progpu_native_color));
        }
        if (retain_compiled_payload) {
            engine->geometry_content_revision = frame->reserved;
            engine->geometry_opacity = draw_state.opacity;
            engine->geometry_payload_hash = 14695981039346656037ULL;
            engine->geometry_payload_hash = append_fnv1a64(
                engine->geometry_payload_hash,
                engine->vertices.data(),
                engine->vertices.size() *
                    sizeof(progpu::native::vector_vertex));
            engine->geometry_payload_hash = append_fnv1a64(
                engine->geometry_payload_hash,
                engine->indices.data(),
                engine->indices.size() * sizeof(std::uint32_t));
            engine->geometry_payload_hash = append_fnv1a64(
                engine->geometry_payload_hash,
                engine->brush_bytes.data(),
                engine->brush_bytes.size());
            engine->geometry_cache_valid = true;
        }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native geometry batch could not be allocated.");
        }
    }

    const bool opacity_changed = compiled_payload_hit &&
        engine->geometry_opacity != draw_state.opacity;
    if (opacity_changed) {
        set_brush_opacity(engine->brush_bytes, draw_state.opacity);
        engine->geometry_opacity = draw_state.opacity;
        engine->geometry_payload_hash = 14695981039346656037ULL;
        engine->geometry_payload_hash = append_fnv1a64(
            engine->geometry_payload_hash,
            engine->vertices.data(),
            engine->vertices.size() *
                sizeof(progpu::native::vector_vertex));
        engine->geometry_payload_hash = append_fnv1a64(
            engine->geometry_payload_hash,
            engine->indices.data(),
            engine->indices.size() * sizeof(std::uint32_t));
        engine->geometry_payload_hash = append_fnv1a64(
            engine->geometry_payload_hash,
            engine->brush_bytes.data(),
            engine->brush_bytes.size());
    }

    const std::uint64_t vertex_bytes =
        engine->vertices.size() * sizeof(progpu::native::vector_vertex);
    const std::uint64_t index_bytes =
        engine->indices.size() * sizeof(std::uint32_t);
    const std::uint64_t brush_upload_bytes = engine->brush_bytes.size();
    const bool upload_compiled_payload =
        !compiled_payload_hit || !engine->geometry_gpu_cache_valid;
    const bool upload_brush_payload =
        upload_compiled_payload || opacity_changed;
    bool uploaded_uniforms = false;
    std::uint64_t payload_hash = 0U;
    if ((frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH) != 0U &&
        retain_compiled_payload && engine->geometry_cache_valid) {
        payload_hash = engine->geometry_payload_hash;
    } else if ((frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH) != 0U) {
        payload_hash = 14695981039346656037ULL;
        payload_hash = append_fnv1a64(
            payload_hash,
            engine->vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
        payload_hash = append_fnv1a64(
            payload_hash,
            engine->indices.data(),
            static_cast<std::size_t>(index_bytes));
        payload_hash = append_fnv1a64(
            payload_hash,
            engine->brush_bytes.data(),
            engine->brush_bytes.size());
    }
    if (vertex_bytes != 0U) {
        if (engine->analytic_pipeline == nullptr &&
            !create_analytic_pipeline(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native indexed geometry WebGPU pipeline could not be created.");
        }
        if (!engine->ensure_vertex_buffer(vertex_bytes) ||
            !engine->ensure_index_buffer(index_bytes) ||
            !ensure_analytic_brush_buffer(*engine, brush_upload_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native indexed geometry WebGPU buffers could not be allocated.");
        }

        const gpu_uniforms uniforms = create_uniforms(
            frame->width,
            frame->height,
            frame->dpi_scale);
        uploaded_uniforms = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        if (upload_compiled_payload) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->vertex_buffer,
                0U,
                engine->vertices.data(),
                static_cast<std::size_t>(vertex_bytes));
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->index_buffer,
                0U,
                engine->indices.data(),
                static_cast<std::size_t>(index_bytes));
            engine->geometry_gpu_cache_valid = retain_compiled_payload;
        }
        if (upload_brush_payload) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->analytic_brush_buffer,
                0U,
                engine->brush_bytes.data(),
                engine->brush_bytes.size());
        }
    }

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native geometry frame encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine->device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native geometry command encoder could not be created.");
    }

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = !use_group_layer &&
            engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native indexed geometry pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        wgpuCommandEncoderRelease(encoder);
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native geometry render pass could not be created.");
    }

    if (!engine->indices.empty() && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->analytic_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            0U,
            engine->analytic_uniform_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            1U,
            engine->analytic_atlas_bind_group,
            0U,
            nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->vertex_buffer,
            0U,
            vertex_bytes);
        wgpuRenderPassEncoderSetIndexBuffer(
            pass,
            engine->index_buffer,
            WGPUIndexFormat_Uint32,
            0U,
            index_bytes);
        wgpuRenderPassEncoderDrawIndexed(
            pass,
            static_cast<std::uint32_t>(engine->indices.size()),
            1U,
            0U,
            0,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (use_group_layer) {
        engine->last_layer_metrics.content_pass_count = 1U;
        if (!encode_group_effect(
                *engine,
                encoder,
                draw_state,
                frame->dpi_scale) ||
            !encode_layer_composite(
                *engine,
                encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                frame->clear_color,
                draw_state)) {
            wgpuCommandEncoderRelease(encoder);
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The geometry group composite pass could not be created.");
        }
    }

    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native geometry frame commands");
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (command == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native geometry command buffer could not be finished.");
    }

    engine->submit(command);
    wgpuCommandBufferRelease(command);
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::geometry,
            frame->dpi_scale,
            draw_state);
    }
    engine->last_error.clear();

    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_geometry_frame_metrics)) {
        metrics->draw_call_count = engine->indices.empty() ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count =
            static_cast<std::uint32_t>(engine->vertices.size());
        metrics->index_count =
            static_cast<std::uint32_t>(engine->indices.size());
        metrics->vertex_upload_bytes = upload_compiled_payload
            ? vertex_bytes
            : 0U;
        metrics->index_upload_bytes = upload_compiled_payload
            ? index_bytes
            : 0U;
        metrics->brush_upload_bytes =
            engine->indices.empty() || !upload_brush_payload
                ? 0U
                : brush_upload_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(gpu_uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
        metrics->payload_hash = payload_hash;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_render_paths(
    progpu_native_engine* engine,
    const progpu_native_path_frame* frame,
    progpu_native_path_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_path_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->path_count != 0U && frame->paths == nullptr) ||
        (frame->segment_count != 0U && frame->segments == nullptr) ||
        (frame->flags &
            ~(PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH |
              PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD)) != 0U ||
        (((frame->flags &
                PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U) !=
            (frame->content_revision != 0U)) ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The path frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_path_frame)
            ? frame->draw_state
            : nullptr;
    if (!resolve_draw_state(
            requested_draw_state,
            frame->target_view,
            frame->width,
            frame->height,
            frame->dpi_scale,
            draw_state)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The path frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    if (!engine->semantic_path_draw_active) {
        engine->release_semantic_render_bundle();
        engine->semantic_path_gpu_scene_hash = 0U;
    }
    reset_layer_metrics(*engine);
    if (frame->path_count > (1U << 20U) ||
        frame->segment_count > (1U << 24U) ||
        frame->path_count >
            std::numeric_limits<std::uint32_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The path batch exceeds the native safety bound.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::path,
        frame->width,
        frame->height,
        frame->dpi_scale,
        reinterpret_cast<WGPUTextureView>(frame->target_view),
        frame->clear_color,
        draw_state,
        use_group_layer,
        group_cache_hit);
    if (group_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return group_status;
    }
    if (group_cache_hit) {
        if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_path_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    const bool retain_compiled_payload =
        (frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U;
    const bool compiled_payload_hit = retain_compiled_payload &&
        engine->path_cache_valid &&
        engine->path_content_revision == frame->content_revision &&
        engine->path_dpi_scale == frame->dpi_scale;
    std::uint64_t coverage_staging_bytes = 0U;
    std::uint64_t path_upload_bytes = 0U;
    std::uint32_t rasterized_path_count = 0U;
    std::uint32_t required_atlas_size = engine->path_atlas_size;

    std::vector<gpu_path_uniforms> path_uniforms;
    std::vector<gpu_path_record> path_records;
    if (!compiled_payload_hit) {
        engine->path_cache_valid = false;
        engine->path_gpu_cache_valid = false;
        try {
            engine->path_vertices.clear();
            engine->path_indices.clear();
            engine->path_brush_bytes.clear();
            engine->path_rasters.clear();
            path_uniforms.reserve(frame->path_count);
            path_records.reserve(frame->path_count);
            engine->path_rasters.reserve(frame->path_count);
            engine->path_vertices.reserve(frame->path_count * 4U);
            engine->path_indices.reserve(frame->path_count * 6U);
            engine->path_brush_bytes.resize(
                (frame->path_count + 1U) * gpu_brush_size);

            set_brush_opacity(
                engine->path_brush_bytes,
                draw_state.opacity);

            std::uint32_t atlas_x = 2U;
            std::uint32_t atlas_y = 2U;
            std::uint32_t row_height = 0U;
            std::uint32_t output_offset = 0U;
            std::unordered_map<
                native_path_cache_key,
                std::size_t,
                native_path_cache_key_hash> retained_tiles;
            retained_tiles.reserve(frame->path_count);
            for (std::size_t segment_index = 0U;
                 segment_index < frame->segment_count;
                 ++segment_index) {
                const auto& segment = frame->segments[segment_index];
                const bool is_arc =
                    segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC;
                if (segment.kind > PROGPU_NATIVE_PATH_SEGMENT_ARC ||
                    !progpu::native::is_finite(segment.p0) ||
                    !progpu::native::is_finite(segment.p1) ||
                    !progpu::native::is_finite(segment.p2) ||
                    !progpu::native::is_finite(segment.p3) ||
                    (is_arc &&
                        (segment.p3.x <= 0.0F || segment.p3.y <= 0.0F ||
                         !std::isfinite(std::bit_cast<float>(segment.pad0)) ||
                         !std::isfinite(std::bit_cast<float>(segment.pad1)) ||
                         !std::isfinite(std::bit_cast<float>(segment.pad2)))) ||
                    (!is_arc &&
                        (segment.pad0 != 0U || segment.pad1 != 0U ||
                         segment.pad2 != 0U))) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A path segment kind, point, arc, or reserved field is invalid.");
                }
            }
            for (std::size_t index = 0U;
                 index < frame->path_count;
                 ++index) {
                const auto& path = frame->paths[index];
                if (path.segment_count == 0U ||
                    path.segment_offset > frame->segment_count ||
                    path.segment_count >
                        frame->segment_count - path.segment_offset ||
                    !std::isfinite(path.min_x) ||
                    !std::isfinite(path.min_y) ||
                    !std::isfinite(path.max_x) ||
                    !std::isfinite(path.max_y) ||
                    path.max_x <= path.min_x ||
                    path.max_y <= path.min_y ||
                    !progpu::native::is_finite(path.color) ||
                    !progpu::native::is_finite(path.transform) ||
                    path.fill_rule > PROGPU_NATIVE_FILL_RULE_EVEN_ODD ||
                    (path.sample_grid != 4U && path.sample_grid != 8U)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A path range, bound, transform, fill rule, or sample grid is invalid.");
                }
                float maximum_scale = 0.0F;
                float minimum_scale = 0.0F;
                if (!progpu::native::try_get_stroke_scales(
                        path.transform,
                        maximum_scale,
                        minimum_scale)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A path transform is singular.");
                }
                (void)minimum_scale;
                const float raster_scale = maximum_scale;
                const float subpixel_x = quantize_subpixel_phase(
                    path.transform.m31);
                const float subpixel_y = quantize_subpixel_phase(
                    path.transform.m32);
                native_path_cache_key cache_key{};
                cache_key.segment_offset = path.segment_offset;
                cache_key.segment_count = path.segment_count;
                cache_key.min_x = std::bit_cast<std::uint32_t>(path.min_x);
                cache_key.min_y = std::bit_cast<std::uint32_t>(path.min_y);
                cache_key.max_x = std::bit_cast<std::uint32_t>(path.max_x);
                cache_key.max_y = std::bit_cast<std::uint32_t>(path.max_y);
                cache_key.scale = std::bit_cast<std::uint32_t>(raster_scale);
                cache_key.subpixel_x =
                    std::bit_cast<std::uint32_t>(subpixel_x);
                cache_key.subpixel_y =
                    std::bit_cast<std::uint32_t>(subpixel_y);
                cache_key.fill_rule = path.fill_rule;
                cache_key.sample_grid = path.sample_grid;
                const float raster_min_x =
                    std::floor(path.min_x * raster_scale) - path_padding;
                const float raster_min_y =
                    std::floor(path.min_y * raster_scale) - path_padding;
                const float raster_max_x =
                    std::ceil(path.max_x * raster_scale) + path_padding;
                const float raster_max_y =
                    std::ceil(path.max_y * raster_scale) + path_padding;
                const double raster_width = raster_max_x - raster_min_x;
                const double raster_height = raster_max_y - raster_min_y;
                if (!std::isfinite(raster_width) ||
                    !std::isfinite(raster_height) ||
                    raster_width <= 0.0 || raster_height <= 0.0 ||
                    raster_width > native_max_atlas_size - 4U ||
                    raster_height > native_max_atlas_size - 4U) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_UNSUPPORTED,
                        "A transformed path exceeds the bounded native atlas tile size.");
                }
                std::size_t raster_index = 0U;
                const auto retained_tile = retained_tiles.find(cache_key);
                if (retained_tile != retained_tiles.end()) {
                    raster_index = retained_tile->second;
                } else {
                    const auto width =
                        static_cast<std::uint32_t>(raster_width);
                    const auto height =
                        static_cast<std::uint32_t>(raster_height);
                    while (width + 4U > required_atlas_size &&
                           required_atlas_size < native_max_atlas_size) {
                        required_atlas_size *= 2U;
                    }
                    if (atlas_x + width + 2U > required_atlas_size) {
                        atlas_x = 2U;
                        atlas_y += row_height + 2U;
                        row_height = 0U;
                    }
                    while (atlas_y + height + 2U > required_atlas_size &&
                           required_atlas_size < native_max_atlas_size) {
                        required_atlas_size *= 2U;
                    }
                    if (atlas_y + height + 2U > required_atlas_size) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                            "The retained native path set does not fit the bounded atlas.");
                    }
                    const std::uint32_t output_bytes_per_row = align_up(
                        width,
                        webgpu_copy_row_alignment);
                    output_offset = align_up(
                        output_offset,
                        webgpu_copy_row_alignment);
                    const std::uint64_t next_output =
                        static_cast<std::uint64_t>(output_offset) +
                        static_cast<std::uint64_t>(output_bytes_per_row) * height;
                    if (next_output >
                        std::numeric_limits<std::uint32_t>::max()) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                            "The path coverage staging batch exceeds 4 GiB.");
                    }
                    raster_index = engine->path_rasters.size();
                    engine->path_rasters.push_back({
                        atlas_x,
                        atlas_y,
                        width,
                        height,
                        output_offset,
                        output_bytes_per_row,
                        raster_scale,
                        raster_scale,
                        raster_min_x,
                        raster_min_y,
                        subpixel_x,
                        subpixel_y
                    });
                    path_uniforms.push_back({
                        raster_min_x - subpixel_x,
                        raster_min_y - subpixel_y,
                        raster_scale,
                        raster_scale,
                        static_cast<std::uint32_t>(raster_index),
                        output_offset / 4U,
                        output_bytes_per_row / 4U,
                        width,
                        height,
                        path.sample_grid,
                        0U,
                        0U
                    });
                    path_records.push_back({
                        static_cast<std::uint32_t>(path.segment_offset),
                        static_cast<std::uint32_t>(path.segment_count),
                        path.min_x,
                        path.min_y,
                        path.max_x,
                        path.max_y,
                        path.fill_rule,
                        0U
                    });
                    retained_tiles.emplace(cache_key, raster_index);
                    output_offset = static_cast<std::uint32_t>(next_output);
                    atlas_x += width + 2U;
                    row_height = std::max(row_height, height);
                }
                const auto& raster = engine->path_rasters[raster_index];

                const float local_min_x = raster_min_x / raster_scale;
                const float local_min_y = raster_min_y / raster_scale;
                const float local_max_x = raster_max_x / raster_scale;
                const float local_max_y = raster_max_y / raster_scale;
                const std::array<progpu_native_point, 4U> local_points{{
                    {local_min_x, local_min_y},
                    {local_max_x, local_min_y},
                    {local_max_x, local_max_y},
                    {local_min_x, local_max_y}
                }};
                const std::array<progpu_native_point, 4U> atlas_points{{
                    {raster.atlas_x + subpixel_x, raster.atlas_y + subpixel_y},
                    {raster.atlas_x + raster.width + subpixel_x, raster.atlas_y + subpixel_y},
                    {raster.atlas_x + raster.width + subpixel_x, raster.atlas_y + raster.height + subpixel_y},
                    {raster.atlas_x + subpixel_x, raster.atlas_y + raster.height + subpixel_y}
                }};
                const std::uint32_t vertex_start = static_cast<std::uint32_t>(
                    engine->path_vertices.size());
                for (std::size_t corner = 0U; corner < 4U; ++corner) {
                    progpu::native::vector_vertex vertex{};
                    progpu::native::transform_point(
                        path.transform,
                        local_points[corner].x,
                        local_points[corner].y,
                        vertex.position[0],
                        vertex.position[1]);
                    std::memcpy(
                        vertex.color,
                        &path.color,
                        sizeof(path.color));
                    vertex.texture_coordinate[0] = atlas_points[corner].x;
                    vertex.texture_coordinate[1] = atlas_points[corner].y;
                    vertex.brush_index = static_cast<float>(index + 1U);
                    vertex.shape_size[0] = local_points[corner].x;
                    vertex.shape_size[1] = local_points[corner].y;
                    vertex.corner_radius = 1.0F;
                    vertex.shape_type = 4.0F;
                    engine->path_vertices.push_back(vertex);
                }
                engine->path_indices.insert(
                    engine->path_indices.end(),
                    {vertex_start, vertex_start + 1U, vertex_start + 2U,
                     vertex_start, vertex_start + 2U, vertex_start + 3U});
                std::memcpy(
                    engine->path_brush_bytes.data() +
                        (index + 1U) * gpu_brush_size + 64U,
                    &path.color,
                    sizeof(path.color));

            }
            coverage_staging_bytes = output_offset;
            rasterized_path_count = static_cast<std::uint32_t>(
                engine->path_rasters.size());
            if (retain_compiled_payload) {
                engine->path_content_revision = frame->content_revision;
                engine->path_dpi_scale = frame->dpi_scale;
                engine->path_opacity = draw_state.opacity;
                engine->path_payload_hash = 14695981039346656037ULL;
                engine->path_payload_hash = append_fnv1a64(
                    engine->path_payload_hash,
                    engine->path_vertices.data(),
                    engine->path_vertices.size() *
                        sizeof(progpu::native::vector_vertex));
                engine->path_payload_hash = append_fnv1a64(
                    engine->path_payload_hash,
                    engine->path_indices.data(),
                    engine->path_indices.size() * sizeof(std::uint32_t));
                engine->path_payload_hash = append_fnv1a64(
                    engine->path_payload_hash,
                    engine->path_brush_bytes.data(),
                    engine->path_brush_bytes.size());
                engine->path_cache_valid = true;
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native path batch could not be allocated.");
        }
    }

    const bool opacity_changed = compiled_payload_hit &&
        engine->path_opacity != draw_state.opacity;
    if (opacity_changed) {
        set_brush_opacity(engine->path_brush_bytes, draw_state.opacity);
        engine->path_opacity = draw_state.opacity;
        engine->path_payload_hash = 14695981039346656037ULL;
        engine->path_payload_hash = append_fnv1a64(
            engine->path_payload_hash,
            engine->path_vertices.data(),
            engine->path_vertices.size() *
                sizeof(progpu::native::vector_vertex));
        engine->path_payload_hash = append_fnv1a64(
            engine->path_payload_hash,
            engine->path_indices.data(),
            engine->path_indices.size() * sizeof(std::uint32_t));
        engine->path_payload_hash = append_fnv1a64(
            engine->path_payload_hash,
            engine->path_brush_bytes.data(),
            engine->path_brush_bytes.size());
    }

    const std::uint32_t atlas_generation_before =
        engine->path_atlas_generation;
    if (engine->path_atlas_texture == nullptr) {
        engine->path_atlas_size = required_atlas_size;
    }
    if (!create_path_resources(*engine) ||
        !resize_path_atlas(*engine, required_atlas_size)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native path atlas WebGPU resources could not be created.");
    }
    if (!compiled_payload_hit && frame->path_count != 0U &&
        engine->path_atlas_generation == atlas_generation_before) {
        ++engine->path_atlas_generation;
    }

    const std::uint64_t vertex_bytes = engine->path_vertices.size() *
        sizeof(progpu::native::vector_vertex);
    const std::uint64_t index_bytes = engine->path_indices.size() *
        sizeof(std::uint32_t);
    const std::uint64_t brush_bytes = engine->path_brush_bytes.size();
    const bool upload_draw_payload =
        !compiled_payload_hit || !engine->path_gpu_cache_valid;
    const bool upload_brush_payload = upload_draw_payload || opacity_changed;
    bool uploaded_uniforms = false;
    if (vertex_bytes != 0U &&
        (!engine->ensure_path_vertex_buffer(vertex_bytes) ||
         !engine->ensure_path_index_buffer(index_bytes) ||
         !ensure_analytic_brush_buffer(*engine, brush_bytes))) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native path draw buffers could not be allocated.");
    }
    const gpu_uniforms uniforms = create_uniforms(
        frame->width,
        frame->height,
        frame->dpi_scale);
    if (vertex_bytes != 0U) {
        uploaded_uniforms = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        if (upload_draw_payload) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->path_vertex_buffer,
                0U,
                engine->path_vertices.data(),
                vertex_bytes);
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->path_index_buffer,
                0U,
                engine->path_indices.data(),
                index_bytes);
            engine->path_gpu_cache_valid = retain_compiled_payload;
            engine->geometry_gpu_cache_valid = false;
        }
        if (upload_brush_payload) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->analytic_brush_buffer,
                0U,
                engine->path_brush_bytes.data(),
                brush_bytes);
        }
    }
    path_raster_resources temporary;
    WGPUBuffer& path_uniform_buffer = temporary.uniforms;
    WGPUBuffer& path_record_buffer = temporary.records;
    WGPUBuffer& path_segment_buffer = temporary.segments;
    WGPUBuffer& coverage_buffer = temporary.coverage;
    WGPUBindGroup& raster_bind_group = temporary.bind_group;
    const auto create_buffer = [&](
        const char* label,
        std::uint64_t size,
        progpu::native::webgpu::buffer_usage_flags usage) -> WGPUBuffer {
        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(label);
        descriptor.size = std::max<std::uint64_t>(size, 4U);
        descriptor.usage = usage;
        return wgpuDeviceCreateBuffer(engine->device, &descriptor);
    };
    if (!compiled_payload_hit && frame->path_count != 0U) {
        path_uniform_buffer = create_buffer(
            "ProGPU native path uniforms",
            path_uniforms.size() * sizeof(gpu_path_uniforms),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        path_record_buffer = create_buffer(
            "ProGPU native path records",
            path_records.size() * sizeof(gpu_path_record),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        path_segment_buffer = create_buffer(
            "ProGPU native path segments",
            frame->segment_count * sizeof(progpu_native_path_segment),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        coverage_buffer = create_buffer(
            "ProGPU native path coverage staging",
            coverage_staging_bytes,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc);
        if (path_uniform_buffer == nullptr || path_record_buffer == nullptr ||
            path_segment_buffer == nullptr || coverage_buffer == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native path raster staging buffers could not be allocated.");
        }
        const std::uint64_t uniform_bytes = path_uniforms.size() *
            sizeof(gpu_path_uniforms);
        const std::uint64_t record_bytes = path_records.size() *
            sizeof(gpu_path_record);
        const std::uint64_t segment_bytes = frame->segment_count *
            sizeof(progpu_native_path_segment);
        wgpuQueueWriteBuffer(engine->queue, path_uniform_buffer, 0U,
            path_uniforms.data(), uniform_bytes);
        wgpuQueueWriteBuffer(engine->queue, path_record_buffer, 0U,
            path_records.data(), record_bytes);
        wgpuQueueWriteBuffer(engine->queue, path_segment_buffer, 0U,
            frame->segments, segment_bytes);
        path_upload_bytes = uniform_bytes + record_bytes + segment_bytes;

        const std::array<WGPUBindGroupEntry, 4U> entries{{
            {nullptr, 0U, path_uniform_buffer, 0U, uniform_bytes,
                nullptr, nullptr},
            {nullptr, 1U, path_record_buffer, 0U, record_bytes,
                nullptr, nullptr},
            {nullptr, 2U, path_segment_buffer, 0U, segment_bytes,
                nullptr, nullptr},
            {nullptr, 3U, coverage_buffer, 0U, coverage_staging_bytes,
                nullptr, nullptr}
        }};
        WGPUBindGroupDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster bind group");
        descriptor.layout = engine->path_raster_layout;
        descriptor.entryCount = entries.size();
        descriptor.entries = entries.data();
        raster_bind_group = wgpuDeviceCreateBindGroup(
            engine->device,
            &descriptor);
        if (raster_bind_group == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native path raster bind group could not be created.");
        }
    }

    const bool owns_encoder = engine->semantic_encoder == nullptr;
    WGPUCommandEncoder encoder = engine->semantic_encoder;
    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path frame encoder");
    if (owns_encoder) {
        encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
    }
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native path command encoder could not be created.");
    }

    if (raster_bind_group != nullptr) {
        std::uint32_t workgroups_x = 0U;
        std::uint32_t workgroups_y = 0U;
        for (const auto& raster : engine->path_rasters) {
            workgroups_x = std::max(
                workgroups_x,
                (raster.width + 63U) / 64U);
            workgroups_y = std::max(
                workgroups_y,
                (raster.height + 15U) / 16U);
        }
        WGPUComputePassDescriptor compute_descriptor{};
        compute_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path coverage pass");
        WGPUComputePassEncoder compute_pass =
            wgpuCommandEncoderBeginComputePass(encoder, &compute_descriptor);
        if (compute_pass == nullptr) {
            if (owns_encoder) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native path compute pass could not be created.");
        }
        wgpuComputePassEncoderSetPipeline(
            compute_pass,
            engine->path_raster_pipeline);
        wgpuComputePassEncoderSetBindGroup(
            compute_pass,
            0U,
            raster_bind_group,
            0U,
            nullptr);
        wgpuComputePassEncoderDispatchWorkgroups(
            compute_pass,
            workgroups_x,
            workgroups_y,
            static_cast<std::uint32_t>(engine->path_rasters.size()));
        wgpuComputePassEncoderEnd(compute_pass);
        wgpuComputePassEncoderRelease(compute_pass);

        for (const auto& raster : engine->path_rasters) {
            progpu::native::webgpu::image_copy_buffer source{};
            source.buffer = coverage_buffer;
            source.layout.offset = raster.output_offset;
            source.layout.bytesPerRow = raster.output_bytes_per_row;
            source.layout.rowsPerImage = raster.height;
            progpu::native::webgpu::image_copy_texture destination{};
            destination.texture = engine->path_atlas_texture;
            destination.origin = {raster.atlas_x, raster.atlas_y, 0U};
            destination.aspect = WGPUTextureAspect_All;
            const WGPUExtent3D extent{raster.width, raster.height, 1U};
            wgpuCommandEncoderCopyBufferToTexture(
                encoder,
                &source,
                &destination,
                &extent);
        }
    }

    const std::uint32_t selected_first_index =
        engine->semantic_path_draw_active
        ? engine->semantic_path_first_index
        : 0U;
    const std::uint32_t selected_index_count =
        engine->semantic_path_draw_active
        ? engine->semantic_path_index_count
        : static_cast<std::uint32_t>(engine->path_indices.size());
    if (!engine->semantic_prepare_only) {
    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = !use_group_layer &&
            engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native path render pass could not be created.");
    }
    if (selected_first_index > engine->path_indices.size() ||
        selected_index_count >
            engine->path_indices.size() - selected_first_index) {
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic path packed-page draw range is invalid.");
    }
    if (selected_index_count != 0U && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->analytic_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 0U, engine->analytic_uniform_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 1U, engine->path_atlas_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass, 0U, engine->path_vertex_buffer, 0U, vertex_bytes);
        wgpuRenderPassEncoderSetIndexBuffer(
            pass,
            engine->path_index_buffer,
            WGPUIndexFormat_Uint32,
            0U,
            index_bytes);
        wgpuRenderPassEncoderDrawIndexed(
            pass,
            selected_index_count,
            1U,
            selected_first_index,
            0,
            0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (use_group_layer) {
        engine->last_layer_metrics.content_pass_count = 1U;
        if (!encode_group_effect(
                *engine,
                encoder,
                draw_state,
                frame->dpi_scale) ||
            !encode_layer_composite(
                *engine,
                encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                frame->clear_color,
                draw_state)) {
            if (owns_encoder) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The path group composite pass could not be created.");
        }
    }

    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native path command buffer could not be finished.");
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
    }
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::path,
            frame->dpi_scale,
            draw_state);
    }
    }

    std::uint64_t payload_hash = 0U;
    if ((frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH) != 0U) {
        payload_hash = retain_compiled_payload
            ? engine->path_payload_hash
            : append_fnv1a64(
                append_fnv1a64(
                    append_fnv1a64(
                        14695981039346656037ULL,
                        engine->path_vertices.data(),
                        vertex_bytes),
                    engine->path_indices.data(),
                    index_bytes),
                engine->path_brush_bytes.data(),
                engine->path_brush_bytes.size());
    }
    engine->last_error.clear();
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_path_frame_metrics)) {
        metrics->draw_call_count = engine->semantic_prepare_only ||
            selected_index_count == 0U ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count = static_cast<std::uint32_t>(
            engine->path_vertices.size());
        metrics->index_count = static_cast<std::uint32_t>(
            engine->path_indices.size());
        metrics->rasterized_path_count = rasterized_path_count;
        metrics->atlas_width = engine->path_atlas_size;
        metrics->atlas_height = engine->path_atlas_size;
        metrics->atlas_generation = engine->path_atlas_generation;
        metrics->vertex_upload_bytes = upload_draw_payload ? vertex_bytes : 0U;
        metrics->index_upload_bytes = upload_draw_payload ? index_bytes : 0U;
        metrics->brush_upload_bytes = upload_brush_payload ? brush_bytes : 0U;
        metrics->path_upload_bytes = path_upload_bytes;
        metrics->coverage_staging_bytes = coverage_staging_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(gpu_uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
        metrics->payload_hash = payload_hash;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_render_glyphs(
    progpu_native_engine* engine,
    const progpu_native_glyph_frame* frame,
    progpu_native_glyph_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_glyph_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        (frame->outline_count != 0U && frame->outlines == nullptr) ||
        (frame->segment_count != 0U && frame->segments == nullptr) ||
        (frame->glyph_count != 0U && frame->glyphs == nullptr) ||
        (frame->flags &
            ~(PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH |
              PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD)) != 0U ||
        (((frame->flags &
                PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U) !=
            (frame->content_revision != 0U)) ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The positioned glyph frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_glyph_frame)
            ? frame->draw_state
            : nullptr;
    if (!resolve_draw_state(
            requested_draw_state,
            frame->target_view,
            frame->width,
            frame->height,
            frame->dpi_scale,
            draw_state)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The positioned glyph frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    if (!engine->semantic_glyph_draw_active) {
        engine->release_semantic_render_bundle();
        engine->semantic_glyph_gpu_scene_hash = 0U;
    }
    reset_layer_metrics(*engine);
    if (frame->outline_count > (1U << 20U) ||
        frame->segment_count > (1U << 24U) ||
        frame->glyph_count > (1U << 24U)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The positioned glyph batch exceeds the native safety bound.");
    }
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::glyph,
        frame->width,
        frame->height,
        frame->dpi_scale,
        reinterpret_cast<WGPUTextureView>(frame->target_view),
        frame->clear_color,
        draw_state,
        use_group_layer,
        group_cache_hit);
    if (group_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return group_status;
    }
    if (group_cache_hit) {
        if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_glyph_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    const bool retain_compiled_payload =
        (frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD) != 0U;
    const bool compiled_payload_hit = retain_compiled_payload &&
        engine->glyph_cache_valid &&
        engine->glyph_content_revision == frame->content_revision &&
        engine->glyph_dpi_scale == frame->dpi_scale;
    std::vector<gpu_glyph_record> records;
    std::vector<gpu_glyph_uniforms> uniforms;
    std::uint64_t coverage_staging_bytes = 0U;
    std::uint64_t outline_upload_bytes = 0U;
    std::uint32_t rasterized_glyph_count = 0U;
    std::uint32_t required_atlas_size = engine->glyph_atlas_size;

    if (!compiled_payload_hit) {
        engine->glyph_cache_valid = false;
        engine->glyph_gpu_cache_valid = false;
        try {
            records.reserve(frame->outline_count);
            uniforms.reserve(frame->outline_count);
            engine->glyph_rasters.clear();
            engine->glyph_rasters.reserve(frame->outline_count);
            engine->glyph_instances.clear();
            engine->glyph_instances.reserve(frame->glyph_count);
            engine->glyph_source_alphas.clear();
            engine->glyph_source_alphas.reserve(frame->glyph_count);

            for (std::size_t index = 0U;
                 index < frame->segment_count;
                 ++index) {
                const auto& segment = frame->segments[index];
                if (segment.kind > PROGPU_NATIVE_PATH_SEGMENT_CUBIC ||
                    !progpu::native::is_finite(segment.p0) ||
                    !progpu::native::is_finite(segment.p1) ||
                    !progpu::native::is_finite(segment.p2) ||
                    !progpu::native::is_finite(segment.p3) ||
                    segment.pad0 != 0U || segment.pad1 != 0U ||
                    segment.pad2 != 0U) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A glyph segment kind, point, or reserved field is invalid.");
                }
            }

            std::uint32_t atlas_x = 2U;
            std::uint32_t atlas_y = 2U;
            std::uint32_t row_height = 0U;
            std::uint32_t output_offset = 0U;
            for (std::size_t index = 0U;
                 index < frame->outline_count;
                 ++index) {
                const auto& outline = frame->outlines[index];
                if (outline.segment_count == 0U ||
                    outline.segment_offset > frame->segment_count ||
                    outline.segment_count >
                        frame->segment_count - outline.segment_offset ||
                    !std::isfinite(outline.min_x) ||
                    !std::isfinite(outline.min_y) ||
                    !std::isfinite(outline.max_x) ||
                    !std::isfinite(outline.max_y) ||
                    outline.max_x <= outline.min_x ||
                    outline.max_y <= outline.min_y ||
                    !std::isfinite(outline.raster_scale) ||
                    outline.raster_scale <= 0.0F ||
                    !std::isfinite(outline.subpixel_x) ||
                    outline.subpixel_x < 0.0F ||
                    outline.subpixel_x > 0.75F ||
                    std::abs(
                        outline.subpixel_x * 4.0F -
                        std::round(outline.subpixel_x * 4.0F)) > 0.0001F) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A glyph outline range, bound, scale, or phase is invalid.");
                }
                const float scaled_min_x =
                    outline.min_x * outline.raster_scale;
                const float scaled_min_y =
                    -outline.max_y * outline.raster_scale;
                const float scaled_max_x =
                    outline.max_x * outline.raster_scale;
                const float scaled_max_y =
                    -outline.min_y * outline.raster_scale;
                const float x_start = std::floor(scaled_min_x) - path_padding;
                const float y_start = std::floor(scaled_min_y) - path_padding;
                const double width_value =
                    std::ceil(scaled_max_x) + path_padding - x_start;
                const double height_value =
                    std::ceil(scaled_max_y) + path_padding - y_start;
                if (!std::isfinite(width_value) ||
                    !std::isfinite(height_value) ||
                    width_value <= 0.0 || height_value <= 0.0 ||
                    width_value > native_max_atlas_size - 4U ||
                    height_value > native_max_atlas_size - 4U) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_UNSUPPORTED,
                        "A glyph exceeds the bounded native atlas tile size.");
                }
                const auto width = static_cast<std::uint32_t>(width_value);
                const auto height = static_cast<std::uint32_t>(height_value);
                while (width + 4U > required_atlas_size &&
                       required_atlas_size < native_max_atlas_size) {
                    required_atlas_size *= 2U;
                }
                if (atlas_x + width + 2U > required_atlas_size) {
                    atlas_x = 2U;
                    atlas_y += row_height + 2U;
                    row_height = 0U;
                }
                while (atlas_y + height + 2U > required_atlas_size &&
                       required_atlas_size < native_max_atlas_size) {
                    required_atlas_size *= 2U;
                }
                if (atlas_y + height + 2U > required_atlas_size) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "The retained native glyph set does not fit the bounded atlas.");
                }
                const std::uint32_t output_bytes_per_row = align_up(
                    width,
                    webgpu_copy_row_alignment);
                output_offset = align_up(
                    output_offset,
                    webgpu_copy_row_alignment);
                const std::uint64_t next_output =
                    static_cast<std::uint64_t>(output_offset) +
                    static_cast<std::uint64_t>(output_bytes_per_row) * height;
                if (next_output >
                    std::numeric_limits<std::uint32_t>::max()) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "The glyph coverage staging batch exceeds 4 GiB.");
                }
                engine->glyph_rasters.push_back({
                    atlas_x,
                    atlas_y,
                    width,
                    height,
                    output_offset,
                    output_bytes_per_row,
                    x_start,
                    y_start
                });
                records.push_back({
                    static_cast<std::uint32_t>(outline.segment_offset),
                    static_cast<std::uint32_t>(outline.segment_count),
                    outline.min_x,
                    outline.min_y,
                    outline.max_x,
                    outline.max_y,
                    0U,
                    0U
                });
                uniforms.push_back({
                    x_start,
                    y_start,
                    outline.raster_scale,
                    static_cast<std::uint32_t>(index),
                    output_offset / 4U,
                    output_bytes_per_row / 4U,
                    width,
                    height,
                    outline.subpixel_x,
                    0.0F,
                    0.0F,
                    0.0F
                });
                output_offset = static_cast<std::uint32_t>(next_output);
                atlas_x += width + 2U;
                row_height = std::max(row_height, height);
            }

            for (std::size_t index = 0U;
                 index < frame->glyph_count;
                 ++index) {
                const auto& glyph = frame->glyphs[index];
                if (glyph.outline_index >= frame->outline_count ||
                    glyph.reserved != 0U || glyph.reserved2 != 0.0F ||
                    !progpu::native::is_finite(glyph.position) ||
                    !progpu::native::is_finite(glyph.basis_x) ||
                    !progpu::native::is_finite(glyph.basis_y) ||
                    !progpu::native::is_finite(glyph.color) ||
                    !std::isfinite(glyph.atlas_to_logical_scale) ||
                    glyph.atlas_to_logical_scale <= 0.0F ||
                    !std::isfinite(glyph.bold_offset) ||
                    !std::isfinite(glyph.italic_skew)) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                        "A positioned glyph reference or presentation value is invalid.");
                }
                const auto& raster =
                    engine->glyph_rasters[glyph.outline_index];
                gpu_glyph_instance instance{};
                std::memcpy(
                    instance.snapped_logical_position,
                    &glyph.position,
                    sizeof(glyph.position));
                std::memcpy(
                    instance.basis_x,
                    &glyph.basis_x,
                    sizeof(glyph.basis_x));
                std::memcpy(
                    instance.basis_y,
                    &glyph.basis_y,
                    sizeof(glyph.basis_y));
                instance.bear_size[0] = raster.x_start;
                instance.bear_size[1] = raster.y_start;
                instance.bear_size[2] = static_cast<float>(raster.width);
                instance.bear_size[3] = static_cast<float>(raster.height);
                instance.texture_coordinates[0] =
                    static_cast<float>(raster.atlas_x);
                instance.texture_coordinates[1] =
                    static_cast<float>(raster.atlas_y);
                instance.texture_coordinates[2] =
                    static_cast<float>(raster.atlas_x + raster.width);
                instance.texture_coordinates[3] =
                    static_cast<float>(raster.atlas_y + raster.height);
                std::memcpy(
                    instance.color,
                    &glyph.color,
                    sizeof(glyph.color));
                instance.color[3] *= draw_state.opacity;
                instance.scale_bold_italic_flags[0] =
                    glyph.atlas_to_logical_scale;
                instance.scale_bold_italic_flags[1] = glyph.bold_offset;
                instance.scale_bold_italic_flags[2] = glyph.italic_skew;
                instance.scale_bold_italic_flags[3] = 0.0F;
                instance.brush_index = -1.0F;
                engine->glyph_instances.push_back(instance);
                engine->glyph_source_alphas.push_back(glyph.color.a);
            }

            coverage_staging_bytes = output_offset;
            rasterized_glyph_count = static_cast<std::uint32_t>(
                engine->glyph_rasters.size());
            if (retain_compiled_payload) {
                engine->glyph_content_revision = frame->content_revision;
                engine->glyph_dpi_scale = frame->dpi_scale;
                engine->glyph_opacity = draw_state.opacity;
                engine->glyph_payload_hash = append_fnv1a64(
                    14695981039346656037ULL,
                    engine->glyph_instances.data(),
                    engine->glyph_instances.size() *
                        sizeof(gpu_glyph_instance));
                engine->glyph_payload_hash = append_fnv1a64(
                    engine->glyph_payload_hash,
                    frame->outlines,
                    frame->outline_count *
                        sizeof(progpu_native_glyph_outline));
                engine->glyph_payload_hash = append_fnv1a64(
                    engine->glyph_payload_hash,
                    frame->segments,
                    frame->segment_count *
                        sizeof(progpu_native_path_segment));
                engine->glyph_cache_valid = true;
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native positioned glyph batch could not be allocated.");
        }
    }

    const bool opacity_changed = compiled_payload_hit &&
        engine->glyph_opacity != draw_state.opacity;
    if (opacity_changed) {
        if (engine->glyph_source_alphas.size() !=
            engine->glyph_instances.size()) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The retained glyph opacity cache is inconsistent.");
        }
        for (std::size_t index = 0U;
             index < engine->glyph_instances.size();
             ++index) {
            engine->glyph_instances[index].color[3] =
                engine->glyph_source_alphas[index] * draw_state.opacity;
        }
        engine->glyph_opacity = draw_state.opacity;
        engine->glyph_payload_hash = append_fnv1a64(
            14695981039346656037ULL,
            engine->glyph_instances.data(),
            engine->glyph_instances.size() * sizeof(gpu_glyph_instance));
        engine->glyph_payload_hash = append_fnv1a64(
            engine->glyph_payload_hash,
            frame->outlines,
            frame->outline_count * sizeof(progpu_native_glyph_outline));
        engine->glyph_payload_hash = append_fnv1a64(
            engine->glyph_payload_hash,
            frame->segments,
            frame->segment_count * sizeof(progpu_native_path_segment));
    }

    const std::uint32_t atlas_generation_before =
        engine->glyph_atlas_generation;
    if (engine->glyph_atlas_texture == nullptr) {
        while (engine->glyph_atlas_size < required_atlas_size) {
            engine->glyph_atlas_size *= 2U;
            ++engine->glyph_atlas_growth_count;
        }
    }
    if (!create_glyph_resources(*engine) ||
        !resize_glyph_atlas(*engine, required_atlas_size)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native glyph atlas WebGPU resources could not be created.");
    }
    if (!compiled_payload_hit && frame->outline_count != 0U &&
        engine->glyph_atlas_generation == atlas_generation_before) {
        ++engine->glyph_atlas_generation;
    }
    const std::uint64_t instance_bytes = engine->glyph_instances.size() *
        sizeof(gpu_glyph_instance);
    const bool upload_instances =
        !compiled_payload_hit || !engine->glyph_gpu_cache_valid ||
        opacity_changed;
    if (instance_bytes != 0U &&
        !engine->ensure_text_vertex_buffer(instance_bytes)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native positioned glyph instance buffer could not be allocated.");
    }
    bool uploaded_uniforms = false;
    if (instance_bytes != 0U) {
        const gpu_uniforms frame_uniforms = create_uniforms(
            frame->width,
            frame->height,
            frame->dpi_scale);
        uploaded_uniforms = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            frame_uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        if (upload_instances) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->text_vertex_buffer,
                0U,
                engine->glyph_instances.data(),
                instance_bytes);
            engine->glyph_gpu_cache_valid = retain_compiled_payload;
        }
    }
    path_raster_resources temporary;
    std::vector<std::byte> uniform_bytes;
    if (!compiled_payload_hit && frame->outline_count != 0U) {
        try {
            uniform_bytes.resize(frame->outline_count * 256U);
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The glyph uniform staging arena could not be allocated.");
        }
        for (std::size_t index = 0U; index < uniforms.size(); ++index) {
            std::memcpy(
                uniform_bytes.data() + index * 256U,
                &uniforms[index],
                sizeof(gpu_glyph_uniforms));
        }
        const auto create_buffer = [&engine](
            const char* label,
            std::uint64_t size,
            progpu::native::webgpu::buffer_usage_flags usage) -> WGPUBuffer {
            WGPUBufferDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(label);
            descriptor.size = std::max<std::uint64_t>(size, 4U);
            descriptor.usage = usage;
            return wgpuDeviceCreateBuffer(engine->device, &descriptor);
        };
        temporary.uniforms = create_buffer(
            "ProGPU native glyph uniform ring",
            uniform_bytes.size(),
            WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst);
        temporary.records = create_buffer(
            "ProGPU native glyph records",
            records.size() * sizeof(gpu_glyph_record),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.segments = create_buffer(
            "ProGPU native glyph segments",
            frame->segment_count * sizeof(progpu_native_path_segment),
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.coverage = create_buffer(
            "ProGPU native glyph coverage staging",
            coverage_staging_bytes,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc);
        if (temporary.uniforms == nullptr || temporary.records == nullptr ||
            temporary.segments == nullptr || temporary.coverage == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The native glyph raster staging buffers could not be allocated.");
        }
        const std::uint64_t record_bytes = records.size() *
            sizeof(gpu_glyph_record);
        const std::uint64_t segment_bytes = frame->segment_count *
            sizeof(progpu_native_path_segment);
        wgpuQueueWriteBuffer(
            engine->queue,
            temporary.uniforms,
            0U,
            uniform_bytes.data(),
            uniform_bytes.size());
        wgpuQueueWriteBuffer(
            engine->queue,
            temporary.records,
            0U,
            records.data(),
            record_bytes);
        wgpuQueueWriteBuffer(
            engine->queue,
            temporary.segments,
            0U,
            frame->segments,
            segment_bytes);
        outline_upload_bytes = uniform_bytes.size() +
            record_bytes + segment_bytes;
        const std::array<WGPUBindGroupEntry, 4U> entries{{
            {nullptr, 0U, temporary.uniforms, 0U,
                sizeof(gpu_glyph_uniforms), nullptr, nullptr},
            {nullptr, 1U, temporary.records, 0U,
                record_bytes, nullptr, nullptr},
            {nullptr, 2U, temporary.segments, 0U,
                segment_bytes, nullptr, nullptr},
            {nullptr, 3U, temporary.coverage, 0U,
                coverage_staging_bytes, nullptr, nullptr}
        }};
        WGPUBindGroupDescriptor bind_group_descriptor{};
        bind_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph raster bind group");
        bind_group_descriptor.layout = engine->glyph_raster_layout;
        bind_group_descriptor.entryCount = entries.size();
        bind_group_descriptor.entries = entries.data();
        temporary.bind_group = wgpuDeviceCreateBindGroup(
            engine->device,
            &bind_group_descriptor);
        if (temporary.bind_group == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native glyph raster bind group could not be created.");
        }
    }

    const bool owns_encoder = engine->semantic_encoder == nullptr;
    WGPUCommandEncoder encoder = engine->semantic_encoder;
    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph frame encoder");
    if (owns_encoder) {
        encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
    }
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native positioned glyph command encoder could not be created.");
    }
    if (temporary.bind_group != nullptr) {
        WGPUComputePassDescriptor compute_descriptor{};
        compute_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph coverage pass");
        WGPUComputePassEncoder compute_pass =
            wgpuCommandEncoderBeginComputePass(encoder, &compute_descriptor);
        if (compute_pass == nullptr) {
            if (owns_encoder) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native glyph compute pass could not be created.");
        }
        wgpuComputePassEncoderSetPipeline(
            compute_pass,
            engine->glyph_raster_pipeline);
        for (std::uint32_t index = 0U;
             index < engine->glyph_rasters.size();
             ++index) {
            const std::uint32_t dynamic_offset = index * 256U;
            wgpuComputePassEncoderSetBindGroup(
                compute_pass,
                0U,
                temporary.bind_group,
                1U,
                &dynamic_offset);
            const auto& raster = engine->glyph_rasters[index];
            wgpuComputePassEncoderDispatchWorkgroups(
                compute_pass,
                (raster.width + 63U) / 64U,
                (raster.height + 15U) / 16U,
                1U);
        }
        wgpuComputePassEncoderEnd(compute_pass);
        wgpuComputePassEncoderRelease(compute_pass);
        for (const auto& raster : engine->glyph_rasters) {
            progpu::native::webgpu::image_copy_buffer source{};
            source.buffer = temporary.coverage;
            source.layout.offset = raster.output_offset;
            source.layout.bytesPerRow = raster.output_bytes_per_row;
            source.layout.rowsPerImage = raster.height;
            progpu::native::webgpu::image_copy_texture destination{};
            destination.texture = engine->glyph_atlas_texture;
            destination.origin = {raster.atlas_x, raster.atlas_y, 0U};
            destination.aspect = WGPUTextureAspect_All;
            const WGPUExtent3D extent{raster.width, raster.height, 1U};
            wgpuCommandEncoderCopyBufferToTexture(
                encoder,
                &source,
                &destination,
                &extent);
        }
    }

    const std::uint32_t selected_first_instance =
        engine->semantic_glyph_draw_active
        ? engine->semantic_glyph_first_instance
        : 0U;
    const std::uint32_t selected_instance_count =
        engine->semantic_glyph_draw_active
        ? engine->semantic_glyph_instance_count
        : static_cast<std::uint32_t>(engine->glyph_instances.size());
    if (!engine->semantic_prepare_only) {
    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    color_attachment.loadOp = !use_group_layer &&
            engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native positioned glyph render pass could not be created.");
    }
    if (selected_first_instance > engine->glyph_instances.size() ||
        selected_instance_count >
            engine->glyph_instances.size() - selected_first_instance) {
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic glyph packed-page draw range is invalid.");
    }
    if (selected_instance_count != 0U && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(pass, engine->text_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 0U, engine->text_uniform_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 1U, engine->text_atlas_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->text_vertex_buffer,
            0U,
            instance_bytes);
        wgpuRenderPassEncoderDraw(
            pass,
            6U,
            selected_instance_count,
            0U,
            selected_first_instance);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (use_group_layer) {
        engine->last_layer_metrics.content_pass_count = 1U;
        if (!encode_group_effect(
                *engine,
                encoder,
                draw_state,
                frame->dpi_scale) ||
            !encode_layer_composite(
                *engine,
                encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                frame->clear_color,
                draw_state)) {
            if (owns_encoder) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The glyph group composite pass could not be created.");
        }
    }
    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native positioned glyph command buffer could not be finished.");
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
    }
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::glyph,
            frame->dpi_scale,
            draw_state);
    }
    }

    std::uint64_t payload_hash = 0U;
    if ((frame->flags &
            PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH) != 0U) {
        payload_hash = retain_compiled_payload
            ? engine->glyph_payload_hash
            : append_fnv1a64(
                14695981039346656037ULL,
                engine->glyph_instances.data(),
                instance_bytes);
    }
    engine->last_error.clear();
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_glyph_frame_metrics)) {
        metrics->draw_call_count = engine->semantic_prepare_only ||
            selected_instance_count == 0U ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->glyph_count = selected_instance_count;
        metrics->rasterized_glyph_count = rasterized_glyph_count;
        metrics->atlas_width = engine->glyph_atlas_size;
        metrics->atlas_height = engine->glyph_atlas_size;
        metrics->atlas_generation = engine->glyph_atlas_generation;
        metrics->atlas_growth_count = engine->glyph_atlas_growth_count;
        metrics->instance_upload_bytes = upload_instances
            ? instance_bytes
            : 0U;
        metrics->outline_upload_bytes = outline_upload_bytes;
        metrics->coverage_staging_bytes = coverage_staging_bytes;
        metrics->uniform_upload_bytes = uploaded_uniforms
            ? sizeof(gpu_uniforms)
            : 0U;
        metrics->submission_count = engine->submission_count;
        metrics->payload_hash = payload_hash;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_render_image(
    progpu_native_engine* engine,
    const progpu_native_image_frame* frame,
    progpu_native_image_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    clear_metrics(metrics);
    const auto valid_rect = [](const progpu_native_image_rect& rect) {
        return std::isfinite(rect.x) && std::isfinite(rect.y) &&
            std::isfinite(rect.width) && std::isfinite(rect.height) &&
            rect.width > 0.0F && rect.height > 0.0F;
    };
    const bool has_mask = frame != nullptr &&
        frame->external_mask_view != 0U;
    const bool empty_mask_descriptor = frame != nullptr &&
        frame->mask_width == 0U && frame->mask_height == 0U &&
        frame->mask_revision == 0U && frame->mask_sampling == 0U &&
        frame->mask_destination_rect.x == 0.0F &&
        frame->mask_destination_rect.y == 0.0F &&
        frame->mask_destination_rect.width == 0.0F &&
        frame->mask_destination_rect.height == 0.0F;
    if (engine == nullptr || frame == nullptr ||
        frame->struct_size < offsetof(progpu_native_image_frame, draw_state) ||
        frame->width == 0U || frame->height == 0U ||
        !std::isfinite(frame->dpi_scale) || frame->dpi_scale <= 0.0F ||
        frame->target_view == 0U ||
        frame->image_width == 0U || frame->image_height == 0U ||
        frame->image_width > 16384U || frame->image_height > 16384U ||
        (frame->source_flags &
            ~PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) != 0U ||
        (((frame->source_flags &
                PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) == 0U) &&
            frame->row_bytes < frame->image_width * 4U) ||
        (((frame->source_flags &
                PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) != 0U) &&
            (frame->external_source_view == 0U ||
             frame->rgba_pixels != nullptr || frame->pixel_bytes != 0U)) ||
        frame->sampling > PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR ||
        (has_mask &&
            (frame->mask_width == 0U || frame->mask_height == 0U ||
             frame->mask_width > 16384U || frame->mask_height > 16384U ||
             frame->mask_revision == 0U ||
             frame->mask_sampling > PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR ||
             !valid_rect(frame->mask_destination_rect))) ||
        (!has_mask && !empty_mask_descriptor) ||
        frame->image_revision == 0U || frame->content_revision == 0U ||
        !valid_rect(frame->source_rect) ||
        !valid_rect(frame->destination_rect) ||
        frame->source_rect.x < 0.0F || frame->source_rect.y < 0.0F ||
        frame->source_rect.x + frame->source_rect.width >
            static_cast<float>(frame->image_width) ||
        frame->source_rect.y + frame->source_rect.height >
            static_cast<float>(frame->image_height) ||
        !progpu::native::is_finite(frame->transform) ||
        !std::isfinite(frame->opacity) ||
        frame->opacity < 0.0F || frame->opacity > 1.0F ||
        frame->reserved != 0U || frame->reserved2 != 0U ||
        !progpu::native::is_finite(frame->clear_color)) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The retained RGBA image frame descriptor is invalid.");
    }
    resolved_draw_state draw_state{};
    const auto* requested_draw_state =
        frame->struct_size >= sizeof(progpu_native_image_frame)
            ? frame->draw_state
            : nullptr;
    if (!resolve_draw_state(
            requested_draw_state,
            frame->target_view,
            frame->width,
            frame->height,
            frame->dpi_scale,
            draw_state)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The retained image frame draw state is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer must be used from its owner thread.");
    }
    engine->release_semantic_render_bundle();
    reset_layer_metrics(*engine);

    const bool created_resources = engine->image_pipeline == nullptr;
    bool use_group_layer = false;
    bool group_cache_hit = false;
    const auto group_status = prepare_group_layer(
        *engine,
        layer_family::image,
        frame->width,
        frame->height,
        frame->dpi_scale,
        reinterpret_cast<WGPUTextureView>(frame->target_view),
        frame->clear_color,
        draw_state,
        use_group_layer,
        group_cache_hit);
    if (group_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        return group_status;
    }
    if (group_cache_hit) {
        if (metrics != nullptr && metrics->struct_size >=
                sizeof(progpu_native_image_frame_metrics)) {
            metrics->submission_count = engine->submission_count;
        }
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    const bool external = (frame->source_flags &
        PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) != 0U;
    const std::uint64_t required_upload_bytes = external
        ? 0U
        : static_cast<std::uint64_t>(frame->row_bytes) *
                (frame->image_height - 1U) +
            static_cast<std::uint64_t>(frame->image_width) * 4U;
    const bool dimensions_changed = engine->image_texture_view != nullptr &&
        (engine->image_width != frame->image_width ||
         engine->image_height != frame->image_height);
    const bool upload_texture = engine->image_texture_view == nullptr ||
        engine->image_revision != frame->image_revision ||
        engine->image_source_is_external != external ||
        dimensions_changed ||
        (external && engine->image_texture_view !=
            reinterpret_cast<WGPUTextureView>(frame->external_source_view));
    if ((!upload_texture && dimensions_changed) ||
        (!external && upload_texture &&
            (frame->rgba_pixels == nullptr ||
             frame->pixel_bytes < required_upload_bytes))) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The retained RGBA image revision or pixel payload is invalid.");
    }

    if (!create_image_resources(*engine)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image GPU resources could not be created.");
    }
    if (upload_texture && !upload_image_texture(*engine, *frame)) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image texture could not be uploaded.");
    }
    bool uploaded_mask_uniforms = false;
    if (has_mask &&
        (!create_image_mask_resources(*engine) ||
         !update_image_mask(*engine, *frame, uploaded_mask_uniforms))) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained image mask resources could not be prepared.");
    }

    const bool compiled_payload_hit = engine->image_cache_valid &&
        engine->image_content_revision == frame->content_revision &&
        engine->image_draw_opacity == draw_state.opacity &&
        !dimensions_changed;
    if (!compiled_payload_hit) {
        const float x0 = frame->destination_rect.x;
        const float y0 = frame->destination_rect.y;
        const float x1 = x0 + frame->destination_rect.width;
        const float y1 = y0 + frame->destination_rect.height;
        const float u0 = frame->source_rect.x /
            static_cast<float>(frame->image_width);
        const float v0 = frame->source_rect.y /
            static_cast<float>(frame->image_height);
        const float u1 = (frame->source_rect.x + frame->source_rect.width) /
            static_cast<float>(frame->image_width);
        const float v1 = (frame->source_rect.y + frame->source_rect.height) /
            static_cast<float>(frame->image_height);
        constexpr std::array<std::array<std::uint32_t, 2U>, 4U> corners{{
            {0U, 0U}, {1U, 0U}, {1U, 1U}, {0U, 1U}
        }};
        for (std::size_t index = 0U; index < corners.size(); ++index) {
            const float x = corners[index][0] == 0U ? x0 : x1;
            const float y = corners[index][1] == 0U ? y0 : y1;
            auto& vertex = engine->image_vertices[index];
            progpu::native::transform_point(
                frame->transform,
                x,
                y,
                vertex.position[0],
                vertex.position[1]);
            vertex.color[0] = 1.0F;
            vertex.color[1] = 0.0F;
            vertex.color[2] = 1.0F;
            vertex.color[3] = frame->opacity * draw_state.opacity;
            vertex.texture_coordinate[0] = corners[index][0] == 0U ? u0 : u1;
            vertex.texture_coordinate[1] = corners[index][1] == 0U ? v0 : v1;
            vertex.brush_index = 0.0F;
            vertex.shape_size[0] = 0.0F;
            vertex.shape_size[1] = 0.5F;
            vertex.corner_radius = 0.0F;
            vertex.stroke_thickness = 1.0F;
            vertex.shape_type = 0.0F;
        }
        engine->image_payload_hash = append_fnv1a64(
            14695981039346656037ULL,
            engine->image_vertices.data(),
            sizeof(engine->image_vertices));
        engine->image_content_revision = frame->content_revision;
        engine->image_draw_opacity = draw_state.opacity;
        engine->image_cache_valid = true;
        engine->image_gpu_cache_valid = false;
    }

    const bool upload_vertices = !engine->image_gpu_cache_valid;
    if (upload_vertices) {
        wgpuQueueWriteBuffer(
            engine->queue,
            engine->image_vertex_buffer,
            0U,
            engine->image_vertices.data(),
            sizeof(engine->image_vertices));
        engine->image_gpu_cache_valid = true;
    }
    const gpu_uniforms uniforms = create_uniforms(
        frame->width,
        frame->height,
        frame->dpi_scale);
    const bool uploaded_uniforms = engine->upload_uniform_if_changed(
        engine->image_uniform_buffer,
        uniforms,
        engine->cached_image_uniforms,
        engine->image_uniform_cache_valid);

    const bool owns_encoder = engine->semantic_encoder == nullptr;
    WGPUCommandEncoder encoder = engine->semantic_encoder;
    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image encoder");
    if (owns_encoder) {
        encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &encoder_descriptor);
    }
    if (encoder == nullptr) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image command encoder could not be created.");
    }
    WGPURenderPassColorAttachment attachment{};
    progpu::native::webgpu::initialize_color_attachment(attachment);
    attachment.view = use_group_layer
        ? engine->layer_texture_view
        : reinterpret_cast<WGPUTextureView>(frame->target_view);
    attachment.loadOp = !use_group_layer && engine->semantic_load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = use_group_layer
        ? WGPUColor{0.0, 0.0, 0.0, 0.0}
        : WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        if (owns_encoder) {
            wgpuCommandEncoderRelease(encoder);
        }
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained RGBA image render pass could not be created.");
    }
    if (frame->opacity != 0.0F && draw_state.opacity != 0.0F &&
        (use_group_layer || draw_state.has_drawable_clip)) {
        if (!use_group_layer) {
            apply_scissor(pass, draw_state);
        }
        wgpuRenderPassEncoderSetPipeline(
            pass,
            has_mask ? engine->image_mask_pipeline : engine->image_pipeline);
        wgpuRenderPassEncoderSetBindGroup(
            pass, 0U, engine->image_uniform_bind_group, 0U, nullptr);
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            1U,
            frame->sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
                ? engine->image_nearest_bind_group
                : engine->image_linear_bind_group,
            0U,
            nullptr);
        if (has_mask) {
            wgpuRenderPassEncoderSetBindGroup(
                pass,
                2U,
                frame->mask_sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
                    ? engine->image_mask_nearest_bind_group
                    : engine->image_mask_linear_bind_group,
                0U,
                nullptr);
        }
        wgpuRenderPassEncoderSetVertexBuffer(
            pass,
            0U,
            engine->image_vertex_buffer,
            0U,
            sizeof(engine->image_vertices));
        wgpuRenderPassEncoderSetIndexBuffer(
            pass,
            engine->image_index_buffer,
            WGPUIndexFormat_Uint32,
            0U,
            6U * sizeof(std::uint32_t));
        wgpuRenderPassEncoderDrawIndexed(pass, 6U, 1U, 0U, 0, 0U);
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    if (use_group_layer) {
        engine->last_layer_metrics.content_pass_count = 1U;
        if (!encode_group_effect(
                *engine,
                encoder,
                draw_state,
                frame->dpi_scale) ||
            !encode_layer_composite(
                *engine,
                encoder,
                reinterpret_cast<WGPUTextureView>(frame->target_view),
                frame->clear_color,
                draw_state)) {
            if (owns_encoder) {
                wgpuCommandEncoderRelease(encoder);
            }
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The image group composite pass could not be created.");
        }
    }

    if (owns_encoder) {
        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The retained RGBA image command buffer could not be finished.");
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
    }
    if (use_group_layer) {
        retain_group_layer_content(
            *engine,
            layer_family::image,
            frame->dpi_scale,
            draw_state);
    }

    std::uint64_t payload_hash = engine->image_payload_hash;
    payload_hash = append_fnv1a64(
        payload_hash,
        &frame->image_revision,
        sizeof(frame->image_revision));
    payload_hash = append_fnv1a64(
        payload_hash,
        &frame->sampling,
        sizeof(frame->sampling));
    payload_hash = append_fnv1a64(
        payload_hash,
        &frame->mask_revision,
        sizeof(frame->mask_revision));
    engine->last_error.clear();
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_image_frame_metrics)) {
        metrics->draw_call_count = frame->opacity == 0.0F ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->vertex_count = 4U;
        metrics->index_count = 6U;
        metrics->texture_generation = engine->image_texture_generation;
        metrics->vertex_upload_bytes = upload_vertices
            ? sizeof(engine->image_vertices)
            : 0U;
        metrics->index_upload_bytes = created_resources
            ? 6U * sizeof(std::uint32_t)
            : 0U;
        metrics->texture_upload_bytes = upload_texture && !external
            ? required_upload_bytes
            : 0U;
        metrics->uniform_upload_bytes =
            (uploaded_uniforms ? sizeof(gpu_uniforms) : 0U) +
            (uploaded_mask_uniforms
                ? sizeof(gpu_mask_sampling_uniforms)
                : 0U);
        metrics->submission_count = engine->submission_count;
        metrics->payload_hash = payload_hash;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_render_scene(
    progpu_native_engine* engine,
    const progpu_native_scene_frame* frame,
    progpu_native_scene_frame_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_scene_frame_metrics)) {
        const std::uint32_t struct_size = metrics->struct_size;
        *metrics = {};
        metrics->struct_size = struct_size;
    }
    if (engine == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "Semantic scene rendering is owner-thread affine.");
    }
    if (frame == nullptr ||
        frame->struct_size < sizeof(progpu_native_scene_frame) ||
        frame->width == 0U ||
        frame->height == 0U || !std::isfinite(frame->dpi_scale) ||
        frame->dpi_scale <= 0.0F || frame->target_view == 0U ||
        !std::isfinite(frame->clear_color.r) ||
        !std::isfinite(frame->clear_color.g) ||
        !std::isfinite(frame->clear_color.b) ||
        !std::isfinite(frame->clear_color.a) ||
        frame->scene_id == 0U || frame->generation == 0U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The semantic scene frame descriptor is invalid.");
    }
    if (frame->scene_id != engine->semantic_scene_id ||
        frame->generation != engine->semantic_scene_generation ||
        engine->semantic_scene_snapshot.empty()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
            "The requested immutable semantic scene generation is not installed.");
    }

    const auto* bytes = engine->semantic_scene_snapshot.data();
    const auto& header = engine->semantic_scene_header;
    const auto read_command = [&](std::uint32_t index) noexcept {
        progpu_native_scene_command command{};
        std::memcpy(
            &command,
            bytes + header.command_offset +
                static_cast<std::size_t>(index) * header.command_stride,
            sizeof(command));
        return command;
    };
    const auto read_resource = [&](std::uint32_t index) noexcept {
        progpu_native_scene_resource resource{};
        std::memcpy(
            &resource,
            bytes + header.resource_offset +
                static_cast<std::size_t>(index) * header.resource_stride,
            sizeof(resource));
        return resource;
    };
    const auto revision32 = [](std::uint64_t value) noexcept {
        std::uint32_t result = static_cast<std::uint32_t>(
            value ^ (value >> 32U));
        return result == 0U ? 1U : result;
    };
    const auto span_is_multiple = [](std::uint32_t size,
                                     std::size_t stride) noexcept {
        return stride != 0U && size != 0U && size % stride == 0U;
    };

    semantic_layer_budget layer_budget{};
    semantic_layer_target_cursor layer_budget_cursor(
        bytes,
        frame->width,
        frame->height,
        frame->dpi_scale);
    bool semantic_has_materialized_layers = false;
    bool semantic_has_layer_masks = false;
    bool semantic_has_layer_effects = false;
    bool semantic_has_drop_shadows = false;
    bool semantic_has_unsupported_layers = false;
    std::uint32_t semantic_materialized_layer_count = 0U;
    std::uint32_t semantic_effect_node_count = 0U;
    std::uint32_t semantic_effect_pass_count = 0U;
    std::uint32_t semantic_effect_chain_revision = 0U;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command = read_command(index);
        const auto target_extent = layer_budget_cursor.advance(command);
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
            auto layer = semantic_default_layer();
            if (command.payload_size != 0U) {
                std::memcpy(
                    &layer,
                    bytes + command.payload_offset,
                    sizeof(layer));
            }
            const bool materialized =
                progpu::native::scene::layer_requires_materialization(layer);
            semantic_has_materialized_layers |= materialized;
            semantic_has_layer_masks |= layer.mask_resource_index !=
                PROGPU_NATIVE_SCENE_NO_INDEX;
            const bool effected = layer.effect_resource_index !=
                PROGPU_NATIVE_SCENE_NO_INDEX;
            semantic_has_layer_effects |= effected;
            if (effected) {
                const auto effect_resource = read_resource(
                    layer.effect_resource_index);
                progpu_native_scene_effect_chain chain{};
                std::memcpy(
                    &chain,
                    bytes + effect_resource.payload_offset,
                    sizeof(chain));
                if (chain.effect_count >
                    PROGPU_NATIVE_MAX_GROUP_EFFECTS ||
                    semantic_effect_node_count >
                        semantic_max_effect_passes - chain.effect_count) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "The semantic effect-chain node count exceeds its bounded compilation budget.");
                }
                semantic_effect_node_count += chain.effect_count;
                semantic_effect_chain_revision = chain.revision;
                for (std::uint32_t effect_index = 0U;
                     effect_index < chain.effect_count;
                     ++effect_index) {
                    progpu_native_group_effect effect{};
                    std::memcpy(
                        &effect,
                        bytes + effect_resource.auxiliary_offset +
                            static_cast<std::size_t>(effect_index) *
                                sizeof(effect),
                        sizeof(effect));
                    const bool drop_shadow = effect.kind ==
                        PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
                    const std::uint32_t passes = drop_shadow ? 3U : 2U;
                    if (semantic_effect_pass_count >
                        semantic_max_effect_passes - passes) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                            "The semantic effect-chain pass count exceeds its bounded compilation budget.");
                    }
                    semantic_effect_pass_count += passes;
                    semantic_has_drop_shadows |= drop_shadow;
                    constexpr float maximum_physical_sigma =
                        128.0F / 3.0F;
                    const float sigma_x = effect.sigma_x * frame->dpi_scale;
                    const float sigma_y = effect.sigma_y * frame->dpi_scale;
                    const float offset_x = effect.offset_x * frame->dpi_scale;
                    const float offset_y = effect.offset_y * frame->dpi_scale;
                    if (!std::isfinite(sigma_x) ||
                        !std::isfinite(sigma_y) ||
                        sigma_x > maximum_physical_sigma ||
                        sigma_y > maximum_physical_sigma ||
                        (drop_shadow &&
                            (!std::isfinite(offset_x) ||
                             !std::isfinite(offset_y)))) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                            "A semantic effect exceeds the finite physical kernel contract.");
                    }
                }
            }
            if (materialized && semantic_materialized_layer_count ==
                    semantic_max_draw_passes) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                    "The semantic isolated-layer pass count exceeds its bounded compilation budget.");
            }
            semantic_materialized_layer_count += materialized ? 1U : 0U;
            semantic_has_unsupported_layers |= materialized &&
                (((layer.flags & PROGPU_NATIVE_SCENE_LAYER_BACKDROP) != 0U) ||
                    is_advanced_group_blend(layer.blend_mode));
            if (!layer_budget.push(
                    target_extent,
                    materialized,
                    effected)) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                    "The semantic isolated-layer stack exceeds its bounded depth or aggregate pixel budget.");
            }
        } else if (command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
            layer_budget.pop();
        }
    }

    /* Preflight every typed payload before the first target submission. */
    std::uint32_t semantic_draw_count = 0U;
    std::uint32_t semantic_analytic_draw_count = 0U;
    std::uint32_t semantic_path_draw_count = 0U;
    std::uint32_t semantic_glyph_draw_count = 0U;
    std::uint32_t semantic_image_draw_count = 0U;
    std::uint64_t semantic_analytic_vertex_bytes = 0U;
    std::uint64_t semantic_analytic_index_bytes = 0U;
    std::uint64_t semantic_path_count = 0U;
    std::uint64_t semantic_path_segment_count = 0U;
    std::uint64_t semantic_glyph_outline_count = 0U;
    std::uint64_t semantic_glyph_segment_count = 0U;
    std::uint64_t semantic_glyph_count = 0U;
    semantic_compilation_budget compilation_budget{};
    semantic_state_cursor preflight_state_cursor(bytes, header);
    semantic_layer_target_cursor preflight_target_cursor(
        bytes,
        frame->width,
        frame->height,
        frame->dpi_scale);
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command = read_command(index);
        const auto target_extent = preflight_target_cursor.advance(command);
        const auto state = localize_semantic_state(
            preflight_state_cursor.advance(command),
            target_extent,
            frame->dpi_scale);
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE) {
            continue;
        }
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
            continue;
        }
        if (command.kind < PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
            command.kind > PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        const auto resource = read_resource(command.resource_index);
        bool valid = false;
        bool budget_valid = true;
        std::uint64_t compiled_vertex_bytes = 0U;
        std::uint64_t compiled_index_bytes = 0U;
        std::uint64_t compiled_texture_bytes = 0U;
        std::uint64_t compiled_coverage_bytes = 0U;
        switch (command.kind) {
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC: {
                valid = span_is_multiple(
                    resource.payload_size,
                    sizeof(progpu_native_analytic_primitive)) &&
                    resource.auxiliary_size == 0U &&
                    command.payload_size == 0U;
                const std::uint64_t primitive_count = resource.payload_size /
                    sizeof(progpu_native_analytic_primitive);
                valid = valid && primitive_count <=
                    std::numeric_limits<std::uint32_t>::max() / 6U;
                compiled_vertex_bytes = primitive_count * 4U *
                    sizeof(progpu::native::vector_vertex);
                compiled_index_bytes = primitive_count * 6U *
                    sizeof(std::uint32_t);
                for (std::uint64_t primitive_index = 0U;
                     valid && primitive_index < primitive_count;
                     ++primitive_index) {
                    progpu_native_analytic_primitive primitive{};
                    std::memcpy(
                        &primitive,
                        bytes + resource.payload_offset +
                            primitive_index * sizeof(primitive),
                        sizeof(primitive));
                    apply_semantic_state(primitive, state);
                    valid = is_valid_semantic_analytic(primitive);
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH: {
                valid = span_is_multiple(
                        resource.payload_size,
                        sizeof(progpu_native_scene_path_fill)) &&
                    span_is_multiple(
                        resource.auxiliary_size,
                        sizeof(progpu_native_path_segment)) &&
                    command.payload_size == 0U;
                const std::uint64_t path_count = resource.payload_size /
                    sizeof(progpu_native_scene_path_fill);
                const std::uint64_t segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                valid = valid && path_count <= (1U << 20U) &&
                    segment_count <= (1U << 24U) &&
                    path_count <=
                        std::numeric_limits<std::uint32_t>::max() / 6U;
                compiled_vertex_bytes = path_count * 4U *
                    sizeof(progpu::native::vector_vertex);
                compiled_index_bytes = path_count * 6U *
                    sizeof(std::uint32_t);
                for (std::uint64_t segment_index = 0U;
                     valid && segment_index < segment_count;
                     ++segment_index) {
                    progpu_native_path_segment segment{};
                    std::memcpy(
                        &segment,
                        bytes + resource.auxiliary_offset +
                            segment_index * sizeof(segment),
                        sizeof(segment));
                    valid = is_valid_semantic_segment(segment, true);
                }
                for (std::uint64_t path_index = 0U;
                     valid && budget_valid && path_index < path_count;
                     ++path_index) {
                    progpu_native_scene_path_fill path{};
                    std::memcpy(
                        &path,
                        bytes + resource.payload_offset +
                            path_index * sizeof(path),
                        sizeof(path));
                    apply_semantic_state(path, state);
                    std::uint64_t path_coverage_bytes = 0U;
                    valid = is_valid_semantic_path(
                        path,
                        segment_count,
                        &path_coverage_bytes);
                    budget_valid = valid &&
                        path_coverage_bytes <=
                            semantic_max_coverage_bytes -
                                compiled_coverage_bytes;
                    if (budget_valid) {
                        compiled_coverage_bytes += path_coverage_bytes;
                    }
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN: {
                valid = span_is_multiple(
                        resource.payload_size,
                        sizeof(progpu_native_scene_glyph_outline)) &&
                    span_is_multiple(
                        resource.auxiliary_size,
                        sizeof(progpu_native_path_segment)) &&
                    span_is_multiple(
                        command.payload_size,
                        sizeof(progpu_native_positioned_glyph));
                const std::uint64_t outline_count = resource.payload_size /
                    sizeof(progpu_native_scene_glyph_outline);
                const std::uint64_t segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                const std::uint64_t glyph_count = command.payload_size /
                    sizeof(progpu_native_positioned_glyph);
                valid = valid && outline_count <= (1U << 20U) &&
                    segment_count <= (1U << 24U) &&
                    glyph_count <= (1U << 24U);
                compiled_vertex_bytes = glyph_count *
                    sizeof(gpu_glyph_instance);
                for (std::uint64_t segment_index = 0U;
                     valid && segment_index < segment_count;
                     ++segment_index) {
                    progpu_native_path_segment segment{};
                    std::memcpy(
                        &segment,
                        bytes + resource.auxiliary_offset +
                            segment_index * sizeof(segment),
                        sizeof(segment));
                    valid = is_valid_semantic_segment(segment, false);
                }
                for (std::uint64_t outline_index = 0U;
                     valid && budget_valid && outline_index < outline_count;
                     ++outline_index) {
                    progpu_native_scene_glyph_outline outline{};
                    std::memcpy(
                        &outline,
                        bytes + resource.payload_offset +
                            outline_index * sizeof(outline),
                        sizeof(outline));
                    std::uint64_t outline_coverage_bytes = 0U;
                    valid = is_valid_semantic_glyph_outline(
                        outline,
                        segment_count,
                        &outline_coverage_bytes);
                    budget_valid = valid &&
                        outline_coverage_bytes <=
                            semantic_max_coverage_bytes -
                                compiled_coverage_bytes;
                    if (budget_valid) {
                        compiled_coverage_bytes += outline_coverage_bytes;
                    }
                }
                for (std::uint64_t glyph_index = 0U;
                     valid && glyph_index < glyph_count;
                     ++glyph_index) {
                    progpu_native_positioned_glyph glyph{};
                    std::memcpy(
                        &glyph,
                        bytes + command.payload_offset +
                            glyph_index * sizeof(glyph),
                        sizeof(glyph));
                    apply_semantic_state(glyph, state);
                    valid = is_valid_semantic_positioned_glyph(
                        glyph,
                        outline_count);
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE: {
                if (command.payload_size <
                    sizeof(progpu_native_scene_image_draw)) {
                    break;
                }
                progpu_native_scene_image_draw image{};
                std::memcpy(
                    &image,
                    bytes + command.payload_offset,
                    sizeof(image));
                apply_semantic_state(image, state);
                valid = image.struct_size >= sizeof(image) &&
                    image.struct_size <= command.payload_size &&
                    resource.auxiliary_size == 0U &&
                    is_valid_semantic_image(
                        image,
                        resource.payload_size);
                compiled_vertex_bytes =
                    4U * sizeof(progpu::native::vector_vertex);
                compiled_index_bytes = 6U * sizeof(std::uint32_t);
                compiled_texture_bytes = resource.payload_size;
                break;
            }
            default:
                break;
        }
        if (!valid) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "A typed semantic scene resource payload is invalid.");
        }
        if (!budget_valid || !compilation_budget.add(
                compiled_vertex_bytes,
                compiled_index_bytes,
                compiled_texture_bytes,
                compiled_coverage_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic scene exceeds the bounded aggregate compilation budget.");
        }
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
            ++semantic_analytic_draw_count;
            semantic_analytic_vertex_bytes += compiled_vertex_bytes;
            semantic_analytic_index_bytes += compiled_index_bytes;
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
            ++semantic_path_draw_count;
            semantic_path_count += resource.payload_size /
                sizeof(progpu_native_scene_path_fill);
            semantic_path_segment_count += resource.auxiliary_size /
                sizeof(progpu_native_path_segment);
        } else if (command.kind ==
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
            ++semantic_glyph_draw_count;
            semantic_glyph_outline_count += resource.payload_size /
                sizeof(progpu_native_scene_glyph_outline);
            semantic_glyph_segment_count += resource.auxiliary_size /
                sizeof(progpu_native_path_segment);
            semantic_glyph_count += command.payload_size /
                sizeof(progpu_native_positioned_glyph);
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            ++semantic_image_draw_count;
        }
        ++semantic_draw_count;
    }

    const std::uint64_t semantic_effect_uniform_bytes =
        static_cast<std::uint64_t>(semantic_effect_pass_count) *
            semantic_effect_uniform_alignment;
    const std::uint64_t pooled_layer_bytes = layer_budget.pooled_bytes();
    const std::uint64_t pooled_effect_bytes =
        layer_budget.pooled_effect_bytes();
    const bool invalid_layer_pool =
        pooled_layer_bytes > PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES ||
        pooled_effect_bytes >
            PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES - pooled_layer_bytes;
    const std::uint64_t retained_layer_bytes = invalid_layer_pool
        ? std::numeric_limits<std::uint64_t>::max()
        : pooled_layer_bytes + pooled_effect_bytes;
    const std::uint64_t compiled_bytes =
        compilation_budget.total_bytes();
    if (invalid_layer_pool ||
        semantic_effect_uniform_bytes >
            semantic_max_total_compiled_bytes - compiled_bytes ||
        std::max(layer_budget.peak_bytes, retained_layer_bytes) >
            semantic_max_total_compiled_bytes - compiled_bytes -
                semantic_effect_uniform_bytes) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The semantic scene exceeds the combined layer, effect, and compiled-payload budget.");
    }

    if (semantic_path_count > (1U << 20U) ||
        semantic_path_segment_count > (1U << 24U) ||
        semantic_path_count >
            std::numeric_limits<std::uint32_t>::max() / 6U) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The aggregate semantic path page exceeds the native safety bound.");
    }
    if (semantic_glyph_outline_count > (1U << 20U) ||
        semantic_glyph_segment_count > (1U << 24U) ||
        semantic_glyph_count > (1U << 24U) ||
        semantic_glyph_count >
            std::numeric_limits<std::uint32_t>::max()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The aggregate semantic glyph page exceeds the native safety bound.");
    }

    if (semantic_has_unsupported_layers) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_UNSUPPORTED,
            "Backdrop and advanced-blend semantic layers "
            "are delivered by later M2.4d3b2 checkpoints.");
    }

    const bool semantic_render_bundle_hit =
        engine->semantic_render_bundle_valid &&
        engine->semantic_render_bundle_scene_hash ==
            engine->semantic_scene_hash &&
        engine->semantic_render_bundle_dpi_scale == frame->dpi_scale &&
        engine->semantic_render_bundle_width == frame->width &&
        engine->semantic_render_bundle_height == frame->height &&
        (semantic_path_draw_count == 0U ||
            engine->semantic_path_gpu_scene_hash ==
                engine->semantic_scene_hash) &&
        (semantic_glyph_draw_count == 0U ||
            engine->semantic_glyph_gpu_scene_hash ==
                engine->semantic_scene_hash);
    if (!semantic_render_bundle_hit) {
        engine->release_semantic_render_bundle();
    }

    std::uint64_t semantic_analytic_vertex_upload_bytes = 0U;
    std::uint64_t semantic_analytic_index_upload_bytes = 0U;
    auto& semantic_analytic_page = engine->semantic_analytic_cache;
    const bool semantic_analytic_page_hit =
        semantic_analytic_draw_count != 0U &&
        semantic_analytic_page.cache_valid &&
        semantic_analytic_page.scene_hash == engine->semantic_scene_hash &&
        semantic_analytic_page.dpi_scale == frame->dpi_scale &&
        semantic_analytic_page.target_width == frame->width &&
        semantic_analytic_page.target_height == frame->height &&
        semantic_analytic_page.draws.size() ==
            semantic_analytic_draw_count;
    if (semantic_analytic_draw_count != 0U &&
        !semantic_analytic_page_hit) {
        std::vector<semantic_analytic_draw> compiled_draws;
        try {
            compiled_draws.reserve(semantic_analytic_draw_count);
            engine->vertices.clear();
            engine->indices.clear();
            engine->vertices.reserve(static_cast<std::size_t>(
                semantic_analytic_vertex_bytes /
                    sizeof(progpu::native::vector_vertex)));
            engine->indices.reserve(static_cast<std::size_t>(
                semantic_analytic_index_bytes /
                    sizeof(std::uint32_t)));
            engine->geometry_cache_valid = false;
            engine->geometry_gpu_cache_valid = false;

            semantic_state_cursor state_cursor(bytes, header);
            semantic_layer_target_cursor target_cursor(
                bytes,
                frame->width,
                frame->height,
                frame->dpi_scale);
            for (std::uint32_t index = 0U;
                 index < header.command_count;
                 ++index) {
                const auto command = read_command(index);
                const auto target_extent = target_cursor.advance(command);
                const auto state = localize_semantic_state(
                    state_cursor.advance(command),
                    target_extent,
                    frame->dpi_scale);
                if (command.kind !=
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
                    continue;
                }
                const auto resource = read_resource(command.resource_index);
                const std::size_t vertex_start = engine->vertices.size();
                const std::size_t index_start = engine->indices.size();
                const std::size_t primitive_count = resource.payload_size /
                    sizeof(progpu_native_analytic_primitive);
                for (std::size_t primitive_index = 0U;
                     primitive_index < primitive_count;
                     ++primitive_index) {
                    progpu_native_analytic_primitive primitive{};
                    std::memcpy(
                        &primitive,
                        bytes + resource.payload_offset +
                            primitive_index * sizeof(primitive),
                        sizeof(primitive));
                    apply_semantic_state(primitive, state);
                    float minimum_scale = 0.0F;
                    if (!progpu::native::try_get_minimum_scale(
                            primitive.transform,
                            minimum_scale) ||
                        !progpu::native::append_analytic_primitive(
                            primitive,
                            antialias_padding_pixels / minimum_scale,
                            engine->vertices,
                            engine->indices)) {
                        return engine->fail(
                            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                            "A preflighted semantic analytic payload could not be compiled.");
                    }
                }
                const std::size_t vertex_count =
                    engine->vertices.size() - vertex_start;
                const std::size_t index_count =
                    engine->indices.size() - index_start;
                if (vertex_count >
                        std::numeric_limits<std::uint32_t>::max() ||
                    index_count >
                        std::numeric_limits<std::uint32_t>::max()) {
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A semantic analytic packed draw exceeds WebGPU index limits.");
                }
                compiled_draws.push_back({
                    vertex_start *
                        sizeof(progpu::native::vector_vertex),
                    index_start * sizeof(std::uint32_t),
                    static_cast<std::uint32_t>(vertex_count),
                    static_cast<std::uint32_t>(index_count)});
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic analytic packed page could not be compiled.");
        }

        const std::uint64_t compiled_vertex_bytes =
            engine->vertices.size() *
                sizeof(progpu::native::vector_vertex);
        const std::uint64_t compiled_index_bytes =
            engine->indices.size() * sizeof(std::uint32_t);
        if (compiled_draws.size() != semantic_analytic_draw_count ||
            compiled_vertex_bytes != semantic_analytic_vertex_bytes ||
            compiled_index_bytes != semantic_analytic_index_bytes) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic analytic packed-page budget did not match compilation.");
        }
        if (!engine->ensure_semantic_analytic_page_buffers(
                compiled_vertex_bytes,
                compiled_index_bytes)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic analytic packed GPU page could not be allocated.");
        }
        wgpuQueueWriteBuffer(
            engine->queue,
            semantic_analytic_page.vertex_buffer,
            0U,
            engine->vertices.data(),
            static_cast<std::size_t>(compiled_vertex_bytes));
        wgpuQueueWriteBuffer(
            engine->queue,
            semantic_analytic_page.index_buffer,
            0U,
            engine->indices.data(),
            static_cast<std::size_t>(compiled_index_bytes));
        semantic_analytic_page.draws = std::move(compiled_draws);
        semantic_analytic_page.vertex_bytes = compiled_vertex_bytes;
        semantic_analytic_page.index_bytes = compiled_index_bytes;
        semantic_analytic_page.scene_hash = engine->semantic_scene_hash;
        semantic_analytic_page.dpi_scale = frame->dpi_scale;
        semantic_analytic_page.target_width = frame->width;
        semantic_analytic_page.target_height = frame->height;
        semantic_analytic_page.cache_valid = true;
        semantic_analytic_vertex_upload_bytes = compiled_vertex_bytes;
        semantic_analytic_index_upload_bytes = compiled_index_bytes;
    }

    auto& semantic_path_page = engine->semantic_path_cache;
    const bool semantic_path_page_hit =
        semantic_path_draw_count != 0U &&
        semantic_path_page.cache_valid &&
        semantic_path_page.scene_hash == engine->semantic_scene_hash &&
        semantic_path_page.dpi_scale == frame->dpi_scale &&
        semantic_path_page.target_width == frame->width &&
        semantic_path_page.target_height == frame->height &&
        semantic_path_page.draws.size() == semantic_path_draw_count;
    if (semantic_path_draw_count != 0U && !semantic_path_page_hit) {
        std::vector<progpu_native_path_fill> compiled_paths;
        std::vector<progpu_native_path_segment> compiled_segments;
        std::vector<semantic_path_draw> compiled_draws;
        try {
            compiled_paths.reserve(
                static_cast<std::size_t>(semantic_path_count));
            compiled_segments.reserve(
                static_cast<std::size_t>(semantic_path_segment_count));
            compiled_draws.reserve(semantic_path_draw_count);
            semantic_state_cursor state_cursor(bytes, header);
            semantic_layer_target_cursor target_cursor(
                bytes,
                frame->width,
                frame->height,
                frame->dpi_scale);
            for (std::uint32_t index = 0U;
                 index < header.command_count;
                 ++index) {
                const auto command = read_command(index);
                const auto target_extent = target_cursor.advance(command);
                const auto state = localize_semantic_state(
                    state_cursor.advance(command),
                    target_extent,
                    frame->dpi_scale);
                if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
                    continue;
                }
                const auto resource = read_resource(command.resource_index);
                const std::size_t path_start = compiled_paths.size();
                const std::size_t segment_start = compiled_segments.size();
                const std::size_t path_count = resource.payload_size /
                    sizeof(progpu_native_scene_path_fill);
                const std::size_t segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                const auto* source_segments = reinterpret_cast<
                    const progpu_native_path_segment*>(
                        bytes + resource.auxiliary_offset);
                compiled_segments.insert(
                    compiled_segments.end(),
                    source_segments,
                    source_segments + segment_count);
                for (std::size_t path_index = 0U;
                     path_index < path_count;
                     ++path_index) {
                    progpu_native_path_fill path{};
                    std::memcpy(
                        &path,
                        bytes + resource.payload_offset +
                            path_index *
                                sizeof(progpu_native_scene_path_fill),
                        sizeof(path));
                    apply_semantic_state(path, state);
                    path.segment_offset += segment_start;
                    compiled_paths.push_back(path);
                }
                compiled_draws.push_back({
                    static_cast<std::uint32_t>(path_start * 6U),
                    static_cast<std::uint32_t>(path_count * 6U)});
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic path packed page could not be compiled.");
        }
        if (compiled_paths.size() != semantic_path_count ||
            compiled_segments.size() != semantic_path_segment_count ||
            compiled_draws.size() != semantic_path_draw_count) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic path packed-page budget did not match compilation.");
        }
        semantic_path_page.paths = std::move(compiled_paths);
        semantic_path_page.segments = std::move(compiled_segments);
        semantic_path_page.draws = std::move(compiled_draws);
        semantic_path_page.scene_hash = engine->semantic_scene_hash;
        semantic_path_page.dpi_scale = frame->dpi_scale;
        semantic_path_page.target_width = frame->width;
        semantic_path_page.target_height = frame->height;
        semantic_path_page.cache_valid = true;
        engine->semantic_path_gpu_scene_hash = 0U;
    }

    if (semantic_path_draw_count != 0U &&
        engine->semantic_path_gpu_scene_hash !=
            engine->semantic_scene_hash) {
        engine->path_cache_valid = false;
        engine->path_gpu_cache_valid = false;
    }

    auto& semantic_glyph_page = engine->semantic_glyph_cache;
    const bool semantic_glyph_page_hit =
        semantic_glyph_draw_count != 0U &&
        semantic_glyph_page.cache_valid &&
        semantic_glyph_page.scene_hash == engine->semantic_scene_hash &&
        semantic_glyph_page.dpi_scale == frame->dpi_scale &&
        semantic_glyph_page.target_width == frame->width &&
        semantic_glyph_page.target_height == frame->height &&
        semantic_glyph_page.draws.size() == semantic_glyph_draw_count;
    if (semantic_glyph_draw_count != 0U && !semantic_glyph_page_hit) {
        std::vector<progpu_native_glyph_outline> compiled_outlines;
        std::vector<progpu_native_path_segment> compiled_segments;
        std::vector<progpu_native_positioned_glyph> compiled_glyphs;
        std::vector<semantic_glyph_draw> compiled_draws;
        try {
            compiled_outlines.reserve(
                static_cast<std::size_t>(semantic_glyph_outline_count));
            compiled_segments.reserve(
                static_cast<std::size_t>(semantic_glyph_segment_count));
            compiled_glyphs.reserve(
                static_cast<std::size_t>(semantic_glyph_count));
            compiled_draws.reserve(semantic_glyph_draw_count);
            semantic_state_cursor state_cursor(bytes, header);
            semantic_layer_target_cursor target_cursor(
                bytes,
                frame->width,
                frame->height,
                frame->dpi_scale);
            for (std::uint32_t index = 0U;
                 index < header.command_count;
                 ++index) {
                const auto command = read_command(index);
                const auto target_extent = target_cursor.advance(command);
                const auto state = localize_semantic_state(
                    state_cursor.advance(command),
                    target_extent,
                    frame->dpi_scale);
                if (command.kind !=
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
                    continue;
                }
                const auto resource = read_resource(command.resource_index);
                const std::size_t outline_start = compiled_outlines.size();
                const std::size_t segment_start = compiled_segments.size();
                const std::size_t glyph_start = compiled_glyphs.size();
                const std::size_t outline_count = resource.payload_size /
                    sizeof(progpu_native_scene_glyph_outline);
                const std::size_t segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                const std::size_t glyph_count = command.payload_size /
                    sizeof(progpu_native_positioned_glyph);
                const auto* source_segments = reinterpret_cast<
                    const progpu_native_path_segment*>(
                        bytes + resource.auxiliary_offset);
                compiled_segments.insert(
                    compiled_segments.end(),
                    source_segments,
                    source_segments + segment_count);
                for (std::size_t outline_index = 0U;
                     outline_index < outline_count;
                     ++outline_index) {
                    progpu_native_glyph_outline outline{};
                    std::memcpy(
                        &outline,
                        bytes + resource.payload_offset +
                            outline_index *
                                sizeof(progpu_native_scene_glyph_outline),
                        sizeof(outline));
                    outline.segment_offset += segment_start;
                    compiled_outlines.push_back(outline);
                }
                for (std::size_t glyph_index = 0U;
                     glyph_index < glyph_count;
                     ++glyph_index) {
                    progpu_native_positioned_glyph glyph{};
                    std::memcpy(
                        &glyph,
                        bytes + command.payload_offset +
                            glyph_index * sizeof(glyph),
                        sizeof(glyph));
                    apply_semantic_state(glyph, state);
                    glyph.outline_index += static_cast<std::uint32_t>(
                        outline_start);
                    compiled_glyphs.push_back(glyph);
                }
                compiled_draws.push_back({
                    static_cast<std::uint32_t>(glyph_start),
                    static_cast<std::uint32_t>(glyph_count)});
            }
        } catch (const std::bad_alloc&) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic glyph packed page could not be compiled.");
        }
        if (compiled_outlines.size() != semantic_glyph_outline_count ||
            compiled_segments.size() != semantic_glyph_segment_count ||
            compiled_glyphs.size() != semantic_glyph_count ||
            compiled_draws.size() != semantic_glyph_draw_count) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic glyph packed-page budget did not match compilation.");
        }
        semantic_glyph_page.outlines = std::move(compiled_outlines);
        semantic_glyph_page.segments = std::move(compiled_segments);
        semantic_glyph_page.glyphs = std::move(compiled_glyphs);
        semantic_glyph_page.draws = std::move(compiled_draws);
        semantic_glyph_page.scene_hash = engine->semantic_scene_hash;
        semantic_glyph_page.dpi_scale = frame->dpi_scale;
        semantic_glyph_page.target_width = frame->width;
        semantic_glyph_page.target_height = frame->height;
        semantic_glyph_page.cache_valid = true;
        engine->semantic_glyph_gpu_scene_hash = 0U;
    }

    if (semantic_glyph_draw_count != 0U &&
        engine->semantic_glyph_gpu_scene_hash !=
            engine->semantic_scene_hash) {
        engine->glyph_cache_valid = false;
        engine->glyph_gpu_cache_valid = false;
    }

    std::uint64_t semantic_image_vertex_upload_bytes = 0U;
    std::uint64_t semantic_image_index_upload_bytes = 0U;
    std::uint64_t semantic_image_texture_upload_bytes = 0U;
    auto& semantic_image_page = engine->semantic_image_cache;
    const bool semantic_image_page_hit =
        semantic_image_draw_count != 0U &&
        semantic_image_page.cache_valid &&
        semantic_image_page.scene_hash == engine->semantic_scene_hash &&
        semantic_image_page.dpi_scale == frame->dpi_scale &&
        semantic_image_page.target_width == frame->width &&
        semantic_image_page.target_height == frame->height &&
        semantic_image_page.draws.size() == semantic_image_draw_count;
    if (semantic_image_draw_count != 0U && !semantic_image_page_hit) {
        const bool created_resources = engine->image_pipeline == nullptr;
        if (!create_image_resources(*engine)) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic image WebGPU resources could not be created.");
        }
        std::vector<progpu::native::vector_vertex> vertices;
        std::vector<semantic_image_draw> compiled_draws;
        WGPUBuffer compiled_vertex_buffer = nullptr;
        const auto release_compiled = [&]() noexcept {
            for (auto& draw : compiled_draws) {
                if (draw.linear_bind_group != nullptr) {
                    wgpuBindGroupRelease(draw.linear_bind_group);
                }
                if (draw.nearest_bind_group != nullptr) {
                    wgpuBindGroupRelease(draw.nearest_bind_group);
                }
                if (draw.view != nullptr) {
                    wgpuTextureViewRelease(draw.view);
                }
                if (draw.texture != nullptr) {
                    wgpuTextureDestroy(draw.texture);
                    wgpuTextureRelease(draw.texture);
                }
            }
            compiled_draws.clear();
            if (compiled_vertex_buffer != nullptr) {
                wgpuBufferDestroy(compiled_vertex_buffer);
                wgpuBufferRelease(compiled_vertex_buffer);
                compiled_vertex_buffer = nullptr;
            }
        };
        try {
            vertices.reserve(
                static_cast<std::size_t>(semantic_image_draw_count) * 4U);
            compiled_draws.reserve(semantic_image_draw_count);
            semantic_state_cursor state_cursor(bytes, header);
            semantic_layer_target_cursor target_cursor(
                bytes,
                frame->width,
                frame->height,
                frame->dpi_scale);
            for (std::uint32_t index = 0U;
                 index < header.command_count;
                 ++index) {
                const auto command = read_command(index);
                const auto target_extent = target_cursor.advance(command);
                const auto state = localize_semantic_state(
                    state_cursor.advance(command),
                    target_extent,
                    frame->dpi_scale);
                if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
                    continue;
                }
                const auto resource = read_resource(command.resource_index);
                progpu_native_scene_image_draw image{};
                std::memcpy(
                    &image,
                    bytes + command.payload_offset,
                    sizeof(image));
                apply_semantic_state(image, state);
                const std::uint32_t first_vertex =
                    static_cast<std::uint32_t>(vertices.size());
                const float x0 = image.destination_rect.x;
                const float y0 = image.destination_rect.y;
                const float x1 = x0 + image.destination_rect.width;
                const float y1 = y0 + image.destination_rect.height;
                const float u0 = image.source_rect.x /
                    static_cast<float>(image.image_width);
                const float v0 = image.source_rect.y /
                    static_cast<float>(image.image_height);
                const float u1 = (image.source_rect.x +
                    image.source_rect.width) /
                    static_cast<float>(image.image_width);
                const float v1 = (image.source_rect.y +
                    image.source_rect.height) /
                    static_cast<float>(image.image_height);
                constexpr std::array<
                    std::array<std::uint32_t, 2U>, 4U> corners{{
                    {0U, 0U}, {1U, 0U}, {1U, 1U}, {0U, 1U}
                }};
                for (const auto& corner : corners) {
                    const float x = corner[0] == 0U ? x0 : x1;
                    const float y = corner[1] == 0U ? y0 : y1;
                    progpu::native::vector_vertex vertex{};
                    progpu::native::transform_point(
                        image.transform,
                        x,
                        y,
                        vertex.position[0],
                        vertex.position[1]);
                    vertex.color[0] = 1.0F;
                    vertex.color[1] = 0.0F;
                    vertex.color[2] = 1.0F;
                    vertex.color[3] = image.opacity;
                    vertex.texture_coordinate[0] =
                        corner[0] == 0U ? u0 : u1;
                    vertex.texture_coordinate[1] =
                        corner[1] == 0U ? v0 : v1;
                    vertex.brush_index = 0.0F;
                    vertex.shape_size[0] = 0.0F;
                    vertex.shape_size[1] = 0.5F;
                    vertex.corner_radius = 0.0F;
                    vertex.stroke_thickness = 1.0F;
                    vertex.shape_type = 0.0F;
                    vertices.push_back(vertex);
                }

                WGPUTextureDescriptor texture_descriptor{};
                texture_descriptor.label =
                    progpu::native::webgpu::string_view(
                        "ProGPU semantic retained RGBA image");
                texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
                    WGPUTextureUsage_CopyDst;
                texture_descriptor.dimension = WGPUTextureDimension_2D;
                texture_descriptor.size = {
                    image.image_width, image.image_height, 1U};
                texture_descriptor.format = WGPUTextureFormat_RGBA8Unorm;
                texture_descriptor.mipLevelCount = 1U;
                texture_descriptor.sampleCount = 1U;
                semantic_image_draw draw{};
                draw.first_vertex = first_vertex;
                draw.sampling = image.sampling;
                draw.texture = wgpuDeviceCreateTexture(
                    engine->device,
                    &texture_descriptor);
                if (draw.texture != nullptr) {
                    draw.view = wgpuTextureCreateView(draw.texture, nullptr);
                }
                if (draw.view != nullptr) {
                    draw.nearest_bind_group = create_image_texture_bind_group(
                        *engine,
                        engine->image_nearest_sampler,
                        draw.view,
                        "ProGPU semantic nearest image bind group");
                    draw.linear_bind_group = create_image_texture_bind_group(
                        *engine,
                        engine->image_linear_sampler,
                        draw.view,
                        "ProGPU semantic linear image bind group");
                }
                compiled_draws.push_back(draw);
                auto& retained_draw = compiled_draws.back();
                if (retained_draw.texture == nullptr ||
                    retained_draw.view == nullptr ||
                    retained_draw.nearest_bind_group == nullptr ||
                    retained_draw.linear_bind_group == nullptr) {
                    release_compiled();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                        "A semantic image page texture could not be allocated.");
                }
                progpu::native::webgpu::image_copy_texture destination{};
                destination.texture = retained_draw.texture;
                destination.aspect = WGPUTextureAspect_All;
                progpu::native::webgpu::texture_data_layout layout{};
                layout.bytesPerRow = image.row_bytes;
                layout.rowsPerImage = image.image_height;
                const std::uint64_t upload_bytes =
                    static_cast<std::uint64_t>(image.row_bytes) *
                        (image.image_height - 1U) +
                    static_cast<std::uint64_t>(image.image_width) * 4U;
                const WGPUExtent3D extent{
                    image.image_width, image.image_height, 1U};
                wgpuQueueWriteTexture(
                    engine->queue,
                    &destination,
                    bytes + resource.payload_offset,
                    static_cast<std::size_t>(upload_bytes),
                    &layout,
                    &extent);
                semantic_image_texture_upload_bytes += upload_bytes;
            }
        } catch (const std::bad_alloc&) {
            release_compiled();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic image packed page could not be compiled.");
        }
        const std::uint64_t vertex_bytes = vertices.size() *
            sizeof(progpu::native::vector_vertex);
        if (compiled_draws.size() != semantic_image_draw_count ||
            vertex_bytes != static_cast<std::uint64_t>(
                semantic_image_draw_count) * 4U *
                    sizeof(progpu::native::vector_vertex)) {
            release_compiled();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic image packed-page budget did not match compilation.");
        }
        WGPUBufferDescriptor vertex_descriptor{};
        vertex_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU semantic image packed vertex page");
        vertex_descriptor.usage =
            WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
        vertex_descriptor.size = vertex_bytes;
        compiled_vertex_buffer = wgpuDeviceCreateBuffer(
            engine->device,
            &vertex_descriptor);
        if (compiled_vertex_buffer == nullptr) {
            release_compiled();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The semantic image packed vertex page could not be allocated.");
        }
        wgpuQueueWriteBuffer(
            engine->queue,
            compiled_vertex_buffer,
            0U,
            vertices.data(),
            static_cast<std::size_t>(vertex_bytes));
        engine->release_semantic_image_page();
        semantic_image_page.vertex_buffer = compiled_vertex_buffer;
        compiled_vertex_buffer = nullptr;
        semantic_image_page.vertex_bytes = vertex_bytes;
        semantic_image_page.draws = std::move(compiled_draws);
        semantic_image_page.scene_hash = engine->semantic_scene_hash;
        semantic_image_page.dpi_scale = frame->dpi_scale;
        semantic_image_page.target_width = frame->width;
        semantic_image_page.target_height = frame->height;
        semantic_image_page.cache_valid = true;
        semantic_image_vertex_upload_bytes = vertex_bytes;
        semantic_image_index_upload_bytes = created_resources
            ? 6U * sizeof(std::uint32_t)
            : 0U;
    }

    const std::uint64_t submission_start = engine->submission_count;
    std::uint32_t draw_calls = 0U;
    std::uint32_t family_switches = 0U;
    std::uint32_t previous_family = 0U;
    std::uint64_t vertex_upload_bytes =
        semantic_analytic_vertex_upload_bytes +
        semantic_image_vertex_upload_bytes;
    std::uint64_t index_upload_bytes =
        semantic_analytic_index_upload_bytes +
        semantic_image_index_upload_bytes;
    std::uint64_t texture_upload_bytes =
        semantic_image_texture_upload_bytes;
    std::uint64_t uniform_upload_bytes = 0U;
    std::uint64_t coverage_staging_bytes = 0U;
    std::uint64_t semantic_layer_vertex_upload_bytes = 0U;
    std::uint64_t semantic_layer_uniform_upload_bytes = 0U;
    std::uint64_t semantic_layer_mask_uniform_upload_bytes = 0U;
    std::uint64_t semantic_layer_effect_uniform_upload_bytes = 0U;
    std::uint32_t semantic_layer_effect_pass_count = 0U;
    std::uint32_t semantic_effect_operation_count = 0U;
    std::uint32_t semantic_effect_cache_hit_count = 0U;
    std::array<progpu::native::effects::semantic_output_cache,
        PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
        semantic_effect_working_caches{};
    std::array<bool, PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
        semantic_effect_cache_updates{};
    for (std::size_t index = 0U;
         index < semantic_effect_working_caches.size();
         ++index) {
        semantic_effect_working_caches[index] =
            engine->semantic_layer_slots[index].effect_output_cache;
    }
    const std::uint64_t payload_hash = engine->semantic_scene_hash;
    std::uint32_t semantic_analytic_draw_index = 0U;
    std::uint32_t semantic_path_draw_index = 0U;
    std::uint32_t semantic_glyph_draw_index = 0U;
    std::uint32_t semantic_image_draw_index = 0U;

    const auto discard_encoder = [&]() noexcept {
        if (engine->semantic_encoder != nullptr) {
            wgpuCommandEncoderRelease(engine->semantic_encoder);
            engine->semantic_encoder = nullptr;
        }
    };
    const auto begin_encoder = [&]() noexcept {
        if (engine->semantic_encoder != nullptr) {
            return true;
        }
        WGPUCommandEncoderDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native semantic scene encoder");
        engine->semantic_encoder = wgpuDeviceCreateCommandEncoder(
            engine->device,
            &descriptor);
        return engine->semantic_encoder != nullptr;
    };
    const auto flush_encoder = [&]() noexcept {
        if (engine->semantic_encoder == nullptr) {
            return PROGPU_NATIVE_STATUS_SUCCESS;
        }
        WGPUCommandEncoder encoder = engine->semantic_encoder;
        engine->semantic_encoder = nullptr;
        WGPUCommandBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native semantic scene commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic scene command buffer could not be finished.");
        }
        engine->submit(command);
        wgpuCommandBufferRelease(command);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    };

    const auto note_family = [&](std::uint32_t family) noexcept {
        if (family != previous_family) {
            ++family_switches;
            previous_family = family;
        }
    };
    const auto reset_semantic_prepare_state = [&]() noexcept {
        engine->semantic_prepare_only = false;
        engine->semantic_load_target = false;
        engine->semantic_path_draw_active = false;
        engine->semantic_path_first_index = 0U;
        engine->semantic_path_index_count = 0U;
        engine->semantic_glyph_draw_active = false;
        engine->semantic_glyph_first_instance = 0U;
        engine->semantic_glyph_instance_count = 0U;
    };

    if ((semantic_draw_count != 0U ||
            semantic_has_materialized_layers) &&
        !begin_encoder()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic scene command encoder could not be created.");
    }

    if (semantic_path_draw_count != 0U &&
        (engine->semantic_path_gpu_scene_hash !=
                engine->semantic_scene_hash ||
            !engine->path_cache_valid ||
            !engine->path_gpu_cache_valid)) {
        progpu_native_path_frame family{};
        family.struct_size = sizeof(family);
        family.width = frame->width;
        family.height = frame->height;
        family.dpi_scale = frame->dpi_scale;
        family.target_view = frame->target_view;
        family.clear_color = frame->clear_color;
        static_assert(sizeof(std::size_t) == sizeof(std::uint64_t));
        static_assert(sizeof(progpu_native_scene_path_fill) ==
            sizeof(progpu_native_path_fill));
        static_assert(offsetof(
            progpu_native_scene_path_fill,
            segment_offset) == offsetof(
            progpu_native_path_fill,
            segment_offset));
        static_assert(offsetof(
            progpu_native_scene_path_fill,
            fill_rule) == offsetof(
            progpu_native_path_fill,
            fill_rule));
        family.paths = semantic_path_page.paths.data();
        family.path_count = semantic_path_page.paths.size();
        family.segments = semantic_path_page.segments.data();
        family.segment_count = semantic_path_page.segments.size();
        family.flags =
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD;
        family.content_revision = revision32(engine->semantic_scene_hash);
        progpu_native_path_frame_metrics family_metrics{};
        family_metrics.struct_size = sizeof(family_metrics);
        engine->semantic_prepare_only = true;
        engine->semantic_path_draw_active = true;
        engine->semantic_path_first_index =
            semantic_path_page.draws.front().first_index;
        engine->semantic_path_index_count =
            semantic_path_page.draws.front().index_count;
        const auto status = progpu_native_engine_render_paths(
            engine, &family, &family_metrics);
        reset_semantic_prepare_state();
        vertex_upload_bytes += family_metrics.vertex_upload_bytes;
        index_upload_bytes += family_metrics.index_upload_bytes;
        uniform_upload_bytes += family_metrics.uniform_upload_bytes;
        coverage_staging_bytes += family_metrics.coverage_staging_bytes;
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
            discard_encoder();
            return status;
        }
        engine->semantic_path_gpu_scene_hash = engine->semantic_scene_hash;
    }

    if (semantic_glyph_draw_count != 0U &&
        (engine->semantic_glyph_gpu_scene_hash !=
                engine->semantic_scene_hash ||
            !engine->glyph_cache_valid ||
            !engine->glyph_gpu_cache_valid)) {
        progpu_native_glyph_frame family{};
        family.struct_size = sizeof(family);
        family.width = frame->width;
        family.height = frame->height;
        family.dpi_scale = frame->dpi_scale;
        family.target_view = frame->target_view;
        family.clear_color = frame->clear_color;
        static_assert(sizeof(std::size_t) == sizeof(std::uint64_t));
        static_assert(sizeof(progpu_native_scene_glyph_outline) ==
            sizeof(progpu_native_glyph_outline));
        static_assert(offsetof(
            progpu_native_scene_glyph_outline,
            segment_offset) == offsetof(
            progpu_native_glyph_outline,
            segment_offset));
        static_assert(offsetof(
            progpu_native_scene_glyph_outline,
            raster_scale) == offsetof(
            progpu_native_glyph_outline,
            raster_scale));
        family.outlines = semantic_glyph_page.outlines.data();
        family.outline_count = semantic_glyph_page.outlines.size();
        family.segments = semantic_glyph_page.segments.data();
        family.segment_count = semantic_glyph_page.segments.size();
        family.glyphs = semantic_glyph_page.glyphs.data();
        family.glyph_count = semantic_glyph_page.glyphs.size();
        family.flags =
            PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD;
        family.content_revision = revision32(engine->semantic_scene_hash);
        progpu_native_glyph_frame_metrics family_metrics{};
        family_metrics.struct_size = sizeof(family_metrics);
        engine->semantic_prepare_only = true;
        engine->semantic_glyph_draw_active = true;
        engine->semantic_glyph_first_instance =
            semantic_glyph_page.draws.front().first_instance;
        engine->semantic_glyph_instance_count =
            semantic_glyph_page.draws.front().instance_count;
        const auto status = progpu_native_engine_render_glyphs(
            engine, &family, &family_metrics);
        reset_semantic_prepare_state();
        vertex_upload_bytes += family_metrics.instance_upload_bytes;
        uniform_upload_bytes += family_metrics.uniform_upload_bytes;
        coverage_staging_bytes += family_metrics.coverage_staging_bytes;
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
            discard_encoder();
            return status;
        }
        engine->semantic_glyph_gpu_scene_hash =
            engine->semantic_scene_hash;
    }

    const gpu_uniforms uniforms = create_uniforms(
        frame->width,
        frame->height,
        frame->dpi_scale);
    if (semantic_analytic_draw_count != 0U ||
        semantic_path_draw_count != 0U ||
        semantic_glyph_draw_count != 0U) {
        if (engine->analytic_pipeline == nullptr &&
            !create_analytic_pipeline(*engine)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic vector pipeline could not be created.");
        }
        const bool uploaded = engine->upload_uniform_if_changed(
            engine->analytic_uniform_buffer,
            uniforms,
            engine->cached_analytic_uniforms,
            engine->analytic_uniform_cache_valid);
        uniform_upload_bytes += uploaded ? sizeof(gpu_uniforms) : 0U;
    }
    if (semantic_image_draw_count != 0U) {
        if (!create_image_resources(*engine)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic image pipeline could not be created.");
        }
        const bool uploaded = engine->upload_uniform_if_changed(
            engine->image_uniform_buffer,
            uniforms,
            engine->cached_image_uniforms,
            engine->image_uniform_cache_valid);
        uniform_upload_bytes += uploaded ? sizeof(gpu_uniforms) : 0U;
    }
    if (semantic_has_materialized_layers) {
        if (semantic_has_layer_masks &&
            !create_layer_mask_resources(*engine)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The retained semantic layer-mask pipeline could not be prepared.");
        }
        if (semantic_has_layer_effects &&
            (!create_gaussian_effect_resources(*engine) ||
             (semantic_has_drop_shadows &&
                !create_drop_shadow_effect_resources(*engine)) ||
             !ensure_semantic_effect_uniform_buffer(
                *engine,
                semantic_effect_uniform_bytes))) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The retained semantic effect-chain resources could not be prepared.");
        }
        if (!prepare_semantic_layer_resources(
                *engine,
                layer_budget,
                frame->width,
                frame->height,
                frame->dpi_scale,
                semantic_materialized_layer_count,
                semantic_layer_uniform_upload_bytes)) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The bounded semantic isolated-layer GPU pool could not be prepared.");
        }
    }

    if ((semantic_draw_count != 0U ||
            semantic_has_materialized_layers) &&
        !engine->semantic_render_bundle_valid) {
        WGPURenderBundleEncoderDescriptor bundle_descriptor{};
        bundle_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU retained semantic mixed-scene bundle encoder");
        bundle_descriptor.colorFormatCount = 1U;
        bundle_descriptor.colorFormats = &engine->target_format;
        bundle_descriptor.sampleCount = 1U;
        std::vector<semantic_render_bundle_span> compiled_spans;
        std::vector<semantic_effect_dispatch> compiled_effect_dispatches;
        std::vector<std::byte> semantic_effect_uniform_data;
        std::vector<progpu::native::vector_vertex>
            semantic_layer_vertices;
        try {
            compiled_spans.reserve(header.command_count);
            compiled_effect_dispatches.reserve(semantic_effect_node_count);
            semantic_effect_uniform_data.resize(
                static_cast<std::size_t>(semantic_effect_uniform_bytes));
            semantic_layer_vertices.reserve(
                static_cast<std::size_t>(
                    semantic_materialized_layer_count) * 4U);
        } catch (const std::bad_alloc&) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                "The retained semantic clip-span table could not be allocated.");
        }
        WGPURenderBundleEncoder bundle_encoder = nullptr;
        std::uint32_t semantic_effect_uniform_cursor = 0U;
        semantic_scissor active_scissor{};
        bool has_active_scissor = false;
        std::uint32_t active_target_layer =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        const auto release_compiled_spans = [&]() noexcept {
            for (auto& span : compiled_spans) {
                if (span.mask_bind_group != nullptr) {
                    wgpuBindGroupRelease(span.mask_bind_group);
                    span.mask_bind_group = nullptr;
                }
                if (span.mask_uniform_buffer != nullptr) {
                    wgpuBufferDestroy(span.mask_uniform_buffer);
                    wgpuBufferRelease(span.mask_uniform_buffer);
                    span.mask_uniform_buffer = nullptr;
                }
                if (span.bundle != nullptr) {
                    wgpuRenderBundleRelease(span.bundle);
                    span.bundle = nullptr;
                }
            }
            compiled_spans.clear();
        };
        const auto fail_bundle = [&](progpu_native_status status) noexcept {
            if (bundle_encoder != nullptr) {
                wgpuRenderBundleEncoderRelease(bundle_encoder);
                bundle_encoder = nullptr;
            }
            release_compiled_spans();
            discard_encoder();
            return status;
        };
        const auto finish_active_bundle = [&]() {
            if (bundle_encoder == nullptr) {
                return PROGPU_NATIVE_STATUS_SUCCESS;
            }
            WGPURenderBundleDescriptor finish_descriptor{};
            finish_descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU retained semantic clip-span bundle");
            WGPURenderBundle bundle = wgpuRenderBundleEncoderFinish(
                bundle_encoder,
                &finish_descriptor);
            wgpuRenderBundleEncoderRelease(bundle_encoder);
            bundle_encoder = nullptr;
            if (bundle == nullptr) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A retained semantic clip-span bundle could not be finished.");
            }
            semantic_render_bundle_span operation{};
            operation.kind = semantic_replay_kind::bundle;
            operation.bundle = bundle;
            operation.clip_x = active_scissor.x;
            operation.clip_y = active_scissor.y;
            operation.clip_width = active_scissor.width;
            operation.clip_height = active_scissor.height;
            operation.target_layer = active_target_layer;
            compiled_spans.push_back(operation);
            return PROGPU_NATIVE_STATUS_SUCCESS;
        };
        const auto begin_active_bundle = [&](
            semantic_scissor scissor,
            std::uint32_t target_layer) {
            bundle_encoder = wgpuDeviceCreateRenderBundleEncoder(
                engine->device,
                &bundle_descriptor);
            if (bundle_encoder == nullptr) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "A retained semantic clip-span encoder could not be created.");
            }
            active_scissor = scissor;
            active_target_layer = target_layer;
            has_active_scissor = true;
            return PROGPU_NATIVE_STATUS_SUCCESS;
        };

        semantic_state_cursor state_cursor(bytes, header);
        semantic_layer_target_cursor target_cursor(
            bytes,
            frame->width,
            frame->height,
            frame->dpi_scale);
        std::array<bool,
            PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH>
            layer_scope_materialized{};
        std::array<progpu_native_scene_layer,
            PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
            materialized_layers{};
        std::array<semantic_scissor,
            PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS>
            materialized_extents{};
        std::uint32_t layer_scope_depth = 0U;
        std::uint32_t materialized_depth = 0U;
        std::uint32_t current_target_layer =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        for (std::uint32_t index = 0U;
             index < header.command_count;
             ++index) {
            const auto command = read_command(index);
            const auto state = state_cursor.advance(command);
            const auto target_extent = target_cursor.advance(command);
            if (command.kind ==
                PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
                auto layer = semantic_default_layer();
                if (command.payload_size != 0U) {
                    std::memcpy(
                        &layer,
                        bytes + command.payload_offset,
                        sizeof(layer));
                }
                const bool materialized =
                    progpu::native::scene::layer_requires_materialization(
                        layer);
                layer_scope_materialized[layer_scope_depth++] =
                    materialized;
                if (materialized) {
                    const auto finish_status = finish_active_bundle();
                    if (finish_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                        return fail_bundle(finish_status);
                    }
                    const std::uint32_t slot = materialized_depth;
                    materialized_layers[materialized_depth++] = layer;
                    materialized_extents[slot] = target_extent;
                    semantic_render_bundle_span operation{};
                    operation.kind = semantic_replay_kind::push_layer;
                    operation.target_layer = slot;
                    compiled_spans.push_back(operation);
                    current_target_layer = slot;
                    has_active_scissor = false;
                }
                continue;
            }
            if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
                const bool materialized =
                    layer_scope_materialized[--layer_scope_depth];
                if (materialized) {
                    const auto finish_status = finish_active_bundle();
                    if (finish_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                        return fail_bundle(finish_status);
                    }
                    const std::uint32_t source_layer =
                        --materialized_depth;
                    const auto& layer = materialized_layers[source_layer];
                    const auto& source_extent =
                        materialized_extents[source_layer];
                    const std::uint32_t first_vertex =
                        static_cast<std::uint32_t>(
                            semantic_layer_vertices.size());
                    append_semantic_layer_quad(
                        semantic_layer_vertices,
                        source_extent,
                        target_extent,
                        layer_budget.slot_widths[source_layer],
                        layer_budget.slot_heights[source_layer],
                        frame->dpi_scale,
                        layer.opacity);
                    semantic_render_bundle_span operation{};
                    operation.kind = semantic_replay_kind::pop_layer;
                    operation.operation_id = command.command_id;
                    operation.target_layer = materialized_depth == 0U
                        ? PROGPU_NATIVE_SCENE_NO_INDEX
                        : materialized_depth - 1U;
                    operation.source_layer = source_layer;
                    operation.first_composite_vertex = first_vertex;
                    operation.blend_mode = layer.blend_mode;
                    if (layer.effect_resource_index !=
                            PROGPU_NATIVE_SCENE_NO_INDEX) {
                        const auto resource = read_resource(
                            layer.effect_resource_index);
                        progpu_native_scene_effect_chain chain{};
                        std::memcpy(
                            &chain,
                            bytes + resource.payload_offset,
                            sizeof(chain));
                        std::array<progpu_native_group_effect,
                            PROGPU_NATIVE_MAX_GROUP_EFFECTS> effects{};
                        for (std::uint32_t effect_index = 0U;
                             effect_index < chain.effect_count;
                             ++effect_index) {
                            std::memcpy(
                                &effects[effect_index],
                                bytes + resource.auxiliary_offset +
                                    static_cast<std::size_t>(effect_index) *
                                        sizeof(progpu_native_group_effect),
                                sizeof(progpu_native_group_effect));
                        }
                        const auto plan =
                            progpu::native::effects::create_chain_plan(
                            effects.data(),
                            chain.effect_count);
                        operation.first_effect_dispatch =
                            static_cast<std::uint32_t>(
                                compiled_effect_dispatches.size());
                        operation.effect_count = chain.effect_count;
                        operation.final_effect_texture =
                            plan[chain.effect_count - 1U].output;
                        const auto append_effect_uniform = [&]<typename T>(
                            const T& value) {
                            const std::uint32_t offset =
                                semantic_effect_uniform_cursor;
                            std::memcpy(
                                semantic_effect_uniform_data.data() + offset,
                                &value,
                                sizeof(value));
                            semantic_effect_uniform_cursor +=
                                semantic_effect_uniform_alignment;
                            return offset;
                        };
                        for (std::uint32_t effect_index = 0U;
                             effect_index < chain.effect_count;
                             ++effect_index) {
                            const auto& effect = effects[effect_index];
                            semantic_effect_dispatch dispatch{};
                            dispatch.kind = effect.kind;
                            dispatch.source_texture =
                                plan[effect_index].source;
                            dispatch.horizontal_texture =
                                plan[effect_index].horizontal;
                            dispatch.vertical_texture =
                                plan[effect_index].vertical;
                            dispatch.output_texture =
                                plan[effect_index].output;
                            const auto create_blur = [frame](float sigma) {
                                gpu_gaussian_blur_params parameters{};
                                parameters.sigma = sigma * frame->dpi_scale;
                                parameters.radius =
                                    static_cast<std::uint32_t>(std::clamp(
                                        static_cast<int>(std::ceil(
                                            parameters.sigma * 3.0F)),
                                        0,
                                        128));
                                return parameters;
                            };
                            const auto horizontal = create_blur(
                                effect.sigma_x);
                            const auto vertical = create_blur(
                                effect.sigma_y);
                            dispatch.horizontal_uniform_offset =
                                append_effect_uniform(horizontal);
                            dispatch.vertical_uniform_offset =
                                append_effect_uniform(vertical);
                            if (effect.kind ==
                                PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
                                gpu_drop_shadow_params drop{};
                                drop.offset[0] = effect.offset_x *
                                    frame->dpi_scale;
                                drop.offset[1] = effect.offset_y *
                                    frame->dpi_scale;
                                drop.color[0] = effect.color_r;
                                drop.color[1] = effect.color_g;
                                drop.color[2] = effect.color_b;
                                drop.color[3] = effect.color_a;
                                dispatch.drop_shadow_uniform_offset =
                                    append_effect_uniform(drop);
                            }
                            compiled_effect_dispatches.push_back(dispatch);
                        }
                    }
                    if (layer.mask_resource_index !=
                            PROGPU_NATIVE_SCENE_NO_INDEX) {
                        const auto resource = read_resource(
                            layer.mask_resource_index);
                        progpu_native_scene_layer_mask mask{};
                        std::memcpy(
                            &mask,
                            bytes + resource.payload_offset,
                            sizeof(mask));
                        if (!create_semantic_layer_mask_binding(
                                *engine,
                                mask,
                                target_extent,
                                frame->dpi_scale,
                                operation)) {
                            return fail_bundle(engine->fail(
                                PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
                                "A retained semantic layer-mask binding could not be prepared."));
                        }
                        semantic_layer_mask_uniform_upload_bytes +=
                            sizeof(gpu_mask_sampling_uniforms);
                        semantic_layer_uniform_upload_bytes +=
                            sizeof(gpu_mask_sampling_uniforms);
                    }
                    compiled_spans.push_back(operation);
                    current_target_layer = operation.target_layer;
                    has_active_scissor = false;
                    ++draw_calls;
                }
                continue;
            }
            if (command.kind <
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
                command.kind > PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
                continue;
            }
            const auto scissor = resolve_semantic_target_scissor(
                state,
                target_extent,
                frame->width,
                frame->height,
                frame->dpi_scale);
            if (scissor.drawable &&
                (!has_active_scissor || scissor != active_scissor ||
                    current_target_layer != active_target_layer)) {
                const auto finish_status = finish_active_bundle();
                if (finish_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                    return fail_bundle(finish_status);
                }
                const auto begin_status = begin_active_bundle(
                    scissor,
                    current_target_layer);
                if (begin_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                    return fail_bundle(begin_status);
                }
            }
            if (scissor.drawable) {
                note_family(command.kind);
            }
            progpu_native_status status =
                PROGPU_NATIVE_STATUS_SUCCESS;
            switch (command.kind) {
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC: {
                    if (semantic_analytic_draw_index >=
                        semantic_analytic_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic analytic packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_analytic_draw_index;
                    ++semantic_analytic_draw_index;
                    if (scissor.drawable) {
                        status = encode_semantic_analytic_draw<
                            semantic_render_bundle_commands>(
                                *engine,
                                bundle_encoder,
                                semantic_analytic_page.draws[draw_index],
                                current_target_layer);
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH: {
                    if (semantic_path_draw_index >=
                        semantic_path_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic path packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_path_draw_index;
                    ++semantic_path_draw_index;
                    if (scissor.drawable) {
                        status = encode_semantic_path_draw<
                            semantic_render_bundle_commands>(
                                *engine,
                                bundle_encoder,
                                semantic_path_page.draws[draw_index],
                                current_target_layer);
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN: {
                    if (semantic_glyph_draw_index >=
                        semantic_glyph_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic glyph packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_glyph_draw_index;
                    ++semantic_glyph_draw_index;
                    if (scissor.drawable) {
                        status = encode_semantic_glyph_draw<
                            semantic_render_bundle_commands>(
                                *engine,
                                bundle_encoder,
                                semantic_glyph_page.draws[draw_index],
                                current_target_layer);
                    }
                    break;
                }
                case PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE: {
                    if (semantic_image_draw_index >=
                        semantic_image_page.draws.size()) {
                        return fail_bundle(engine->fail(
                            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                            "The semantic image packed-page index is invalid."));
                    }
                    const auto draw_index = semantic_image_draw_index;
                    ++semantic_image_draw_index;
                    if (scissor.drawable) {
                        status = encode_semantic_image_draw<
                            semantic_render_bundle_commands>(
                                *engine,
                                bundle_encoder,
                                semantic_image_page.draws[draw_index],
                                current_target_layer);
                    }
                    break;
                }
                default:
                    break;
            }
            if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
                return fail_bundle(status);
            }
            draw_calls += scissor.drawable ? 1U : 0U;
        }

        if (semantic_analytic_draw_index !=
                semantic_analytic_draw_count ||
            semantic_path_draw_index != semantic_path_draw_count ||
            semantic_glyph_draw_index != semantic_glyph_draw_count ||
            semantic_image_draw_index != semantic_image_draw_count) {
            return fail_bundle(engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "A semantic packed-page draw count is inconsistent."));
        }
        if (layer_scope_depth != 0U || materialized_depth != 0U ||
            semantic_layer_vertices.size() !=
                static_cast<std::size_t>(
                    semantic_materialized_layer_count) * 4U ||
            compiled_effect_dispatches.size() !=
                semantic_effect_node_count ||
            semantic_effect_uniform_cursor !=
                semantic_effect_uniform_bytes) {
            return fail_bundle(engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic isolated-layer replay program is inconsistent."));
        }

        const auto finish_status = finish_active_bundle();
        if (finish_status != PROGPU_NATIVE_STATUS_SUCCESS) {
            return fail_bundle(finish_status);
        }
        if (!semantic_layer_vertices.empty()) {
            const std::uint64_t layer_vertex_bytes =
                semantic_layer_vertices.size() *
                sizeof(progpu::native::vector_vertex);
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->semantic_layer_vertex_buffer,
                0U,
                semantic_layer_vertices.data(),
                layer_vertex_bytes);
            vertex_upload_bytes += layer_vertex_bytes;
            semantic_layer_vertex_upload_bytes = layer_vertex_bytes;
        }
        if (!semantic_effect_uniform_data.empty()) {
            wgpuQueueWriteBuffer(
                engine->queue,
                engine->semantic_effect_uniform_buffer,
                0U,
                semantic_effect_uniform_data.data(),
                semantic_effect_uniform_data.size());
            semantic_layer_effect_uniform_upload_bytes =
                semantic_effect_uniform_data.size();
            semantic_layer_uniform_upload_bytes +=
                semantic_effect_uniform_data.size();
        }
        engine->semantic_render_bundle_spans = std::move(compiled_spans);
        engine->semantic_effect_dispatches =
            std::move(compiled_effect_dispatches);
        engine->semantic_render_bundle_valid = true;
        engine->semantic_render_bundle_scene_hash =
            engine->semantic_scene_hash;
        engine->semantic_render_bundle_dpi_scale = frame->dpi_scale;
        engine->semantic_render_bundle_width = frame->width;
        engine->semantic_render_bundle_height = frame->height;
        engine->semantic_render_bundle_draw_call_count = draw_calls;
        engine->semantic_render_bundle_family_switch_count =
            family_switches;
    } else if (semantic_draw_count != 0U ||
        semantic_has_materialized_layers) {
        draw_calls = engine->semantic_render_bundle_draw_call_count;
        family_switches =
            engine->semantic_render_bundle_family_switch_count;
    }
    uniform_upload_bytes += semantic_layer_uniform_upload_bytes;

    WGPURenderPassEncoder pass = nullptr;
    if (semantic_draw_count != 0U &&
        !semantic_has_materialized_layers) {
        WGPURenderPassColorAttachment color_attachment{};
        progpu::native::webgpu::initialize_color_attachment(
            color_attachment);
        color_attachment.view = reinterpret_cast<WGPUTextureView>(
            frame->target_view);
        color_attachment.loadOp = WGPULoadOp_Clear;
        color_attachment.storeOp = WGPUStoreOp_Store;
        color_attachment.clearValue = WGPUColor{
            frame->clear_color.r,
            frame->clear_color.g,
            frame->clear_color.b,
            frame->clear_color.a};
        WGPURenderPassDescriptor pass_descriptor{};
        pass_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU retained semantic bundle replay pass");
        pass_descriptor.colorAttachmentCount = 1U;
        pass_descriptor.colorAttachments = &color_attachment;
        pass = wgpuCommandEncoderBeginRenderPass(
            engine->semantic_encoder,
            &pass_descriptor);
        if (pass == nullptr) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic bundle replay pass could not be created.");
        }
        for (const auto& span : engine->semantic_render_bundle_spans) {
            wgpuRenderPassEncoderSetScissorRect(
                pass,
                span.clip_x,
                span.clip_y,
                span.clip_width,
                span.clip_height);
            wgpuRenderPassEncoderExecuteBundles(
                pass, 1U, &span.bundle);
        }
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
    } else if (semantic_has_materialized_layers) {
        std::uint32_t active_target_layer =
            PROGPU_NATIVE_SCENE_NO_INDEX;
        const auto finish_pass = [&]() noexcept {
            if (pass != nullptr) {
                wgpuRenderPassEncoderEnd(pass);
                wgpuRenderPassEncoderRelease(pass);
                pass = nullptr;
            }
        };
        const auto target_view = [&](std::uint32_t target_layer) {
            if (target_layer == PROGPU_NATIVE_SCENE_NO_INDEX) {
                return reinterpret_cast<WGPUTextureView>(
                    frame->target_view);
            }
            return target_layer < engine->semantic_layer_slots.size()
                ? engine->semantic_layer_slots[target_layer].view
                : nullptr;
        };
        const auto begin_pass = [&](
            std::uint32_t target_layer,
            WGPULoadOp load_op) {
            WGPUTextureView view = target_view(target_layer);
            if (view == nullptr) {
                return false;
            }
            WGPURenderPassColorAttachment color_attachment{};
            progpu::native::webgpu::initialize_color_attachment(
                color_attachment);
            color_attachment.view = view;
            color_attachment.loadOp = load_op;
            color_attachment.storeOp = WGPUStoreOp_Store;
            color_attachment.clearValue = target_layer ==
                    PROGPU_NATIVE_SCENE_NO_INDEX
                ? WGPUColor{
                    frame->clear_color.r,
                    frame->clear_color.g,
                    frame->clear_color.b,
                    frame->clear_color.a}
                : WGPUColor{0.0, 0.0, 0.0, 0.0};
            WGPURenderPassDescriptor pass_descriptor{};
            pass_descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU retained semantic isolated-layer replay pass");
            pass_descriptor.colorAttachmentCount = 1U;
            pass_descriptor.colorAttachments = &color_attachment;
            pass = wgpuCommandEncoderBeginRenderPass(
                engine->semantic_encoder,
                &pass_descriptor);
            active_target_layer = target_layer;
            return pass != nullptr;
        };
        const auto fail_replay = [&](const char* message) {
            finish_pass();
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                message);
        };

        if (!begin_pass(
                PROGPU_NATIVE_SCENE_NO_INDEX,
                WGPULoadOp_Clear)) {
            return fail_replay(
                "The semantic isolated-layer root pass could not be created.");
        }
        for (const auto& operation :
             engine->semantic_render_bundle_spans) {
            if (operation.kind == semantic_replay_kind::push_layer) {
                finish_pass();
                if (!begin_pass(
                        operation.target_layer,
                        WGPULoadOp_Clear)) {
                    return fail_replay(
                        "A semantic isolated-layer content pass could not be created.");
                }
                continue;
            }
            if (operation.kind == semantic_replay_kind::pop_layer) {
                finish_pass();
                bool effect_ready = true;
                if (operation.effect_count != 0U) {
                    ++semantic_effect_operation_count;
                    if (operation.source_layer >=
                            engine->semantic_layer_slots.size()) {
                        return fail_replay(
                            "A semantic effect layer index is invalid.");
                    }
                    const auto& slot = engine->semantic_layer_slots[
                        operation.source_layer];
                    const progpu::native::effects::semantic_output_cache_key
                        cache_key{
                            engine->semantic_scene_hash,
                            operation.operation_id,
                            slot.effect_generation,
                            slot.effect_width,
                            slot.effect_height};
                    if (progpu::native::effects::semantic_output_cache_hit(
                            semantic_effect_working_caches[
                                operation.source_layer],
                            cache_key)) {
                        ++semantic_effect_cache_hit_count;
                    } else {
                        effect_ready = encode_semantic_effect_chain(
                            *engine,
                            engine->semantic_encoder,
                            operation,
                            semantic_layer_effect_pass_count);
                        if (effect_ready) {
                            progpu::native::effects::
                                commit_semantic_output_cache(
                                    semantic_effect_working_caches[
                                        operation.source_layer],
                                    cache_key);
                            semantic_effect_cache_updates[
                                operation.source_layer] = true;
                        }
                    }
                }
                if (!effect_ready ||
                    !begin_pass(
                        operation.target_layer,
                        WGPULoadOp_Load) ||
                    !encode_semantic_layer_composite(
                        *engine,
                        pass,
                        operation)) {
                    return fail_replay(
                        "A semantic isolated-layer composite pass could not be encoded.");
                }
                continue;
            }
            if (operation.target_layer != active_target_layer) {
                finish_pass();
                if (!begin_pass(
                        operation.target_layer,
                        WGPULoadOp_Load)) {
                    return fail_replay(
                        "A semantic isolated-layer continuation pass could not be created.");
                }
            }
            wgpuRenderPassEncoderSetScissorRect(
                pass,
                operation.clip_x,
                operation.clip_y,
                operation.clip_width,
                operation.clip_height);
            wgpuRenderPassEncoderExecuteBundles(
                pass,
                1U,
                &operation.bundle);
        }
        finish_pass();
    }

    const auto flush_status = flush_encoder();
    if (flush_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        engine->semantic_load_target = false;
        return flush_status;
    }
    for (std::size_t index = 0U;
         index < semantic_effect_cache_updates.size();
         ++index) {
        if (semantic_effect_cache_updates[index]) {
            engine->semantic_layer_slots[index].effect_output_cache =
                semantic_effect_working_caches[index];
        }
    }

    if (semantic_draw_count == 0U &&
        !semantic_has_materialized_layers) {
        progpu_native_analytic_frame clear{};
        clear.struct_size = sizeof(clear);
        clear.width = frame->width;
        clear.height = frame->height;
        clear.dpi_scale = frame->dpi_scale;
        clear.target_view = frame->target_view;
        clear.clear_color = frame->clear_color;
        progpu_native_analytic_frame_metrics clear_metrics{};
        clear_metrics.struct_size = sizeof(clear_metrics);
        const auto status = progpu_native_engine_render_analytic(
            engine, &clear, &clear_metrics);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
            return status;
        }
        uniform_upload_bytes += clear_metrics.uniform_upload_bytes;
    }

    if (semantic_has_materialized_layers) {
        engine->last_layer_metrics = {};
        engine->last_layer_metrics.struct_size =
            sizeof(progpu_native_layer_metrics);
        std::uint32_t texture_generation = 0U;
        std::uint32_t effect_texture_generation = 0U;
        for (std::uint32_t index = 0U;
             index < layer_budget.peak_materialized_depth;
             ++index) {
            texture_generation = std::max(
                texture_generation,
                engine->semantic_layer_slots[index].generation);
            effect_texture_generation = std::max(
                effect_texture_generation,
                engine->semantic_layer_slots[index].effect_generation);
        }
        engine->last_layer_metrics.texture_width =
            layer_budget.maximum_width();
        engine->last_layer_metrics.texture_height =
            layer_budget.maximum_height();
        engine->last_layer_metrics.texture_generation = texture_generation;
        engine->last_layer_metrics.allocation_count =
            engine->semantic_layer_allocation_count;
        engine->last_layer_metrics.content_pass_count =
            semantic_materialized_layer_count;
        engine->last_layer_metrics.composite_pass_count =
            semantic_materialized_layer_count;
        engine->last_layer_metrics.cache_hit =
            semantic_render_bundle_hit ? 1U : 0U;
        engine->last_layer_metrics.texture_bytes =
            layer_budget.pooled_bytes();
        engine->last_layer_metrics.vertex_upload_bytes =
            semantic_layer_vertex_upload_bytes;
        engine->last_layer_metrics.uniform_upload_bytes =
            semantic_layer_uniform_upload_bytes;
        engine->last_layer_metrics.mask_kind = semantic_has_layer_masks
            ? PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE
            : PROGPU_NATIVE_GROUP_MASK_NONE;
        engine->last_layer_metrics.mask_bind_group_generation =
            engine->layer_mask_bind_group_generation;
        engine->last_layer_metrics.mask_uniform_upload_bytes =
            semantic_layer_mask_uniform_upload_bytes;
        engine->last_layer_metrics.effect_kind =
            semantic_has_layer_effects
                ? semantic_has_drop_shadows
                    ? PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW
                    : PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR
                : PROGPU_NATIVE_GROUP_EFFECT_NONE;
        engine->last_layer_metrics.effect_revision =
            semantic_has_layer_effects
                ? semantic_effect_chain_revision
                : 0U;
        engine->last_layer_metrics.effect_pass_count =
            semantic_layer_effect_pass_count;
        engine->last_layer_metrics.effect_texture_generation =
            semantic_has_layer_effects ? effect_texture_generation : 0U;
        engine->last_layer_metrics.effect_allocation_count =
            semantic_has_layer_effects
                ? engine->semantic_effect_allocation_count
                : 0U;
        engine->last_layer_metrics.effect_cache_hit =
            semantic_effect_operation_count != 0U &&
                semantic_effect_cache_hit_count ==
                    semantic_effect_operation_count
            ? 1U
            : 0U;
        engine->last_layer_metrics.effect_texture_bytes =
            pooled_effect_bytes;
        engine->last_layer_metrics.effect_uniform_upload_bytes =
            semantic_layer_effect_uniform_upload_bytes;
        engine->last_layer_metrics.effect_count =
            semantic_effect_node_count;
        engine->last_layer_metrics.effect_chain_revision =
            semantic_has_layer_effects
                ? semantic_effect_chain_revision
                : 0U;
        engine->last_layer_metrics.blend_mode =
            PROGPU_NATIVE_BLEND_SRC_OVER;
    }

    engine->last_error.clear();
    if (metrics != nullptr && metrics->struct_size >=
            sizeof(progpu_native_scene_frame_metrics)) {
        metrics->command_count = header.command_count;
        metrics->draw_call_count = draw_calls;
        metrics->family_switch_count = family_switches;
        metrics->submission_count =
            engine->submission_count - submission_start;
        metrics->vertex_upload_bytes = vertex_upload_bytes;
        metrics->index_upload_bytes = index_upload_bytes;
        metrics->texture_upload_bytes = texture_upload_bytes;
        metrics->uniform_upload_bytes = uniform_upload_bytes;
        metrics->coverage_staging_bytes = coverage_staging_bytes;
        metrics->payload_hash = payload_hash;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_get_last_submission(
    progpu_native_engine* engine,
    std::uint64_t* submission_index) {
    if (engine == nullptr || submission_index == nullptr) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer submission timeline must be queried from its owner thread.");
    }
    *submission_index = engine->last_submission_index;
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_get_layer_metrics(
    progpu_native_engine* engine,
    progpu_native_layer_metrics* metrics) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    constexpr std::uint32_t legacy_size =
        offsetof(progpu_native_layer_metrics, mask_kind);
    if (engine == nullptr || metrics == nullptr ||
        metrics->struct_size < legacy_size) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer layer metrics must be queried from its owner thread.");
    }
    const std::uint32_t requested_size = metrics->struct_size;
    std::memcpy(
        metrics,
        &engine->last_layer_metrics,
        std::min<std::size_t>(
            requested_size,
            sizeof(progpu_native_layer_metrics)));
    metrics->struct_size = sizeof(progpu_native_layer_metrics);
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_engine_poll_submission(
    progpu_native_engine* engine,
    std::uint64_t submission_index,
    std::uint8_t wait,
    std::uint8_t* complete) {
    const progpu::native::webgpu::dispatch_scope dispatch_scope(
        engine == nullptr ? nullptr : &engine->webgpu_dispatch);
    if (engine == nullptr || complete == nullptr || wait > 1U ||
        submission_index == 0U ||
        submission_index > engine->last_submission_index) {
        return engine == nullptr
            ? PROGPU_NATIVE_STATUS_INVALID_ARGUMENT
            : engine->fail(
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
                "The native renderer submission token is invalid.");
    }
    if (!engine->is_owner_thread()) {
        return engine->fail(
            PROGPU_NATIVE_STATUS_WRONG_THREAD,
            "The native renderer submission timeline must be polled from its owner thread.");
    }
    *complete = progpu::native::webgpu::poll_submission(
        engine->instance,
        engine->device,
        engine->queue,
        submission_index,
        wait != 0U)
        ? 1U
        : 0U;
    engine->last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

size_t progpu_native_engine_get_last_error(
    const progpu_native_engine* engine,
    char* destination,
    size_t destination_size) {
    if (engine == nullptr) {
        return 0U;
    }
    const std::size_t required = engine->last_error.size() + 1U;
    if (destination != nullptr && destination_size != 0U) {
        const std::size_t copy_size = std::min(
            engine->last_error.size(),
            destination_size - 1U);
        std::memcpy(destination, engine->last_error.data(), copy_size);
        destination[copy_size] = '\0';
    }
    return required;
}

} // extern "C"
