#include "progpu_native.h"
#include "progpu_native_geometry.hpp"
#include "progpu_native_scene.hpp"
#include "GlyphRasterizerWgsl.generated.hpp"
#include "ClipComposeWgsl.generated.hpp"
#include "GaussianBlurHorizontalWgsl.generated.hpp"
#include "GaussianBlurVerticalWgsl.generated.hpp"
#include "GroupDropShadowComposeWgsl.generated.hpp"
#include "GroupBlendWgsl.generated.hpp"
#include "PathRasterizerWgsl.generated.hpp"
#include "TextWgsl.generated.hpp"
#include "TextureWgsl.generated.hpp"
#include "VectorWgsl.generated.hpp"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_webgpu_compat.hpp"

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

constexpr std::uint64_t initial_vertex_buffer_size = 64U * 1024U;
constexpr std::uint64_t initial_index_buffer_size = 16U * 1024U;
constexpr std::uint64_t initial_brush_buffer_size = 64U * 256U;
constexpr std::uint64_t gpu_brush_size = 256U;
constexpr float antialias_padding_pixels = 1.5F;
constexpr std::uint32_t native_initial_atlas_size = 1024U;
constexpr std::uint32_t native_max_atlas_size = 4096U;
constexpr std::uint32_t path_padding = 4U;
constexpr std::uint32_t webgpu_copy_row_alignment = 256U;

enum class layer_family : std::uint32_t {
    solid = 1U,
    analytic = 2U,
    geometry = 3U,
    path = 4U,
    glyph = 5U,
    image = 6U
};

struct gpu_uniforms {
    float projection[16];
    float model_view_projection[16];
    float view[16];
    float canvas_size[2];
    float dpi_scale;
    float pad0;
    float render_origin[2];
    float pad1[2];
};

static_assert(sizeof(gpu_uniforms) == 224U);

struct gpu_mask_sampling_uniforms {
    float coordinate0[4];
    float coordinate1[4];
    float bounds[4];
    float corner_radii_x[4];
    float corner_radii_y[4];
    float options[4];
};

struct gpu_drop_shadow_params {
    float offset[2];
    float padding[2];
    float color[4];
};

static_assert(sizeof(gpu_drop_shadow_params) == 32U);

struct gpu_group_blend_uniforms {
    float backdrop[4];
    std::uint32_t blend_mode;
    std::uint32_t padding[3];
};

static_assert(sizeof(gpu_group_blend_uniforms) == 32U);

static_assert(sizeof(gpu_mask_sampling_uniforms) == 96U);

struct gpu_gaussian_blur_params {
    float sigma;
    std::uint32_t radius;
    std::uint32_t padding0;
    std::uint32_t padding1;
};

static_assert(sizeof(gpu_gaussian_blur_params) == 16U);

struct gpu_path_uniforms {
    float x_start;
    float y_start;
    float scale_x;
    float scale_y;
    std::uint32_t path_index;
    std::uint32_t output_offset_words;
    std::uint32_t output_row_words;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t sample_grid;
    std::uint32_t path_index_b;
    std::uint32_t path_op_kind;
};

struct gpu_path_record {
    std::uint32_t start_segment;
    std::uint32_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    std::uint32_t fill_rule;
    std::uint32_t pad1;
};

struct native_path_raster {
    std::uint32_t atlas_x;
    std::uint32_t atlas_y;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t output_offset;
    std::uint32_t output_bytes_per_row;
    float scale_x;
    float scale_y;
    float raster_min_x;
    float raster_min_y;
    float subpixel_x;
    float subpixel_y;
};

struct gpu_clip_vertex {
    float position[2];
    float atlas_uv[2];
};

struct gpu_clip_compose_uniforms {
    std::uint32_t operation;
    std::uint32_t first;
    std::uint32_t width;
    std::uint32_t height;
};

static_assert(sizeof(gpu_clip_vertex) == 16U);
static_assert(sizeof(gpu_clip_compose_uniforms) == 16U);

struct native_path_cache_key {
    std::size_t segment_offset;
    std::size_t segment_count;
    std::uint32_t min_x;
    std::uint32_t min_y;
    std::uint32_t max_x;
    std::uint32_t max_y;
    std::uint32_t scale;
    std::uint32_t subpixel_x;
    std::uint32_t subpixel_y;
    std::uint32_t fill_rule;
    std::uint32_t sample_grid;

    bool operator==(const native_path_cache_key&) const = default;
};

struct native_path_cache_key_hash {
    std::size_t operator()(const native_path_cache_key& key) const noexcept {
        std::uint64_t hash = 14695981039346656037ULL;
        const auto mix = [&hash](std::uint64_t value) {
            for (std::uint32_t byte = 0U; byte < 8U; ++byte) {
                hash = (hash ^ static_cast<std::uint8_t>(value)) *
                    1099511628211ULL;
                value >>= 8U;
            }
        };
        mix(key.segment_offset);
        mix(key.segment_count);
        mix(key.min_x);
        mix(key.min_y);
        mix(key.max_x);
        mix(key.max_y);
        mix(key.scale);
        mix(key.subpixel_x);
        mix(key.subpixel_y);
        mix(key.fill_rule);
        mix(key.sample_grid);
        return static_cast<std::size_t>(hash);
    }
};

struct gpu_glyph_record {
    std::uint32_t start_segment;
    std::uint32_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    std::uint32_t pad0;
    std::uint32_t pad1;
};

struct gpu_glyph_uniforms {
    float x_start;
    float y_start;
    float scale;
    std::uint32_t glyph_index;
    std::uint32_t output_offset_words;
    std::uint32_t output_row_words;
    std::uint32_t width;
    std::uint32_t height;
    float subpixel_x;
    float pad0;
    float pad1;
    float pad2;
};

struct gpu_glyph_instance {
    float snapped_logical_position[2];
    float basis_x[2];
    float basis_y[2];
    float bear_size[4];
    float texture_coordinates[4];
    float color[4];
    float scale_bold_italic_flags[4];
    float brush_index;
    float padding;
};

struct native_glyph_raster {
    std::uint32_t atlas_x;
    std::uint32_t atlas_y;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t output_offset;
    std::uint32_t output_bytes_per_row;
    float x_start;
    float y_start;
};

static_assert(sizeof(gpu_path_uniforms) == 48U);
static_assert(sizeof(gpu_path_record) == 32U);
static_assert(sizeof(gpu_path_record) == sizeof(progpu_native_path_segment) - 16U);
static_assert(sizeof(gpu_glyph_record) == 32U);
static_assert(sizeof(gpu_glyph_uniforms) == 48U);
static_assert(sizeof(gpu_glyph_instance) == 96U);

std::uint32_t align_up(
    std::uint32_t value,
    std::uint32_t alignment) noexcept {
    return (value + alignment - 1U) / alignment * alignment;
}

float quantize_subpixel_phase(float value) noexcept {
    value -= std::floor(value);
    const float quantized = std::round(value * 64.0F) / 64.0F;
    return quantized >= 1.0F ? 0.0F : quantized;
}

struct path_raster_resources {
    WGPUBuffer uniforms = nullptr;
    WGPUBuffer records = nullptr;
    WGPUBuffer segments = nullptr;
    WGPUBuffer coverage = nullptr;
    WGPUBindGroup bind_group = nullptr;

    ~path_raster_resources() {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
        }
        release_buffer(uniforms);
        release_buffer(records);
        release_buffer(segments);
        release_buffer(coverage);
    }

private:
    static void release_buffer(WGPUBuffer buffer) {
        if (buffer != nullptr) {
            // Encoders and submitted command buffers retain temporary staging
            // resources. Dropping caller ownership is safe; explicitly
            // destroying here would invalidate a shared semantic encoder
            // before it is finished.
            wgpuBufferRelease(buffer);
        }
    }
};

WGPUTextureFormat texture_format(std::uint32_t value) noexcept {
    switch (value) {
        case PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM:
            return WGPUTextureFormat_RGBA8Unorm;
        case PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM:
            return WGPUTextureFormat_BGRA8Unorm;
        case PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM_SRGB:
            return WGPUTextureFormat_RGBA8UnormSrgb;
        case PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM_SRGB:
            return WGPUTextureFormat_BGRA8UnormSrgb;
        default:
            return WGPUTextureFormat_Undefined;
    }
}

void set_identity(float* matrix) noexcept {
    std::fill_n(matrix, 16U, 0.0F);
    matrix[0] = 1.0F;
    matrix[5] = 1.0F;
    matrix[10] = 1.0F;
    matrix[15] = 1.0F;
}

gpu_uniforms create_uniforms(
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale) noexcept {
    gpu_uniforms uniforms{};
    set_identity(uniforms.projection);
    set_identity(uniforms.model_view_projection);
    set_identity(uniforms.view);

    const float logical_width = static_cast<float>(width) / dpi_scale;
    const float logical_height = static_cast<float>(height) / dpi_scale;
    uniforms.projection[0] = 2.0F / logical_width;
    uniforms.projection[5] = -2.0F / logical_height;
    uniforms.projection[10] = -1.0F;
    uniforms.projection[12] = -1.0F;
    uniforms.projection[13] = 1.0F;
    uniforms.canvas_size[0] = logical_width;
    uniforms.canvas_size[1] = logical_height;
    uniforms.dpi_scale = dpi_scale;
    return uniforms;
}

std::uint64_t append_fnv1a64(
    std::uint64_t hash,
    const void* data,
    std::size_t size) noexcept {
    const auto* bytes = static_cast<const std::uint8_t*>(data);
    for (std::size_t index = 0; index < size; ++index) {
        hash = (hash ^ bytes[index]) * 1099511628211ULL;
    }
    return hash;
}

struct resolved_draw_state {
    float opacity = 1.0F;
    float group_opacity = 1.0F;
    std::uint32_t group_revision = 0U;
    std::uint32_t group_blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    bool has_clip = false;
    bool has_drawable_clip = true;
    std::uint32_t clip_x = 0U;
    std::uint32_t clip_y = 0U;
    std::uint32_t clip_width = 0U;
    std::uint32_t clip_height = 0U;
    bool has_group_mask = false;
    progpu_native_group_mask group_mask{};
    bool has_group_effect = false;
    progpu_native_group_effect group_effect{};
    std::uint32_t effect_count = 0U;
    std::uint32_t effect_chain_revision = 0U;
    std::array<progpu_native_group_effect,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS> group_effects{};
};

bool is_finite_positive_rect(
    const progpu_native_image_rect& rect) noexcept {
    return std::isfinite(rect.x) && std::isfinite(rect.y) &&
        std::isfinite(rect.width) && std::isfinite(rect.height) &&
        rect.width > 0.0F && rect.height > 0.0F;
}

bool is_finite_nonnegative_radii(
    const float (&values)[4]) noexcept {
    return std::ranges::all_of(values, [](float value) {
        return std::isfinite(value) && value >= 0.0F;
    });
}

void normalize_group_mask_radii(
    progpu_native_group_mask& mask) noexcept {
    const float width = mask.bounds.width;
    const float height = mask.bounds.height;
    float scale = 1.0F;
    const auto constrain = [&scale](float available, float requested) {
        if (requested > 0.0F) {
            scale = std::min(scale, available / requested);
        }
    };
    constrain(width, mask.corner_radii_x[0] + mask.corner_radii_x[1]);
    constrain(width, mask.corner_radii_x[3] + mask.corner_radii_x[2]);
    constrain(height, mask.corner_radii_y[0] + mask.corner_radii_y[3]);
    constrain(height, mask.corner_radii_y[1] + mask.corner_radii_y[2]);
    scale = std::clamp(scale, 0.0F, 1.0F);
    for (std::size_t index = 0U; index < 4U; ++index) {
        mask.corner_radii_x[index] *= scale;
        mask.corner_radii_y[index] *= scale;
    }
}

bool resolve_group_mask(
    const progpu_native_group_mask* requested,
    std::uintptr_t target_view,
    progpu_native_group_mask& resolved) noexcept {
    if (requested == nullptr) {
        return true;
    }
    constexpr std::uint32_t legacy_size =
        offsetof(progpu_native_group_mask, clip_chain);
    if (requested->struct_size < legacy_size ||
        requested->flags != 0U || requested->reserved != 0U ||
        requested->reserved2 != 0U || requested->reserved3 != 0U) {
        return false;
    }
    resolved = {};
    std::memcpy(
        &resolved,
        requested,
        std::min<std::size_t>(requested->struct_size, sizeof(resolved)));
    constexpr std::uint32_t clip_chain_size =
        offsetof(progpu_native_group_mask, clip_chain) +
        sizeof(requested->clip_chain);
    if (requested->struct_size < clip_chain_size) {
        resolved.clip_chain = nullptr;
    }
    if (resolved.kind == PROGPU_NATIVE_GROUP_MASK_TEXTURE) {
        return resolved.external_view != 0U &&
            resolved.external_view != target_view &&
            resolved.width > 0U && resolved.width <= 16384U &&
            resolved.height > 0U && resolved.height <= 16384U &&
            resolved.sampling <= PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR &&
            resolved.texture_format >= PROGPU_NATIVE_MASK_TEXTURE_R8_UNORM &&
            resolved.texture_format <=
                PROGPU_NATIVE_MASK_TEXTURE_BGRA8_UNORM &&
            resolved.revision != 0U &&
            is_finite_positive_rect(resolved.destination_rect) &&
            resolved.clip_chain == nullptr;
    }
    if (resolved.kind == PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN) {
        if (requested->struct_size < clip_chain_size ||
            resolved.external_view != 0U || resolved.width != 0U ||
            resolved.height != 0U || resolved.texture_format != 0U ||
            resolved.revision == 0U || resolved.clip_chain == nullptr) {
            return false;
        }
        const auto& chain = *resolved.clip_chain;
        return chain.struct_size >= sizeof(progpu_native_clip_chain) &&
            chain.flags == 0U && chain.paths != nullptr &&
            chain.path_count > 0U && chain.path_count <= (1U << 16U) &&
            chain.segments != nullptr && chain.segment_count > 0U &&
            chain.segment_count <= (1U << 24U);
    }
    if (resolved.kind != PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE ||
        resolved.external_view != 0U || resolved.width != 0U ||
        resolved.height != 0U || resolved.texture_format != 0U ||
        resolved.revision != 0U || resolved.clip_chain != nullptr ||
        !is_finite_positive_rect(resolved.bounds) ||
        !progpu::native::is_finite(resolved.transform) ||
        !is_finite_nonnegative_radii(resolved.corner_radii_x) ||
        !is_finite_nonnegative_radii(resolved.corner_radii_y) ||
        !std::isfinite(resolved.opacity) || resolved.opacity < 0.0F ||
        resolved.opacity > 1.0F) {
        return false;
    }
    const double determinant =
        static_cast<double>(resolved.transform.m11) *
            resolved.transform.m22 -
        static_cast<double>(resolved.transform.m12) *
            resolved.transform.m21;
    if (!std::isfinite(determinant) || std::abs(determinant) <= 0.000001) {
        return false;
    }
    normalize_group_mask_radii(resolved);
    return true;
}

bool resolve_group_effect(
    const progpu_native_group_effect* requested,
    float dpi_scale,
    progpu_native_group_effect& resolved) noexcept {
    if (requested == nullptr) {
        return true;
    }
    constexpr std::uint32_t gaussian_prefix_size =
        offsetof(progpu_native_group_effect, offset_x);
    if (requested->struct_size < gaussian_prefix_size ||
        (requested->kind != PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR &&
         requested->kind != PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) ||
        requested->flags != 0U || requested->revision == 0U ||
        requested->reserved != 0U || requested->reserved2 != 0U ||
        !std::isfinite(requested->sigma_x) ||
        !std::isfinite(requested->sigma_y) ||
        requested->sigma_x < 0.0F || requested->sigma_y < 0.0F) {
        return false;
    }
    if (requested->kind == PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR &&
        (requested->sigma_x <= 0.01F || requested->sigma_y <= 0.01F)) {
        return false;
    }
    if (requested->kind == PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
        if (requested->struct_size < sizeof(progpu_native_group_effect) ||
            !std::isfinite(requested->offset_x) ||
            !std::isfinite(requested->offset_y) ||
            !std::isfinite(requested->color_r) ||
            !std::isfinite(requested->color_g) ||
            !std::isfinite(requested->color_b) ||
            !std::isfinite(requested->color_a) ||
            requested->color_r < 0.0F || requested->color_r > 1.0F ||
            requested->color_g < 0.0F || requested->color_g > 1.0F ||
            requested->color_b < 0.0F || requested->color_b > 1.0F ||
            requested->color_a < 0.0F || requested->color_a > 1.0F) {
            return false;
        }
    }
    constexpr float maximum_physical_sigma = 128.0F / 3.0F;
    if (requested->sigma_x * dpi_scale > maximum_physical_sigma ||
        requested->sigma_y * dpi_scale > maximum_physical_sigma) {
        return false;
    }
    resolved = {};
    std::memcpy(
        &resolved,
        requested,
        std::min<std::size_t>(requested->struct_size, sizeof(resolved)));
    resolved.struct_size = sizeof(resolved);
    return true;
}

bool resolve_group_effect_chain(
    const progpu_native_group_effect_chain* requested,
    float dpi_scale,
    resolved_draw_state& resolved) noexcept {
    if (requested == nullptr) {
        return true;
    }
    if (requested->struct_size < sizeof(progpu_native_group_effect_chain) ||
        requested->effect_count == 0U ||
        requested->effect_count > PROGPU_NATIVE_MAX_GROUP_EFFECTS ||
        requested->revision == 0U || requested->reserved != 0U ||
        requested->effects == nullptr) {
        return false;
    }
    for (std::uint32_t index = 0U;
         index < requested->effect_count;
         ++index) {
        if (!resolve_group_effect(
                &requested->effects[index],
                dpi_scale,
                resolved.group_effects[index])) {
            return false;
        }
    }
    resolved.effect_count = requested->effect_count;
    resolved.effect_chain_revision = requested->revision;
    resolved.group_effect =
        resolved.group_effects[requested->effect_count - 1U];
    resolved.has_group_effect = true;
    return true;
}

float snap_scissor_coordinate(float value) noexcept {
    const float rounded = std::round(value);
    return std::abs(value - rounded) < 0.0001F ? rounded : value;
}

bool resolve_draw_state(
    const progpu_native_draw_state* state,
    std::uintptr_t target_view,
    std::uint32_t target_width,
    std::uint32_t target_height,
    float dpi_scale,
    resolved_draw_state& resolved) noexcept {
    resolved = {};
    if (state == nullptr) {
        return true;
    }
    constexpr std::uint32_t legacy_size =
        offsetof(progpu_native_draw_state, group_opacity);
    if (state->struct_size < legacy_size ||
        (state->flags & ~PROGPU_NATIVE_DRAW_STATE_CLIP_RECT) != 0U ||
        state->reserved != 0U || !std::isfinite(state->opacity) ||
        state->opacity < 0.0F || state->opacity > 1.0F) {
        return false;
    }
    resolved.opacity = state->opacity;
    constexpr std::uint32_t group_size =
        offsetof(progpu_native_draw_state, group_mask);
    if (state->struct_size >= group_size) {
        if (!std::isfinite(state->group_opacity) ||
            state->group_opacity < 0.0F || state->group_opacity > 1.0F) {
            return false;
        }
        resolved.group_opacity = state->group_opacity;
        resolved.group_revision = state->group_revision;
    }
    constexpr std::uint32_t mask_size =
        offsetof(progpu_native_draw_state, group_mask) +
        sizeof(state->group_mask);
    if (state->struct_size >= mask_size && state->group_mask != nullptr) {
        if (!resolve_group_mask(
                state->group_mask,
                target_view,
                resolved.group_mask)) {
            return false;
        }
        resolved.has_group_mask = true;
    }
    constexpr std::uint32_t effect_size =
        offsetof(progpu_native_draw_state, group_effect) +
        sizeof(state->group_effect);
    if (state->struct_size >= effect_size && state->group_effect != nullptr) {
        if (!resolve_group_effect(
                state->group_effect,
                dpi_scale,
                resolved.group_effect)) {
            return false;
        }
        resolved.has_group_effect = true;
        resolved.effect_count = 1U;
        resolved.effect_chain_revision = resolved.group_effect.revision;
        resolved.group_effects[0] = resolved.group_effect;
    }
    constexpr std::uint32_t effect_chain_size =
        offsetof(progpu_native_draw_state, group_effect_chain) +
        sizeof(state->group_effect_chain);
    if (state->struct_size >= effect_chain_size &&
        state->group_effect_chain != nullptr) {
        if (resolved.has_group_effect ||
            !resolve_group_effect_chain(
                state->group_effect_chain,
                dpi_scale,
                resolved)) {
            return false;
        }
    }
    constexpr std::uint32_t blend_mode_size =
        offsetof(progpu_native_draw_state, group_blend_mode) +
        sizeof(state->group_blend_mode);
    if (state->struct_size >= blend_mode_size) {
        if (state->group_blend_mode > PROGPU_NATIVE_BLEND_MODULATE) {
            return false;
        }
        resolved.group_blend_mode = state->group_blend_mode;
    }
    constexpr std::uint32_t reserved2_size =
        offsetof(progpu_native_draw_state, reserved2) +
        sizeof(state->reserved2);
    if (state->struct_size >= reserved2_size && state->reserved2 != 0U) {
        return false;
    }
    if ((state->flags & PROGPU_NATIVE_DRAW_STATE_CLIP_RECT) == 0U) {
        return state->clip_rect.x == 0.0F && state->clip_rect.y == 0.0F &&
            state->clip_rect.width == 0.0F &&
            state->clip_rect.height == 0.0F;
    }
    const auto& clip = state->clip_rect;
    if (!std::isfinite(clip.x) || !std::isfinite(clip.y) ||
        !std::isfinite(clip.width) || !std::isfinite(clip.height) ||
        clip.width < 0.0F || clip.height < 0.0F) {
        return false;
    }

    resolved.has_clip = true;
    const float left = std::clamp(clip.x * dpi_scale, 0.0F,
        static_cast<float>(target_width));
    const float top = std::clamp(clip.y * dpi_scale, 0.0F,
        static_cast<float>(target_height));
    const float right = std::clamp(
        (clip.x + clip.width) * dpi_scale,
        0.0F,
        static_cast<float>(target_width));
    const float bottom = std::clamp(
        (clip.y + clip.height) * dpi_scale,
        0.0F,
        static_cast<float>(target_height));
    const float snapped_left = snap_scissor_coordinate(left);
    const float snapped_top = snap_scissor_coordinate(top);
    const float snapped_right = snap_scissor_coordinate(right);
    const float snapped_bottom = snap_scissor_coordinate(bottom);
    if (snapped_right <= snapped_left || snapped_bottom <= snapped_top) {
        resolved.has_drawable_clip = false;
        return true;
    }
    resolved.clip_x = static_cast<std::uint32_t>(std::floor(snapped_left));
    resolved.clip_y = static_cast<std::uint32_t>(std::floor(snapped_top));
    const std::uint32_t right_pixel = static_cast<std::uint32_t>(
        std::ceil(snapped_right));
    const std::uint32_t bottom_pixel = static_cast<std::uint32_t>(
        std::ceil(snapped_bottom));
    resolved.clip_width = right_pixel - resolved.clip_x;
    resolved.clip_height = bottom_pixel - resolved.clip_y;
    return true;
}

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

bool is_valid_semantic_analytic(
    const progpu_native_analytic_primitive& primitive) noexcept {
    float minimum_scale = 0.0F;
    if (!progpu::native::try_get_minimum_scale(
            primitive.transform,
            minimum_scale)) {
        return false;
    }
    return progpu::native::is_valid_analytic_primitive(
        primitive,
        antialias_padding_pixels / minimum_scale);
}

bool is_valid_semantic_segment(
    const progpu_native_path_segment& segment,
    bool allow_arc) noexcept {
    const bool is_arc = segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC;
    const std::uint32_t maximum_kind = static_cast<std::uint32_t>(allow_arc
        ? PROGPU_NATIVE_PATH_SEGMENT_ARC
        : PROGPU_NATIVE_PATH_SEGMENT_CUBIC);
    return segment.kind <= maximum_kind &&
        progpu::native::is_finite(segment.p0) &&
        progpu::native::is_finite(segment.p1) &&
        progpu::native::is_finite(segment.p2) &&
        progpu::native::is_finite(segment.p3) &&
        ((!is_arc && segment.pad0 == 0U && segment.pad1 == 0U &&
             segment.pad2 == 0U) ||
         (allow_arc && is_arc && segment.p3.x > 0.0F &&
             segment.p3.y > 0.0F &&
             std::isfinite(std::bit_cast<float>(segment.pad0)) &&
             std::isfinite(std::bit_cast<float>(segment.pad1)) &&
             std::isfinite(std::bit_cast<float>(segment.pad2))));
}

bool is_valid_semantic_path(
    const progpu_native_scene_path_fill& path,
    std::uint64_t segment_count,
    std::uint64_t* coverage_bytes = nullptr) noexcept {
    if (path.segment_count == 0U ||
        path.segment_offset > segment_count ||
        path.segment_count > segment_count - path.segment_offset ||
        !std::isfinite(path.min_x) || !std::isfinite(path.min_y) ||
        !std::isfinite(path.max_x) || !std::isfinite(path.max_y) ||
        path.max_x <= path.min_x || path.max_y <= path.min_y ||
        !progpu::native::is_finite(path.color) ||
        !progpu::native::is_finite(path.transform) ||
        path.fill_rule > PROGPU_NATIVE_FILL_RULE_EVEN_ODD ||
        (path.sample_grid != 4U && path.sample_grid != 8U)) {
        return false;
    }
    float maximum_scale = 0.0F;
    float minimum_scale = 0.0F;
    if (!progpu::native::try_get_stroke_scales(
            path.transform,
            maximum_scale,
            minimum_scale)) {
        return false;
    }
    (void)minimum_scale;
    const double raster_width =
        std::ceil(path.max_x * maximum_scale) + path_padding -
        (std::floor(path.min_x * maximum_scale) - path_padding);
    const double raster_height =
        std::ceil(path.max_y * maximum_scale) + path_padding -
        (std::floor(path.min_y * maximum_scale) - path_padding);
    const bool valid =
        std::isfinite(raster_width) && std::isfinite(raster_height) &&
        raster_width > 0.0 && raster_height > 0.0 &&
        raster_width <= native_max_atlas_size - 4U &&
        raster_height <= native_max_atlas_size - 4U;
    if (valid && coverage_bytes != nullptr) {
        const auto width = static_cast<std::uint32_t>(raster_width);
        const auto height = static_cast<std::uint32_t>(raster_height);
        *coverage_bytes = static_cast<std::uint64_t>(
            align_up(width, webgpu_copy_row_alignment)) * height;
    }
    return valid;
}

bool is_valid_semantic_glyph_outline(
    const progpu_native_scene_glyph_outline& outline,
    std::uint64_t segment_count,
    std::uint64_t* coverage_bytes = nullptr) noexcept {
    if (outline.segment_count == 0U ||
        outline.segment_offset > segment_count ||
        outline.segment_count > segment_count - outline.segment_offset ||
        !std::isfinite(outline.min_x) || !std::isfinite(outline.min_y) ||
        !std::isfinite(outline.max_x) || !std::isfinite(outline.max_y) ||
        outline.max_x <= outline.min_x || outline.max_y <= outline.min_y ||
        !std::isfinite(outline.raster_scale) ||
        outline.raster_scale <= 0.0F ||
        !std::isfinite(outline.subpixel_x) || outline.subpixel_x < 0.0F ||
        outline.subpixel_x > 0.75F ||
        std::abs(outline.subpixel_x * 4.0F -
            std::round(outline.subpixel_x * 4.0F)) > 0.0001F) {
        return false;
    }
    const float scaled_min_x = outline.min_x * outline.raster_scale;
    const float scaled_min_y = -outline.max_y * outline.raster_scale;
    const float scaled_max_x = outline.max_x * outline.raster_scale;
    const float scaled_max_y = -outline.min_y * outline.raster_scale;
    const double width = std::ceil(scaled_max_x) + path_padding -
        (std::floor(scaled_min_x) - path_padding);
    const double height = std::ceil(scaled_max_y) + path_padding -
        (std::floor(scaled_min_y) - path_padding);
    const bool valid = std::isfinite(width) && std::isfinite(height) &&
        width > 0.0 && height > 0.0 &&
        width <= native_max_atlas_size - 4U &&
        height <= native_max_atlas_size - 4U;
    if (valid && coverage_bytes != nullptr) {
        const auto pixel_width = static_cast<std::uint32_t>(width);
        const auto pixel_height = static_cast<std::uint32_t>(height);
        *coverage_bytes = static_cast<std::uint64_t>(
            align_up(pixel_width, webgpu_copy_row_alignment)) *
            pixel_height;
    }
    return valid;
}

bool is_valid_semantic_positioned_glyph(
    const progpu_native_positioned_glyph& glyph,
    std::uint64_t outline_count) noexcept {
    return glyph.outline_index < outline_count && glyph.reserved == 0U &&
        glyph.reserved2 == 0.0F &&
        progpu::native::is_finite(glyph.position) &&
        progpu::native::is_finite(glyph.basis_x) &&
        progpu::native::is_finite(glyph.basis_y) &&
        progpu::native::is_finite(glyph.color) &&
        std::isfinite(glyph.atlas_to_logical_scale) &&
        glyph.atlas_to_logical_scale > 0.0F &&
        std::isfinite(glyph.bold_offset) &&
        std::isfinite(glyph.italic_skew);
}

bool is_valid_semantic_image(
    const progpu_native_scene_image_draw& image,
    std::uint64_t pixel_bytes) noexcept {
    const auto valid_rect = [](const progpu_native_image_rect& rect) noexcept {
        return std::isfinite(rect.x) && std::isfinite(rect.y) &&
            std::isfinite(rect.width) && std::isfinite(rect.height) &&
            rect.width > 0.0F && rect.height > 0.0F;
    };
    const std::uint64_t minimum_row_bytes =
        static_cast<std::uint64_t>(image.image_width) * 4U;
    const std::uint64_t required_pixels = image.image_height == 0U
        ? 0U
        : static_cast<std::uint64_t>(image.row_bytes) *
                (image.image_height - 1U) + minimum_row_bytes;
    return image.struct_size >= sizeof(image) && image.flags == 0U &&
        image.reserved == 0U && image.image_width != 0U &&
        image.image_height != 0U && image.image_width <= 16384U &&
        image.image_height <= 16384U &&
        image.row_bytes >= minimum_row_bytes &&
        required_pixels <= pixel_bytes &&
        image.sampling <= PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR &&
        valid_rect(image.source_rect) && valid_rect(image.destination_rect) &&
        image.source_rect.x >= 0.0F && image.source_rect.y >= 0.0F &&
        image.source_rect.x + image.source_rect.width <=
            static_cast<float>(image.image_width) &&
        image.source_rect.y + image.source_rect.height <=
            static_cast<float>(image.image_height) &&
        progpu::native::is_finite(image.transform) &&
        std::isfinite(image.opacity) && image.opacity >= 0.0F &&
        image.opacity <= 1.0F;
}

constexpr std::uint32_t semantic_max_draw_passes = 16U * 1024U;
constexpr std::uint64_t semantic_max_vertex_bytes = 256ULL * 1024ULL * 1024ULL;
constexpr std::uint64_t semantic_max_index_bytes = 64ULL * 1024ULL * 1024ULL;
constexpr std::uint64_t semantic_max_texture_bytes = 256ULL * 1024ULL * 1024ULL;
constexpr std::uint64_t semantic_max_coverage_bytes = 256ULL * 1024ULL * 1024ULL;
constexpr std::uint64_t semantic_max_total_compiled_bytes =
    512ULL * 1024ULL * 1024ULL;

struct semantic_compilation_budget {
    std::uint32_t draw_passes = 0U;
    std::uint64_t vertex_bytes = 0U;
    std::uint64_t index_bytes = 0U;
    std::uint64_t texture_bytes = 0U;
    std::uint64_t coverage_bytes = 0U;

    bool add(
        std::uint64_t vertices,
        std::uint64_t indices,
        std::uint64_t textures,
        std::uint64_t coverage) noexcept {
        const auto checked_add = [](std::uint64_t current,
                                    std::uint64_t value,
                                    std::uint64_t limit,
                                    std::uint64_t& result) noexcept {
            if (value > limit || current > limit - value) {
                return false;
            }
            result = current + value;
            return true;
        };
        std::uint64_t next_vertices = 0U;
        std::uint64_t next_indices = 0U;
        std::uint64_t next_textures = 0U;
        std::uint64_t next_coverage = 0U;
        if (draw_passes == semantic_max_draw_passes ||
            !checked_add(vertex_bytes, vertices,
                semantic_max_vertex_bytes, next_vertices) ||
            !checked_add(index_bytes, indices,
                semantic_max_index_bytes, next_indices) ||
            !checked_add(texture_bytes, textures,
                semantic_max_texture_bytes, next_textures) ||
            !checked_add(coverage_bytes, coverage,
                semantic_max_coverage_bytes, next_coverage)) {
            return false;
        }
        const std::uint64_t total = next_vertices + next_indices +
            next_textures + next_coverage;
        if (total > semantic_max_total_compiled_bytes) {
            return false;
        }
        ++draw_passes;
        vertex_bytes = next_vertices;
        index_bytes = next_indices;
        texture_bytes = next_textures;
        coverage_bytes = next_coverage;
        return true;
    }
};

} // namespace

struct semantic_analytic_draw {
    std::uint64_t vertex_offset_bytes = 0U;
    std::uint64_t index_offset_bytes = 0U;
    std::uint32_t vertex_count = 0U;
    std::uint32_t index_count = 0U;
};

struct semantic_analytic_page {
    WGPUBuffer vertex_buffer = nullptr;
    WGPUBuffer index_buffer = nullptr;
    std::uint64_t vertex_buffer_size = 0U;
    std::uint64_t index_buffer_size = 0U;
    std::uint64_t vertex_bytes = 0U;
    std::uint64_t index_bytes = 0U;
    std::uint64_t scene_hash = 0U;
    float dpi_scale = 0.0F;
    bool cache_valid = false;
    std::vector<semantic_analytic_draw> draws;
};

struct progpu_native_engine {
    std::thread::id owner_thread;
    progpu::native::webgpu::dispatch webgpu_dispatch{};
    WGPUInstance instance = nullptr;
    WGPUDevice device = nullptr;
    WGPUQueue queue = nullptr;
    WGPUTextureFormat target_format = WGPUTextureFormat_Undefined;
    WGPUShaderModule shader = nullptr;
    WGPURenderPipeline pipeline = nullptr;
    WGPURenderPipeline analytic_pipeline = nullptr;
    WGPUBindGroupLayout uniform_layout = nullptr;
    WGPUBindGroupLayout analytic_uniform_layout = nullptr;
    WGPUBindGroupLayout analytic_atlas_layout = nullptr;
    WGPUBuffer uniform_buffer = nullptr;
    WGPUBindGroup uniform_bind_group = nullptr;
    WGPUBuffer analytic_uniform_buffer = nullptr;
    gpu_uniforms cached_uniforms{};
    gpu_uniforms cached_analytic_uniforms{};
    bool uniform_cache_valid = false;
    bool analytic_uniform_cache_valid = false;
    WGPUBuffer analytic_brush_buffer = nullptr;
    std::uint64_t analytic_brush_buffer_size = 0;
    WGPUBuffer analytic_gradient_buffer = nullptr;
    WGPUBindGroup analytic_uniform_bind_group = nullptr;
    WGPUBindGroup analytic_atlas_bind_group = nullptr;
    WGPUSampler analytic_sentinel_sampler = nullptr;
    WGPUTexture analytic_sentinel_texture = nullptr;
    WGPUTextureView analytic_sentinel_texture_view = nullptr;
    WGPUShaderModule path_raster_shader = nullptr;
    WGPUComputePipeline path_raster_pipeline = nullptr;
    WGPUBindGroupLayout path_raster_layout = nullptr;
    WGPUPipelineLayout path_raster_pipeline_layout = nullptr;
    WGPUSampler path_atlas_sampler = nullptr;
    WGPUTexture path_atlas_texture = nullptr;
    WGPUTextureView path_atlas_texture_view = nullptr;
    WGPUBindGroup path_atlas_bind_group = nullptr;
    std::uint32_t path_atlas_size = native_initial_atlas_size;
    std::uint32_t path_atlas_generation = 0U;
    WGPUShaderModule glyph_raster_shader = nullptr;
    WGPUComputePipeline glyph_raster_pipeline = nullptr;
    WGPUBindGroupLayout glyph_raster_layout = nullptr;
    WGPUPipelineLayout glyph_raster_pipeline_layout = nullptr;
    WGPUShaderModule text_shader = nullptr;
    WGPURenderPipeline text_pipeline = nullptr;
    WGPUBindGroupLayout text_uniform_layout = nullptr;
    WGPUBindGroupLayout text_atlas_layout = nullptr;
    WGPUBuffer text_style_buffer = nullptr;
    WGPUBindGroup text_uniform_bind_group = nullptr;
    WGPUSampler glyph_atlas_sampler = nullptr;
    WGPUTexture glyph_atlas_texture = nullptr;
    WGPUTextureView glyph_atlas_texture_view = nullptr;
    WGPUBindGroup text_atlas_bind_group = nullptr;
    std::uint32_t glyph_atlas_size = native_initial_atlas_size;
    std::uint32_t glyph_atlas_generation = 0U;
    std::uint32_t glyph_atlas_growth_count = 0U;
    WGPUBuffer text_vertex_buffer = nullptr;
    std::uint64_t text_vertex_buffer_size = 0U;
    WGPUBuffer vertex_buffer = nullptr;
    WGPUBuffer index_buffer = nullptr;
    std::uint64_t vertex_buffer_size = 0;
    std::uint64_t index_buffer_size = 0;
    WGPUBuffer path_vertex_buffer = nullptr;
    WGPUBuffer path_index_buffer = nullptr;
    std::uint64_t path_vertex_buffer_size = 0U;
    std::uint64_t path_index_buffer_size = 0U;
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    std::vector<std::uint32_t> primitive_brush_indices;
    std::vector<std::uint32_t> polyline_brush_indices;
    std::vector<std::uint32_t> spline_brush_indices;
    std::vector<std::size_t> spline_segment_counts;
    std::array<progpu_native_point, 101U> spline_sampled_points{};
    std::vector<progpu::native::spline_homogeneous_point> spline_work;
    std::vector<std::byte> brush_bytes;
    std::uint32_t geometry_content_revision = 0U;
    float geometry_opacity = 1.0F;
    std::uint64_t geometry_payload_hash = 0U;
    bool geometry_cache_valid = false;
    bool geometry_gpu_cache_valid = false;
    std::vector<progpu::native::vector_vertex> path_vertices;
    std::vector<std::uint32_t> path_indices;
    std::vector<std::byte> path_brush_bytes;
    std::vector<native_path_raster> path_rasters;
    std::uint32_t path_content_revision = 0U;
    float path_dpi_scale = 0.0F;
    float path_opacity = 1.0F;
    std::uint64_t path_payload_hash = 0U;
    bool path_cache_valid = false;
    bool path_gpu_cache_valid = false;
    std::vector<gpu_glyph_instance> glyph_instances;
    std::vector<float> glyph_source_alphas;
    std::vector<native_glyph_raster> glyph_rasters;
    std::uint32_t glyph_content_revision = 0U;
    float glyph_dpi_scale = 0.0F;
    float glyph_opacity = 1.0F;
    std::uint64_t glyph_payload_hash = 0U;
    bool glyph_cache_valid = false;
    bool glyph_gpu_cache_valid = false;
    WGPUShaderModule image_shader = nullptr;
    WGPURenderPipeline image_pipeline = nullptr;
    WGPURenderPipeline image_mask_pipeline = nullptr;
    WGPUBindGroupLayout image_uniform_layout = nullptr;
    WGPUBindGroupLayout image_texture_layout = nullptr;
    WGPUBindGroupLayout image_mask_layout = nullptr;
    WGPUBuffer image_uniform_buffer = nullptr;
    WGPUBindGroup image_uniform_bind_group = nullptr;
    WGPUSampler image_nearest_sampler = nullptr;
    WGPUSampler image_linear_sampler = nullptr;
    WGPUTexture image_texture = nullptr;
    WGPUTextureView image_texture_view = nullptr;
    WGPUBindGroup image_nearest_bind_group = nullptr;
    WGPUBindGroup image_linear_bind_group = nullptr;
    WGPUTextureView image_mask_view = nullptr;
    WGPUBuffer image_mask_uniform_buffer = nullptr;
    WGPUBindGroup image_mask_nearest_bind_group = nullptr;
    WGPUBindGroup image_mask_linear_bind_group = nullptr;
    WGPUBuffer image_vertex_buffer = nullptr;
    WGPUBuffer image_index_buffer = nullptr;
    std::array<progpu::native::vector_vertex, 4U> image_vertices{};
    gpu_uniforms cached_image_uniforms{};
    std::uint32_t image_revision = 0U;
    std::uint32_t image_content_revision = 0U;
    float image_draw_opacity = 1.0F;
    std::uint32_t image_width = 0U;
    std::uint32_t image_height = 0U;
    std::uint32_t image_texture_generation = 0U;
    std::uint32_t image_mask_revision = 0U;
    std::uint32_t image_mask_width = 0U;
    std::uint32_t image_mask_height = 0U;
    gpu_mask_sampling_uniforms cached_image_mask_uniforms{};
    bool image_mask_uniform_cache_valid = false;
    bool image_source_is_external = false;
    std::uint64_t image_payload_hash = 0U;
    bool image_uniform_cache_valid = false;
    bool image_cache_valid = false;
    bool image_gpu_cache_valid = false;
    WGPURenderPipeline layer_composite_pipeline = nullptr;
    WGPURenderPipeline layer_mask_pipeline = nullptr;
    std::array<WGPURenderPipeline, PROGPU_NATIVE_BLEND_MODULATE + 1U>
        layer_blend_pipelines{};
    std::array<WGPURenderPipeline, PROGPU_NATIVE_BLEND_MODULATE + 1U>
        layer_mask_blend_pipelines{};
    WGPUBindGroupLayout layer_mask_layout = nullptr;
    WGPUBuffer layer_mask_uniform_buffer = nullptr;
    WGPUTexture layer_mask_dummy_texture = nullptr;
    WGPUTextureView layer_mask_dummy_view = nullptr;
    WGPUBindGroup layer_analytic_mask_bind_group = nullptr;
    WGPUTextureView layer_external_mask_view = nullptr;
    WGPUBindGroup layer_external_mask_nearest_bind_group = nullptr;
    WGPUBindGroup layer_external_mask_linear_bind_group = nullptr;
    std::uint32_t layer_external_mask_width = 0U;
    std::uint32_t layer_external_mask_height = 0U;
    std::uint32_t layer_mask_bind_group_generation = 0U;
    gpu_mask_sampling_uniforms cached_layer_mask_uniforms{};
    bool layer_mask_uniform_cache_valid = false;
    WGPUShaderModule group_blend_shader = nullptr;
    WGPURenderPipeline group_blend_pipeline = nullptr;
    WGPUBindGroupLayout group_blend_layout = nullptr;
    WGPUBuffer group_blend_uniform_buffer = nullptr;
    WGPUTexture group_blend_source_texture = nullptr;
    WGPUTextureView group_blend_source_view = nullptr;
    WGPUBindGroup group_blend_bind_group = nullptr;
    gpu_group_blend_uniforms cached_group_blend_uniforms{};
    std::uint32_t group_blend_source_width = 0U;
    std::uint32_t group_blend_source_height = 0U;
    std::uint32_t group_blend_source_texture_generation = 0U;
    std::uint32_t group_blend_source_allocation_count = 0U;
    std::uint64_t group_blend_source_signature = 0U;
    bool group_blend_uniform_cache_valid = false;
    bool group_blend_source_cache_valid = false;
    WGPUShaderModule clip_compose_shader = nullptr;
    WGPURenderPipeline clip_path_pipeline = nullptr;
    WGPURenderPipeline clip_compose_pipeline = nullptr;
    WGPUBindGroupLayout clip_compose_layout = nullptr;
    WGPUSampler clip_sampler = nullptr;
    WGPUTexture clip_atlas_texture = nullptr;
    WGPUTextureView clip_atlas_view = nullptr;
    WGPUBindGroup clip_path_bind_group = nullptr;
    std::uint32_t clip_atlas_size = native_initial_atlas_size;
    std::uint32_t clip_atlas_generation = 0U;
    WGPUTexture clip_node_texture = nullptr;
    WGPUTextureView clip_node_view = nullptr;
    std::array<WGPUTexture, 2U> clip_accumulation_textures{};
    std::array<WGPUTextureView, 2U> clip_accumulation_views{};
    std::array<WGPUBindGroup, 2U> clip_compose_bind_groups{};
    std::array<WGPUBindGroup, 2U> layer_clip_mask_bind_groups{};
    WGPUBuffer clip_compose_uniform_buffer = nullptr;
    std::uint64_t clip_compose_uniform_buffer_size = 0U;
    WGPUBuffer clip_vertex_buffer = nullptr;
    WGPUBuffer clip_index_buffer = nullptr;
    std::uint64_t clip_vertex_buffer_size = 0U;
    std::uint64_t clip_index_buffer_size = 0U;
    std::uint32_t clip_width = 0U;
    std::uint32_t clip_height = 0U;
    std::uint32_t clip_texture_generation = 0U;
    std::uint32_t clip_cached_revision = 0U;
    float clip_cached_dpi_scale = 0.0F;
    std::uint32_t clip_final_index = 0U;
    bool clip_cache_valid = false;
    WGPUShaderModule effect_blur_horizontal_shader = nullptr;
    WGPUShaderModule effect_blur_vertical_shader = nullptr;
    WGPUComputePipeline effect_blur_horizontal_pipeline = nullptr;
    WGPUComputePipeline effect_blur_vertical_pipeline = nullptr;
    WGPUBindGroupLayout effect_blur_layout = nullptr;
    WGPUBuffer effect_blur_horizontal_uniform_buffer = nullptr;
    WGPUBuffer effect_blur_vertical_uniform_buffer = nullptr;
    std::array<WGPUTexture, 2U> effect_textures{};
    std::array<WGPUTextureView, 2U> effect_texture_views{};
    WGPUBindGroup effect_blur_horizontal_bind_group = nullptr;
    WGPUBindGroup effect_blur_vertical_bind_group = nullptr;
    WGPUBindGroup effect_output_bind_group = nullptr;
    WGPUShaderModule effect_drop_shadow_shader = nullptr;
    WGPUComputePipeline effect_drop_shadow_pipeline = nullptr;
    WGPUBindGroupLayout effect_drop_shadow_layout = nullptr;
    WGPUBuffer effect_drop_shadow_uniform_buffer = nullptr;
    WGPUBindGroup effect_drop_shadow_bind_group = nullptr;
    WGPUBindGroup effect_drop_shadow_output_bind_group = nullptr;
    std::array<WGPUTexture, 3U> effect_chain_textures{};
    std::array<WGPUTextureView, 3U> effect_chain_texture_views{};
    std::array<WGPUBindGroup, 3U> effect_chain_output_bind_groups{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_horizontal_uniform_buffers{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_vertical_uniform_buffers{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_drop_shadow_uniform_buffers{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_horizontal_bind_groups{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_vertical_bind_groups{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_drop_shadow_bind_groups{};
    std::array<gpu_gaussian_blur_params,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        cached_effect_chain_blur_horizontal{};
    std::array<gpu_gaussian_blur_params,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        cached_effect_chain_blur_vertical{};
    std::array<gpu_drop_shadow_params,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        cached_effect_chain_drop_shadow{};
    std::array<bool, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_horizontal_uniform_cache_valid{};
    std::array<bool, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_blur_vertical_uniform_cache_valid{};
    std::array<bool, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_drop_shadow_uniform_cache_valid{};
    std::array<std::uint32_t, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
        effect_chain_cached_kinds{};
    std::uint32_t effect_chain_cached_count = 0U;
    std::uint32_t effect_chain_final_texture_index = 0U;
    std::uint32_t effect_chain_width = 0U;
    std::uint32_t effect_chain_height = 0U;
    std::uint32_t effect_chain_texture_generation = 0U;
    std::uint32_t effect_chain_allocation_count = 0U;
    bool effect_chain_bindings_valid = false;
    gpu_gaussian_blur_params cached_effect_blur_horizontal{};
    gpu_gaussian_blur_params cached_effect_blur_vertical{};
    gpu_drop_shadow_params cached_effect_drop_shadow{};
    std::uint32_t effect_width = 0U;
    std::uint32_t effect_height = 0U;
    std::uint32_t effect_texture_generation = 0U;
    std::uint32_t effect_allocation_count = 0U;
    std::uint32_t effect_cached_revision = 0U;
    std::uint32_t effect_cached_content_revision = 0U;
    std::uint32_t effect_cached_kind = PROGPU_NATIVE_GROUP_EFFECT_NONE;
    float effect_cached_dpi_scale = 0.0F;
    bool effect_blur_horizontal_uniform_cache_valid = false;
    bool effect_blur_vertical_uniform_cache_valid = false;
    bool effect_drop_shadow_uniform_cache_valid = false;
    bool effect_cache_valid = false;
    WGPUBuffer layer_uniform_buffer = nullptr;
    WGPUBindGroup layer_uniform_bind_group = nullptr;
    WGPUBuffer layer_vertex_buffer = nullptr;
    WGPUBuffer layer_index_buffer = nullptr;
    WGPUTexture layer_texture = nullptr;
    WGPUTextureView layer_texture_view = nullptr;
    WGPUBindGroup layer_texture_bind_group = nullptr;
    std::array<progpu::native::vector_vertex, 4U> layer_vertices{};
    gpu_uniforms cached_layer_uniforms{};
    bool layer_uniform_cache_valid = false;
    bool layer_vertex_cache_valid = false;
    std::uint32_t layer_width = 0U;
    std::uint32_t layer_height = 0U;
    std::uint32_t layer_texture_generation = 0U;
    std::uint32_t layer_allocation_count = 0U;
    std::uint32_t layer_cached_family = 0U;
    std::uint32_t layer_cached_revision = 0U;
    float layer_cached_dpi_scale = 0.0F;
    float layer_cached_primitive_opacity = 1.0F;
    bool layer_content_cache_valid = false;
    progpu_native_layer_metrics last_layer_metrics{};
    std::vector<std::byte> semantic_scene_snapshot;
    std::uint64_t semantic_scene_id = 0U;
    std::uint64_t semantic_scene_generation = 0U;
    std::uint64_t semantic_scene_hash = 0U;
    progpu_native_scene_header semantic_scene_header{};
    progpu_native_scene_metrics semantic_scene_metrics{};
    semantic_analytic_page semantic_analytic_cache;
    WGPUCommandEncoder semantic_encoder = nullptr;
    bool semantic_load_target = false;
    std::string last_error;
    std::uint64_t submission_count = 0;
    std::uint64_t last_submission_index = 0U;

    void submit(WGPUCommandBuffer command) noexcept {
        last_submission_index = progpu::native::webgpu::submit(
            queue,
            1U,
            &command);
        ++submission_count;
    }

    bool upload_uniform_if_changed(
        WGPUBuffer buffer,
        const gpu_uniforms& uniforms,
        gpu_uniforms& cached,
        bool& cache_valid) noexcept {
        if (cache_valid &&
            std::memcmp(&cached, &uniforms, sizeof(uniforms)) == 0) {
            return false;
        }
        wgpuQueueWriteBuffer(
            queue,
            buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        cached = uniforms;
        cache_valid = true;
        return true;
    }

    void release_clip_resources() noexcept {
        for (auto& bind_group : layer_clip_mask_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& bind_group : clip_compose_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        if (clip_path_bind_group != nullptr) {
            wgpuBindGroupRelease(clip_path_bind_group);
            clip_path_bind_group = nullptr;
        }
        if (clip_index_buffer != nullptr) {
            wgpuBufferDestroy(clip_index_buffer);
            wgpuBufferRelease(clip_index_buffer);
            clip_index_buffer = nullptr;
        }
        if (clip_vertex_buffer != nullptr) {
            wgpuBufferDestroy(clip_vertex_buffer);
            wgpuBufferRelease(clip_vertex_buffer);
            clip_vertex_buffer = nullptr;
        }
        if (clip_compose_uniform_buffer != nullptr) {
            wgpuBufferDestroy(clip_compose_uniform_buffer);
            wgpuBufferRelease(clip_compose_uniform_buffer);
            clip_compose_uniform_buffer = nullptr;
        }
        for (auto& view : clip_accumulation_views) {
            if (view != nullptr) {
                wgpuTextureViewRelease(view);
                view = nullptr;
            }
        }
        for (auto& texture : clip_accumulation_textures) {
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
                texture = nullptr;
            }
        }
        if (clip_node_view != nullptr) {
            wgpuTextureViewRelease(clip_node_view);
            clip_node_view = nullptr;
        }
        if (clip_node_texture != nullptr) {
            wgpuTextureDestroy(clip_node_texture);
            wgpuTextureRelease(clip_node_texture);
            clip_node_texture = nullptr;
        }
        if (clip_atlas_view != nullptr) {
            wgpuTextureViewRelease(clip_atlas_view);
            clip_atlas_view = nullptr;
        }
        if (clip_atlas_texture != nullptr) {
            wgpuTextureDestroy(clip_atlas_texture);
            wgpuTextureRelease(clip_atlas_texture);
            clip_atlas_texture = nullptr;
        }
        if (clip_sampler != nullptr) {
            wgpuSamplerRelease(clip_sampler);
            clip_sampler = nullptr;
        }
        if (clip_compose_layout != nullptr) {
            wgpuBindGroupLayoutRelease(clip_compose_layout);
            clip_compose_layout = nullptr;
        }
        if (clip_compose_pipeline != nullptr) {
            wgpuRenderPipelineRelease(clip_compose_pipeline);
            clip_compose_pipeline = nullptr;
        }
        if (clip_path_pipeline != nullptr) {
            wgpuRenderPipelineRelease(clip_path_pipeline);
            clip_path_pipeline = nullptr;
        }
        if (clip_compose_shader != nullptr) {
            wgpuShaderModuleRelease(clip_compose_shader);
            clip_compose_shader = nullptr;
        }
    }

    void release_effect_resources() noexcept {
        for (auto& bind_group : effect_chain_drop_shadow_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& bind_group : effect_chain_blur_vertical_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& bind_group : effect_chain_blur_horizontal_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& bind_group : effect_chain_output_bind_groups) {
            if (bind_group != nullptr) {
                wgpuBindGroupRelease(bind_group);
                bind_group = nullptr;
            }
        }
        for (auto& view : effect_chain_texture_views) {
            if (view != nullptr) {
                wgpuTextureViewRelease(view);
                view = nullptr;
            }
        }
        for (auto& texture : effect_chain_textures) {
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
                texture = nullptr;
            }
        }
        const auto release_buffers = [](auto& buffers) {
            for (auto& buffer : buffers) {
                if (buffer != nullptr) {
                    wgpuBufferDestroy(buffer);
                    wgpuBufferRelease(buffer);
                    buffer = nullptr;
                }
            }
        };
        release_buffers(effect_chain_drop_shadow_uniform_buffers);
        release_buffers(effect_chain_blur_vertical_uniform_buffers);
        release_buffers(effect_chain_blur_horizontal_uniform_buffers);
        if (effect_drop_shadow_output_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_drop_shadow_output_bind_group);
            effect_drop_shadow_output_bind_group = nullptr;
        }
        if (effect_drop_shadow_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_drop_shadow_bind_group);
            effect_drop_shadow_bind_group = nullptr;
        }
        if (effect_output_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_output_bind_group);
            effect_output_bind_group = nullptr;
        }
        if (effect_blur_vertical_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_blur_vertical_bind_group);
            effect_blur_vertical_bind_group = nullptr;
        }
        if (effect_blur_horizontal_bind_group != nullptr) {
            wgpuBindGroupRelease(effect_blur_horizontal_bind_group);
            effect_blur_horizontal_bind_group = nullptr;
        }
        for (auto& view : effect_texture_views) {
            if (view != nullptr) {
                wgpuTextureViewRelease(view);
                view = nullptr;
            }
        }
        for (auto& texture : effect_textures) {
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
                texture = nullptr;
            }
        }
        if (effect_blur_vertical_uniform_buffer != nullptr) {
            wgpuBufferDestroy(effect_blur_vertical_uniform_buffer);
            wgpuBufferRelease(effect_blur_vertical_uniform_buffer);
            effect_blur_vertical_uniform_buffer = nullptr;
        }
        if (effect_blur_horizontal_uniform_buffer != nullptr) {
            wgpuBufferDestroy(effect_blur_horizontal_uniform_buffer);
            wgpuBufferRelease(effect_blur_horizontal_uniform_buffer);
            effect_blur_horizontal_uniform_buffer = nullptr;
        }
        if (effect_drop_shadow_uniform_buffer != nullptr) {
            wgpuBufferDestroy(effect_drop_shadow_uniform_buffer);
            wgpuBufferRelease(effect_drop_shadow_uniform_buffer);
            effect_drop_shadow_uniform_buffer = nullptr;
        }
        if (effect_drop_shadow_pipeline != nullptr) {
            wgpuComputePipelineRelease(effect_drop_shadow_pipeline);
            effect_drop_shadow_pipeline = nullptr;
        }
        if (effect_drop_shadow_layout != nullptr) {
            wgpuBindGroupLayoutRelease(effect_drop_shadow_layout);
            effect_drop_shadow_layout = nullptr;
        }
        if (effect_drop_shadow_shader != nullptr) {
            wgpuShaderModuleRelease(effect_drop_shadow_shader);
            effect_drop_shadow_shader = nullptr;
        }
        if (effect_blur_vertical_pipeline != nullptr) {
            wgpuComputePipelineRelease(effect_blur_vertical_pipeline);
            effect_blur_vertical_pipeline = nullptr;
        }
        if (effect_blur_horizontal_pipeline != nullptr) {
            wgpuComputePipelineRelease(effect_blur_horizontal_pipeline);
            effect_blur_horizontal_pipeline = nullptr;
        }
        if (effect_blur_layout != nullptr) {
            wgpuBindGroupLayoutRelease(effect_blur_layout);
            effect_blur_layout = nullptr;
        }
        if (effect_blur_vertical_shader != nullptr) {
            wgpuShaderModuleRelease(effect_blur_vertical_shader);
            effect_blur_vertical_shader = nullptr;
        }
        if (effect_blur_horizontal_shader != nullptr) {
            wgpuShaderModuleRelease(effect_blur_horizontal_shader);
            effect_blur_horizontal_shader = nullptr;
        }
        effect_width = 0U;
        effect_height = 0U;
        effect_chain_width = 0U;
        effect_chain_height = 0U;
        effect_chain_cached_count = 0U;
        effect_chain_bindings_valid = false;
        effect_cache_valid = false;
        effect_drop_shadow_uniform_cache_valid = false;
        effect_chain_blur_horizontal_uniform_cache_valid.fill(false);
        effect_chain_blur_vertical_uniform_cache_valid.fill(false);
        effect_chain_drop_shadow_uniform_cache_valid.fill(false);
    }

    void release_semantic_analytic_page() noexcept {
        auto& page = semantic_analytic_cache;
        if (page.index_buffer != nullptr) {
            wgpuBufferDestroy(page.index_buffer);
            wgpuBufferRelease(page.index_buffer);
            page.index_buffer = nullptr;
        }
        if (page.vertex_buffer != nullptr) {
            wgpuBufferDestroy(page.vertex_buffer);
            wgpuBufferRelease(page.vertex_buffer);
            page.vertex_buffer = nullptr;
        }
        page.index_buffer_size = 0U;
        page.vertex_buffer_size = 0U;
        page.index_bytes = 0U;
        page.vertex_bytes = 0U;
        page.scene_hash = 0U;
        page.dpi_scale = 0.0F;
        page.cache_valid = false;
        page.draws.clear();
    }

    bool ensure_semantic_analytic_page_buffers(
        std::uint64_t required_vertex_bytes,
        std::uint64_t required_index_bytes) noexcept {
        auto& page = semantic_analytic_cache;
        const auto required_capacity = [](std::uint64_t current,
                                          std::uint64_t required,
                                          std::uint64_t& capacity) noexcept {
            capacity = std::max<std::uint64_t>(256U, current);
            while (capacity < required) {
                if (capacity >
                    std::numeric_limits<std::uint64_t>::max() / 2U) {
                    return false;
                }
                capacity *= 2U;
            }
            return true;
        };
        const bool grow_vertex = page.vertex_buffer == nullptr ||
            required_vertex_bytes > page.vertex_buffer_size;
        const bool grow_index = page.index_buffer == nullptr ||
            required_index_bytes > page.index_buffer_size;
        std::uint64_t vertex_capacity = page.vertex_buffer_size;
        std::uint64_t index_capacity = page.index_buffer_size;
        if ((grow_vertex && !required_capacity(
                page.vertex_buffer_size,
                required_vertex_bytes,
                vertex_capacity)) ||
            (grow_index && !required_capacity(
                page.index_buffer_size,
                required_index_bytes,
                index_capacity))) {
            return false;
        }

        WGPUBuffer replacement_vertex = nullptr;
        WGPUBuffer replacement_index = nullptr;
        if (grow_vertex) {
            WGPUBufferDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU semantic analytic packed vertex page");
            descriptor.usage =
                WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
            descriptor.size = vertex_capacity;
            replacement_vertex = wgpuDeviceCreateBuffer(device, &descriptor);
            if (replacement_vertex == nullptr) {
                return false;
            }
        }
        if (grow_index) {
            WGPUBufferDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU semantic analytic packed index page");
            descriptor.usage =
                WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
            descriptor.size = index_capacity;
            replacement_index = wgpuDeviceCreateBuffer(device, &descriptor);
            if (replacement_index == nullptr) {
                if (replacement_vertex != nullptr) {
                    wgpuBufferDestroy(replacement_vertex);
                    wgpuBufferRelease(replacement_vertex);
                }
                return false;
            }
        }

        if (replacement_vertex != nullptr) {
            if (page.vertex_buffer != nullptr) {
                wgpuBufferDestroy(page.vertex_buffer);
                wgpuBufferRelease(page.vertex_buffer);
            }
            page.vertex_buffer = replacement_vertex;
            page.vertex_buffer_size = vertex_capacity;
        }
        if (replacement_index != nullptr) {
            if (page.index_buffer != nullptr) {
                wgpuBufferDestroy(page.index_buffer);
                wgpuBufferRelease(page.index_buffer);
            }
            page.index_buffer = replacement_index;
            page.index_buffer_size = index_capacity;
        }
        return true;
    }

    ~progpu_native_engine() {
        const progpu::native::webgpu::dispatch_scope dispatch_scope(
            &webgpu_dispatch);
        if (semantic_encoder != nullptr) {
            wgpuCommandEncoderRelease(semantic_encoder);
            semantic_encoder = nullptr;
        }
        release_semantic_analytic_page();
        release_effect_resources();
        release_clip_resources();
        if (group_blend_bind_group != nullptr) {
            wgpuBindGroupRelease(group_blend_bind_group);
        }
        if (group_blend_source_view != nullptr) {
            wgpuTextureViewRelease(group_blend_source_view);
        }
        if (group_blend_source_texture != nullptr) {
            wgpuTextureDestroy(group_blend_source_texture);
            wgpuTextureRelease(group_blend_source_texture);
        }
        if (group_blend_uniform_buffer != nullptr) {
            wgpuBufferDestroy(group_blend_uniform_buffer);
            wgpuBufferRelease(group_blend_uniform_buffer);
        }
        if (group_blend_layout != nullptr) {
            wgpuBindGroupLayoutRelease(group_blend_layout);
        }
        if (group_blend_pipeline != nullptr) {
            wgpuRenderPipelineRelease(group_blend_pipeline);
        }
        if (group_blend_shader != nullptr) {
            wgpuShaderModuleRelease(group_blend_shader);
        }
        if (layer_external_mask_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_external_mask_linear_bind_group);
        }
        if (layer_external_mask_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_external_mask_nearest_bind_group);
        }
        if (layer_external_mask_view != nullptr) {
            wgpuTextureViewRelease(layer_external_mask_view);
        }
        if (layer_analytic_mask_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_analytic_mask_bind_group);
        }
        if (layer_mask_dummy_view != nullptr) {
            wgpuTextureViewRelease(layer_mask_dummy_view);
        }
        if (layer_mask_dummy_texture != nullptr) {
            wgpuTextureDestroy(layer_mask_dummy_texture);
            wgpuTextureRelease(layer_mask_dummy_texture);
        }
        if (layer_mask_uniform_buffer != nullptr) {
            wgpuBufferDestroy(layer_mask_uniform_buffer);
            wgpuBufferRelease(layer_mask_uniform_buffer);
        }
        if (layer_mask_layout != nullptr) {
            wgpuBindGroupLayoutRelease(layer_mask_layout);
        }
        if (layer_mask_pipeline != nullptr) {
            wgpuRenderPipelineRelease(layer_mask_pipeline);
        }
        for (auto mask_blend_pipeline : layer_mask_blend_pipelines) {
            if (mask_blend_pipeline != nullptr) {
                wgpuRenderPipelineRelease(mask_blend_pipeline);
            }
        }
        for (auto blend_pipeline : layer_blend_pipelines) {
            if (blend_pipeline != nullptr) {
                wgpuRenderPipelineRelease(blend_pipeline);
            }
        }
        if (layer_texture_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_texture_bind_group);
        }
        if (layer_texture_view != nullptr) {
            wgpuTextureViewRelease(layer_texture_view);
        }
        if (layer_texture != nullptr) {
            wgpuTextureDestroy(layer_texture);
            wgpuTextureRelease(layer_texture);
        }
        if (layer_index_buffer != nullptr) {
            wgpuBufferDestroy(layer_index_buffer);
            wgpuBufferRelease(layer_index_buffer);
        }
        if (layer_vertex_buffer != nullptr) {
            wgpuBufferDestroy(layer_vertex_buffer);
            wgpuBufferRelease(layer_vertex_buffer);
        }
        if (layer_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(layer_uniform_bind_group);
        }
        if (layer_uniform_buffer != nullptr) {
            wgpuBufferDestroy(layer_uniform_buffer);
            wgpuBufferRelease(layer_uniform_buffer);
        }
        if (layer_composite_pipeline != nullptr) {
            wgpuRenderPipelineRelease(layer_composite_pipeline);
        }
        if (image_mask_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(image_mask_linear_bind_group);
        }
        if (image_mask_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(image_mask_nearest_bind_group);
        }
        if (image_mask_view != nullptr) {
            wgpuTextureViewRelease(image_mask_view);
        }
        if (image_mask_uniform_buffer != nullptr) {
            wgpuBufferDestroy(image_mask_uniform_buffer);
            wgpuBufferRelease(image_mask_uniform_buffer);
        }
        if (image_mask_layout != nullptr) {
            wgpuBindGroupLayoutRelease(image_mask_layout);
        }
        if (image_mask_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_mask_pipeline);
        }
        if (image_index_buffer != nullptr) {
            wgpuBufferDestroy(image_index_buffer);
            wgpuBufferRelease(image_index_buffer);
        }
        if (image_vertex_buffer != nullptr) {
            wgpuBufferDestroy(image_vertex_buffer);
            wgpuBufferRelease(image_vertex_buffer);
        }
        if (image_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(image_linear_bind_group);
        }
        if (image_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(image_nearest_bind_group);
        }
        if (image_texture_view != nullptr) {
            wgpuTextureViewRelease(image_texture_view);
        }
        if (image_texture != nullptr) {
            wgpuTextureDestroy(image_texture);
            wgpuTextureRelease(image_texture);
        }
        if (image_linear_sampler != nullptr) {
            wgpuSamplerRelease(image_linear_sampler);
        }
        if (image_nearest_sampler != nullptr) {
            wgpuSamplerRelease(image_nearest_sampler);
        }
        if (image_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(image_uniform_bind_group);
        }
        if (image_uniform_buffer != nullptr) {
            wgpuBufferDestroy(image_uniform_buffer);
            wgpuBufferRelease(image_uniform_buffer);
        }
        if (image_texture_layout != nullptr) {
            wgpuBindGroupLayoutRelease(image_texture_layout);
        }
        if (image_uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(image_uniform_layout);
        }
        if (image_pipeline != nullptr) {
            wgpuRenderPipelineRelease(image_pipeline);
        }
        if (image_shader != nullptr) {
            wgpuShaderModuleRelease(image_shader);
        }
        if (text_vertex_buffer != nullptr) {
            wgpuBufferDestroy(text_vertex_buffer);
            wgpuBufferRelease(text_vertex_buffer);
        }
        if (text_atlas_bind_group != nullptr) {
            wgpuBindGroupRelease(text_atlas_bind_group);
        }
        if (glyph_atlas_texture_view != nullptr) {
            wgpuTextureViewRelease(glyph_atlas_texture_view);
        }
        if (glyph_atlas_texture != nullptr) {
            wgpuTextureDestroy(glyph_atlas_texture);
            wgpuTextureRelease(glyph_atlas_texture);
        }
        if (glyph_atlas_sampler != nullptr) {
            wgpuSamplerRelease(glyph_atlas_sampler);
        }
        if (text_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(text_uniform_bind_group);
        }
        if (text_style_buffer != nullptr) {
            wgpuBufferDestroy(text_style_buffer);
            wgpuBufferRelease(text_style_buffer);
        }
        if (text_uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(text_uniform_layout);
        }
        if (text_atlas_layout != nullptr) {
            wgpuBindGroupLayoutRelease(text_atlas_layout);
        }
        if (text_pipeline != nullptr) {
            wgpuRenderPipelineRelease(text_pipeline);
        }
        if (text_shader != nullptr) {
            wgpuShaderModuleRelease(text_shader);
        }
        if (glyph_raster_pipeline != nullptr) {
            wgpuComputePipelineRelease(glyph_raster_pipeline);
        }
        if (glyph_raster_pipeline_layout != nullptr) {
            wgpuPipelineLayoutRelease(glyph_raster_pipeline_layout);
        }
        if (glyph_raster_layout != nullptr) {
            wgpuBindGroupLayoutRelease(glyph_raster_layout);
        }
        if (glyph_raster_shader != nullptr) {
            wgpuShaderModuleRelease(glyph_raster_shader);
        }
        if (path_atlas_bind_group != nullptr) {
            wgpuBindGroupRelease(path_atlas_bind_group);
        }
        if (path_atlas_texture_view != nullptr) {
            wgpuTextureViewRelease(path_atlas_texture_view);
        }
        if (path_atlas_texture != nullptr) {
            wgpuTextureDestroy(path_atlas_texture);
            wgpuTextureRelease(path_atlas_texture);
        }
        if (path_atlas_sampler != nullptr) {
            wgpuSamplerRelease(path_atlas_sampler);
        }
        if (path_raster_pipeline != nullptr) {
            wgpuComputePipelineRelease(path_raster_pipeline);
        }
        if (path_raster_pipeline_layout != nullptr) {
            wgpuPipelineLayoutRelease(path_raster_pipeline_layout);
        }
        if (path_raster_layout != nullptr) {
            wgpuBindGroupLayoutRelease(path_raster_layout);
        }
        if (path_raster_shader != nullptr) {
            wgpuShaderModuleRelease(path_raster_shader);
        }
        if (index_buffer != nullptr) {
            wgpuBufferDestroy(index_buffer);
            wgpuBufferRelease(index_buffer);
        }
        if (vertex_buffer != nullptr) {
            wgpuBufferDestroy(vertex_buffer);
            wgpuBufferRelease(vertex_buffer);
        }
        if (path_index_buffer != nullptr) {
            wgpuBufferDestroy(path_index_buffer);
            wgpuBufferRelease(path_index_buffer);
        }
        if (path_vertex_buffer != nullptr) {
            wgpuBufferDestroy(path_vertex_buffer);
            wgpuBufferRelease(path_vertex_buffer);
        }
        if (uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(uniform_bind_group);
        }
        if (analytic_uniform_bind_group != nullptr) {
            wgpuBindGroupRelease(analytic_uniform_bind_group);
        }
        if (analytic_atlas_bind_group != nullptr) {
            wgpuBindGroupRelease(analytic_atlas_bind_group);
        }
        if (analytic_sentinel_texture_view != nullptr) {
            wgpuTextureViewRelease(analytic_sentinel_texture_view);
        }
        if (analytic_sentinel_texture != nullptr) {
            wgpuTextureDestroy(analytic_sentinel_texture);
            wgpuTextureRelease(analytic_sentinel_texture);
        }
        if (analytic_sentinel_sampler != nullptr) {
            wgpuSamplerRelease(analytic_sentinel_sampler);
        }
        if (analytic_gradient_buffer != nullptr) {
            wgpuBufferDestroy(analytic_gradient_buffer);
            wgpuBufferRelease(analytic_gradient_buffer);
        }
        if (analytic_brush_buffer != nullptr) {
            wgpuBufferDestroy(analytic_brush_buffer);
            wgpuBufferRelease(analytic_brush_buffer);
        }
        if (analytic_uniform_buffer != nullptr) {
            wgpuBufferDestroy(analytic_uniform_buffer);
            wgpuBufferRelease(analytic_uniform_buffer);
        }
        if (uniform_buffer != nullptr) {
            wgpuBufferDestroy(uniform_buffer);
            wgpuBufferRelease(uniform_buffer);
        }
        if (uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(uniform_layout);
        }
        if (analytic_uniform_layout != nullptr) {
            wgpuBindGroupLayoutRelease(analytic_uniform_layout);
        }
        if (analytic_atlas_layout != nullptr) {
            wgpuBindGroupLayoutRelease(analytic_atlas_layout);
        }
        if (analytic_pipeline != nullptr) {
            wgpuRenderPipelineRelease(analytic_pipeline);
        }
        if (pipeline != nullptr) {
            wgpuRenderPipelineRelease(pipeline);
        }
        if (shader != nullptr) {
            wgpuShaderModuleRelease(shader);
        }
        if (queue != nullptr) {
            wgpuQueueRelease(queue);
        }
        if (device != nullptr) {
            wgpuDeviceRelease(device);
        }
        if (instance != nullptr) {
            progpu::native::webgpu::instance_release(instance);
        }
    }

    progpu_native_status fail(
        progpu_native_status status,
        const char* message) {
        last_error = message == nullptr ? "Unknown native renderer error." : message;
        return status;
    }

    bool is_owner_thread() const noexcept {
        return owner_thread == std::this_thread::get_id();
    }

    bool ensure_vertex_buffer(std::uint64_t required_size) {
        if (required_size <= vertex_buffer_size && vertex_buffer != nullptr) {
            return true;
        }

        std::uint64_t new_size = std::max(
            initial_vertex_buffer_size,
            vertex_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }

        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native vector vertex buffer");
        descriptor.usage = WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }

        if (vertex_buffer != nullptr) {
            wgpuBufferDestroy(vertex_buffer);
            wgpuBufferRelease(vertex_buffer);
        }
        vertex_buffer = replacement;
        vertex_buffer_size = new_size;
        geometry_gpu_cache_valid = false;
        return true;
    }

    bool ensure_index_buffer(std::uint64_t required_size) {
        if (required_size <= index_buffer_size && index_buffer != nullptr) {
            return true;
        }

        std::uint64_t new_size = std::max(
            initial_index_buffer_size,
            index_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }

        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native vector index buffer");
        descriptor.usage = WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }

        if (index_buffer != nullptr) {
            wgpuBufferDestroy(index_buffer);
            wgpuBufferRelease(index_buffer);
        }
        index_buffer = replacement;
        index_buffer_size = new_size;
        geometry_gpu_cache_valid = false;
        return true;
    }

    bool ensure_path_vertex_buffer(std::uint64_t required_size) {
        if (required_size <= path_vertex_buffer_size &&
            path_vertex_buffer != nullptr) {
            return true;
        }
        std::uint64_t new_size = std::max(
            initial_vertex_buffer_size,
            path_vertex_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }
        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained path vertex buffer");
        descriptor.usage = WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }
        if (path_vertex_buffer != nullptr) {
            wgpuBufferDestroy(path_vertex_buffer);
            wgpuBufferRelease(path_vertex_buffer);
        }
        path_vertex_buffer = replacement;
        path_vertex_buffer_size = new_size;
        path_gpu_cache_valid = false;
        return true;
    }

    bool ensure_path_index_buffer(std::uint64_t required_size) {
        if (required_size <= path_index_buffer_size &&
            path_index_buffer != nullptr) {
            return true;
        }
        std::uint64_t new_size = std::max(
            initial_index_buffer_size,
            path_index_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }
        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained path index buffer");
        descriptor.usage = WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }
        if (path_index_buffer != nullptr) {
            wgpuBufferDestroy(path_index_buffer);
            wgpuBufferRelease(path_index_buffer);
        }
        path_index_buffer = replacement;
        path_index_buffer_size = new_size;
        path_gpu_cache_valid = false;
        return true;
    }

    bool ensure_text_vertex_buffer(std::uint64_t required_size) {
        if (required_size <= text_vertex_buffer_size &&
            text_vertex_buffer != nullptr) {
            return true;
        }
        std::uint64_t new_size = std::max(
            initial_vertex_buffer_size,
            text_vertex_buffer_size);
        while (new_size < required_size) {
            if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
                return false;
            }
            new_size *= 2U;
        }
        WGPUBufferDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph instances");
        descriptor.usage = WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
        descriptor.size = new_size;
        WGPUBuffer replacement = wgpuDeviceCreateBuffer(device, &descriptor);
        if (replacement == nullptr) {
            return false;
        }
        if (text_vertex_buffer != nullptr) {
            wgpuBufferDestroy(text_vertex_buffer);
            wgpuBufferRelease(text_vertex_buffer);
        }
        text_vertex_buffer = replacement;
        text_vertex_buffer_size = new_size;
        glyph_gpu_cache_valid = false;
        return true;
    }
};

namespace {

bool create_pipeline(progpu_native_engine& engine) {
    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::vector_wgsl,
        progpu::native::generated::vector_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared Vector.wgsl");
    engine.shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.shader == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 52U, 7U)
    }};
    WGPUVertexBufferLayout vertex_buffer_layout{};
    vertex_buffer_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_buffer_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_buffer_layout.attributeCount = attributes.size();
    vertex_buffer_layout.attributes = attributes.data();

    WGPUVertexState vertex_state{};
    vertex_state.module = engine.shader;
    vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_solid_rect");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_buffer_layout;

    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;

    WGPUColorTargetState color_target{};
    color_target.format = engine.target_format;
    color_target.blend = &blend;
    color_target.writeMask = WGPUColorWriteMask_All;

    WGPUFragmentState fragment_state{};
    fragment_state.module = engine.shader;
    fragment_state.entryPoint = progpu::native::webgpu::string_view("fs_solid_rect_main_unmasked");
    fragment_state.targetCount = 1U;
    fragment_state.targets = &color_target;

    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native solid rectangle pipeline");
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment_state;
    engine.pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.pipeline == nullptr) {
        return false;
    }

    engine.uniform_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.pipeline,
        0U);
    if (engine.uniform_layout == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view("ProGPU native frame uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    if (engine.uniform_buffer == nullptr) {
        return false;
    }

    WGPUBindGroupEntry uniform_entry{};
    uniform_entry.binding = 0U;
    uniform_entry.buffer = engine.uniform_buffer;
    uniform_entry.size = sizeof(gpu_uniforms);
    WGPUBindGroupDescriptor bind_group_descriptor{};
    bind_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native frame uniform bind group");
    bind_group_descriptor.layout = engine.uniform_layout;
    bind_group_descriptor.entryCount = 1U;
    bind_group_descriptor.entries = &uniform_entry;
    engine.uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &bind_group_descriptor);
    return engine.uniform_bind_group != nullptr;
}

WGPUBindGroup create_analytic_uniform_bind_group(
    progpu_native_engine& engine,
    WGPUBuffer brush_buffer,
    std::uint64_t brush_buffer_size) {
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, engine.analytic_uniform_buffer, 0U, sizeof(gpu_uniforms),
            nullptr, nullptr},
        {nullptr, 1U, brush_buffer, 0U, brush_buffer_size,
            nullptr, nullptr},
        {nullptr, 2U, engine.analytic_gradient_buffer, 0U, 32U,
            nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic bind group");
    descriptor.layout = engine.analytic_uniform_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool ensure_analytic_brush_buffer(
    progpu_native_engine& engine,
    std::uint64_t required_size) {
    if (required_size <= engine.analytic_brush_buffer_size &&
        engine.analytic_brush_buffer != nullptr &&
        engine.analytic_uniform_bind_group != nullptr) {
        return true;
    }

    std::uint64_t new_size = std::max(
        initial_brush_buffer_size,
        engine.analytic_brush_buffer_size);
    while (new_size < required_size) {
        if (new_size > std::numeric_limits<std::uint64_t>::max() / 2U) {
            return false;
        }
        new_size *= 2U;
    }

    WGPUBufferDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native solid brush table");
    descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    descriptor.size = new_size;
    WGPUBuffer replacement = wgpuDeviceCreateBuffer(engine.device, &descriptor);
    if (replacement == nullptr) {
        return false;
    }
    WGPUBindGroup replacement_group = create_analytic_uniform_bind_group(
        engine,
        replacement,
        new_size);
    if (replacement_group == nullptr) {
        wgpuBufferDestroy(replacement);
        wgpuBufferRelease(replacement);
        return false;
    }

    if (engine.analytic_uniform_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.analytic_uniform_bind_group);
    }
    if (engine.analytic_brush_buffer != nullptr) {
        wgpuBufferDestroy(engine.analytic_brush_buffer);
        wgpuBufferRelease(engine.analytic_brush_buffer);
    }
    engine.analytic_brush_buffer = replacement;
    engine.analytic_brush_buffer_size = new_size;
    engine.analytic_uniform_bind_group = replacement_group;
    return true;
}

bool create_analytic_pipeline(progpu_native_engine& engine) {
    const std::array<WGPUVertexAttribute, 8U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 52U, 7U)
    }};
    WGPUVertexBufferLayout vertex_buffer_layout{};
    vertex_buffer_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_buffer_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_buffer_layout.attributeCount = attributes.size();
    vertex_buffer_layout.attributes = attributes.data();

    WGPUVertexState vertex_state{};
    vertex_state.module = engine.shader;
    vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_buffer_layout;

    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;

    WGPUColorTargetState color_target{};
    color_target.format = engine.target_format;
    color_target.blend = &blend;
    color_target.writeMask = WGPUColorWriteMask_All;

    WGPUFragmentState fragment_state{};
    fragment_state.module = engine.shader;
    fragment_state.entryPoint = progpu::native::webgpu::string_view("fs_main_unmasked");
    fragment_state.targetCount = 1U;
    fragment_state.targets = &color_target;

    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic primitive pipeline");
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment_state;
    engine.analytic_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.analytic_pipeline == nullptr) {
        return false;
    }

    engine.analytic_uniform_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.analytic_pipeline,
        0U);
    engine.analytic_atlas_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.analytic_pipeline,
        1U);
    if (engine.analytic_uniform_layout == nullptr ||
        engine.analytic_atlas_layout == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic frame uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.analytic_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);

    WGPUBufferDescriptor gradient_descriptor{};
    gradient_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic gradient sentinel");
    gradient_descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    gradient_descriptor.size = 32U;
    engine.analytic_gradient_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &gradient_descriptor);

    if (engine.analytic_uniform_buffer == nullptr ||
        engine.analytic_gradient_buffer == nullptr) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic sentinel texture");
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {1U, 1U, 1U};
    texture_descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    engine.analytic_sentinel_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (engine.analytic_sentinel_texture == nullptr) {
        return false;
    }
    engine.analytic_sentinel_texture_view = wgpuTextureCreateView(
        engine.analytic_sentinel_texture,
        nullptr);

    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view("ProGPU native analytic sentinel sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Nearest;
    sampler_descriptor.minFilter = WGPUFilterMode_Nearest;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.analytic_sentinel_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    if (engine.analytic_sentinel_texture_view == nullptr ||
        engine.analytic_sentinel_sampler == nullptr) {
        return false;
    }

    if (!ensure_analytic_brush_buffer(engine, gpu_brush_size)) {
        return false;
    }

    std::array<std::byte, gpu_brush_size> solid_brush{};
    constexpr float opacity = 1.0F;
    std::memcpy(solid_brush.data() + 4U, &opacity, sizeof(opacity));
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.analytic_brush_buffer,
        0U,
        solid_brush.data(),
        solid_brush.size());
    std::array<std::byte, 32U> gradient_sentinel{};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.analytic_gradient_buffer,
        0U,
        gradient_sentinel.data(),
        gradient_sentinel.size());

    const std::array<WGPUBindGroupEntry, 2U> atlas_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.analytic_sentinel_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.analytic_sentinel_texture_view}
    }};
    WGPUBindGroupDescriptor atlas_bind_group_descriptor{};
    atlas_bind_group_descriptor.label =
        progpu::native::webgpu::string_view(
            "ProGPU native analytic atlas sentinel bind group");
    atlas_bind_group_descriptor.layout = engine.analytic_atlas_layout;
    atlas_bind_group_descriptor.entryCount = atlas_entries.size();
    atlas_bind_group_descriptor.entries = atlas_entries.data();
    engine.analytic_atlas_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &atlas_bind_group_descriptor);

    return engine.analytic_atlas_bind_group != nullptr;
}

bool create_path_resources(progpu_native_engine& engine) {
    if (engine.path_raster_pipeline != nullptr &&
        engine.path_atlas_bind_group != nullptr) {
        return true;
    }
    if (engine.path_raster_shader != nullptr ||
        engine.path_raster_pipeline != nullptr ||
        engine.path_raster_layout != nullptr ||
        engine.path_raster_pipeline_layout != nullptr ||
        engine.path_atlas_sampler != nullptr ||
        engine.path_atlas_texture != nullptr ||
        engine.path_atlas_texture_view != nullptr ||
        engine.path_atlas_bind_group != nullptr) {
        return false;
    }
    if (engine.analytic_pipeline == nullptr &&
        !create_analytic_pipeline(engine)) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::path_rasterizer_wgsl,
        progpu::native::generated::path_rasterizer_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared PathRasterizer.wgsl");
    engine.path_raster_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.path_raster_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> layout_entries{};
    for (std::uint32_t index = 0U; index < layout_entries.size(); ++index) {
        layout_entries[index].binding = index;
        layout_entries[index].visibility = WGPUShaderStage_Compute;
        layout_entries[index].buffer.hasDynamicOffset = false;
        layout_entries[index].buffer.type = index == 3U
            ? WGPUBufferBindingType_Storage
            : WGPUBufferBindingType_ReadOnlyStorage;
    }
    layout_entries[0].buffer.minBindingSize = sizeof(gpu_path_uniforms);
    layout_entries[1].buffer.minBindingSize = sizeof(gpu_path_record);
    layout_entries[2].buffer.minBindingSize = sizeof(progpu_native_path_segment);
    layout_entries[3].buffer.minBindingSize = sizeof(std::uint32_t);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster bindings");
    layout_descriptor.entryCount = layout_entries.size();
    layout_descriptor.entries = layout_entries.data();
    engine.path_raster_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.path_raster_layout == nullptr) {
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &engine.path_raster_layout;
    engine.path_raster_pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (engine.path_raster_pipeline_layout == nullptr) {
        return false;
    }

    WGPUComputePipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path raster pipeline");
    pipeline_descriptor.layout = engine.path_raster_pipeline_layout;
    pipeline_descriptor.compute.module = engine.path_raster_shader;
    pipeline_descriptor.compute.entryPoint = progpu::native::webgpu::string_view("cs_main");
    engine.path_raster_pipeline = wgpuDeviceCreateComputePipeline(
        engine.device,
        &pipeline_descriptor);
    if (engine.path_raster_pipeline == nullptr) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path atlas");
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {
        engine.path_atlas_size,
        engine.path_atlas_size,
        1U
    };
    texture_descriptor.format = WGPUTextureFormat_R8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    engine.path_atlas_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (engine.path_atlas_texture == nullptr) {
        return false;
    }
    engine.path_atlas_texture_view = wgpuTextureCreateView(
        engine.path_atlas_texture,
        nullptr);

    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path atlas sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Linear;
    sampler_descriptor.minFilter = WGPUFilterMode_Linear;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.path_atlas_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    if (engine.path_atlas_texture_view == nullptr ||
        engine.path_atlas_sampler == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupEntry, 2U> atlas_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.path_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.path_atlas_texture_view}
    }};
    WGPUBindGroupDescriptor atlas_descriptor{};
    atlas_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path atlas bind group");
    atlas_descriptor.layout = engine.analytic_atlas_layout;
    atlas_descriptor.entryCount = atlas_entries.size();
    atlas_descriptor.entries = atlas_entries.data();
    engine.path_atlas_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &atlas_descriptor);
    if (engine.path_atlas_bind_group == nullptr) {
        return false;
    }
    ++engine.path_atlas_generation;
    return true;
}

bool create_glyph_resources(progpu_native_engine& engine) {
    if (engine.glyph_raster_pipeline != nullptr &&
        engine.text_pipeline != nullptr &&
        engine.text_uniform_bind_group != nullptr &&
        engine.text_atlas_bind_group != nullptr) {
        return true;
    }
    if (engine.glyph_raster_shader != nullptr ||
        engine.glyph_raster_pipeline != nullptr ||
        engine.glyph_raster_layout != nullptr ||
        engine.glyph_raster_pipeline_layout != nullptr ||
        engine.text_shader != nullptr || engine.text_pipeline != nullptr ||
        engine.text_uniform_layout != nullptr ||
        engine.text_atlas_layout != nullptr ||
        engine.text_style_buffer != nullptr ||
        engine.text_uniform_bind_group != nullptr ||
        engine.glyph_atlas_sampler != nullptr ||
        engine.glyph_atlas_texture != nullptr ||
        engine.glyph_atlas_texture_view != nullptr ||
        engine.text_atlas_bind_group != nullptr) {
        return false;
    }
    if (engine.analytic_pipeline == nullptr &&
        !create_analytic_pipeline(engine)) {
        return false;
    }

    progpu::native::webgpu::wgsl_source glyph_wgsl(
        progpu::native::generated::glyph_rasterizer_wgsl,
        progpu::native::generated::glyph_rasterizer_wgsl_size);
    WGPUShaderModuleDescriptor glyph_shader_descriptor{};
    glyph_shader_descriptor.nextInChain = glyph_wgsl.chain();
    glyph_shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared GlyphRasterizer.wgsl");
    engine.glyph_raster_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &glyph_shader_descriptor);
    if (engine.glyph_raster_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> compute_entries{};
    for (std::uint32_t index = 0U; index < compute_entries.size(); ++index) {
        compute_entries[index].binding = index;
        compute_entries[index].visibility = WGPUShaderStage_Compute;
        compute_entries[index].buffer.type = index == 0U
            ? WGPUBufferBindingType_Uniform
            : index == 3U
            ? WGPUBufferBindingType_Storage
            : WGPUBufferBindingType_ReadOnlyStorage;
    }
    compute_entries[0].buffer.hasDynamicOffset = true;
    compute_entries[0].buffer.minBindingSize = sizeof(gpu_glyph_uniforms);
    compute_entries[1].buffer.minBindingSize = sizeof(gpu_glyph_record);
    compute_entries[2].buffer.minBindingSize = sizeof(progpu_native_path_segment);
    compute_entries[3].buffer.minBindingSize = sizeof(std::uint32_t);
    WGPUBindGroupLayoutDescriptor compute_layout_descriptor{};
    compute_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph raster bindings");
    compute_layout_descriptor.entryCount = compute_entries.size();
    compute_layout_descriptor.entries = compute_entries.data();
    engine.glyph_raster_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &compute_layout_descriptor);
    if (engine.glyph_raster_layout == nullptr) {
        return false;
    }
    WGPUPipelineLayoutDescriptor compute_pipeline_layout_descriptor{};
    compute_pipeline_layout_descriptor.label =
        progpu::native::webgpu::string_view(
            "ProGPU native glyph raster layout");
    compute_pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    compute_pipeline_layout_descriptor.bindGroupLayouts =
        &engine.glyph_raster_layout;
    engine.glyph_raster_pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &compute_pipeline_layout_descriptor);
    if (engine.glyph_raster_pipeline_layout == nullptr) {
        return false;
    }
    WGPUComputePipelineDescriptor compute_pipeline_descriptor{};
    compute_pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph raster pipeline");
    compute_pipeline_descriptor.layout = engine.glyph_raster_pipeline_layout;
    compute_pipeline_descriptor.compute.module = engine.glyph_raster_shader;
    compute_pipeline_descriptor.compute.entryPoint = progpu::native::webgpu::string_view("cs_main");
    engine.glyph_raster_pipeline = wgpuDeviceCreateComputePipeline(
        engine.device,
        &compute_pipeline_descriptor);
    if (engine.glyph_raster_pipeline == nullptr) {
        return false;
    }

    progpu::native::webgpu::wgsl_source text_wgsl(
        progpu::native::generated::text_wgsl,
        progpu::native::generated::text_wgsl_size);
    WGPUShaderModuleDescriptor text_shader_descriptor{};
    text_shader_descriptor.nextInChain = text_wgsl.chain();
    text_shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared Text.wgsl");
    engine.text_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &text_shader_descriptor);
    if (engine.text_shader == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 8U> text_attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 16U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 24U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 40U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 56U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 72U, 6U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 88U, 7U)
    }};
    WGPUVertexBufferLayout text_vertex_layout{};
    text_vertex_layout.arrayStride = sizeof(gpu_glyph_instance);
    text_vertex_layout.stepMode = WGPUVertexStepMode_Instance;
    text_vertex_layout.attributeCount = text_attributes.size();
    text_vertex_layout.attributes = text_attributes.data();
    WGPUVertexState text_vertex_state{};
    text_vertex_state.module = engine.text_shader;
    text_vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    text_vertex_state.bufferCount = 1U;
    text_vertex_state.buffers = &text_vertex_layout;
    WGPUBlendState text_blend{};
    text_blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    text_blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    text_blend.color.operation = WGPUBlendOperation_Add;
    text_blend.alpha.srcFactor = WGPUBlendFactor_One;
    text_blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    text_blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState text_target{};
    text_target.format = engine.target_format;
    text_target.blend = &text_blend;
    text_target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState text_fragment_state{};
    text_fragment_state.module = engine.text_shader;
    text_fragment_state.entryPoint = progpu::native::webgpu::string_view("fs_main_unmasked");
    text_fragment_state.targetCount = 1U;
    text_fragment_state.targets = &text_target;
    WGPURenderPipelineDescriptor text_pipeline_descriptor{};
    text_pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native positioned glyph pipeline");
    text_pipeline_descriptor.vertex = text_vertex_state;
    text_pipeline_descriptor.primitive.topology =
        WGPUPrimitiveTopology_TriangleList;
    text_pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    text_pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    text_pipeline_descriptor.multisample.count = 1U;
    text_pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    text_pipeline_descriptor.fragment = &text_fragment_state;
    engine.text_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &text_pipeline_descriptor);
    if (engine.text_pipeline == nullptr) {
        return false;
    }
    engine.text_uniform_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.text_pipeline,
        0U);
    engine.text_atlas_layout = wgpuRenderPipelineGetBindGroupLayout(
        engine.text_pipeline,
        1U);
    if (engine.text_uniform_layout == nullptr ||
        engine.text_atlas_layout == nullptr) {
        return false;
    }

    WGPUBufferDescriptor style_descriptor{};
    style_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text style sentinel");
    style_descriptor.usage = WGPUBufferUsage_Storage |
        WGPUBufferUsage_CopyDst;
    style_descriptor.size = 32U;
    engine.text_style_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &style_descriptor);
    if (engine.text_style_buffer == nullptr) {
        return false;
    }
    std::array<std::byte, 32U> style_bytes{};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.text_style_buffer,
        0U,
        style_bytes.data(),
        style_bytes.size());
    const std::array<WGPUBindGroupEntry, 2U> uniform_entries{{
        {nullptr, 0U, engine.analytic_uniform_buffer, 0U,
            sizeof(gpu_uniforms), nullptr, nullptr},
        {nullptr, 1U, engine.text_style_buffer, 0U, 32U, nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor uniform_group_descriptor{};
    uniform_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text uniform bind group");
    uniform_group_descriptor.layout = engine.text_uniform_layout;
    uniform_group_descriptor.entryCount = uniform_entries.size();
    uniform_group_descriptor.entries = uniform_entries.data();
    engine.text_uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &uniform_group_descriptor);
    if (engine.text_uniform_bind_group == nullptr) {
        return false;
    }

    WGPUTextureDescriptor atlas_descriptor{};
    atlas_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained glyph atlas");
    atlas_descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    atlas_descriptor.dimension = WGPUTextureDimension_2D;
    atlas_descriptor.size = {
        engine.glyph_atlas_size,
        engine.glyph_atlas_size,
        1U
    };
    atlas_descriptor.format = WGPUTextureFormat_R8Unorm;
    atlas_descriptor.mipLevelCount = 1U;
    atlas_descriptor.sampleCount = 1U;
    engine.glyph_atlas_texture = wgpuDeviceCreateTexture(
        engine.device,
        &atlas_descriptor);
    if (engine.glyph_atlas_texture == nullptr) {
        return false;
    }
    engine.glyph_atlas_texture_view = wgpuTextureCreateView(
        engine.glyph_atlas_texture,
        nullptr);
    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view("ProGPU native glyph atlas sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Linear;
    sampler_descriptor.minFilter = WGPUFilterMode_Linear;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.glyph_atlas_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    if (engine.glyph_atlas_texture_view == nullptr ||
        engine.glyph_atlas_sampler == nullptr) {
        return false;
    }
    const std::array<WGPUBindGroupEntry, 3U> atlas_entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.glyph_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U,
            nullptr, engine.glyph_atlas_texture_view},
        {nullptr, 2U, nullptr, 0U, 0U,
            nullptr, engine.analytic_sentinel_texture_view}
    }};
    WGPUBindGroupDescriptor atlas_group_descriptor{};
    atlas_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text atlas bind group");
    atlas_group_descriptor.layout = engine.text_atlas_layout;
    atlas_group_descriptor.entryCount = atlas_entries.size();
    atlas_group_descriptor.entries = atlas_entries.data();
    engine.text_atlas_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &atlas_group_descriptor);
    if (engine.text_atlas_bind_group == nullptr) {
        return false;
    }
    ++engine.glyph_atlas_generation;
    return true;
}

bool resize_path_atlas(
    progpu_native_engine& engine,
    std::uint32_t requested_size) {
    if (requested_size <= engine.path_atlas_size) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained path atlas");
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {requested_size, requested_size, 1U};
    descriptor.format = WGPUTextureFormat_R8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(engine.device, &descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    const std::array<WGPUBindGroupEntry, 2U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.path_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view}
    }};
    WGPUBindGroupDescriptor group_descriptor{};
    group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native path atlas bind group");
    group_descriptor.layout = engine.analytic_atlas_layout;
    group_descriptor.entryCount = entries.size();
    group_descriptor.entries = entries.data();
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(
        engine.device,
        &group_descriptor);
    if (group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }

    wgpuBindGroupRelease(engine.path_atlas_bind_group);
    wgpuTextureViewRelease(engine.path_atlas_texture_view);
    wgpuTextureDestroy(engine.path_atlas_texture);
    wgpuTextureRelease(engine.path_atlas_texture);
    engine.path_atlas_bind_group = group;
    engine.path_atlas_texture_view = view;
    engine.path_atlas_texture = texture;
    engine.path_atlas_size = requested_size;
    ++engine.path_atlas_generation;
    return true;
}

bool resize_glyph_atlas(
    progpu_native_engine& engine,
    std::uint32_t requested_size) {
    if (requested_size <= engine.glyph_atlas_size) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained glyph atlas");
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_CopyDst;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {requested_size, requested_size, 1U};
    descriptor.format = WGPUTextureFormat_R8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(engine.device, &descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U,
            engine.glyph_atlas_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 2U, nullptr, 0U, 0U,
            nullptr, engine.analytic_sentinel_texture_view}
    }};
    WGPUBindGroupDescriptor group_descriptor{};
    group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native text atlas bind group");
    group_descriptor.layout = engine.text_atlas_layout;
    group_descriptor.entryCount = entries.size();
    group_descriptor.entries = entries.data();
    WGPUBindGroup group = wgpuDeviceCreateBindGroup(
        engine.device,
        &group_descriptor);
    if (group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }

    wgpuBindGroupRelease(engine.text_atlas_bind_group);
    wgpuTextureViewRelease(engine.glyph_atlas_texture_view);
    wgpuTextureDestroy(engine.glyph_atlas_texture);
    wgpuTextureRelease(engine.glyph_atlas_texture);
    engine.text_atlas_bind_group = group;
    engine.glyph_atlas_texture_view = view;
    engine.glyph_atlas_texture = texture;
    engine.glyph_atlas_size = requested_size;
    ++engine.glyph_atlas_generation;
    ++engine.glyph_atlas_growth_count;
    return true;
}

WGPUBindGroup create_image_texture_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 2U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.image_texture_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_image_resources(progpu_native_engine& engine) {
    if (engine.image_pipeline != nullptr) {
        return true;
    }
    if (engine.image_shader != nullptr ||
        engine.image_uniform_layout != nullptr ||
        engine.image_texture_layout != nullptr ||
        engine.image_uniform_buffer != nullptr ||
        engine.image_uniform_bind_group != nullptr ||
        engine.image_nearest_sampler != nullptr ||
        engine.image_linear_sampler != nullptr ||
        engine.image_vertex_buffer != nullptr ||
        engine.image_index_buffer != nullptr) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::texture_wgsl,
        progpu::native::generated::texture_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view("ProGPU shared Texture.wgsl");
    engine.image_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.image_shader == nullptr) {
        return false;
    }

    WGPUBindGroupLayoutEntry uniform_layout_entry{};
    uniform_layout_entry.binding = 0U;
    uniform_layout_entry.visibility = WGPUShaderStage_Vertex |
        WGPUShaderStage_Fragment;
    uniform_layout_entry.buffer.type = WGPUBufferBindingType_Uniform;
    uniform_layout_entry.buffer.minBindingSize = sizeof(gpu_uniforms);
    WGPUBindGroupLayoutDescriptor uniform_layout_descriptor{};
    uniform_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image uniform layout");
    uniform_layout_descriptor.entryCount = 1U;
    uniform_layout_descriptor.entries = &uniform_layout_entry;
    engine.image_uniform_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &uniform_layout_descriptor);

    std::array<WGPUBindGroupLayoutEntry, 2U> texture_layout_entries{};
    texture_layout_entries[0].binding = 0U;
    texture_layout_entries[0].visibility = WGPUShaderStage_Fragment;
    texture_layout_entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    texture_layout_entries[1].binding = 1U;
    texture_layout_entries[1].visibility = WGPUShaderStage_Fragment;
    texture_layout_entries[1].texture.sampleType =
        WGPUTextureSampleType_Float;
    texture_layout_entries[1].texture.viewDimension =
        WGPUTextureViewDimension_2D;
    texture_layout_entries[1].texture.multisampled = false;
    WGPUBindGroupLayoutDescriptor texture_layout_descriptor{};
    texture_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image texture layout");
    texture_layout_descriptor.entryCount = texture_layout_entries.size();
    texture_layout_descriptor.entries = texture_layout_entries.data();
    engine.image_texture_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &texture_layout_descriptor);
    if (engine.image_uniform_layout == nullptr ||
        engine.image_texture_layout == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 2U> pipeline_layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native unmasked image layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = pipeline_layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = pipeline_layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 7U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;
    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view("fs_main_unmasked");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained image pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.image_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.image_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image frame uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.image_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    WGPUBufferDescriptor vertex_descriptor{};
    vertex_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained image vertices");
    vertex_descriptor.usage = WGPUBufferUsage_Vertex |
        WGPUBufferUsage_CopyDst;
    vertex_descriptor.size = sizeof(engine.image_vertices);
    engine.image_vertex_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &vertex_descriptor);
    WGPUBufferDescriptor index_descriptor{};
    index_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained image indices");
    index_descriptor.usage = WGPUBufferUsage_Index |
        WGPUBufferUsage_CopyDst;
    index_descriptor.size = 6U * sizeof(std::uint32_t);
    engine.image_index_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &index_descriptor);
    if (engine.image_uniform_buffer == nullptr ||
        engine.image_vertex_buffer == nullptr ||
        engine.image_index_buffer == nullptr) {
        return false;
    }

    WGPUBindGroupEntry uniform_entry{};
    uniform_entry.binding = 0U;
    uniform_entry.buffer = engine.image_uniform_buffer;
    uniform_entry.size = sizeof(gpu_uniforms);
    WGPUBindGroupDescriptor uniform_group_descriptor{};
    uniform_group_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image uniform bind group");
    uniform_group_descriptor.layout = engine.image_uniform_layout;
    uniform_group_descriptor.entryCount = 1U;
    uniform_group_descriptor.entries = &uniform_entry;
    engine.image_uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &uniform_group_descriptor);
    if (engine.image_uniform_bind_group == nullptr) {
        return false;
    }

    const auto create_sampler = [&](WGPUFilterMode filter) {
        WGPUSamplerDescriptor descriptor{};
        descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
        descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
        descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
        descriptor.magFilter = filter;
        descriptor.minFilter = filter;
        descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
        descriptor.lodMinClamp = 0.0F;
        descriptor.lodMaxClamp = 0.0F;
        descriptor.maxAnisotropy = 1U;
        return wgpuDeviceCreateSampler(engine.device, &descriptor);
    };
    engine.image_nearest_sampler = create_sampler(WGPUFilterMode_Nearest);
    engine.image_linear_sampler = create_sampler(WGPUFilterMode_Linear);
    if (engine.image_nearest_sampler == nullptr ||
        engine.image_linear_sampler == nullptr) {
        return false;
    }

    constexpr std::array<std::uint32_t, 6U> indices{
        0U, 1U, 2U, 0U, 2U, 3U};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.image_index_buffer,
        0U,
        indices.data(),
        sizeof(indices));
    return true;
}

bool create_layer_resources(progpu_native_engine& engine) {
    if (engine.layer_composite_pipeline != nullptr) {
        return true;
    }
    if (!create_image_resources(engine) ||
        engine.layer_uniform_buffer != nullptr ||
        engine.layer_uniform_bind_group != nullptr ||
        engine.layer_vertex_buffer != nullptr ||
        engine.layer_index_buffer != nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 2U> layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label =
        progpu::native::webgpu::string_view(
            "ProGPU native group composite layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 7U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;

    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_One;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint =
        progpu::native::webgpu::string_view("fs_main_unmasked");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native premultiplied group composite pipeline");
    descriptor.layout = pipeline_layout;
    descriptor.vertex = vertex_state;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    engine.layer_composite_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.layer_composite_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native group composite uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_uniforms);
    engine.layer_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    WGPUBufferDescriptor vertex_descriptor{};
    vertex_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native group composite vertices");
    vertex_descriptor.usage = WGPUBufferUsage_Vertex |
        WGPUBufferUsage_CopyDst;
    vertex_descriptor.size = sizeof(engine.layer_vertices);
    engine.layer_vertex_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &vertex_descriptor);
    WGPUBufferDescriptor index_descriptor{};
    index_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native group composite indices");
    index_descriptor.usage = WGPUBufferUsage_Index |
        WGPUBufferUsage_CopyDst;
    index_descriptor.size = 6U * sizeof(std::uint32_t);
    engine.layer_index_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &index_descriptor);
    if (engine.layer_uniform_buffer == nullptr ||
        engine.layer_vertex_buffer == nullptr ||
        engine.layer_index_buffer == nullptr) {
        return false;
    }

    WGPUBindGroupEntry uniform_entry{};
    uniform_entry.binding = 0U;
    uniform_entry.buffer = engine.layer_uniform_buffer;
    uniform_entry.size = sizeof(gpu_uniforms);
    WGPUBindGroupDescriptor uniform_group_descriptor{};
    uniform_group_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native group composite uniform bind group");
    uniform_group_descriptor.layout = engine.image_uniform_layout;
    uniform_group_descriptor.entryCount = 1U;
    uniform_group_descriptor.entries = &uniform_entry;
    engine.layer_uniform_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &uniform_group_descriptor);
    if (engine.layer_uniform_bind_group == nullptr) {
        return false;
    }
    constexpr std::array<std::uint32_t, 6U> indices{
        0U, 1U, 2U, 0U, 2U, 3U};
    wgpuQueueWriteBuffer(
        engine.queue,
        engine.layer_index_buffer,
        0U,
        indices.data(),
        sizeof(indices));
    return true;
}

WGPUBindGroup create_layer_mask_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 2U, engine.layer_mask_uniform_buffer, 0U,
            sizeof(gpu_mask_sampling_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.layer_mask_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_layer_mask_resources(progpu_native_engine& engine) {
    if (engine.layer_mask_pipeline != nullptr) {
        return true;
    }
    if (!create_layer_resources(engine) ||
        engine.layer_mask_layout != nullptr ||
        engine.layer_mask_uniform_buffer != nullptr ||
        engine.layer_mask_dummy_texture != nullptr ||
        engine.layer_mask_dummy_view != nullptr ||
        engine.layer_analytic_mask_bind_group != nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 3U> mask_entries{};
    mask_entries[0].binding = 0U;
    mask_entries[0].visibility = WGPUShaderStage_Fragment;
    mask_entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    mask_entries[1].binding = 1U;
    mask_entries[1].visibility = WGPUShaderStage_Fragment;
    mask_entries[1].texture.sampleType = WGPUTextureSampleType_Float;
    mask_entries[1].texture.viewDimension = WGPUTextureViewDimension_2D;
    mask_entries[1].texture.multisampled = false;
    mask_entries[2].binding = 2U;
    mask_entries[2].visibility = WGPUShaderStage_Fragment;
    mask_entries[2].buffer.type = WGPUBufferBindingType_Uniform;
    mask_entries[2].buffer.minBindingSize =
        sizeof(gpu_mask_sampling_uniforms);
    WGPUBindGroupLayoutDescriptor mask_layout_descriptor{};
    mask_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native common group mask layout");
    mask_layout_descriptor.entryCount = mask_entries.size();
    mask_layout_descriptor.entries = mask_entries.data();
    engine.layer_mask_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &mask_layout_descriptor);
    if (engine.layer_mask_layout == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.layer_mask_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native masked group composite layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 7U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;
    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_One;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view("fs_main");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native masked premultiplied group composite pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology =
        WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.layer_mask_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.layer_mask_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native common group mask uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(gpu_mask_sampling_uniforms);
    engine.layer_mask_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    if (engine.layer_mask_uniform_buffer == nullptr) {
        return false;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native analytic group mask sentinel");
    texture_descriptor.usage = WGPUTextureUsage_TextureBinding;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {1U, 1U, 1U};
    texture_descriptor.format = WGPUTextureFormat_R8Unorm;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    engine.layer_mask_dummy_texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (engine.layer_mask_dummy_texture == nullptr) {
        return false;
    }
    engine.layer_mask_dummy_view = wgpuTextureCreateView(
        engine.layer_mask_dummy_texture,
        nullptr);
    if (engine.layer_mask_dummy_view == nullptr) {
        return false;
    }
    engine.layer_analytic_mask_bind_group = create_layer_mask_bind_group(
        engine,
        engine.image_linear_sampler,
        engine.layer_mask_dummy_view,
        "ProGPU native analytic group mask bind group");
    if (engine.layer_analytic_mask_bind_group == nullptr) {
        return false;
    }
    ++engine.layer_mask_bind_group_generation;
    return true;
}

bool is_advanced_group_blend(std::uint32_t blend_mode) noexcept {
    return (blend_mode >= PROGPU_NATIVE_BLEND_MULTIPLY &&
            blend_mode <= PROGPU_NATIVE_BLEND_EXCLUSION) ||
        (blend_mode >= PROGPU_NATIVE_BLEND_OVERLAY &&
            blend_mode <= PROGPU_NATIVE_BLEND_LUMINOSITY);
}

bool configure_fixed_group_blend(
    std::uint32_t blend_mode,
    WGPUBlendState& blend) noexcept {
    blend = {};
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.operation = WGPUBlendOperation_Add;
    switch (blend_mode) {
        case PROGPU_NATIVE_BLEND_SRC_OVER:
            blend.color.srcFactor = WGPUBlendFactor_One;
            blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_One;
            blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_SRC:
            blend.color.srcFactor = WGPUBlendFactor_One;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_One;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        case PROGPU_NATIVE_BLEND_DST:
            blend.color.srcFactor = WGPUBlendFactor_Zero;
            blend.color.dstFactor = WGPUBlendFactor_One;
            blend.alpha.srcFactor = WGPUBlendFactor_Zero;
            blend.alpha.dstFactor = WGPUBlendFactor_One;
            return true;
        case PROGPU_NATIVE_BLEND_SRC_IN:
            blend.color.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        case PROGPU_NATIVE_BLEND_DST_IN:
            blend.color.srcFactor = WGPUBlendFactor_Zero;
            blend.color.dstFactor = WGPUBlendFactor_SrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_Zero;
            blend.alpha.dstFactor = WGPUBlendFactor_SrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_SRC_OUT:
            blend.color.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        case PROGPU_NATIVE_BLEND_DST_OUT:
            blend.color.srcFactor = WGPUBlendFactor_Zero;
            blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_Zero;
            blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_SRC_ATOP:
            blend.color.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_DST_ATOP:
            blend.color.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_SrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_SrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_XOR:
            blend.color.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            blend.alpha.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
            return true;
        case PROGPU_NATIVE_BLEND_DST_OVER:
            blend.color.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.color.dstFactor = WGPUBlendFactor_One;
            blend.alpha.srcFactor = WGPUBlendFactor_OneMinusDstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_One;
            return true;
        case PROGPU_NATIVE_BLEND_PLUS:
            blend.color.srcFactor = WGPUBlendFactor_One;
            blend.color.dstFactor = WGPUBlendFactor_One;
            blend.alpha.srcFactor = WGPUBlendFactor_One;
            blend.alpha.dstFactor = WGPUBlendFactor_One;
            return true;
        case PROGPU_NATIVE_BLEND_CLEAR:
            blend.color.srcFactor = WGPUBlendFactor_Zero;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_Zero;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        case PROGPU_NATIVE_BLEND_MODULATE:
            blend.color.srcFactor = WGPUBlendFactor_Dst;
            blend.color.dstFactor = WGPUBlendFactor_Zero;
            blend.alpha.srcFactor = WGPUBlendFactor_DstAlpha;
            blend.alpha.dstFactor = WGPUBlendFactor_Zero;
            return true;
        default:
            return false;
    }
}

WGPURenderPipeline get_or_create_fixed_group_blend_pipeline(
    progpu_native_engine& engine,
    std::uint32_t blend_mode,
    bool masked,
    bool& cache_hit) {
    if (blend_mode == PROGPU_NATIVE_BLEND_SRC_OVER) {
        cache_hit = true;
        return masked
            ? engine.layer_mask_pipeline
            : engine.layer_composite_pipeline;
    }
    if (blend_mode > PROGPU_NATIVE_BLEND_MODULATE ||
        is_advanced_group_blend(blend_mode)) {
        return nullptr;
    }
    auto& pipelines = masked
        ? engine.layer_mask_blend_pipelines
        : engine.layer_blend_pipelines;
    if (pipelines[blend_mode] != nullptr) {
        cache_hit = true;
        return pipelines[blend_mode];
    }
    cache_hit = false;
    if (!create_layer_resources(engine) ||
        (masked && !create_layer_mask_resources(engine))) {
        return nullptr;
    }

    std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.layer_mask_layout
    }};
    WGPUPipelineLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native fixed group-blend layout");
    layout_descriptor.bindGroupLayoutCount = masked ? 3U : 2U;
    layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &layout_descriptor);
    if (pipeline_layout == nullptr) {
        return nullptr;
    }

    const std::array<WGPUVertexAttribute, 7U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;

    WGPUBlendState blend{};
    if (!configure_fixed_group_blend(blend_mode, blend)) {
        wgpuPipelineLayoutRelease(pipeline_layout);
        return nullptr;
    }
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view(
        masked ? "fs_main" : "fs_main_unmasked");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native fixed group-blend pipeline");
    descriptor.layout = pipeline_layout;
    descriptor.vertex = vertex_state;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    pipelines[blend_mode] = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    return pipelines[blend_mode];
}

bool create_advanced_group_blend_resources(progpu_native_engine& engine) {
    if (engine.group_blend_pipeline != nullptr &&
        engine.group_blend_layout != nullptr &&
        engine.group_blend_uniform_buffer != nullptr) {
        return true;
    }
    if (engine.group_blend_shader != nullptr ||
        engine.group_blend_pipeline != nullptr ||
        engine.group_blend_layout != nullptr ||
        engine.group_blend_uniform_buffer != nullptr) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::group_blend_wgsl,
        progpu::native::generated::group_blend_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU shared GroupBlend.wgsl");
    engine.group_blend_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.group_blend_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 2U> entries{};
    entries[0].binding = 0U;
    entries[0].visibility = WGPUShaderStage_Fragment;
    entries[0].texture.sampleType = WGPUTextureSampleType_Float;
    entries[0].texture.viewDimension = WGPUTextureViewDimension_2D;
    entries[0].texture.multisampled = false;
    entries[1].binding = 1U;
    entries[1].visibility = WGPUShaderStage_Fragment;
    entries[1].buffer.type = WGPUBufferBindingType_Uniform;
    entries[1].buffer.minBindingSize = sizeof(gpu_group_blend_uniforms);
    WGPUBindGroupLayoutDescriptor bind_layout_descriptor{};
    bind_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend bind layout");
    bind_layout_descriptor.entryCount = entries.size();
    bind_layout_descriptor.entries = entries.data();
    engine.group_blend_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &bind_layout_descriptor);
    if (engine.group_blend_layout == nullptr) {
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &engine.group_blend_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.group_blend_shader;
    vertex_state.entryPoint =
        progpu::native::webgpu::string_view("vs_main");
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = nullptr;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.group_blend_shader;
    fragment.entryPoint =
        progpu::native::webgpu::string_view("fs_main");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology =
        WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.group_blend_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.group_blend_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor uniform_descriptor{};
    uniform_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend uniforms");
    uniform_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    uniform_descriptor.size = sizeof(gpu_group_blend_uniforms);
    engine.group_blend_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &uniform_descriptor);
    return engine.group_blend_uniform_buffer != nullptr;
}

bool ensure_advanced_group_blend_source(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    if (!create_advanced_group_blend_resources(engine)) {
        return false;
    }
    if (engine.group_blend_source_texture != nullptr &&
        engine.group_blend_source_width == width &&
        engine.group_blend_source_height == height) {
        return true;
    }

    WGPUTextureDescriptor texture_descriptor{};
    texture_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend source");
    texture_descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
    texture_descriptor.dimension = WGPUTextureDimension_2D;
    texture_descriptor.size = {width, height, 1U};
    texture_descriptor.format = engine.target_format;
    texture_descriptor.mipLevelCount = 1U;
    texture_descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(
        engine.device,
        &texture_descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    const std::array<WGPUBindGroupEntry, 2U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 1U, engine.group_blend_uniform_buffer, 0U,
            sizeof(gpu_group_blend_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor bind_group_descriptor{};
    bind_group_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend bind group");
    bind_group_descriptor.layout = engine.group_blend_layout;
    bind_group_descriptor.entryCount = entries.size();
    bind_group_descriptor.entries = entries.data();
    WGPUBindGroup bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &bind_group_descriptor);
    if (bind_group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    if (engine.group_blend_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.group_blend_bind_group);
    }
    if (engine.group_blend_source_view != nullptr) {
        wgpuTextureViewRelease(engine.group_blend_source_view);
    }
    if (engine.group_blend_source_texture != nullptr) {
        wgpuTextureDestroy(engine.group_blend_source_texture);
        wgpuTextureRelease(engine.group_blend_source_texture);
    }
    engine.group_blend_source_texture = texture;
    engine.group_blend_source_view = view;
    engine.group_blend_bind_group = bind_group;
    engine.group_blend_source_width = width;
    engine.group_blend_source_height = height;
    engine.group_blend_source_cache_valid = false;
    ++engine.group_blend_source_texture_generation;
    ++engine.group_blend_source_allocation_count;
    return true;
}

bool create_clip_chain_resources(progpu_native_engine& engine) {
    if (engine.clip_path_pipeline != nullptr &&
        engine.clip_compose_pipeline != nullptr &&
        engine.clip_compose_layout != nullptr &&
        engine.clip_sampler != nullptr) {
        return true;
    }
    if (!create_layer_mask_resources(engine) ||
        !create_path_resources(engine) ||
        engine.clip_compose_shader != nullptr ||
        engine.clip_path_pipeline != nullptr ||
        engine.clip_compose_pipeline != nullptr ||
        engine.clip_compose_layout != nullptr ||
        engine.clip_sampler != nullptr) {
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::clip_compose_wgsl,
        progpu::native::generated::clip_compose_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU shared ClipCompose.wgsl");
    engine.clip_compose_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.clip_compose_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> entries{};
    entries[0].binding = 0U;
    entries[0].visibility = WGPUShaderStage_Fragment;
    entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    for (std::uint32_t index = 1U; index <= 2U; ++index) {
        entries[index].binding = index;
        entries[index].visibility = WGPUShaderStage_Fragment;
        entries[index].texture.sampleType = WGPUTextureSampleType_Float;
        entries[index].texture.viewDimension = WGPUTextureViewDimension_2D;
        entries[index].texture.multisampled = false;
    }
    entries[3].binding = 3U;
    entries[3].visibility = WGPUShaderStage_Fragment;
    entries[3].buffer.type = WGPUBufferBindingType_Uniform;
    entries[3].buffer.hasDynamicOffset = true;
    entries[3].buffer.minBindingSize = sizeof(gpu_clip_compose_uniforms);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip layout");
    layout_descriptor.entryCount = entries.size();
    layout_descriptor.entries = entries.data();
    engine.clip_compose_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.clip_compose_layout == nullptr) {
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &engine.clip_compose_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 2U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 8U, 1U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(gpu_clip_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUColorTargetState target{};
    target.format = WGPUTextureFormat_R8Unorm;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState path_fragment{};
    path_fragment.module = engine.clip_compose_shader;
    path_fragment.entryPoint = progpu::native::webgpu::string_view("fs_path");
    path_fragment.targetCount = 1U;
    path_fragment.targets = &target;
    WGPURenderPipelineDescriptor path_descriptor{};
    path_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip path pipeline");
    path_descriptor.layout = pipeline_layout;
    path_descriptor.vertex.module = engine.clip_compose_shader;
    path_descriptor.vertex.entryPoint =
        progpu::native::webgpu::string_view("vs_path");
    path_descriptor.vertex.bufferCount = 1U;
    path_descriptor.vertex.buffers = &vertex_layout;
    path_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    path_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    path_descriptor.primitive.cullMode = WGPUCullMode_None;
    path_descriptor.multisample.count = 1U;
    path_descriptor.multisample.mask = 0xFFFFFFFFU;
    path_descriptor.fragment = &path_fragment;
    engine.clip_path_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &path_descriptor);

    WGPUFragmentState compose_fragment{};
    compose_fragment.module = engine.clip_compose_shader;
    compose_fragment.entryPoint =
        progpu::native::webgpu::string_view("fs_compose");
    compose_fragment.targetCount = 1U;
    compose_fragment.targets = &target;
    WGPURenderPipelineDescriptor compose_descriptor{};
    compose_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip composition pipeline");
    compose_descriptor.layout = pipeline_layout;
    compose_descriptor.vertex.module = engine.clip_compose_shader;
    compose_descriptor.vertex.entryPoint =
        progpu::native::webgpu::string_view("vs_compose");
    compose_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    compose_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    compose_descriptor.primitive.cullMode = WGPUCullMode_None;
    compose_descriptor.multisample.count = 1U;
    compose_descriptor.multisample.mask = 0xFFFFFFFFU;
    compose_descriptor.fragment = &compose_fragment;
    engine.clip_compose_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &compose_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.clip_path_pipeline == nullptr ||
        engine.clip_compose_pipeline == nullptr) {
        return false;
    }

    WGPUSamplerDescriptor sampler_descriptor{};
    sampler_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip atlas sampler");
    sampler_descriptor.addressModeU = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeV = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    sampler_descriptor.magFilter = WGPUFilterMode_Linear;
    sampler_descriptor.minFilter = WGPUFilterMode_Linear;
    sampler_descriptor.mipmapFilter = WGPUMipmapFilterMode_Nearest;
    sampler_descriptor.lodMinClamp = 0.0F;
    sampler_descriptor.lodMaxClamp = 0.0F;
    sampler_descriptor.maxAnisotropy = 1U;
    engine.clip_sampler = wgpuDeviceCreateSampler(
        engine.device,
        &sampler_descriptor);
    return engine.clip_sampler != nullptr;
}

bool ensure_clip_buffer(
    progpu_native_engine& engine,
    WGPUBuffer& buffer,
    std::uint64_t& capacity,
    std::uint64_t required,
    progpu::native::webgpu::buffer_usage_flags usage,
    const char* label) {
    if (buffer != nullptr && required <= capacity) {
        return true;
    }
    std::uint64_t replacement_size = std::max<std::uint64_t>(256U, capacity);
    while (replacement_size < required) {
        if (replacement_size >
            std::numeric_limits<std::uint64_t>::max() / 2U) {
            return false;
        }
        replacement_size *= 2U;
    }
    WGPUBufferDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.usage = usage;
    descriptor.size = replacement_size;
    WGPUBuffer replacement = wgpuDeviceCreateBuffer(
        engine.device,
        &descriptor);
    if (replacement == nullptr) {
        return false;
    }
    if (buffer != nullptr) {
        wgpuBufferDestroy(buffer);
        wgpuBufferRelease(buffer);
    }
    buffer = replacement;
    capacity = replacement_size;
    return true;
}

void release_clip_bind_groups(progpu_native_engine& engine) noexcept {
    for (auto& bind_group : engine.layer_clip_mask_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    for (auto& bind_group : engine.clip_compose_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    if (engine.clip_path_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.clip_path_bind_group);
        engine.clip_path_bind_group = nullptr;
    }
}

bool rebuild_clip_bind_groups(progpu_native_engine& engine) {
    release_clip_bind_groups(engine);
    const std::array<WGPUBindGroupEntry, 4U> path_entries{{
        {nullptr, 0U, nullptr, 0U, 0U, engine.clip_sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, engine.clip_atlas_view},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr, engine.clip_atlas_view},
        {nullptr, 3U, engine.clip_compose_uniform_buffer, 0U,
            sizeof(gpu_clip_compose_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor path_descriptor{};
    path_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained clip path bind group");
    path_descriptor.layout = engine.clip_compose_layout;
    path_descriptor.entryCount = path_entries.size();
    path_descriptor.entries = path_entries.data();
    engine.clip_path_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &path_descriptor);
    if (engine.clip_path_bind_group == nullptr) {
        return false;
    }

    for (std::size_t index = 0U; index < 2U; ++index) {
        const std::array<WGPUBindGroupEntry, 4U> entries{{
            {nullptr, 0U, nullptr, 0U, 0U, engine.clip_sampler, nullptr},
            {nullptr, 1U, nullptr, 0U, 0U, nullptr, engine.clip_node_view},
            {nullptr, 2U, nullptr, 0U, 0U, nullptr,
                engine.clip_accumulation_views[index]},
            {nullptr, 3U, engine.clip_compose_uniform_buffer, 0U,
                sizeof(gpu_clip_compose_uniforms), nullptr, nullptr}
        }};
        WGPUBindGroupDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained clip composition bind group");
        descriptor.layout = engine.clip_compose_layout;
        descriptor.entryCount = entries.size();
        descriptor.entries = entries.data();
        engine.clip_compose_bind_groups[index] =
            wgpuDeviceCreateBindGroup(engine.device, &descriptor);
        engine.layer_clip_mask_bind_groups[index] =
            create_layer_mask_bind_group(
                engine,
                engine.image_linear_sampler,
                engine.clip_accumulation_views[index],
                "ProGPU native retained clip final mask bind group");
        if (engine.clip_compose_bind_groups[index] == nullptr ||
            engine.layer_clip_mask_bind_groups[index] == nullptr) {
            return false;
        }
    }
    ++engine.layer_mask_bind_group_generation;
    return true;
}

bool ensure_clip_textures(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t atlas_size) {
    const bool atlas_changed = engine.clip_atlas_texture == nullptr ||
        engine.clip_atlas_size != atlas_size;
    const bool target_changed = engine.clip_node_texture == nullptr ||
        engine.clip_width != width || engine.clip_height != height;
    if (!atlas_changed && !target_changed) {
        return true;
    }
    release_clip_bind_groups(engine);

    if (atlas_changed) {
        WGPUTextureDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained clip atlas");
        descriptor.usage = WGPUTextureUsage_TextureBinding |
            WGPUTextureUsage_CopyDst;
        descriptor.dimension = WGPUTextureDimension_2D;
        descriptor.size = {atlas_size, atlas_size, 1U};
        descriptor.format = WGPUTextureFormat_R8Unorm;
        descriptor.mipLevelCount = 1U;
        descriptor.sampleCount = 1U;
        WGPUTexture texture = wgpuDeviceCreateTexture(
            engine.device,
            &descriptor);
        WGPUTextureView view = texture == nullptr
            ? nullptr
            : wgpuTextureCreateView(texture, nullptr);
        if (texture == nullptr || view == nullptr) {
            if (view != nullptr) wgpuTextureViewRelease(view);
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
            }
            return false;
        }
        if (engine.clip_atlas_view != nullptr) {
            wgpuTextureViewRelease(engine.clip_atlas_view);
        }
        if (engine.clip_atlas_texture != nullptr) {
            wgpuTextureDestroy(engine.clip_atlas_texture);
            wgpuTextureRelease(engine.clip_atlas_texture);
        }
        engine.clip_atlas_texture = texture;
        engine.clip_atlas_view = view;
        engine.clip_atlas_size = atlas_size;
        ++engine.clip_atlas_generation;
    }

    if (target_changed) {
        const auto create_texture = [&](const char* label) {
            WGPUTextureDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(label);
            descriptor.usage = WGPUTextureUsage_RenderAttachment |
                WGPUTextureUsage_TextureBinding;
            descriptor.dimension = WGPUTextureDimension_2D;
            descriptor.size = {width, height, 1U};
            descriptor.format = WGPUTextureFormat_R8Unorm;
            descriptor.mipLevelCount = 1U;
            descriptor.sampleCount = 1U;
            return wgpuDeviceCreateTexture(engine.device, &descriptor);
        };
        WGPUTexture node = create_texture(
            "ProGPU native retained clip node mask");
        WGPUTextureView node_view = node == nullptr
            ? nullptr
            : wgpuTextureCreateView(node, nullptr);
        std::array<WGPUTexture, 2U> accumulation{{
            create_texture("ProGPU native retained clip accumulation A"),
            create_texture("ProGPU native retained clip accumulation B")
        }};
        std::array<WGPUTextureView, 2U> accumulation_views{{
            accumulation[0] == nullptr
                ? nullptr
                : wgpuTextureCreateView(accumulation[0], nullptr),
            accumulation[1] == nullptr
                ? nullptr
                : wgpuTextureCreateView(accumulation[1], nullptr)
        }};
        if (node == nullptr || node_view == nullptr ||
            accumulation[0] == nullptr || accumulation[1] == nullptr ||
            accumulation_views[0] == nullptr ||
            accumulation_views[1] == nullptr) {
            if (node_view != nullptr) wgpuTextureViewRelease(node_view);
            if (node != nullptr) {
                wgpuTextureDestroy(node);
                wgpuTextureRelease(node);
            }
            for (std::size_t index = 0U; index < 2U; ++index) {
                if (accumulation_views[index] != nullptr) {
                    wgpuTextureViewRelease(accumulation_views[index]);
                }
                if (accumulation[index] != nullptr) {
                    wgpuTextureDestroy(accumulation[index]);
                    wgpuTextureRelease(accumulation[index]);
                }
            }
            return false;
        }
        if (engine.clip_node_view != nullptr) {
            wgpuTextureViewRelease(engine.clip_node_view);
        }
        if (engine.clip_node_texture != nullptr) {
            wgpuTextureDestroy(engine.clip_node_texture);
            wgpuTextureRelease(engine.clip_node_texture);
        }
        for (std::size_t index = 0U; index < 2U; ++index) {
            if (engine.clip_accumulation_views[index] != nullptr) {
                wgpuTextureViewRelease(
                    engine.clip_accumulation_views[index]);
            }
            if (engine.clip_accumulation_textures[index] != nullptr) {
                wgpuTextureDestroy(
                    engine.clip_accumulation_textures[index]);
                wgpuTextureRelease(
                    engine.clip_accumulation_textures[index]);
            }
        }
        engine.clip_node_texture = node;
        engine.clip_node_view = node_view;
        engine.clip_accumulation_textures = accumulation;
        engine.clip_accumulation_views = accumulation_views;
        engine.clip_width = width;
        engine.clip_height = height;
        ++engine.clip_texture_generation;
    }
    engine.clip_cache_valid = false;
    return true;
}

bool validate_native_path_segment(
    const progpu_native_path_segment& segment) noexcept {
    const bool is_arc =
        segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC;
    return segment.kind <= PROGPU_NATIVE_PATH_SEGMENT_ARC &&
        progpu::native::is_finite(segment.p0) &&
        progpu::native::is_finite(segment.p1) &&
        progpu::native::is_finite(segment.p2) &&
        progpu::native::is_finite(segment.p3) &&
        (!is_arc ||
            (segment.p3.x > 0.0F && segment.p3.y > 0.0F &&
             std::isfinite(std::bit_cast<float>(segment.pad0)) &&
             std::isfinite(std::bit_cast<float>(segment.pad1)) &&
             std::isfinite(std::bit_cast<float>(segment.pad2)))) &&
        (is_arc ||
            (segment.pad0 == 0U && segment.pad1 == 0U &&
             segment.pad2 == 0U));
}

bool rebuild_vector_clip_chain(
    progpu_native_engine& engine,
    const progpu_native_group_mask& mask,
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale) {
    const auto& chain = *mask.clip_chain;
    engine.last_layer_metrics.clip_path_count =
        static_cast<std::uint32_t>(chain.path_count);
    const bool cache_hit = engine.clip_cache_valid &&
        engine.clip_cached_revision == mask.revision &&
        engine.clip_cached_dpi_scale == dpi_scale &&
        engine.clip_width == width && engine.clip_height == height;
    engine.last_layer_metrics.clip_cache_hit = cache_hit ? 1U : 0U;
    if (cache_hit) {
        engine.last_layer_metrics.clip_texture_bytes =
            static_cast<std::uint64_t>(engine.clip_atlas_size) *
                engine.clip_atlas_size +
            static_cast<std::uint64_t>(width) * height * 3U;
        return true;
    }
    if (!create_clip_chain_resources(engine)) {
        return false;
    }

    try {
        for (std::size_t index = 0U; index < chain.segment_count; ++index) {
            if (!validate_native_path_segment(chain.segments[index])) {
                return false;
            }
        }

        std::vector<gpu_path_uniforms> path_uniforms;
        std::vector<gpu_path_record> path_records;
        std::vector<native_path_raster> rasters;
        std::vector<gpu_clip_vertex> vertices;
        std::vector<std::uint32_t> indices;
        std::vector<std::byte> compose_uniform_bytes;
        path_uniforms.reserve(chain.path_count);
        path_records.reserve(chain.path_count);
        rasters.reserve(chain.path_count);
        vertices.reserve(chain.path_count * 4U);
        indices.reserve(chain.path_count * 6U);
        compose_uniform_bytes.resize(chain.path_count * 256U);

        std::uint32_t required_atlas_size = engine.clip_atlas_size;
        std::uint32_t atlas_x = 2U;
        std::uint32_t atlas_y = 2U;
        std::uint32_t row_height = 0U;
        std::uint32_t output_offset = 0U;
        std::unordered_map<
            native_path_cache_key,
            std::size_t,
            native_path_cache_key_hash> retained_tiles;
        retained_tiles.reserve(chain.path_count);

        for (std::size_t index = 0U; index < chain.path_count; ++index) {
            const auto& path = chain.paths[index];
            if (path.segment_count == 0U ||
                path.segment_offset > chain.segment_count ||
                path.segment_count >
                    chain.segment_count - path.segment_offset ||
                !std::isfinite(path.min_x) ||
                !std::isfinite(path.min_y) ||
                !std::isfinite(path.max_x) ||
                !std::isfinite(path.max_y) ||
                path.max_x <= path.min_x || path.max_y <= path.min_y ||
                !progpu::native::is_finite(path.transform) ||
                path.fill_rule > PROGPU_NATIVE_FILL_RULE_EVEN_ODD ||
                (path.sample_grid != 4U && path.sample_grid != 8U) ||
                path.operation > PROGPU_NATIVE_CLIP_DIFFERENCE ||
                path.reserved != 0U) {
                return false;
            }
            float maximum_scale = 0.0F;
            float minimum_scale = 0.0F;
            if (!progpu::native::try_get_stroke_scales(
                    path.transform,
                    maximum_scale,
                    minimum_scale)) {
                return false;
            }
            (void)minimum_scale;
            const float raster_scale = maximum_scale * dpi_scale;
            if (!std::isfinite(raster_scale) || raster_scale <= 0.0F) {
                return false;
            }
            const float subpixel_x = quantize_subpixel_phase(
                path.transform.m31 * dpi_scale);
            const float subpixel_y = quantize_subpixel_phase(
                path.transform.m32 * dpi_scale);
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
                return false;
            }

            std::size_t raster_index = 0U;
            const auto retained = retained_tiles.find(cache_key);
            if (retained != retained_tiles.end()) {
                raster_index = retained->second;
            } else {
                const auto raster_width_u =
                    static_cast<std::uint32_t>(raster_width);
                const auto raster_height_u =
                    static_cast<std::uint32_t>(raster_height);
                while (raster_width_u + 4U > required_atlas_size &&
                       required_atlas_size < native_max_atlas_size) {
                    required_atlas_size *= 2U;
                }
                if (atlas_x + raster_width_u + 2U > required_atlas_size) {
                    atlas_x = 2U;
                    atlas_y += row_height + 2U;
                    row_height = 0U;
                }
                while (atlas_y + raster_height_u + 2U >
                           required_atlas_size &&
                       required_atlas_size < native_max_atlas_size) {
                    required_atlas_size *= 2U;
                }
                if (atlas_y + raster_height_u + 2U >
                    required_atlas_size) {
                    return false;
                }
                const std::uint32_t output_bytes_per_row = align_up(
                    raster_width_u,
                    webgpu_copy_row_alignment);
                output_offset = align_up(
                    output_offset,
                    webgpu_copy_row_alignment);
                const std::uint64_t next_output =
                    static_cast<std::uint64_t>(output_offset) +
                    static_cast<std::uint64_t>(output_bytes_per_row) *
                        raster_height_u;
                if (next_output >
                    std::numeric_limits<std::uint32_t>::max()) {
                    return false;
                }
                raster_index = rasters.size();
                rasters.push_back({
                    atlas_x,
                    atlas_y,
                    raster_width_u,
                    raster_height_u,
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
                    raster_width_u,
                    raster_height_u,
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
                atlas_x += raster_width_u + 2U;
                row_height = std::max(row_height, raster_height_u);
            }
            const auto& raster = rasters[raster_index];
            const float local_min_x = raster.raster_min_x / raster.scale_x;
            const float local_min_y = raster.raster_min_y / raster.scale_y;
            const float local_max_x =
                (raster.raster_min_x + raster.width) / raster.scale_x;
            const float local_max_y =
                (raster.raster_min_y + raster.height) / raster.scale_y;
            const std::array<progpu_native_point, 4U> local_points{{
                {local_min_x, local_min_y},
                {local_max_x, local_min_y},
                {local_max_x, local_max_y},
                {local_min_x, local_max_y}
            }};
            const std::array<progpu_native_point, 4U> atlas_points{{
                {raster.atlas_x + raster.subpixel_x,
                    raster.atlas_y + raster.subpixel_y},
                {raster.atlas_x + raster.width + raster.subpixel_x,
                    raster.atlas_y + raster.subpixel_y},
                {raster.atlas_x + raster.width + raster.subpixel_x,
                    raster.atlas_y + raster.height + raster.subpixel_y},
                {raster.atlas_x + raster.subpixel_x,
                    raster.atlas_y + raster.height + raster.subpixel_y}
            }};
            const std::uint32_t vertex_start =
                static_cast<std::uint32_t>(vertices.size());
            for (std::size_t corner = 0U; corner < 4U; ++corner) {
                float logical_x = 0.0F;
                float logical_y = 0.0F;
                progpu::native::transform_point(
                    path.transform,
                    local_points[corner].x,
                    local_points[corner].y,
                    logical_x,
                    logical_y);
                gpu_clip_vertex vertex{};
                vertex.position[0] =
                    2.0F * logical_x * dpi_scale /
                        static_cast<float>(width) -
                    1.0F;
                vertex.position[1] =
                    1.0F - 2.0F * logical_y * dpi_scale /
                        static_cast<float>(height);
                vertex.atlas_uv[0] =
                    atlas_points[corner].x /
                    static_cast<float>(required_atlas_size);
                vertex.atlas_uv[1] =
                    atlas_points[corner].y /
                    static_cast<float>(required_atlas_size);
                vertices.push_back(vertex);
            }
            indices.insert(
                indices.end(),
                {vertex_start,
                 vertex_start + 1U,
                 vertex_start + 2U,
                 vertex_start,
                 vertex_start + 2U,
                 vertex_start + 3U});
            const gpu_clip_compose_uniforms compose{
                path.operation,
                index == 0U ? 1U : 0U,
                width,
                height
            };
            std::memcpy(
                compose_uniform_bytes.data() + index * 256U,
                &compose,
                sizeof(compose));
        }

        const std::uint64_t vertex_bytes =
            vertices.size() * sizeof(gpu_clip_vertex);
        const std::uint64_t index_bytes =
            indices.size() * sizeof(std::uint32_t);
        const std::uint64_t compose_bytes =
            compose_uniform_bytes.size();
        WGPUBuffer old_vertex = engine.clip_vertex_buffer;
        WGPUBuffer old_index = engine.clip_index_buffer;
        WGPUBuffer old_uniform = engine.clip_compose_uniform_buffer;
        if (!ensure_clip_buffer(
                engine,
                engine.clip_vertex_buffer,
                engine.clip_vertex_buffer_size,
                vertex_bytes,
                WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst,
                "ProGPU native retained clip vertices") ||
            !ensure_clip_buffer(
                engine,
                engine.clip_index_buffer,
                engine.clip_index_buffer_size,
                index_bytes,
                WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst,
                "ProGPU native retained clip indices") ||
            !ensure_clip_buffer(
                engine,
                engine.clip_compose_uniform_buffer,
                engine.clip_compose_uniform_buffer_size,
                compose_bytes,
                WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst,
                "ProGPU native retained clip composition uniforms") ||
            !ensure_clip_textures(
                engine,
                width,
                height,
                required_atlas_size)) {
            return false;
        }
        const bool binding_resources_changed =
            old_vertex != engine.clip_vertex_buffer ||
            old_index != engine.clip_index_buffer ||
            old_uniform != engine.clip_compose_uniform_buffer ||
            engine.clip_path_bind_group == nullptr;
        if (binding_resources_changed &&
            !rebuild_clip_bind_groups(engine)) {
            return false;
        }
        if (engine.clip_path_bind_group == nullptr &&
            !rebuild_clip_bind_groups(engine)) {
            return false;
        }

        wgpuQueueWriteBuffer(
            engine.queue,
            engine.clip_vertex_buffer,
            0U,
            vertices.data(),
            vertex_bytes);
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.clip_index_buffer,
            0U,
            indices.data(),
            index_bytes);
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.clip_compose_uniform_buffer,
            0U,
            compose_uniform_bytes.data(),
            compose_bytes);

        path_raster_resources temporary;
        const auto create_buffer = [&engine](
            const char* label,
            std::uint64_t size,
            progpu::native::webgpu::buffer_usage_flags usage) {
            WGPUBufferDescriptor descriptor{};
            descriptor.label = progpu::native::webgpu::string_view(label);
            descriptor.size = std::max<std::uint64_t>(size, 4U);
            descriptor.usage = usage;
            return wgpuDeviceCreateBuffer(engine.device, &descriptor);
        };
        const std::uint64_t path_uniform_bytes =
            path_uniforms.size() * sizeof(gpu_path_uniforms);
        const std::uint64_t path_record_bytes =
            path_records.size() * sizeof(gpu_path_record);
        const std::uint64_t path_segment_bytes =
            chain.segment_count * sizeof(progpu_native_path_segment);
        temporary.uniforms = create_buffer(
            "ProGPU native clip path uniforms",
            path_uniform_bytes,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.records = create_buffer(
            "ProGPU native clip path records",
            path_record_bytes,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.segments = create_buffer(
            "ProGPU native clip path segments",
            path_segment_bytes,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst);
        temporary.coverage = create_buffer(
            "ProGPU native clip coverage staging",
            output_offset,
            WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc);
        if (temporary.uniforms == nullptr || temporary.records == nullptr ||
            temporary.segments == nullptr || temporary.coverage == nullptr) {
            return false;
        }
        wgpuQueueWriteBuffer(engine.queue, temporary.uniforms, 0U,
            path_uniforms.data(), path_uniform_bytes);
        wgpuQueueWriteBuffer(engine.queue, temporary.records, 0U,
            path_records.data(), path_record_bytes);
        wgpuQueueWriteBuffer(engine.queue, temporary.segments, 0U,
            chain.segments, path_segment_bytes);
        const std::array<WGPUBindGroupEntry, 4U> raster_entries{{
            {nullptr, 0U, temporary.uniforms, 0U,
                path_uniform_bytes, nullptr, nullptr},
            {nullptr, 1U, temporary.records, 0U,
                path_record_bytes, nullptr, nullptr},
            {nullptr, 2U, temporary.segments, 0U,
                path_segment_bytes, nullptr, nullptr},
            {nullptr, 3U, temporary.coverage, 0U,
                output_offset, nullptr, nullptr}
        }};
        WGPUBindGroupDescriptor raster_descriptor{};
        raster_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained clip raster bind group");
        raster_descriptor.layout = engine.path_raster_layout;
        raster_descriptor.entryCount = raster_entries.size();
        raster_descriptor.entries = raster_entries.data();
        temporary.bind_group = wgpuDeviceCreateBindGroup(
            engine.device,
            &raster_descriptor);
        if (temporary.bind_group == nullptr) {
            return false;
        }

        WGPUCommandEncoderDescriptor encoder_descriptor{};
        encoder_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained clip encoder");
        WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
            engine.device,
            &encoder_descriptor);
        if (encoder == nullptr) {
            return false;
        }
        std::uint32_t workgroups_x = 0U;
        std::uint32_t workgroups_y = 0U;
        for (const auto& raster : rasters) {
            workgroups_x = std::max(
                workgroups_x,
                (raster.width + 63U) / 64U);
            workgroups_y = std::max(
                workgroups_y,
                (raster.height + 15U) / 16U);
        }
        WGPUComputePassDescriptor compute_descriptor{};
        compute_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained clip coverage pass");
        WGPUComputePassEncoder compute =
            wgpuCommandEncoderBeginComputePass(encoder, &compute_descriptor);
        if (compute == nullptr) {
            wgpuCommandEncoderRelease(encoder);
            return false;
        }
        wgpuComputePassEncoderSetPipeline(
            compute,
            engine.path_raster_pipeline);
        wgpuComputePassEncoderSetBindGroup(
            compute,
            0U,
            temporary.bind_group,
            0U,
            nullptr);
        wgpuComputePassEncoderDispatchWorkgroups(
            compute,
            workgroups_x,
            workgroups_y,
            static_cast<std::uint32_t>(rasters.size()));
        wgpuComputePassEncoderEnd(compute);
        wgpuComputePassEncoderRelease(compute);
        for (const auto& raster : rasters) {
            progpu::native::webgpu::image_copy_buffer source{};
            source.buffer = temporary.coverage;
            source.layout.offset = raster.output_offset;
            source.layout.bytesPerRow = raster.output_bytes_per_row;
            source.layout.rowsPerImage = raster.height;
            progpu::native::webgpu::image_copy_texture destination{};
            destination.texture = engine.clip_atlas_texture;
            destination.origin = {raster.atlas_x, raster.atlas_y, 0U};
            destination.aspect = WGPUTextureAspect_All;
            const WGPUExtent3D extent{raster.width, raster.height, 1U};
            wgpuCommandEncoderCopyBufferToTexture(
                encoder,
                &source,
                &destination,
                &extent);
        }

        for (std::uint32_t index = 0U;
             index < static_cast<std::uint32_t>(chain.path_count);
             ++index) {
            WGPURenderPassColorAttachment node_attachment{};
            progpu::native::webgpu::initialize_color_attachment(
                node_attachment);
            node_attachment.view = engine.clip_node_view;
            node_attachment.loadOp = WGPULoadOp_Clear;
            node_attachment.storeOp = WGPUStoreOp_Store;
            node_attachment.clearValue = WGPUColor{0.0, 0.0, 0.0, 0.0};
            WGPURenderPassDescriptor node_descriptor{};
            node_descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU native retained clip node pass");
            node_descriptor.colorAttachmentCount = 1U;
            node_descriptor.colorAttachments = &node_attachment;
            WGPURenderPassEncoder node_pass =
                wgpuCommandEncoderBeginRenderPass(
                    encoder,
                    &node_descriptor);
            if (node_pass == nullptr) {
                wgpuCommandEncoderRelease(encoder);
                return false;
            }
            const std::uint32_t zero_offset = 0U;
            wgpuRenderPassEncoderSetPipeline(
                node_pass,
                engine.clip_path_pipeline);
            wgpuRenderPassEncoderSetBindGroup(
                node_pass,
                0U,
                engine.clip_path_bind_group,
                1U,
                &zero_offset);
            wgpuRenderPassEncoderSetVertexBuffer(
                node_pass,
                0U,
                engine.clip_vertex_buffer,
                0U,
                vertex_bytes);
            wgpuRenderPassEncoderSetIndexBuffer(
                node_pass,
                engine.clip_index_buffer,
                WGPUIndexFormat_Uint32,
                0U,
                index_bytes);
            wgpuRenderPassEncoderDrawIndexed(
                node_pass,
                6U,
                1U,
                index * 6U,
                0,
                0U);
            wgpuRenderPassEncoderEnd(node_pass);
            wgpuRenderPassEncoderRelease(node_pass);

            const std::uint32_t destination_index = index % 2U;
            const std::uint32_t previous_index = 1U - destination_index;
            WGPURenderPassColorAttachment compose_attachment{};
            progpu::native::webgpu::initialize_color_attachment(
                compose_attachment);
            compose_attachment.view =
                engine.clip_accumulation_views[destination_index];
            compose_attachment.loadOp = WGPULoadOp_Clear;
            compose_attachment.storeOp = WGPUStoreOp_Store;
            compose_attachment.clearValue = WGPUColor{0.0, 0.0, 0.0, 0.0};
            WGPURenderPassDescriptor compose_descriptor{};
            compose_descriptor.label = progpu::native::webgpu::string_view(
                "ProGPU native retained clip composition pass");
            compose_descriptor.colorAttachmentCount = 1U;
            compose_descriptor.colorAttachments = &compose_attachment;
            WGPURenderPassEncoder compose_pass =
                wgpuCommandEncoderBeginRenderPass(
                    encoder,
                    &compose_descriptor);
            if (compose_pass == nullptr) {
                wgpuCommandEncoderRelease(encoder);
                return false;
            }
            const std::uint32_t dynamic_offset = index * 256U;
            wgpuRenderPassEncoderSetPipeline(
                compose_pass,
                engine.clip_compose_pipeline);
            wgpuRenderPassEncoderSetBindGroup(
                compose_pass,
                0U,
                engine.clip_compose_bind_groups[previous_index],
                1U,
                &dynamic_offset);
            wgpuRenderPassEncoderDraw(
                compose_pass,
                3U,
                1U,
                0U,
                0U);
            wgpuRenderPassEncoderEnd(compose_pass);
            wgpuRenderPassEncoderRelease(compose_pass);
        }

        WGPUCommandBufferDescriptor command_descriptor{};
        command_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained clip commands");
        WGPUCommandBuffer command = wgpuCommandEncoderFinish(
            encoder,
            &command_descriptor);
        wgpuCommandEncoderRelease(encoder);
        if (command == nullptr) {
            return false;
        }
        engine.submit(command);
        wgpuCommandBufferRelease(command);

        engine.clip_cached_revision = mask.revision;
        engine.clip_cached_dpi_scale = dpi_scale;
        engine.clip_final_index = static_cast<std::uint32_t>(
            (chain.path_count - 1U) % 2U);
        engine.clip_cache_valid = true;
        engine.last_layer_metrics.clip_rasterized_path_count =
            static_cast<std::uint32_t>(rasters.size());
        engine.last_layer_metrics.clip_pass_count =
            1U + static_cast<std::uint32_t>(chain.path_count) * 2U;
        engine.last_layer_metrics.clip_path_upload_bytes =
            path_uniform_bytes + path_record_bytes + path_segment_bytes +
            vertex_bytes + index_bytes + compose_bytes;
        engine.last_layer_metrics.clip_coverage_staging_bytes = output_offset;
        engine.last_layer_metrics.clip_texture_bytes =
            static_cast<std::uint64_t>(required_atlas_size) *
                required_atlas_size +
            static_cast<std::uint64_t>(width) * height * 3U;
        return true;
    } catch (const std::bad_alloc&) {
        return false;
    }
}

bool update_layer_external_mask(
    progpu_native_engine& engine,
    const progpu_native_group_mask& mask,
    bool& replaced) {
    WGPUTextureView view = reinterpret_cast<WGPUTextureView>(
        mask.external_view);
    replaced = engine.layer_external_mask_view == nullptr ||
        engine.layer_external_mask_view != view ||
        engine.layer_external_mask_width != mask.width ||
        engine.layer_external_mask_height != mask.height;
    if (!replaced) {
        return true;
    }

    progpu::native::webgpu::texture_view_add_ref(view);
    WGPUBindGroup nearest = create_layer_mask_bind_group(
        engine,
        engine.image_nearest_sampler,
        view,
        "ProGPU native nearest common group mask bind group");
    WGPUBindGroup linear = create_layer_mask_bind_group(
        engine,
        engine.image_linear_sampler,
        view,
        "ProGPU native linear common group mask bind group");
    if (nearest == nullptr || linear == nullptr) {
        if (linear != nullptr) wgpuBindGroupRelease(linear);
        if (nearest != nullptr) wgpuBindGroupRelease(nearest);
        wgpuTextureViewRelease(view);
        return false;
    }
    if (engine.layer_external_mask_linear_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.layer_external_mask_linear_bind_group);
    }
    if (engine.layer_external_mask_nearest_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.layer_external_mask_nearest_bind_group);
    }
    if (engine.layer_external_mask_view != nullptr) {
        wgpuTextureViewRelease(engine.layer_external_mask_view);
    }
    engine.layer_external_mask_view = view;
    engine.layer_external_mask_nearest_bind_group = nearest;
    engine.layer_external_mask_linear_bind_group = linear;
    engine.layer_external_mask_width = mask.width;
    engine.layer_external_mask_height = mask.height;
    ++engine.layer_mask_bind_group_generation;
    return true;
}

bool create_rounded_group_mask_uniforms(
    const progpu_native_group_mask& mask,
    float dpi_scale,
    gpu_mask_sampling_uniforms& uniforms) noexcept {
    const double m11 = static_cast<double>(mask.transform.m11) * dpi_scale;
    const double m12 = static_cast<double>(mask.transform.m12) * dpi_scale;
    const double m21 = static_cast<double>(mask.transform.m21) * dpi_scale;
    const double m22 = static_cast<double>(mask.transform.m22) * dpi_scale;
    const double m31 = static_cast<double>(mask.transform.m31) * dpi_scale;
    const double m32 = static_cast<double>(mask.transform.m32) * dpi_scale;
    const double determinant = m11 * m22 - m12 * m21;
    if (!std::isfinite(determinant) || std::abs(determinant) <= 0.000001) {
        return false;
    }
    const double inverse = 1.0 / determinant;
    const double inverse_m11 = m22 * inverse;
    const double inverse_m12 = -m12 * inverse;
    const double inverse_m21 = -m21 * inverse;
    const double inverse_m22 = m11 * inverse;
    const double inverse_m31 = (m21 * m32 - m22 * m31) * inverse;
    const double inverse_m32 = (m12 * m31 - m11 * m32) * inverse;
    const std::array<double, 6U> inverse_values{
        inverse_m11,
        inverse_m12,
        inverse_m21,
        inverse_m22,
        inverse_m31,
        inverse_m32
    };
    if (!std::ranges::all_of(inverse_values, [](double value) {
            return std::isfinite(value) &&
                value >= -std::numeric_limits<float>::max() &&
                value <= std::numeric_limits<float>::max();
        })) {
        return false;
    }

    uniforms.coordinate0[0] = static_cast<float>(inverse_m11);
    uniforms.coordinate0[1] = static_cast<float>(inverse_m21);
    uniforms.coordinate0[2] = static_cast<float>(inverse_m31);
    uniforms.coordinate1[0] = static_cast<float>(inverse_m12);
    uniforms.coordinate1[1] = static_cast<float>(inverse_m22);
    uniforms.coordinate1[2] = static_cast<float>(inverse_m32);
    uniforms.bounds[0] = mask.bounds.x;
    uniforms.bounds[1] = mask.bounds.y;
    uniforms.bounds[2] = mask.bounds.x + mask.bounds.width;
    uniforms.bounds[3] = mask.bounds.y + mask.bounds.height;
    std::copy_n(
        mask.corner_radii_x,
        4U,
        uniforms.corner_radii_x);
    std::copy_n(
        mask.corner_radii_y,
        4U,
        uniforms.corner_radii_y);
    uniforms.options[0] = 2.0F;
    uniforms.options[1] = mask.opacity;
    return true;
}

bool update_layer_group_mask(
    progpu_native_engine& engine,
    const resolved_draw_state& draw_state,
    float dpi_scale,
    bool& uploaded_uniforms) {
    uploaded_uniforms = false;
    const bool resources_existed = engine.layer_mask_pipeline != nullptr;
    if (!draw_state.has_group_mask || !create_layer_mask_resources(engine)) {
        return !draw_state.has_group_mask;
    }

    const auto& mask = draw_state.group_mask;
    gpu_mask_sampling_uniforms uniforms{};
    bool binding_replaced = false;
    if (mask.kind == PROGPU_NATIVE_GROUP_MASK_TEXTURE) {
        if (!update_layer_external_mask(
                engine,
                mask,
                binding_replaced)) {
            return false;
        }
        uniforms.coordinate0[0] =
            mask.destination_rect.x * dpi_scale;
        uniforms.coordinate0[1] =
            mask.destination_rect.y * dpi_scale;
        uniforms.coordinate1[0] = 1.0F /
            (mask.destination_rect.width * dpi_scale);
        uniforms.coordinate1[1] = 1.0F /
            (mask.destination_rect.height * dpi_scale);
        uniforms.options[0] = 1.0F;
    } else if (mask.kind == PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN) {
        const bool was_cache_valid = engine.clip_cache_valid &&
            engine.clip_cached_revision == mask.revision &&
            engine.clip_cached_dpi_scale == dpi_scale &&
            engine.clip_width == engine.layer_width &&
            engine.clip_height == engine.layer_height;
        if (!rebuild_vector_clip_chain(
                engine,
                mask,
                engine.layer_width,
                engine.layer_height,
                dpi_scale)) {
            return false;
        }
        binding_replaced = !was_cache_valid;
        uniforms.coordinate1[0] =
            1.0F / static_cast<float>(engine.layer_width);
        uniforms.coordinate1[1] =
            1.0F / static_cast<float>(engine.layer_height);
        uniforms.options[0] = 1.0F;
    } else if (!create_rounded_group_mask_uniforms(
            mask,
            dpi_scale,
            uniforms)) {
        return false;
    }

    if (!engine.layer_mask_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_layer_mask_uniforms,
            &uniforms,
            sizeof(uniforms)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.layer_mask_uniform_buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        engine.cached_layer_mask_uniforms = uniforms;
        engine.layer_mask_uniform_cache_valid = true;
        uploaded_uniforms = true;
    }
    engine.last_layer_metrics.mask_kind = mask.kind;
    engine.last_layer_metrics.mask_revision = mask.revision;
    engine.last_layer_metrics.mask_bind_group_generation =
        engine.layer_mask_bind_group_generation;
    engine.last_layer_metrics.mask_bind_group_cache_hit =
        resources_existed && !binding_replaced ? 1U : 0U;
    engine.last_layer_metrics.mask_uniform_upload_bytes =
        uploaded_uniforms ? sizeof(uniforms) : 0U;
    return true;
}

bool ensure_layer_texture(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    if (engine.layer_texture != nullptr &&
        engine.layer_width == width && engine.layer_height == height) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native pooled group layer");
    descriptor.usage = WGPUTextureUsage_RenderAttachment |
        WGPUTextureUsage_TextureBinding;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = engine.target_format;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    WGPUTexture texture = wgpuDeviceCreateTexture(
        engine.device,
        &descriptor);
    if (texture == nullptr) {
        return false;
    }
    WGPUTextureView view = wgpuTextureCreateView(texture, nullptr);
    if (view == nullptr) {
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    WGPUBindGroup bind_group = create_image_texture_bind_group(
        engine,
        engine.image_linear_sampler,
        view,
        "ProGPU native pooled group layer bind group");
    if (bind_group == nullptr) {
        wgpuTextureViewRelease(view);
        wgpuTextureDestroy(texture);
        wgpuTextureRelease(texture);
        return false;
    }
    if (engine.layer_texture_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.layer_texture_bind_group);
    }
    if (engine.layer_texture_view != nullptr) {
        wgpuTextureViewRelease(engine.layer_texture_view);
    }
    if (engine.layer_texture != nullptr) {
        wgpuTextureDestroy(engine.layer_texture);
        wgpuTextureRelease(engine.layer_texture);
    }
    engine.layer_texture = texture;
    engine.layer_texture_view = view;
    engine.layer_texture_bind_group = bind_group;
    engine.layer_width = width;
    engine.layer_height = height;
    engine.layer_content_cache_valid = false;
    engine.layer_vertex_cache_valid = false;
    ++engine.layer_texture_generation;
    ++engine.layer_allocation_count;
    return true;
}

WGPUBindGroup create_effect_blur_bind_group(
    progpu_native_engine& engine,
    WGPUTextureView input,
    WGPUTextureView output,
    WGPUBuffer uniforms,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, nullptr, input},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, output},
        {nullptr, 2U, uniforms, 0U,
            sizeof(gpu_gaussian_blur_params), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.effect_blur_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_gaussian_effect_resources(progpu_native_engine& engine) {
    if (engine.effect_blur_horizontal_pipeline != nullptr &&
        engine.effect_blur_vertical_pipeline != nullptr &&
        engine.effect_blur_layout != nullptr) {
        return true;
    }
    if (engine.effect_blur_horizontal_shader != nullptr ||
        engine.effect_blur_vertical_shader != nullptr ||
        engine.effect_blur_horizontal_pipeline != nullptr ||
        engine.effect_blur_vertical_pipeline != nullptr ||
        engine.effect_blur_layout != nullptr ||
        engine.effect_blur_horizontal_uniform_buffer != nullptr ||
        engine.effect_blur_vertical_uniform_buffer != nullptr) {
        engine.release_effect_resources();
    }

    progpu::native::webgpu::wgsl_source horizontal_wgsl(
        progpu::native::generated::gaussian_blur_horizontal_wgsl,
        progpu::native::generated::gaussian_blur_horizontal_wgsl_size);
    WGPUShaderModuleDescriptor horizontal_descriptor{};
    horizontal_descriptor.nextInChain = horizontal_wgsl.chain();
    horizontal_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU shared GaussianBlurHorizontal.wgsl");
    engine.effect_blur_horizontal_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &horizontal_descriptor);
    progpu::native::webgpu::wgsl_source vertical_wgsl(
        progpu::native::generated::gaussian_blur_vertical_wgsl,
        progpu::native::generated::gaussian_blur_vertical_wgsl_size);
    WGPUShaderModuleDescriptor vertical_descriptor{};
    vertical_descriptor.nextInChain = vertical_wgsl.chain();
    vertical_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU shared GaussianBlurVertical.wgsl");
    engine.effect_blur_vertical_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &vertical_descriptor);
    if (engine.effect_blur_horizontal_shader == nullptr ||
        engine.effect_blur_vertical_shader == nullptr) {
        engine.release_effect_resources();
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 3U> entries{};
    entries[0].binding = 0U;
    entries[0].visibility = WGPUShaderStage_Compute;
    entries[0].texture.sampleType = WGPUTextureSampleType_Float;
    entries[0].texture.viewDimension = WGPUTextureViewDimension_2D;
    entries[0].texture.multisampled = false;
    entries[1].binding = 1U;
    entries[1].visibility = WGPUShaderStage_Compute;
    entries[1].storageTexture.access = WGPUStorageTextureAccess_WriteOnly;
    entries[1].storageTexture.format = WGPUTextureFormat_RGBA8Unorm;
    entries[1].storageTexture.viewDimension = WGPUTextureViewDimension_2D;
    entries[2].binding = 2U;
    entries[2].visibility = WGPUShaderStage_Compute;
    entries[2].buffer.type = WGPUBufferBindingType_Uniform;
    entries[2].buffer.minBindingSize = sizeof(gpu_gaussian_blur_params);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native Gaussian group-effect layout");
    layout_descriptor.entryCount = entries.size();
    layout_descriptor.entries = entries.data();
    engine.effect_blur_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.effect_blur_layout == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native Gaussian group-effect pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts = &engine.effect_blur_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    WGPUComputePipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.compute.entryPoint =
        progpu::native::webgpu::string_view("main");
    pipeline_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native horizontal Gaussian group effect");
    pipeline_descriptor.compute.module =
        engine.effect_blur_horizontal_shader;
    engine.effect_blur_horizontal_pipeline =
        wgpuDeviceCreateComputePipeline(engine.device, &pipeline_descriptor);
    pipeline_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native vertical Gaussian group effect");
    pipeline_descriptor.compute.module = engine.effect_blur_vertical_shader;
    engine.effect_blur_vertical_pipeline =
        wgpuDeviceCreateComputePipeline(engine.device, &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.effect_blur_horizontal_pipeline == nullptr ||
        engine.effect_blur_vertical_pipeline == nullptr) {
        engine.release_effect_resources();
        return false;
    }

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(gpu_gaussian_blur_params);
    buffer_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native horizontal Gaussian effect uniforms");
    engine.effect_blur_horizontal_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    buffer_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native vertical Gaussian effect uniforms");
    engine.effect_blur_vertical_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    if (engine.effect_blur_horizontal_uniform_buffer == nullptr ||
        engine.effect_blur_vertical_uniform_buffer == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    return true;
}

bool create_drop_shadow_effect_resources(progpu_native_engine& engine) {
    if (engine.effect_drop_shadow_pipeline != nullptr &&
        engine.effect_drop_shadow_layout != nullptr &&
        engine.effect_drop_shadow_uniform_buffer != nullptr) {
        return true;
    }
    if (engine.effect_drop_shadow_shader != nullptr ||
        engine.effect_drop_shadow_pipeline != nullptr ||
        engine.effect_drop_shadow_layout != nullptr ||
        engine.effect_drop_shadow_uniform_buffer != nullptr) {
        engine.release_effect_resources();
        return false;
    }

    progpu::native::webgpu::wgsl_source wgsl(
        progpu::native::generated::group_drop_shadow_compose_wgsl,
        progpu::native::generated::group_drop_shadow_compose_wgsl_size);
    WGPUShaderModuleDescriptor shader_descriptor{};
    shader_descriptor.nextInChain = wgsl.chain();
    shader_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU shared GroupDropShadowCompose.wgsl");
    engine.effect_drop_shadow_shader = wgpuDeviceCreateShaderModule(
        engine.device,
        &shader_descriptor);
    if (engine.effect_drop_shadow_shader == nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 4U> entries{};
    for (std::uint32_t index = 0U; index < 2U; ++index) {
        entries[index].binding = index;
        entries[index].visibility = WGPUShaderStage_Compute;
        entries[index].texture.sampleType = WGPUTextureSampleType_Float;
        entries[index].texture.viewDimension = WGPUTextureViewDimension_2D;
        entries[index].texture.multisampled = false;
    }
    entries[2].binding = 2U;
    entries[2].visibility = WGPUShaderStage_Compute;
    entries[2].storageTexture.access = WGPUStorageTextureAccess_WriteOnly;
    entries[2].storageTexture.format = WGPUTextureFormat_RGBA8Unorm;
    entries[2].storageTexture.viewDimension = WGPUTextureViewDimension_2D;
    entries[3].binding = 3U;
    entries[3].visibility = WGPUShaderStage_Compute;
    entries[3].buffer.type = WGPUBufferBindingType_Uniform;
    entries[3].buffer.minBindingSize = sizeof(gpu_drop_shadow_params);
    WGPUBindGroupLayoutDescriptor layout_descriptor{};
    layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group-effect layout");
    layout_descriptor.entryCount = entries.size();
    layout_descriptor.entries = entries.data();
    engine.effect_drop_shadow_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &layout_descriptor);
    if (engine.effect_drop_shadow_layout == nullptr) {
        engine.release_effect_resources();
        return false;
    }

    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group-effect pipeline layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = 1U;
    pipeline_layout_descriptor.bindGroupLayouts =
        &engine.effect_drop_shadow_layout;
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    WGPUComputePipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group effect");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.compute.module = engine.effect_drop_shadow_shader;
    pipeline_descriptor.compute.entryPoint =
        progpu::native::webgpu::string_view("main");
    engine.effect_drop_shadow_pipeline = wgpuDeviceCreateComputePipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.effect_drop_shadow_pipeline == nullptr) {
        engine.release_effect_resources();
        return false;
    }

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group-effect uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform |
        WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(gpu_drop_shadow_params);
    engine.effect_drop_shadow_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    if (engine.effect_drop_shadow_uniform_buffer == nullptr) {
        engine.release_effect_resources();
        return false;
    }
    return true;
}

bool ensure_drop_shadow_effect_bindings(progpu_native_engine& engine) {
    if (engine.effect_drop_shadow_bind_group != nullptr &&
        engine.effect_drop_shadow_output_bind_group != nullptr) {
        return true;
    }
    const std::array<WGPUBindGroupEntry, 4U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, nullptr, engine.layer_texture_view},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr,
            engine.effect_texture_views[1]},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr,
            engine.effect_texture_views[0]},
        {nullptr, 3U, engine.effect_drop_shadow_uniform_buffer, 0U,
            sizeof(gpu_drop_shadow_params), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native drop-shadow group-effect binding");
    descriptor.layout = engine.effect_drop_shadow_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    engine.effect_drop_shadow_bind_group = wgpuDeviceCreateBindGroup(
        engine.device,
        &descriptor);
    engine.effect_drop_shadow_output_bind_group =
        create_image_texture_bind_group(
            engine,
            engine.image_linear_sampler,
            engine.effect_texture_views[0],
            "ProGPU native drop-shadow group-effect output binding");
    if (engine.effect_drop_shadow_bind_group == nullptr ||
        engine.effect_drop_shadow_output_bind_group == nullptr) {
        if (engine.effect_drop_shadow_output_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.effect_drop_shadow_output_bind_group);
            engine.effect_drop_shadow_output_bind_group = nullptr;
        }
        if (engine.effect_drop_shadow_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.effect_drop_shadow_bind_group);
            engine.effect_drop_shadow_bind_group = nullptr;
        }
        return false;
    }
    return true;
}

bool ensure_gaussian_effect_textures(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    if (engine.effect_textures[0] != nullptr &&
        engine.effect_width == width && engine.effect_height == height &&
        engine.effect_blur_horizontal_bind_group != nullptr &&
        engine.effect_blur_vertical_bind_group != nullptr &&
        engine.effect_output_bind_group != nullptr) {
        return true;
    }

    WGPUTextureDescriptor descriptor{};
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_StorageBinding;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    std::array<WGPUTexture, 2U> textures{};
    std::array<WGPUTextureView, 2U> views{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native Gaussian group-effect horizontal texture");
    textures[0] = wgpuDeviceCreateTexture(engine.device, &descriptor);
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native Gaussian group-effect vertical texture");
    textures[1] = wgpuDeviceCreateTexture(engine.device, &descriptor);
    if (textures[0] == nullptr || textures[1] == nullptr) {
        for (auto texture : textures) {
            if (texture != nullptr) {
                wgpuTextureDestroy(texture);
                wgpuTextureRelease(texture);
            }
        }
        return false;
    }
    views[0] = wgpuTextureCreateView(textures[0], nullptr);
    views[1] = wgpuTextureCreateView(textures[1], nullptr);
    if (views[0] == nullptr || views[1] == nullptr) {
        for (auto view : views) {
            if (view != nullptr) wgpuTextureViewRelease(view);
        }
        for (auto texture : textures) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
        }
        return false;
    }
    WGPUBindGroup horizontal = create_effect_blur_bind_group(
        engine,
        engine.layer_texture_view,
        views[0],
        engine.effect_blur_horizontal_uniform_buffer,
        "ProGPU native horizontal Gaussian effect binding");
    WGPUBindGroup vertical = create_effect_blur_bind_group(
        engine,
        views[0],
        views[1],
        engine.effect_blur_vertical_uniform_buffer,
        "ProGPU native vertical Gaussian effect binding");
    WGPUBindGroup output = create_image_texture_bind_group(
        engine,
        engine.image_linear_sampler,
        views[1],
        "ProGPU native Gaussian group-effect output binding");
    if (horizontal == nullptr || vertical == nullptr || output == nullptr) {
        if (output != nullptr) wgpuBindGroupRelease(output);
        if (vertical != nullptr) wgpuBindGroupRelease(vertical);
        if (horizontal != nullptr) wgpuBindGroupRelease(horizontal);
        for (auto view : views) wgpuTextureViewRelease(view);
        for (auto texture : textures) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
        }
        return false;
    }

    if (engine.effect_drop_shadow_output_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_drop_shadow_output_bind_group);
        engine.effect_drop_shadow_output_bind_group = nullptr;
    }
    if (engine.effect_drop_shadow_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_drop_shadow_bind_group);
        engine.effect_drop_shadow_bind_group = nullptr;
    }
    if (engine.effect_output_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_output_bind_group);
    }
    if (engine.effect_blur_vertical_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_blur_vertical_bind_group);
    }
    if (engine.effect_blur_horizontal_bind_group != nullptr) {
        wgpuBindGroupRelease(engine.effect_blur_horizontal_bind_group);
    }
    for (auto view : engine.effect_texture_views) {
        if (view != nullptr) wgpuTextureViewRelease(view);
    }
    for (auto texture : engine.effect_textures) {
        if (texture != nullptr) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
        }
    }
    engine.effect_textures = textures;
    engine.effect_texture_views = views;
    engine.effect_blur_horizontal_bind_group = horizontal;
    engine.effect_blur_vertical_bind_group = vertical;
    engine.effect_output_bind_group = output;
    engine.effect_width = width;
    engine.effect_height = height;
    engine.effect_cache_valid = false;
    ++engine.effect_texture_generation;
    ++engine.effect_allocation_count;
    return true;
}

bool prepare_gaussian_effect(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    return create_gaussian_effect_resources(engine) &&
        ensure_gaussian_effect_textures(engine, width, height);
}

struct effect_chain_plan_entry {
    std::int32_t source = -1;
    std::uint32_t horizontal = 0U;
    std::uint32_t vertical = 0U;
    std::uint32_t output = 0U;
};

std::array<effect_chain_plan_entry, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
create_effect_chain_plan(const resolved_draw_state& draw_state) noexcept {
    std::array<effect_chain_plan_entry,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS> plan{};
    std::int32_t source = -1;
    for (std::uint32_t index = 0U;
         index < draw_state.effect_count;
         ++index) {
        auto& entry = plan[index];
        entry.source = source;
        for (std::uint32_t texture = 0U; texture < 3U; ++texture) {
            if (static_cast<std::int32_t>(texture) != source) {
                entry.horizontal = texture;
                break;
            }
        }
        if (draw_state.group_effects[index].kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
            for (std::uint32_t texture = 0U; texture < 3U; ++texture) {
                if (texture != entry.horizontal &&
                    static_cast<std::int32_t>(texture) != source) {
                    entry.vertical = texture;
                    break;
                }
            }
            entry.output = entry.horizontal;
        } else {
            entry.vertical = source >= 0
                ? static_cast<std::uint32_t>(source)
                : (entry.horizontal == 0U ? 1U : 0U);
            entry.output = entry.vertical;
        }
        source = static_cast<std::int32_t>(entry.output);
    }
    return plan;
}

void release_effect_chain_node_bindings(
    progpu_native_engine& engine) noexcept {
    for (auto& bind_group : engine.effect_chain_drop_shadow_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    for (auto& bind_group : engine.effect_chain_blur_vertical_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    for (auto& bind_group : engine.effect_chain_blur_horizontal_bind_groups) {
        if (bind_group != nullptr) {
            wgpuBindGroupRelease(bind_group);
            bind_group = nullptr;
        }
    }
    engine.effect_chain_bindings_valid = false;
}

bool ensure_effect_chain_uniform_buffers(progpu_native_engine& engine) {
    if (engine.effect_chain_blur_horizontal_uniform_buffers[0] != nullptr &&
        engine.effect_chain_blur_vertical_uniform_buffers[0] != nullptr &&
        engine.effect_chain_drop_shadow_uniform_buffers[0] != nullptr) {
        return true;
    }
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS> horizontal{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS> vertical{};
    std::array<WGPUBuffer, PROGPU_NATIVE_MAX_GROUP_EFFECTS> drop{};
    const auto release = [](auto& buffers) {
        for (auto buffer : buffers) {
            if (buffer != nullptr) {
                wgpuBufferDestroy(buffer);
                wgpuBufferRelease(buffer);
            }
        }
    };
    WGPUBufferDescriptor descriptor{};
    descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    for (std::uint32_t index = 0U;
         index < PROGPU_NATIVE_MAX_GROUP_EFFECTS;
         ++index) {
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native effect-chain horizontal uniforms");
        descriptor.size = sizeof(gpu_gaussian_blur_params);
        horizontal[index] = wgpuDeviceCreateBuffer(
            engine.device,
            &descriptor);
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native effect-chain vertical uniforms");
        vertical[index] = wgpuDeviceCreateBuffer(engine.device, &descriptor);
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native effect-chain drop-shadow uniforms");
        descriptor.size = sizeof(gpu_drop_shadow_params);
        drop[index] = wgpuDeviceCreateBuffer(engine.device, &descriptor);
        if (horizontal[index] == nullptr || vertical[index] == nullptr ||
            drop[index] == nullptr) {
            release(drop);
            release(vertical);
            release(horizontal);
            return false;
        }
    }
    engine.effect_chain_blur_horizontal_uniform_buffers = horizontal;
    engine.effect_chain_blur_vertical_uniform_buffers = vertical;
    engine.effect_chain_drop_shadow_uniform_buffers = drop;
    return true;
}

bool ensure_effect_chain_textures(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height) {
    if (engine.effect_chain_textures[0] != nullptr &&
        engine.effect_chain_width == width &&
        engine.effect_chain_height == height) {
        return true;
    }
    WGPUTextureDescriptor descriptor{};
    descriptor.usage = WGPUTextureUsage_TextureBinding |
        WGPUTextureUsage_StorageBinding;
    descriptor.dimension = WGPUTextureDimension_2D;
    descriptor.size = {width, height, 1U};
    descriptor.format = WGPUTextureFormat_RGBA8Unorm;
    descriptor.mipLevelCount = 1U;
    descriptor.sampleCount = 1U;
    std::array<WGPUTexture, 3U> textures{};
    std::array<WGPUTextureView, 3U> views{};
    std::array<WGPUBindGroup, 3U> outputs{};
    for (std::uint32_t index = 0U; index < 3U; ++index) {
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native bounded effect-chain texture");
        textures[index] = wgpuDeviceCreateTexture(engine.device, &descriptor);
        if (textures[index] != nullptr) {
            views[index] = wgpuTextureCreateView(textures[index], nullptr);
        }
        if (views[index] != nullptr) {
            outputs[index] = create_image_texture_bind_group(
                engine,
                engine.image_linear_sampler,
                views[index],
                "ProGPU native bounded effect-chain output binding");
        }
        if (textures[index] == nullptr || views[index] == nullptr ||
            outputs[index] == nullptr) {
            for (auto output : outputs) {
                if (output != nullptr) wgpuBindGroupRelease(output);
            }
            for (auto view : views) {
                if (view != nullptr) wgpuTextureViewRelease(view);
            }
            for (auto texture : textures) {
                if (texture != nullptr) {
                    wgpuTextureDestroy(texture);
                    wgpuTextureRelease(texture);
                }
            }
            return false;
        }
    }

    release_effect_chain_node_bindings(engine);
    for (auto& output : engine.effect_chain_output_bind_groups) {
        if (output != nullptr) wgpuBindGroupRelease(output);
    }
    for (auto& view : engine.effect_chain_texture_views) {
        if (view != nullptr) wgpuTextureViewRelease(view);
    }
    for (auto& texture : engine.effect_chain_textures) {
        if (texture != nullptr) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
        }
    }
    engine.effect_chain_textures = textures;
    engine.effect_chain_texture_views = views;
    engine.effect_chain_output_bind_groups = outputs;
    engine.effect_chain_width = width;
    engine.effect_chain_height = height;
    engine.effect_cache_valid = false;
    ++engine.effect_chain_texture_generation;
    ++engine.effect_chain_allocation_count;
    return true;
}

WGPUBindGroup create_effect_chain_drop_shadow_bind_group(
    progpu_native_engine& engine,
    WGPUTextureView source,
    WGPUTextureView blurred,
    WGPUTextureView output,
    WGPUBuffer uniforms) {
    const std::array<WGPUBindGroupEntry, 4U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, nullptr, source},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, blurred},
        {nullptr, 2U, nullptr, 0U, 0U, nullptr, output},
        {nullptr, 3U, uniforms, 0U,
            sizeof(gpu_drop_shadow_params), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native bounded effect-chain drop-shadow binding");
    descriptor.layout = engine.effect_drop_shadow_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool ensure_effect_chain_bindings(
    progpu_native_engine& engine,
    const resolved_draw_state& draw_state) {
    bool same_topology = engine.effect_chain_bindings_valid &&
        engine.effect_chain_cached_count == draw_state.effect_count;
    for (std::uint32_t index = 0U;
         same_topology && index < draw_state.effect_count;
         ++index) {
        same_topology = engine.effect_chain_cached_kinds[index] ==
            draw_state.group_effects[index].kind;
    }
    if (same_topology) {
        return true;
    }

    const auto plan = create_effect_chain_plan(draw_state);
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS> horizontal{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS> vertical{};
    std::array<WGPUBindGroup, PROGPU_NATIVE_MAX_GROUP_EFFECTS> drop{};
    const auto release = [](auto& bindings) {
        for (auto binding : bindings) {
            if (binding != nullptr) wgpuBindGroupRelease(binding);
        }
    };
    for (std::uint32_t index = 0U;
         index < draw_state.effect_count;
         ++index) {
        const auto& entry = plan[index];
        WGPUTextureView source = entry.source < 0
            ? engine.layer_texture_view
            : engine.effect_chain_texture_views[
                static_cast<std::uint32_t>(entry.source)];
        horizontal[index] = create_effect_blur_bind_group(
            engine,
            source,
            engine.effect_chain_texture_views[entry.horizontal],
            engine.effect_chain_blur_horizontal_uniform_buffers[index],
            "ProGPU native bounded effect-chain horizontal binding");
        vertical[index] = create_effect_blur_bind_group(
            engine,
            engine.effect_chain_texture_views[entry.horizontal],
            engine.effect_chain_texture_views[entry.vertical],
            engine.effect_chain_blur_vertical_uniform_buffers[index],
            "ProGPU native bounded effect-chain vertical binding");
        if (draw_state.group_effects[index].kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
            drop[index] = create_effect_chain_drop_shadow_bind_group(
                engine,
                source,
                engine.effect_chain_texture_views[entry.vertical],
                engine.effect_chain_texture_views[entry.output],
                engine.effect_chain_drop_shadow_uniform_buffers[index]);
        }
        if (horizontal[index] == nullptr || vertical[index] == nullptr ||
            (draw_state.group_effects[index].kind ==
                 PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW &&
             drop[index] == nullptr)) {
            release(drop);
            release(vertical);
            release(horizontal);
            return false;
        }
    }

    release_effect_chain_node_bindings(engine);
    engine.effect_chain_blur_horizontal_bind_groups = horizontal;
    engine.effect_chain_blur_vertical_bind_groups = vertical;
    engine.effect_chain_drop_shadow_bind_groups = drop;
    engine.effect_chain_cached_count = draw_state.effect_count;
    for (std::uint32_t index = 0U;
         index < draw_state.effect_count;
         ++index) {
        engine.effect_chain_cached_kinds[index] =
            draw_state.group_effects[index].kind;
    }
    engine.effect_chain_final_texture_index =
        plan[draw_state.effect_count - 1U].output;
    engine.effect_chain_bindings_valid = true;
    engine.effect_cache_valid = false;
    return true;
}

bool prepare_effect_chain(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    const resolved_draw_state& draw_state) {
    bool requires_drop_shadow = false;
    for (std::uint32_t index = 0U;
         index < draw_state.effect_count;
         ++index) {
        requires_drop_shadow = requires_drop_shadow ||
            draw_state.group_effects[index].kind ==
                PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
    }
    return create_gaussian_effect_resources(engine) &&
        (!requires_drop_shadow ||
         create_drop_shadow_effect_resources(engine)) &&
        ensure_effect_chain_uniform_buffers(engine) &&
        ensure_effect_chain_textures(engine, width, height) &&
        ensure_effect_chain_bindings(engine, draw_state);
}

bool prepare_group_effect(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    const resolved_draw_state& draw_state) {
    if (draw_state.effect_count > 1U) {
        return prepare_effect_chain(engine, width, height, draw_state);
    }
    if (!prepare_gaussian_effect(engine, width, height)) {
        return false;
    }
    return draw_state.group_effect.kind !=
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW ||
        (create_drop_shadow_effect_resources(engine) &&
         ensure_drop_shadow_effect_bindings(engine));
}

bool encode_group_effect(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    const resolved_draw_state& draw_state,
    float dpi_scale) {
    if (!draw_state.has_group_effect) {
        return true;
    }
    const auto& effect = draw_state.group_effect;
    engine.last_layer_metrics.effect_kind = effect.kind;
    engine.last_layer_metrics.effect_revision =
        draw_state.effect_chain_revision;
    engine.last_layer_metrics.effect_count = draw_state.effect_count;
    engine.last_layer_metrics.effect_chain_revision =
        draw_state.effect_chain_revision;
    engine.last_layer_metrics.effect_texture_generation =
        draw_state.effect_count > 1U
            ? engine.effect_chain_texture_generation
            : engine.effect_texture_generation;
    engine.last_layer_metrics.effect_allocation_count =
        draw_state.effect_count > 1U
            ? engine.effect_chain_allocation_count
            : engine.effect_allocation_count;
    engine.last_layer_metrics.effect_texture_bytes =
        draw_state.effect_count > 1U
            ? static_cast<std::uint64_t>(engine.effect_chain_width) *
                engine.effect_chain_height * 12U
            : static_cast<std::uint64_t>(engine.effect_width) *
                engine.effect_height * 8U;
    const bool cache_hit = draw_state.group_revision != 0U &&
        engine.effect_cache_valid &&
        engine.effect_cached_kind == effect.kind &&
        engine.effect_cached_revision == draw_state.effect_chain_revision &&
        engine.effect_cached_content_revision == draw_state.group_revision &&
        engine.effect_cached_dpi_scale == dpi_scale;
    if (cache_hit) {
        engine.last_layer_metrics.effect_cache_hit = 1U;
        return true;
    }

    if (draw_state.effect_count > 1U) {
        const auto create_parameters = [dpi_scale](float sigma) {
            gpu_gaussian_blur_params parameters{};
            parameters.sigma = sigma * dpi_scale;
            parameters.radius = static_cast<std::uint32_t>(std::clamp(
                static_cast<int>(std::ceil(parameters.sigma * 3.0F)),
                0,
                128));
            return parameters;
        };
        const auto run_pass = [&](WGPUComputePipeline pipeline,
                                  WGPUBindGroup bind_group,
                                  const char* label) {
            WGPUComputePassDescriptor pass_descriptor{};
            pass_descriptor.label =
                progpu::native::webgpu::string_view(label);
            WGPUComputePassEncoder pass = wgpuCommandEncoderBeginComputePass(
                encoder,
                &pass_descriptor);
            if (pass == nullptr) return false;
            wgpuComputePassEncoderSetPipeline(pass, pipeline);
            wgpuComputePassEncoderSetBindGroup(
                pass,
                0U,
                bind_group,
                0U,
                nullptr);
            wgpuComputePassEncoderDispatchWorkgroups(
                pass,
                (engine.effect_chain_width + 15U) / 16U,
                (engine.effect_chain_height + 15U) / 16U,
                1U);
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            return true;
        };
        std::uint64_t uploaded_uniform_bytes = 0U;
        for (std::uint32_t index = 0U;
             index < draw_state.effect_count;
             ++index) {
            const auto& node = draw_state.group_effects[index];
            const auto horizontal = create_parameters(node.sigma_x);
            const auto vertical = create_parameters(node.sigma_y);
            if (!engine.effect_chain_blur_horizontal_uniform_cache_valid[
                    index] ||
                std::memcmp(
                    &engine.cached_effect_chain_blur_horizontal[index],
                    &horizontal,
                    sizeof(horizontal)) != 0) {
                wgpuQueueWriteBuffer(
                    engine.queue,
                    engine.effect_chain_blur_horizontal_uniform_buffers[index],
                    0U,
                    &horizontal,
                    sizeof(horizontal));
                engine.cached_effect_chain_blur_horizontal[index] = horizontal;
                engine.effect_chain_blur_horizontal_uniform_cache_valid[
                    index] = true;
                uploaded_uniform_bytes += sizeof(horizontal);
            }
            if (!engine.effect_chain_blur_vertical_uniform_cache_valid[
                    index] ||
                std::memcmp(
                    &engine.cached_effect_chain_blur_vertical[index],
                    &vertical,
                    sizeof(vertical)) != 0) {
                wgpuQueueWriteBuffer(
                    engine.queue,
                    engine.effect_chain_blur_vertical_uniform_buffers[index],
                    0U,
                    &vertical,
                    sizeof(vertical));
                engine.cached_effect_chain_blur_vertical[index] = vertical;
                engine.effect_chain_blur_vertical_uniform_cache_valid[
                    index] = true;
                uploaded_uniform_bytes += sizeof(vertical);
            }
            if (!run_pass(
                    engine.effect_blur_horizontal_pipeline,
                    engine.effect_chain_blur_horizontal_bind_groups[index],
                    "ProGPU native bounded effect-chain horizontal pass") ||
                !run_pass(
                    engine.effect_blur_vertical_pipeline,
                    engine.effect_chain_blur_vertical_bind_groups[index],
                    "ProGPU native bounded effect-chain vertical pass")) {
                return false;
            }
            engine.last_layer_metrics.effect_pass_count += 2U;
            if (node.kind == PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
                gpu_drop_shadow_params drop_shadow{};
                drop_shadow.offset[0] = node.offset_x * dpi_scale;
                drop_shadow.offset[1] = node.offset_y * dpi_scale;
                drop_shadow.color[0] = node.color_r;
                drop_shadow.color[1] = node.color_g;
                drop_shadow.color[2] = node.color_b;
                drop_shadow.color[3] = node.color_a;
                if (!engine.effect_chain_drop_shadow_uniform_cache_valid[
                        index] ||
                    std::memcmp(
                        &engine.cached_effect_chain_drop_shadow[index],
                        &drop_shadow,
                        sizeof(drop_shadow)) != 0) {
                    wgpuQueueWriteBuffer(
                        engine.queue,
                        engine.effect_chain_drop_shadow_uniform_buffers[index],
                        0U,
                        &drop_shadow,
                        sizeof(drop_shadow));
                    engine.cached_effect_chain_drop_shadow[index] = drop_shadow;
                    engine.effect_chain_drop_shadow_uniform_cache_valid[
                        index] = true;
                    uploaded_uniform_bytes += sizeof(drop_shadow);
                }
                if (!run_pass(
                        engine.effect_drop_shadow_pipeline,
                        engine.effect_chain_drop_shadow_bind_groups[index],
                        "ProGPU native bounded effect-chain drop-shadow pass")) {
                    return false;
                }
                ++engine.last_layer_metrics.effect_pass_count;
            }
        }
        engine.last_layer_metrics.effect_uniform_upload_bytes =
            uploaded_uniform_bytes;
        return true;
    }

    const auto create_parameters = [dpi_scale](float sigma) {
        gpu_gaussian_blur_params parameters{};
        parameters.sigma = sigma * dpi_scale;
        parameters.radius = static_cast<std::uint32_t>(std::clamp(
            static_cast<int>(std::ceil(parameters.sigma * 3.0F)),
            0,
            128));
        return parameters;
    };
    const auto horizontal = create_parameters(effect.sigma_x);
    const auto vertical = create_parameters(effect.sigma_y);
    std::uint64_t uploaded_uniform_bytes = 0U;
    if (!engine.effect_blur_horizontal_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_effect_blur_horizontal,
            &horizontal,
            sizeof(horizontal)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.effect_blur_horizontal_uniform_buffer,
            0U,
            &horizontal,
            sizeof(horizontal));
        engine.cached_effect_blur_horizontal = horizontal;
        engine.effect_blur_horizontal_uniform_cache_valid = true;
        uploaded_uniform_bytes += sizeof(horizontal);
    }
    if (!engine.effect_blur_vertical_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_effect_blur_vertical,
            &vertical,
            sizeof(vertical)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.effect_blur_vertical_uniform_buffer,
            0U,
            &vertical,
            sizeof(vertical));
        engine.cached_effect_blur_vertical = vertical;
        engine.effect_blur_vertical_uniform_cache_valid = true;
        uploaded_uniform_bytes += sizeof(vertical);
    }

    const auto run_pass = [&](WGPUComputePipeline pipeline,
                              WGPUBindGroup bind_group,
                              const char* label) {
        WGPUComputePassDescriptor pass_descriptor{};
        pass_descriptor.label = progpu::native::webgpu::string_view(label);
        WGPUComputePassEncoder pass = wgpuCommandEncoderBeginComputePass(
            encoder,
            &pass_descriptor);
        if (pass == nullptr) {
            return false;
        }
        wgpuComputePassEncoderSetPipeline(pass, pipeline);
        wgpuComputePassEncoderSetBindGroup(
            pass,
            0U,
            bind_group,
            0U,
            nullptr);
        wgpuComputePassEncoderDispatchWorkgroups(
            pass,
            (engine.effect_width + 15U) / 16U,
            (engine.effect_height + 15U) / 16U,
            1U);
        wgpuComputePassEncoderEnd(pass);
        wgpuComputePassEncoderRelease(pass);
        return true;
    };
    if (!run_pass(
            engine.effect_blur_horizontal_pipeline,
            engine.effect_blur_horizontal_bind_group,
            "ProGPU native horizontal Gaussian group-effect pass") ||
        !run_pass(
            engine.effect_blur_vertical_pipeline,
            engine.effect_blur_vertical_bind_group,
            "ProGPU native vertical Gaussian group-effect pass")) {
        return false;
    }
    engine.last_layer_metrics.effect_pass_count = 2U;
    if (effect.kind == PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
        gpu_drop_shadow_params drop_shadow{};
        drop_shadow.offset[0] = effect.offset_x * dpi_scale;
        drop_shadow.offset[1] = effect.offset_y * dpi_scale;
        drop_shadow.color[0] = effect.color_r;
        drop_shadow.color[1] = effect.color_g;
        drop_shadow.color[2] = effect.color_b;
        drop_shadow.color[3] = effect.color_a;
        if (!engine.effect_drop_shadow_uniform_cache_valid ||
            std::memcmp(
                &engine.cached_effect_drop_shadow,
                &drop_shadow,
                sizeof(drop_shadow)) != 0) {
            wgpuQueueWriteBuffer(
                engine.queue,
                engine.effect_drop_shadow_uniform_buffer,
                0U,
                &drop_shadow,
                sizeof(drop_shadow));
            engine.cached_effect_drop_shadow = drop_shadow;
            engine.effect_drop_shadow_uniform_cache_valid = true;
            uploaded_uniform_bytes += sizeof(drop_shadow);
        }
        if (!run_pass(
                engine.effect_drop_shadow_pipeline,
                engine.effect_drop_shadow_bind_group,
                "ProGPU native drop-shadow group-effect composition pass")) {
            return false;
        }
        engine.last_layer_metrics.effect_pass_count = 3U;
    }
    engine.last_layer_metrics.effect_uniform_upload_bytes =
        uploaded_uniform_bytes;
    return true;
}

void retain_group_effect(
    progpu_native_engine& engine,
    float dpi_scale,
    const resolved_draw_state& draw_state) noexcept {
    if (!draw_state.has_group_effect || draw_state.group_revision == 0U) {
        engine.effect_cache_valid = false;
        return;
    }
    engine.effect_cached_revision = draw_state.effect_chain_revision;
    engine.effect_cached_content_revision = draw_state.group_revision;
    engine.effect_cached_kind = draw_state.group_effect.kind;
    engine.effect_cached_dpi_scale = dpi_scale;
    engine.effect_cache_valid = true;
}

bool prepare_layer_composite(
    progpu_native_engine& engine,
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale,
    float opacity) {
    if (!create_layer_resources(engine) ||
        !ensure_layer_texture(engine, width, height)) {
        return false;
    }
    std::array<progpu::native::vector_vertex, 4U> vertices{};
    const float logical_width = static_cast<float>(width) / dpi_scale;
    const float logical_height = static_cast<float>(height) / dpi_scale;
    constexpr std::array<std::array<std::uint32_t, 2U>, 4U> corners{{
        {0U, 0U}, {1U, 0U}, {1U, 1U}, {0U, 1U}
    }};
    for (std::size_t index = 0U; index < corners.size(); ++index) {
        auto& vertex = vertices[index];
        vertex.position[0] = corners[index][0] == 0U
            ? 0.0F
            : logical_width;
        vertex.position[1] = corners[index][1] == 0U
            ? 0.0F
            : logical_height;
        vertex.color[0] = opacity;
        vertex.color[1] = 1.0F;
        vertex.color[2] = 0.0F;
        vertex.color[3] = opacity;
        vertex.texture_coordinate[0] =
            static_cast<float>(corners[index][0]);
        vertex.texture_coordinate[1] =
            static_cast<float>(corners[index][1]);
        vertex.stroke_thickness = 1.0F;
    }
    bool uploaded_vertices = false;
    if (!engine.layer_vertex_cache_valid ||
        std::memcmp(
            engine.layer_vertices.data(),
            vertices.data(),
            sizeof(vertices)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.layer_vertex_buffer,
            0U,
            vertices.data(),
            sizeof(vertices));
        engine.layer_vertices = vertices;
        engine.layer_vertex_cache_valid = true;
        uploaded_vertices = true;
    }
    const gpu_uniforms uniforms = create_uniforms(width, height, dpi_scale);
    const bool uploaded_uniforms = engine.upload_uniform_if_changed(
        engine.layer_uniform_buffer,
        uniforms,
        engine.cached_layer_uniforms,
        engine.layer_uniform_cache_valid);
    engine.last_layer_metrics = {};
    engine.last_layer_metrics.struct_size =
        sizeof(progpu_native_layer_metrics);
    engine.last_layer_metrics.texture_width = engine.layer_width;
    engine.last_layer_metrics.texture_height = engine.layer_height;
    engine.last_layer_metrics.texture_generation =
        engine.layer_texture_generation;
    engine.last_layer_metrics.allocation_count =
        engine.layer_allocation_count;
    engine.last_layer_metrics.texture_bytes =
        static_cast<std::uint64_t>(width) * height * 4U;
    engine.last_layer_metrics.vertex_upload_bytes = uploaded_vertices
        ? sizeof(vertices)
        : 0U;
    engine.last_layer_metrics.uniform_upload_bytes = uploaded_uniforms
        ? sizeof(uniforms)
        : 0U;
    return true;
}

void reset_layer_metrics(progpu_native_engine& engine) noexcept {
    engine.last_layer_metrics = {};
    engine.last_layer_metrics.struct_size =
        sizeof(progpu_native_layer_metrics);
    engine.last_layer_metrics.texture_width = engine.layer_width;
    engine.last_layer_metrics.texture_height = engine.layer_height;
    engine.last_layer_metrics.texture_generation =
        engine.layer_texture_generation;
    engine.last_layer_metrics.allocation_count =
        engine.layer_allocation_count;
    engine.last_layer_metrics.texture_bytes =
        static_cast<std::uint64_t>(engine.layer_width) *
        engine.layer_height * 4U;
}

WGPUBindGroup select_layer_source_bind_group(
    progpu_native_engine& engine,
    const resolved_draw_state& draw_state) noexcept {
    if (!draw_state.has_group_effect) {
        return engine.layer_texture_bind_group;
    }
    if (draw_state.effect_count > 1U) {
        return engine.effect_chain_output_bind_groups[
            engine.effect_chain_final_texture_index];
    }
    return draw_state.group_effect.kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW
        ? engine.effect_drop_shadow_output_bind_group
        : engine.effect_output_bind_group;
}

WGPUBindGroup select_layer_mask_bind_group(
    progpu_native_engine& engine,
    const resolved_draw_state& draw_state) noexcept {
    if (!draw_state.has_group_mask) {
        return nullptr;
    }
    if (draw_state.group_mask.kind == PROGPU_NATIVE_GROUP_MASK_TEXTURE) {
        return draw_state.group_mask.sampling ==
                PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
            ? engine.layer_external_mask_nearest_bind_group
            : engine.layer_external_mask_linear_bind_group;
    }
    if (draw_state.group_mask.kind ==
        PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN) {
        return engine.layer_clip_mask_bind_groups[engine.clip_final_index];
    }
    return engine.layer_analytic_mask_bind_group;
}

std::uint64_t calculate_group_blend_source_signature(
    const resolved_draw_state& draw_state) noexcept {
    std::uint64_t hash = 14695981039346656037ULL;
    hash = append_fnv1a64(
        hash,
        &draw_state.group_revision,
        sizeof(draw_state.group_revision));
    hash = append_fnv1a64(
        hash,
        &draw_state.group_opacity,
        sizeof(draw_state.group_opacity));
    hash = append_fnv1a64(
        hash,
        &draw_state.has_group_mask,
        sizeof(draw_state.has_group_mask));
    if (draw_state.has_group_mask) {
        const auto& mask = draw_state.group_mask;
        hash = append_fnv1a64(
            hash,
            &mask.kind,
            sizeof(mask.kind));
        hash = append_fnv1a64(
            hash,
            &mask.external_view,
            sizeof(mask.external_view));
        hash = append_fnv1a64(hash, &mask.width, sizeof(mask.width));
        hash = append_fnv1a64(hash, &mask.height, sizeof(mask.height));
        hash = append_fnv1a64(hash, &mask.sampling, sizeof(mask.sampling));
        hash = append_fnv1a64(
            hash,
            &mask.texture_format,
            sizeof(mask.texture_format));
        hash = append_fnv1a64(hash, &mask.revision, sizeof(mask.revision));
        hash = append_fnv1a64(
            hash,
            &mask.destination_rect,
            sizeof(mask.destination_rect));
        hash = append_fnv1a64(hash, &mask.bounds, sizeof(mask.bounds));
        hash = append_fnv1a64(hash, &mask.transform, sizeof(mask.transform));
        hash = append_fnv1a64(
            hash,
            mask.corner_radii_x,
            sizeof(mask.corner_radii_x));
        hash = append_fnv1a64(
            hash,
            mask.corner_radii_y,
            sizeof(mask.corner_radii_y));
        hash = append_fnv1a64(hash, &mask.opacity, sizeof(mask.opacity));
    }
    hash = append_fnv1a64(
        hash,
        &draw_state.effect_count,
        sizeof(draw_state.effect_count));
    hash = append_fnv1a64(
        hash,
        &draw_state.effect_chain_revision,
        sizeof(draw_state.effect_chain_revision));
    if (draw_state.effect_count != 0U) {
        hash = append_fnv1a64(
            hash,
            draw_state.group_effects.data(),
            draw_state.effect_count * sizeof(progpu_native_group_effect));
    }
    return hash;
}

float quantize_unorm8(float value) noexcept {
    return std::round(std::clamp(value, 0.0F, 1.0F) * 255.0F) / 255.0F;
}

bool encode_layer_quad(
    progpu_native_engine& engine,
    WGPURenderPassEncoder pass,
    WGPURenderPipeline pipeline,
    const resolved_draw_state& draw_state,
    bool apply_final_clip) {
    WGPUBindGroup mask_bind_group = select_layer_mask_bind_group(
        engine,
        draw_state);
    WGPUBindGroup source_bind_group = select_layer_source_bind_group(
        engine,
        draw_state);
    if (pipeline == nullptr || source_bind_group == nullptr ||
        (draw_state.has_group_mask && mask_bind_group == nullptr)) {
        return false;
    }
    if (apply_final_clip) {
        apply_scissor(pass, draw_state);
    }
    wgpuRenderPassEncoderSetPipeline(pass, pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        0U,
        engine.layer_uniform_bind_group,
        0U,
        nullptr);
    if (draw_state.has_group_mask) {
        wgpuRenderPassEncoderSetBindGroup(
            pass,
            2U,
            mask_bind_group,
            0U,
            nullptr);
    }
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        1U,
        source_bind_group,
        0U,
        nullptr);
    wgpuRenderPassEncoderSetVertexBuffer(
        pass,
        0U,
        engine.layer_vertex_buffer,
        0U,
        sizeof(engine.layer_vertices));
    wgpuRenderPassEncoderSetIndexBuffer(
        pass,
        engine.layer_index_buffer,
        WGPUIndexFormat_Uint32,
        0U,
        6U * sizeof(std::uint32_t));
    wgpuRenderPassEncoderDrawIndexed(pass, 6U, 1U, 0U, 0, 0U);
    return true;
}

bool encode_advanced_group_blend(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView target_view,
    const progpu_native_color& clear_color,
    const resolved_draw_state& draw_state) {
    const std::uint64_t source_signature =
        calculate_group_blend_source_signature(draw_state);
    const bool source_cache_hit = draw_state.group_revision != 0U &&
        engine.group_blend_source_cache_valid &&
        engine.group_blend_source_signature == source_signature;
    if (!source_cache_hit) {
        WGPURenderPassColorAttachment source_attachment{};
        progpu::native::webgpu::initialize_color_attachment(source_attachment);
        source_attachment.view = engine.group_blend_source_view;
        source_attachment.loadOp = WGPULoadOp_Clear;
        source_attachment.storeOp = WGPUStoreOp_Store;
        source_attachment.clearValue = {0.0, 0.0, 0.0, 0.0};
        WGPURenderPassDescriptor source_descriptor{};
        source_descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native advanced group-blend source pass");
        source_descriptor.colorAttachmentCount = 1U;
        source_descriptor.colorAttachments = &source_attachment;
        WGPURenderPassEncoder source_pass = wgpuCommandEncoderBeginRenderPass(
            encoder,
            &source_descriptor);
        if (source_pass == nullptr) {
            return false;
        }
        const bool source_encoded = encode_layer_quad(
            engine,
            source_pass,
            draw_state.has_group_mask
                ? engine.layer_mask_pipeline
                : engine.layer_composite_pipeline,
            draw_state,
            false);
        wgpuRenderPassEncoderEnd(source_pass);
        wgpuRenderPassEncoderRelease(source_pass);
        if (!source_encoded) {
            return false;
        }
        engine.last_layer_metrics.blend_source_pass_count = 1U;
        engine.group_blend_source_signature = source_signature;
        engine.group_blend_source_cache_valid =
            draw_state.group_revision != 0U;
    }

    const gpu_group_blend_uniforms uniforms{{
        quantize_unorm8(clear_color.r),
        quantize_unorm8(clear_color.g),
        quantize_unorm8(clear_color.b),
        quantize_unorm8(clear_color.a)
    }, draw_state.group_blend_mode, {0U, 0U, 0U}};
    if (!engine.group_blend_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_group_blend_uniforms,
            &uniforms,
            sizeof(uniforms)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.group_blend_uniform_buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        engine.cached_group_blend_uniforms = uniforms;
        engine.group_blend_uniform_cache_valid = true;
        engine.last_layer_metrics.uniform_upload_bytes += sizeof(uniforms);
    }

    WGPURenderPassColorAttachment attachment{};
    progpu::native::webgpu::initialize_color_attachment(attachment);
    attachment.view = target_view;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = {
        clear_color.r,
        clear_color.g,
        clear_color.b,
        clear_color.a
    };
    WGPURenderPassDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native advanced group-blend composite pass");
    descriptor.colorAttachmentCount = 1U;
    descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &descriptor);
    if (pass == nullptr) {
        return false;
    }
    apply_scissor(pass, draw_state);
    wgpuRenderPassEncoderSetPipeline(pass, engine.group_blend_pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        0U,
        engine.group_blend_bind_group,
        0U,
        nullptr);
    wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    engine.last_layer_metrics.composite_pass_count = 1U;
    return true;
}

bool encode_layer_composite(
    progpu_native_engine& engine,
    WGPUCommandEncoder encoder,
    WGPUTextureView target_view,
    const progpu_native_color& clear_color,
    const resolved_draw_state& draw_state) {
    if (draw_state.group_opacity != 0.0F &&
        draw_state.has_drawable_clip &&
        is_advanced_group_blend(draw_state.group_blend_mode)) {
        return encode_advanced_group_blend(
            engine,
            encoder,
            target_view,
            clear_color,
            draw_state);
    }
    WGPURenderPassColorAttachment attachment{};
    progpu::native::webgpu::initialize_color_attachment(attachment);
    attachment.view = target_view;
    attachment.loadOp = WGPULoadOp_Clear;
    attachment.storeOp = WGPUStoreOp_Store;
    attachment.clearValue = {
        clear_color.r,
        clear_color.g,
        clear_color.b,
        clear_color.a
    };
    WGPURenderPassDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native group composite pass");
    descriptor.colorAttachmentCount = 1U;
    descriptor.colorAttachments = &attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        encoder,
        &descriptor);
    if (pass == nullptr) {
        return false;
    }
    if (draw_state.group_opacity != 0.0F &&
        draw_state.has_drawable_clip) {
        bool ignored_cache_hit = false;
        WGPURenderPipeline pipeline =
            get_or_create_fixed_group_blend_pipeline(
                engine,
                draw_state.group_blend_mode,
                draw_state.has_group_mask,
                ignored_cache_hit);
        if (!encode_layer_quad(
                engine,
                pass,
                pipeline,
                draw_state,
                true)) {
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            return false;
        }
        engine.last_layer_metrics.composite_pass_count = 1U;
    }
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
    return true;
}

progpu_native_status prepare_group_layer(
    progpu_native_engine& engine,
    layer_family family,
    std::uint32_t width,
    std::uint32_t height,
    float dpi_scale,
    WGPUTextureView target_view,
    const progpu_native_color& clear_color,
    const resolved_draw_state& draw_state,
    bool& use_group_layer,
    bool& submitted_cache_hit) {
    use_group_layer = draw_state.group_opacity < 1.0F ||
        draw_state.group_revision != 0U ||
        draw_state.has_group_mask ||
        draw_state.has_group_effect ||
        draw_state.group_blend_mode != PROGPU_NATIVE_BLEND_SRC_OVER;
    submitted_cache_hit = false;
    if (!use_group_layer) {
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    if (!prepare_layer_composite(
            engine,
            width,
            height,
            dpi_scale,
            draw_state.group_opacity)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The pooled group layer could not be prepared.");
    }
    if (draw_state.has_group_effect &&
        !prepare_group_effect(engine, width, height, draw_state)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained group effect could not be prepared.");
    }
    bool uploaded_mask_uniforms = false;
    if (draw_state.has_group_mask &&
        !update_layer_group_mask(
            engine,
            draw_state,
            dpi_scale,
            uploaded_mask_uniforms)) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The common group mask could not be prepared.");
    }
    if (uploaded_mask_uniforms) {
        engine.last_layer_metrics.uniform_upload_bytes +=
            sizeof(gpu_mask_sampling_uniforms);
    }
    engine.last_layer_metrics.blend_mode = draw_state.group_blend_mode;
    if (draw_state.group_opacity != 0.0F &&
        draw_state.has_drawable_clip) {
        if (is_advanced_group_blend(draw_state.group_blend_mode)) {
            const bool cache_hit = engine.group_blend_pipeline != nullptr &&
                engine.group_blend_source_texture != nullptr &&
                engine.group_blend_source_width == width &&
                engine.group_blend_source_height == height;
            if (!ensure_advanced_group_blend_source(
                    engine,
                    width,
                    height)) {
                return engine.fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "The advanced group-blend resources could not be prepared.");
            }
            engine.last_layer_metrics.blend_pipeline_cache_hit =
                cache_hit ? 1U : 0U;
            engine.last_layer_metrics.blend_source_texture_generation =
                engine.group_blend_source_texture_generation;
            engine.last_layer_metrics.blend_source_allocation_count =
                engine.group_blend_source_allocation_count;
            engine.last_layer_metrics.blend_source_texture_bytes =
                static_cast<std::uint64_t>(width) * height * 4U;
        } else {
            bool pipeline_cache_hit = false;
            if (get_or_create_fixed_group_blend_pipeline(
                    engine,
                    draw_state.group_blend_mode,
                    draw_state.has_group_mask,
                    pipeline_cache_hit) == nullptr) {
                return engine.fail(
                    PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                    "The fixed-function group-blend pipeline could not be prepared.");
            }
            engine.last_layer_metrics.blend_pipeline_cache_hit =
                pipeline_cache_hit ? 1U : 0U;
        }
    }
    const bool cache_hit = draw_state.group_revision != 0U &&
        engine.layer_content_cache_valid &&
        engine.layer_cached_family ==
            static_cast<std::uint32_t>(family) &&
        engine.layer_cached_revision == draw_state.group_revision &&
        engine.layer_cached_dpi_scale == dpi_scale &&
        engine.layer_cached_primitive_opacity == draw_state.opacity;
    if (!cache_hit) {
        engine.effect_cache_valid = false;
        engine.group_blend_source_cache_valid = false;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }

    WGPUCommandEncoderDescriptor encoder_descriptor{};
    encoder_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained group replay encoder");
    WGPUCommandEncoder encoder = wgpuDeviceCreateCommandEncoder(
        engine.device,
        &encoder_descriptor);
    if (encoder == nullptr) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained group replay encoder could not be created.");
    }
    if (!encode_group_effect(
            engine,
            encoder,
            draw_state,
            dpi_scale) ||
        !encode_layer_composite(
            engine,
            encoder,
            target_view,
            clear_color,
            draw_state)) {
        wgpuCommandEncoderRelease(encoder);
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained group replay pass could not be created.");
    }
    WGPUCommandBufferDescriptor command_descriptor{};
    command_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained group replay commands");
    WGPUCommandBuffer command = wgpuCommandEncoderFinish(
        encoder,
        &command_descriptor);
    wgpuCommandEncoderRelease(encoder);
    if (command == nullptr) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The retained group replay command buffer could not be finished.");
    }
    engine.submit(command);
    wgpuCommandBufferRelease(command);
    retain_group_effect(engine, dpi_scale, draw_state);
    engine.last_layer_metrics.cache_hit = 1U;
    submitted_cache_hit = true;
    engine.last_error.clear();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

void retain_group_layer_content(
    progpu_native_engine& engine,
    layer_family family,
    float dpi_scale,
    const resolved_draw_state& draw_state) noexcept {
    if (draw_state.group_revision == 0U) {
        engine.layer_content_cache_valid = false;
        retain_group_effect(engine, dpi_scale, draw_state);
        return;
    }
    engine.layer_cached_family = static_cast<std::uint32_t>(family);
    engine.layer_cached_revision = draw_state.group_revision;
    engine.layer_cached_dpi_scale = dpi_scale;
    engine.layer_cached_primitive_opacity = draw_state.opacity;
    engine.layer_content_cache_valid = true;
    retain_group_effect(engine, dpi_scale, draw_state);
}

WGPUBindGroup create_image_mask_bind_group(
    progpu_native_engine& engine,
    WGPUSampler sampler,
    WGPUTextureView view,
    const char* label) {
    const std::array<WGPUBindGroupEntry, 3U> entries{{
        {nullptr, 0U, nullptr, 0U, 0U, sampler, nullptr},
        {nullptr, 1U, nullptr, 0U, 0U, nullptr, view},
        {nullptr, 2U, engine.image_mask_uniform_buffer, 0U,
            sizeof(gpu_mask_sampling_uniforms), nullptr, nullptr}
    }};
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.image_mask_layout;
    descriptor.entryCount = entries.size();
    descriptor.entries = entries.data();
    return wgpuDeviceCreateBindGroup(engine.device, &descriptor);
}

bool create_image_mask_resources(progpu_native_engine& engine) {
    if (engine.image_mask_pipeline != nullptr) {
        return true;
    }
    if (engine.image_pipeline == nullptr || engine.image_shader == nullptr ||
        engine.image_uniform_layout == nullptr ||
        engine.image_texture_layout == nullptr ||
        engine.image_mask_layout != nullptr ||
        engine.image_mask_uniform_buffer != nullptr) {
        return false;
    }

    std::array<WGPUBindGroupLayoutEntry, 3U> mask_entries{};
    mask_entries[0].binding = 0U;
    mask_entries[0].visibility = WGPUShaderStage_Fragment;
    mask_entries[0].sampler.type = WGPUSamplerBindingType_Filtering;
    mask_entries[1].binding = 1U;
    mask_entries[1].visibility = WGPUShaderStage_Fragment;
    mask_entries[1].texture.sampleType = WGPUTextureSampleType_Float;
    mask_entries[1].texture.viewDimension = WGPUTextureViewDimension_2D;
    mask_entries[1].texture.multisampled = false;
    mask_entries[2].binding = 2U;
    mask_entries[2].visibility = WGPUShaderStage_Fragment;
    mask_entries[2].buffer.type = WGPUBufferBindingType_Uniform;
    mask_entries[2].buffer.minBindingSize = sizeof(gpu_mask_sampling_uniforms);
    WGPUBindGroupLayoutDescriptor mask_layout_descriptor{};
    mask_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image mask layout");
    mask_layout_descriptor.entryCount = mask_entries.size();
    mask_layout_descriptor.entries = mask_entries.data();
    engine.image_mask_layout = wgpuDeviceCreateBindGroupLayout(
        engine.device,
        &mask_layout_descriptor);
    if (engine.image_mask_layout == nullptr) {
        return false;
    }

    const std::array<WGPUBindGroupLayout, 3U> layouts{{
        engine.image_uniform_layout,
        engine.image_texture_layout,
        engine.image_mask_layout
    }};
    WGPUPipelineLayoutDescriptor pipeline_layout_descriptor{};
    pipeline_layout_descriptor.label = progpu::native::webgpu::string_view("ProGPU native masked image layout");
    pipeline_layout_descriptor.bindGroupLayoutCount = layouts.size();
    pipeline_layout_descriptor.bindGroupLayouts = layouts.data();
    WGPUPipelineLayout pipeline_layout = wgpuDeviceCreatePipelineLayout(
        engine.device,
        &pipeline_layout_descriptor);
    if (pipeline_layout == nullptr) {
        return false;
    }

    const std::array<WGPUVertexAttribute, 7U> attributes{{
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 0U, 0U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x4, 8U, 1U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 24U, 2U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 32U, 3U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32x2, 36U, 4U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 44U, 5U),
        progpu::native::webgpu::vertex_attribute(
            WGPUVertexFormat_Float32, 48U, 6U)
    }};
    WGPUVertexBufferLayout vertex_layout{};
    vertex_layout.arrayStride = sizeof(progpu::native::vector_vertex);
    vertex_layout.stepMode = WGPUVertexStepMode_Vertex;
    vertex_layout.attributeCount = attributes.size();
    vertex_layout.attributes = attributes.data();
    WGPUVertexState vertex_state{};
    vertex_state.module = engine.image_shader;
    vertex_state.entryPoint = progpu::native::webgpu::string_view("vs_main");
    vertex_state.bufferCount = 1U;
    vertex_state.buffers = &vertex_layout;
    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState target{};
    target.format = engine.target_format;
    target.blend = &blend;
    target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.image_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view("fs_main");
    fragment.targetCount = 1U;
    fragment.targets = &target;
    WGPURenderPipelineDescriptor pipeline_descriptor{};
    pipeline_descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained masked image pipeline");
    pipeline_descriptor.layout = pipeline_layout;
    pipeline_descriptor.vertex = vertex_state;
    pipeline_descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    pipeline_descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    pipeline_descriptor.primitive.cullMode = WGPUCullMode_None;
    pipeline_descriptor.multisample.count = 1U;
    pipeline_descriptor.multisample.mask = 0xFFFFFFFFU;
    pipeline_descriptor.fragment = &fragment;
    engine.image_mask_pipeline = wgpuDeviceCreateRenderPipeline(
        engine.device,
        &pipeline_descriptor);
    wgpuPipelineLayoutRelease(pipeline_layout);
    if (engine.image_mask_pipeline == nullptr) {
        return false;
    }

    WGPUBufferDescriptor buffer_descriptor{};
    buffer_descriptor.label = progpu::native::webgpu::string_view("ProGPU native image mask sampling uniforms");
    buffer_descriptor.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    buffer_descriptor.size = sizeof(gpu_mask_sampling_uniforms);
    engine.image_mask_uniform_buffer = wgpuDeviceCreateBuffer(
        engine.device,
        &buffer_descriptor);
    return engine.image_mask_uniform_buffer != nullptr;
}

bool update_image_mask(
    progpu_native_engine& engine,
    const progpu_native_image_frame& frame,
    bool& uploaded_uniforms) {
    WGPUTextureView view = reinterpret_cast<WGPUTextureView>(
        frame.external_mask_view);
    const bool replace = engine.image_mask_view == nullptr ||
        engine.image_mask_view != view ||
        engine.image_mask_width != frame.mask_width ||
        engine.image_mask_height != frame.mask_height;
    if (replace) {
        progpu::native::webgpu::texture_view_add_ref(view);
        WGPUBindGroup nearest = create_image_mask_bind_group(
            engine,
            engine.image_nearest_sampler,
            view,
            "ProGPU native nearest image mask bind group");
        WGPUBindGroup linear = create_image_mask_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU native linear image mask bind group");
        if (nearest == nullptr || linear == nullptr) {
            if (linear != nullptr) wgpuBindGroupRelease(linear);
            if (nearest != nullptr) wgpuBindGroupRelease(nearest);
            wgpuTextureViewRelease(view);
            return false;
        }
        if (engine.image_mask_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_mask_linear_bind_group);
        }
        if (engine.image_mask_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_mask_nearest_bind_group);
        }
        if (engine.image_mask_view != nullptr) {
            wgpuTextureViewRelease(engine.image_mask_view);
        }
        engine.image_mask_view = view;
        engine.image_mask_nearest_bind_group = nearest;
        engine.image_mask_linear_bind_group = linear;
        engine.image_mask_width = frame.mask_width;
        engine.image_mask_height = frame.mask_height;
    }

    gpu_mask_sampling_uniforms uniforms{};
    uniforms.coordinate0[0] = frame.mask_destination_rect.x * frame.dpi_scale;
    uniforms.coordinate0[1] = frame.mask_destination_rect.y * frame.dpi_scale;
    uniforms.coordinate1[0] = 1.0F /
        (frame.mask_destination_rect.width * frame.dpi_scale);
    uniforms.coordinate1[1] = 1.0F /
        (frame.mask_destination_rect.height * frame.dpi_scale);
    uniforms.options[0] = 1.0F;
    if (!engine.image_mask_uniform_cache_valid ||
        std::memcmp(
            &engine.cached_image_mask_uniforms,
            &uniforms,
            sizeof(uniforms)) != 0) {
        wgpuQueueWriteBuffer(
            engine.queue,
            engine.image_mask_uniform_buffer,
            0U,
            &uniforms,
            sizeof(uniforms));
        engine.cached_image_mask_uniforms = uniforms;
        engine.image_mask_uniform_cache_valid = true;
        uploaded_uniforms = true;
    }
    engine.image_mask_revision = frame.mask_revision;
    return true;
}

bool upload_image_texture(
    progpu_native_engine& engine,
    const progpu_native_image_frame& frame) {
    const bool external = (frame.source_flags &
        PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW) != 0U;
    WGPUTextureView external_view = reinterpret_cast<WGPUTextureView>(
        frame.external_source_view);
    const bool source_missing = external
        ? engine.image_texture_view == nullptr
        : engine.image_texture == nullptr;
    const bool replace = source_missing ||
        engine.image_source_is_external != external ||
        engine.image_width != frame.image_width ||
        engine.image_height != frame.image_height ||
        (external && engine.image_texture_view != external_view);
    WGPUTexture texture = engine.image_texture;
    WGPUTextureView view = engine.image_texture_view;
    WGPUBindGroup nearest_group = engine.image_nearest_bind_group;
    WGPUBindGroup linear_group = engine.image_linear_bind_group;
    if (replace && external) {
        texture = nullptr;
        view = external_view;
        progpu::native::webgpu::texture_view_add_ref(view);
        nearest_group = create_image_texture_bind_group(
            engine,
            engine.image_nearest_sampler,
            view,
            "ProGPU native external nearest image bind group");
        linear_group = create_image_texture_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU native external linear image bind group");
        if (nearest_group == nullptr || linear_group == nullptr) {
            if (linear_group != nullptr) {
                wgpuBindGroupRelease(linear_group);
            }
            if (nearest_group != nullptr) {
                wgpuBindGroupRelease(nearest_group);
            }
            wgpuTextureViewRelease(view);
            return false;
        }
    } else if (replace) {
        WGPUTextureDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view("ProGPU native retained RGBA image");
        descriptor.usage = WGPUTextureUsage_TextureBinding |
            WGPUTextureUsage_CopyDst;
        descriptor.dimension = WGPUTextureDimension_2D;
        descriptor.size = {frame.image_width, frame.image_height, 1U};
        descriptor.format = WGPUTextureFormat_RGBA8Unorm;
        descriptor.mipLevelCount = 1U;
        descriptor.sampleCount = 1U;
        texture = wgpuDeviceCreateTexture(engine.device, &descriptor);
        if (texture == nullptr) {
            return false;
        }
        view = wgpuTextureCreateView(texture, nullptr);
        if (view == nullptr) {
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
        nearest_group = create_image_texture_bind_group(
            engine,
            engine.image_nearest_sampler,
            view,
            "ProGPU native nearest image bind group");
        linear_group = create_image_texture_bind_group(
            engine,
            engine.image_linear_sampler,
            view,
            "ProGPU native linear image bind group");
        if (nearest_group == nullptr || linear_group == nullptr) {
            if (linear_group != nullptr) {
                wgpuBindGroupRelease(linear_group);
            }
            if (nearest_group != nullptr) {
                wgpuBindGroupRelease(nearest_group);
            }
            wgpuTextureViewRelease(view);
            wgpuTextureDestroy(texture);
            wgpuTextureRelease(texture);
            return false;
        }
    }

    if (!external) {
        progpu::native::webgpu::image_copy_texture destination{};
        destination.texture = texture;
        destination.aspect = WGPUTextureAspect_All;
        progpu::native::webgpu::texture_data_layout layout{};
        layout.bytesPerRow = frame.row_bytes;
        layout.rowsPerImage = frame.image_height;
        const WGPUExtent3D extent{frame.image_width, frame.image_height, 1U};
        const std::size_t upload_bytes =
            static_cast<std::size_t>(frame.row_bytes) *
                (frame.image_height - 1U) +
            static_cast<std::size_t>(frame.image_width) * 4U;
        wgpuQueueWriteTexture(
            engine.queue,
            &destination,
            frame.rgba_pixels,
            upload_bytes,
            &layout,
            &extent);
    }

    if (replace) {
        if (engine.image_linear_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_linear_bind_group);
        }
        if (engine.image_nearest_bind_group != nullptr) {
            wgpuBindGroupRelease(engine.image_nearest_bind_group);
        }
        if (engine.image_texture_view != nullptr) {
            wgpuTextureViewRelease(engine.image_texture_view);
        }
        if (engine.image_texture != nullptr) {
            wgpuTextureDestroy(engine.image_texture);
            wgpuTextureRelease(engine.image_texture);
        }
        engine.image_texture = texture;
        engine.image_texture_view = view;
        engine.image_nearest_bind_group = nearest_group;
        engine.image_linear_bind_group = linear_group;
        engine.image_width = frame.image_width;
        engine.image_height = frame.image_height;
        engine.image_source_is_external = external;
    }
    engine.image_revision = frame.image_revision;
    ++engine.image_texture_generation;
    return true;
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

progpu_native_status encode_semantic_analytic_draw(
    progpu_native_engine& engine,
    const progpu_native_scene_frame& frame,
    const semantic_analytic_draw& draw,
    bool load_target,
    bool& uploaded_uniforms) {
    auto& page = engine.semantic_analytic_cache;
    if (!page.cache_valid || page.vertex_buffer == nullptr ||
        page.index_buffer == nullptr || engine.semantic_encoder == nullptr ||
        draw.vertex_count == 0U || draw.index_count == 0U ||
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

    const gpu_uniforms uniforms = create_uniforms(
        frame.width,
        frame.height,
        frame.dpi_scale);
    uploaded_uniforms = engine.upload_uniform_if_changed(
        engine.analytic_uniform_buffer,
        uniforms,
        engine.cached_analytic_uniforms,
        engine.analytic_uniform_cache_valid);

    WGPURenderPassColorAttachment color_attachment{};
    progpu::native::webgpu::initialize_color_attachment(color_attachment);
    color_attachment.view = reinterpret_cast<WGPUTextureView>(
        frame.target_view);
    color_attachment.loadOp = load_target
        ? WGPULoadOp_Load
        : WGPULoadOp_Clear;
    color_attachment.storeOp = WGPUStoreOp_Store;
    color_attachment.clearValue = WGPUColor{
        frame.clear_color.r,
        frame.clear_color.g,
        frame.clear_color.b,
        frame.clear_color.a};
    WGPURenderPassDescriptor pass_descriptor{};
    pass_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU semantic analytic packed-page pass");
    pass_descriptor.colorAttachmentCount = 1U;
    pass_descriptor.colorAttachments = &color_attachment;
    WGPURenderPassEncoder pass = wgpuCommandEncoderBeginRenderPass(
        engine.semantic_encoder,
        &pass_descriptor);
    if (pass == nullptr) {
        return engine.fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic analytic render pass could not be created.");
    }

    wgpuRenderPassEncoderSetPipeline(pass, engine.analytic_pipeline);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        0U,
        engine.analytic_uniform_bind_group,
        0U,
        nullptr);
    wgpuRenderPassEncoderSetBindGroup(
        pass,
        1U,
        engine.analytic_atlas_bind_group,
        0U,
        nullptr);
    wgpuRenderPassEncoderSetVertexBuffer(
        pass,
        0U,
        page.vertex_buffer,
        0U,
        page.vertex_bytes);
    wgpuRenderPassEncoderSetIndexBuffer(
        pass,
        page.index_buffer,
        WGPUIndexFormat_Uint32,
        0U,
        page.index_bytes);
    wgpuRenderPassEncoderDrawIndexed(
        pass,
        draw.index_count,
        1U,
        static_cast<std::uint32_t>(
            draw.index_offset_bytes / sizeof(std::uint32_t)),
        0,
        0U);
    wgpuRenderPassEncoderEnd(pass);
    wgpuRenderPassEncoderRelease(pass);
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
    reset_layer_metrics(*engine);
    if (frame->path_count > (1U << 20U) ||
        frame->segment_count > (1U << 24U) ||
        frame->path_count >
            std::numeric_limits<std::uint32_t>::max() / 4U) {
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
    if (!engine->path_indices.empty() && draw_state.opacity != 0.0F &&
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
            static_cast<std::uint32_t>(engine->path_indices.size()),
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
        metrics->draw_call_count = engine->path_indices.empty() ||
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
    if (!engine->glyph_instances.empty() && draw_state.opacity != 0.0F &&
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
            static_cast<std::uint32_t>(engine->glyph_instances.size()),
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
        metrics->draw_call_count = engine->glyph_instances.empty() ||
            draw_state.opacity == 0.0F ||
            (!use_group_layer && !draw_state.has_drawable_clip)
            ? 0U
            : 1U;
        metrics->glyph_count = static_cast<std::uint32_t>(
            engine->glyph_instances.size());
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
    const auto command_revision = [&](const progpu_native_scene_command& command,
                                      const progpu_native_scene_resource& resource) noexcept {
        std::uint64_t hash = 14695981039346656037ULL;
        hash = append_fnv1a64(
            hash, &header.scene_id, sizeof(header.scene_id));
        hash = append_fnv1a64(
            hash, &header.generation, sizeof(header.generation));
        hash = append_fnv1a64(
            hash, &resource.resource_id, sizeof(resource.resource_id));
        hash = append_fnv1a64(
            hash, &resource.generation, sizeof(resource.generation));
        hash = append_fnv1a64(
            hash, &command.kind, sizeof(command.kind));
        hash = append_fnv1a64(
            hash,
            bytes + command.payload_offset,
            command.payload_size);
        return revision32(hash);
    };
    const auto span_is_multiple = [](std::uint32_t size,
                                     std::size_t stride) noexcept {
        return stride != 0U && size != 0U && size % stride == 0U;
    };

    /* Preflight every typed payload before the first target submission. */
    std::uint32_t semantic_draw_count = 0U;
    std::uint32_t semantic_analytic_draw_count = 0U;
    std::uint64_t semantic_analytic_vertex_bytes = 0U;
    std::uint64_t semantic_analytic_index_bytes = 0U;
    semantic_compilation_budget compilation_budget{};
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command = read_command(index);
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE) {
            if (command.state_index != PROGPU_NATIVE_SCENE_NO_INDEX) {
                return engine->fail(
                    PROGPU_NATIVE_STATUS_UNSUPPORTED,
                    "Semantic save/restore state resources are not implemented yet.");
            }
            continue;
        }
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_UNSUPPORTED,
                "Semantic isolated layers are delivered by M2.4d3b2.");
        }
        if (command.kind < PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
            command.kind > PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        if (command.state_index != PROGPU_NATIVE_SCENE_NO_INDEX) {
            return engine->fail(
                PROGPU_NATIVE_STATUS_UNSUPPORTED,
                "Semantic draw-state resources are delivered by M2.4d3b2.");
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
                        std::numeric_limits<std::uint32_t>::max() / 4U;
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
        }
        ++semantic_draw_count;
    }

    std::uint64_t semantic_analytic_vertex_upload_bytes = 0U;
    std::uint64_t semantic_analytic_index_upload_bytes = 0U;
    auto& semantic_analytic_page = engine->semantic_analytic_cache;
    const bool semantic_analytic_page_hit =
        semantic_analytic_draw_count != 0U &&
        semantic_analytic_page.cache_valid &&
        semantic_analytic_page.scene_hash == engine->semantic_scene_hash &&
        semantic_analytic_page.dpi_scale == frame->dpi_scale &&
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

            for (std::uint32_t index = 0U;
                 index < header.command_count;
                 ++index) {
                const auto command = read_command(index);
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
        semantic_analytic_page.cache_valid = true;
        semantic_analytic_vertex_upload_bytes = compiled_vertex_bytes;
        semantic_analytic_index_upload_bytes = compiled_index_bytes;
    }

    const std::uint64_t submission_start = engine->submission_count;
    std::uint32_t draw_calls = 0U;
    std::uint32_t family_switches = 0U;
    std::uint32_t previous_family = 0U;
    std::uint64_t vertex_upload_bytes =
        semantic_analytic_vertex_upload_bytes;
    std::uint64_t index_upload_bytes =
        semantic_analytic_index_upload_bytes;
    std::uint64_t texture_upload_bytes = 0U;
    std::uint64_t uniform_upload_bytes = 0U;
    std::uint64_t coverage_staging_bytes = 0U;
    const std::uint64_t payload_hash = engine->semantic_scene_hash;
    bool target_has_content = false;
    std::uint32_t semantic_analytic_draw_index = 0U;
    std::array<std::uint32_t, 4U> encoded_buffer_revisions{};

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
        encoded_buffer_revisions.fill(0U);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    };

    const auto prepare_family = [&](std::uint32_t family) noexcept {
        if (family != previous_family) {
            ++family_switches;
            previous_family = family;
        }
        engine->semantic_load_target = target_has_content;
    };
    const auto finish_family = [&](progpu_native_status status) noexcept {
        engine->semantic_load_target = false;
        if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
            target_has_content = true;
        }
        return status;
    };

    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto command = read_command(index);
        if (command.kind < PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
            command.kind > PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
            continue;
        }
        const std::uint32_t buffer_domain_index =
            command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC
            ? 0U
            : command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH
                ? 1U
                : command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN
                    ? 2U
                    : 3U;
        const auto resource = read_resource(command.resource_index);
        const std::uint32_t buffer_revision =
            command_revision(command, resource);
        if (buffer_domain_index != 0U &&
            encoded_buffer_revisions[buffer_domain_index] != 0U &&
            encoded_buffer_revisions[buffer_domain_index] != buffer_revision) {
            const auto flush_status = flush_encoder();
            if (flush_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                return flush_status;
            }
        }
        if (!begin_encoder()) {
            discard_encoder();
            return engine->fail(
                PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The semantic scene command encoder could not be created.");
        }
        if (buffer_domain_index != 0U) {
            encoded_buffer_revisions[buffer_domain_index] = buffer_revision;
        }
        progpu_native_status status = PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        switch (command.kind) {
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC: {
                if (semantic_analytic_draw_index >=
                    semantic_analytic_page.draws.size()) {
                    discard_encoder();
                    return engine->fail(
                        PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                        "The semantic analytic packed-page index is invalid.");
                }
                prepare_family(command.kind);
                bool uploaded_uniforms = false;
                status = finish_family(encode_semantic_analytic_draw(
                    *engine,
                    *frame,
                    semantic_analytic_page.draws[
                        semantic_analytic_draw_index],
                    engine->semantic_load_target,
                    uploaded_uniforms));
                ++semantic_analytic_draw_index;
                if (status == PROGPU_NATIVE_STATUS_SUCCESS) {
                    ++draw_calls;
                    uniform_upload_bytes += uploaded_uniforms
                        ? sizeof(gpu_uniforms)
                        : 0U;
                }
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH: {
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
                family.paths = reinterpret_cast<
                    const progpu_native_path_fill*>(
                        bytes + resource.payload_offset);
                family.path_count = resource.payload_size /
                    sizeof(progpu_native_scene_path_fill);
                family.segments =
                    reinterpret_cast<const progpu_native_path_segment*>(
                        bytes + resource.auxiliary_offset);
                family.segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                family.flags =
                    PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD;
                family.content_revision = command_revision(command, resource);
                progpu_native_path_frame_metrics family_metrics{};
                family_metrics.struct_size = sizeof(family_metrics);
                prepare_family(command.kind);
                status = finish_family(progpu_native_engine_render_paths(
                    engine, &family, &family_metrics));
                draw_calls += family_metrics.draw_call_count;
                vertex_upload_bytes += family_metrics.vertex_upload_bytes;
                index_upload_bytes += family_metrics.index_upload_bytes;
                uniform_upload_bytes += family_metrics.uniform_upload_bytes;
                coverage_staging_bytes +=
                    family_metrics.coverage_staging_bytes;
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN: {
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
                family.outlines = reinterpret_cast<
                    const progpu_native_glyph_outline*>(
                        bytes + resource.payload_offset);
                family.outline_count = resource.payload_size /
                    sizeof(progpu_native_scene_glyph_outline);
                family.segments =
                    reinterpret_cast<const progpu_native_path_segment*>(
                        bytes + resource.auxiliary_offset);
                family.segment_count = resource.auxiliary_size /
                    sizeof(progpu_native_path_segment);
                family.glyphs =
                    reinterpret_cast<const progpu_native_positioned_glyph*>(
                        bytes + command.payload_offset);
                family.glyph_count = command.payload_size /
                    sizeof(progpu_native_positioned_glyph);
                family.flags =
                    PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD;
                family.content_revision = command_revision(command, resource);
                progpu_native_glyph_frame_metrics family_metrics{};
                family_metrics.struct_size = sizeof(family_metrics);
                prepare_family(command.kind);
                status = finish_family(progpu_native_engine_render_glyphs(
                    engine, &family, &family_metrics));
                uniform_upload_bytes += family_metrics.uniform_upload_bytes;
                vertex_upload_bytes += family_metrics.instance_upload_bytes;
                coverage_staging_bytes +=
                    family_metrics.coverage_staging_bytes;
                draw_calls += family_metrics.draw_call_count;
                break;
            }
            case PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE: {
                progpu_native_scene_image_draw image{};
                std::memcpy(
                    &image,
                    bytes + command.payload_offset,
                    sizeof(image));
                progpu_native_image_frame family{};
                family.struct_size = sizeof(family);
                family.width = frame->width;
                family.height = frame->height;
                family.dpi_scale = frame->dpi_scale;
                family.target_view = frame->target_view;
                family.clear_color = frame->clear_color;
                family.rgba_pixels = reinterpret_cast<const std::uint8_t*>(
                    bytes + resource.payload_offset);
                family.pixel_bytes = resource.payload_size;
                family.image_width = image.image_width;
                family.image_height = image.image_height;
                family.row_bytes = image.row_bytes;
                family.sampling = image.sampling;
                family.image_revision =
                    command_revision(command, resource);
                family.content_revision = family.image_revision;
                family.source_rect = image.source_rect;
                family.destination_rect = image.destination_rect;
                family.transform = image.transform;
                family.opacity = image.opacity;
                progpu_native_image_frame_metrics family_metrics{};
                family_metrics.struct_size = sizeof(family_metrics);
                prepare_family(command.kind);
                status = finish_family(progpu_native_engine_render_image(
                    engine, &family, &family_metrics));
                draw_calls += family_metrics.draw_call_count;
                vertex_upload_bytes += family_metrics.vertex_upload_bytes;
                index_upload_bytes += family_metrics.index_upload_bytes;
                texture_upload_bytes += family_metrics.texture_upload_bytes;
                uniform_upload_bytes += family_metrics.uniform_upload_bytes;
                break;
            }
            default:
                break;
        }
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
            engine->semantic_load_target = false;
            discard_encoder();
            return status;
        }
    }

    if (semantic_analytic_draw_index != semantic_analytic_draw_count) {
        discard_encoder();
        return engine->fail(
            PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The semantic analytic packed-page draw count is inconsistent.");
    }

    const auto flush_status = flush_encoder();
    if (flush_status != PROGPU_NATIVE_STATUS_SUCCESS) {
        engine->semantic_load_target = false;
        return flush_status;
    }

    if (semantic_draw_count == 0U) {
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
