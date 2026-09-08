#include "progpu_native_semantic_state.hpp"

#include "progpu_native_geometry.hpp"
#include "progpu_native_scene.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

#if defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#elif defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__wasm_simd128__)
#include <wasm_simd128.h>
#endif

namespace progpu::native::semantic {

namespace {

// Four independent clip-edge lanes; no FMA or reciprocal approximation.
// O(1) fixed storage, unaligned-safe. Scalar is only the non-SIMD target path.
std::array<float, 4U> scale_translate_four(const std::array<float, 4U>& coordinates,
    const std::array<float, 4U>& scales, const std::array<float, 4U>& origins) noexcept {
    std::array<float, 4U> result{};
#if defined(__SSE2__) || defined(_M_X64)
    _mm_storeu_ps(result.data(), _mm_add_ps(
        _mm_mul_ps(_mm_loadu_ps(coordinates.data()), _mm_loadu_ps(scales.data())),
        _mm_loadu_ps(origins.data())));
#elif defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    vst1q_f32(result.data(), vaddq_f32(vmulq_f32(vld1q_f32(coordinates.data()),
        vld1q_f32(scales.data())), vld1q_f32(origins.data())));
#elif defined(__wasm_simd128__)
    wasm_v128_store(result.data(), wasm_f32x4_add(wasm_f32x4_mul(
        wasm_v128_load(coordinates.data()), wasm_v128_load(scales.data())),
        wasm_v128_load(origins.data())));
#else
    for (std::size_t i = 0U; i < result.size(); ++i)
        result[i] = coordinates[i] * scales[i] + origins[i];
#endif
    return result;
}

std::array<float, 4U> presentation_clip_edges(const progpu_native_image_rect& clip,
    const progpu_native_scene_presentation& presentation) noexcept {
    return scale_translate_four({clip.x, clip.y, clip.x + clip.width, clip.y + clip.height},
        {presentation.dpi_scale_x, presentation.dpi_scale_y,
            presentation.dpi_scale_x, presentation.dpi_scale_y},
        {static_cast<float>(presentation.viewport_x), static_cast<float>(presentation.viewport_y),
            static_cast<float>(presentation.viewport_x), static_cast<float>(presentation.viewport_y)});
}

std::uint32_t target_domain_extent(std::uint32_t frame_extent,
                                  std::uint32_t origin,
                                  std::uint32_t extent) noexcept {
    const auto end = std::min<std::uint64_t>(
        static_cast<std::uint64_t>(origin) + extent,
        std::numeric_limits<std::uint32_t>::max());
    return std::max(frame_extent, static_cast<std::uint32_t>(end));
}

float snap_scissor_coordinate(float value) noexcept {
    const float rounded = std::round(value);
    return std::abs(value - rounded) < 0.0001F ? rounded : value;
}

float round_to_even(float value) noexcept {
    const float lower = std::floor(value);
    const float fraction = value - lower;
    if (fraction < 0.5F) {
        return lower;
    }
    if (fraction > 0.5F) {
        return lower + 1.0F;
    }
    return std::fmod(std::abs(lower), 2.0F) == 0.0F
        ? lower
        : lower + 1.0F;
}

} // namespace

bool try_resolve_semantic_effect(const progpu_native_group_effect& logical,
    const progpu_native_scene_presentation& presentation,
    progpu_native_group_effect& physical) noexcept {
    const bool shadow = logical.kind == PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW;
    const bool box = logical.kind == PROGPU_NATIVE_GROUP_EFFECT_BOX_BLUR;
    if ((!shadow && !box && logical.kind != PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR) ||
        !std::isfinite(presentation.dpi_scale_x) || presentation.dpi_scale_x <= 0.0F ||
        !std::isfinite(presentation.dpi_scale_y) || presentation.dpi_scale_y <= 0.0F)
        return false;
    const auto mapped = scale_translate_four(
        {logical.sigma_x, logical.sigma_y, logical.offset_x, logical.offset_y},
        {presentation.dpi_scale_x, presentation.dpi_scale_y,
            presentation.dpi_scale_x, presentation.dpi_scale_y}, {});
    const float maximum = box ? 128.0F : 128.0F / 3.0F;
    if (!std::isfinite(mapped[0]) || !std::isfinite(mapped[1]) ||
        mapped[0] < 0.0F || mapped[1] < 0.0F ||
        mapped[0] > maximum || mapped[1] > maximum ||
        (shadow && (!std::isfinite(mapped[2]) || !std::isfinite(mapped[3]))))
        return false;
    physical = logical;
    physical.sigma_x = mapped[0];
    physical.sigma_y = mapped[1];
    physical.offset_x = mapped[2];
    physical.offset_y = mapped[3];
    return true;
}

progpu_native_scene_state semantic_identity_state() noexcept {
    progpu_native_scene_state state{};
    state.struct_size = sizeof(state);
    state.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    state.opacity = 1.0F;
    return state;
}

semantic_state_cursor::semantic_state_cursor(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    float dpi_scale) noexcept
    : semantic_state_cursor(bytes, header, dpi_scale, dpi_scale) {
}

semantic_state_cursor::semantic_state_cursor(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    float dpi_scale_x, float dpi_scale_y) noexcept
    : bytes_(bytes),
      header_(header),
      dpi_scale_x_(dpi_scale_x),
      dpi_scale_y_(dpi_scale_y),
      current_(semantic_identity_state()) {
}

progpu_native_scene_state semantic_state_cursor::advance(
    const progpu_native_scene_command& command) noexcept {
    if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
        command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
        stack_[depth_++] = current_;
        if (command.state_index != PROGPU_NATIVE_SCENE_NO_INDEX) {
            current_ = resolve_state(command.state_index);
        }
        return current_;
    }
    if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE ||
        command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
        current_ = stack_[--depth_];
        return current_;
    }
    if (command.state_index != PROGPU_NATIVE_SCENE_NO_INDEX) {
        return resolve_state(command.state_index);
    }
    return current_;
}

progpu_native_scene_state semantic_state_cursor::resolve_state(
    std::uint32_t index) const noexcept {
    return resolve_guidelines(read_state(index));
}

progpu_native_scene_state semantic_state_cursor::read_composite_state(
    std::uint32_t index) const noexcept {
    return read_state(index);
}

void semantic_state_cursor::snap_composite_point(
    const progpu_native_scene_state& state,
    float& target_x,
    float& target_y) const noexcept {
    if ((read_guideline_flags(state) &
            PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT) != 0U) {
        return;
    }
    snap_point(state, target_x, target_y);
}

bool semantic_state_cursor::has_per_point_guidelines(
    const progpu_native_scene_state& state) const noexcept {
    return (read_guideline_flags(state) &
        PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT) != 0U;
}

bool semantic_state_cursor::try_composite_rectangle_inverse(
    const progpu_native_scene_state& state,
    progpu_native_image_rect bounds,
    progpu_native_affine_2d& inverse,
    bool& visible) const noexcept {
    // Algorithm: snap opposite corners with the same nearest-guideline search
    // as the retained cache quad, then invert its separable deformation.
    // Time: O(log X + log Y) for X/Y guidelines. Space: O(1), allocation-free.
    if (!std::isfinite(bounds.x) || !std::isfinite(bounds.y) ||
        !std::isfinite(bounds.width) || !std::isfinite(bounds.height) ||
        bounds.width <= 0.0F || bounds.height <= 0.0F) return false;
    const float right = bounds.x + bounds.width;
    const float bottom = bounds.y + bounds.height;
    if (!std::isfinite(right) || !std::isfinite(bottom)) return false;
    float snapped_left = bounds.x;
    float snapped_top = bounds.y;
    float snapped_right = right;
    float snapped_bottom = bottom;
    snap_composite_point(state, snapped_left, snapped_top);
    snap_composite_point(state, snapped_right, snapped_bottom);
    if (!std::isfinite(snapped_left) || !std::isfinite(snapped_top) ||
        !std::isfinite(snapped_right) || !std::isfinite(snapped_bottom)) return false;
    const double width = static_cast<double>(snapped_right) - snapped_left;
    const double height = static_cast<double>(snapped_bottom) - snapped_top;
    if (width <= 0.0 || height <= 0.0) {
        inverse = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
        visible = false;
        return true;
    }
    const double sx = static_cast<double>(right - bounds.x) / width;
    const double sy = static_cast<double>(bottom - bounds.y) / height;
    const double tx = bounds.x - snapped_left * sx;
    const double ty = bounds.y - snapped_top * sy;
    constexpr double limit = std::numeric_limits<float>::max();
    for (const double value : {sx, sy, tx, ty})
        if (!std::isfinite(value) || std::abs(value) > limit) return false;
    inverse = {static_cast<float>(sx), 0.0F, 0.0F, static_cast<float>(sy),
        static_cast<float>(tx), static_cast<float>(ty)};
    visible = true;
    return true;
}

void semantic_state_cursor::snap_draw_point(
    const progpu_native_scene_state& state,
    float& target_x,
    float& target_y) const noexcept {
    if (!has_per_point_guidelines(state)) {
        return;
    }
    snap_point(state, target_x, target_y);
}

std::uint32_t semantic_state_cursor::read_guideline_flags(
    const progpu_native_scene_state& state) const noexcept {
    if ((state.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) == 0U ||
        state.guideline_resource_index >= header_.resource_count) {
        return 0U;
    }
    progpu_native_scene_resource resource{};
    std::memcpy(
        &resource,
        bytes_ + header_.resource_offset +
            static_cast<std::size_t>(state.guideline_resource_index) *
                header_.resource_stride,
        sizeof(resource));
    progpu_native_scene_guideline_set guidelines{};
    std::memcpy(
        &guidelines,
        bytes_ + resource.payload_offset,
        sizeof(guidelines));
    return guidelines.flags;
}

void semantic_state_cursor::snap_point(
    const progpu_native_scene_state& state,
    float& target_x,
    float& target_y) const noexcept {
    if ((state.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) == 0U ||
        !std::isfinite(dpi_scale_x_) || dpi_scale_x_ <= 0.0F ||
        !std::isfinite(dpi_scale_y_) || dpi_scale_y_ <= 0.0F) {
        return;
    }
    progpu_native_scene_resource resource{};
    std::memcpy(
        &resource,
        bytes_ + header_.resource_offset +
            static_cast<std::size_t>(state.guideline_resource_index) *
                header_.resource_stride,
        sizeof(resource));
    progpu_native_scene_guideline_set guidelines{};
    std::memcpy(
        &guidelines,
        bytes_ + resource.payload_offset,
        sizeof(guidelines));
    const bool explicit_offsets = (guidelines.flags &
        PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS) != 0U;
    const std::size_t coordinate_count =
        static_cast<std::size_t>(guidelines.guideline_x_count) +
        guidelines.guideline_y_count;
    const std::size_t explicit_offset_base =
        sizeof(progpu_native_scene_guideline_set) +
        coordinate_count * sizeof(double);
    const auto snap_axis = [this,
                            &resource,
                            explicit_offsets,
                            explicit_offset_base](
        float& coordinate,
        std::size_t offset,
        std::uint32_t count,
        std::uint32_t offset_index_base,
        float dpi_scale) {
        if (count == 0U) {
            return;
        }
        const float physical_coordinate = coordinate * dpi_scale;
        const auto read_coordinate = [this, &resource, offset, dpi_scale](
            std::uint32_t index) {
            double value = 0.0;
            std::memcpy(
                &value,
                bytes_ + resource.payload_offset + offset +
                    static_cast<std::size_t>(index) * sizeof(double),
                sizeof(value));
            return static_cast<float>(value) * dpi_scale;
        };
        std::uint32_t selected = 0U;
        const float first = read_coordinate(0U);
        if (count > 1U && physical_coordinate > first) {
            std::uint32_t lower = 0U;
            std::uint32_t upper = count - 1U;
            float lower_value = first;
            float upper_value = read_coordinate(upper);
            if (physical_coordinate > upper_value) {
                selected = upper;
            } else {
                while (upper - lower > 1U) {
                    const std::uint32_t middle = (lower + upper) >> 1U;
                    const float middle_value = read_coordinate(middle);
                    if (physical_coordinate > middle_value) {
                        lower = middle;
                        lower_value = middle_value;
                    } else {
                        upper = middle;
                        upper_value = middle_value;
                    }
                }
                selected = upper_value - physical_coordinate <
                        physical_coordinate - lower_value
                    ? upper
                    : lower;
            }
        }
        const float selected_coordinate = read_coordinate(selected);
        float snapping_offset = wpf_guideline_offset(selected_coordinate);
        if (explicit_offsets) {
            double stored_offset = 0.0;
            std::memcpy(
                &stored_offset,
                bytes_ + resource.payload_offset + explicit_offset_base +
                    static_cast<std::size_t>(offset_index_base + selected) *
                        sizeof(double),
                sizeof(stored_offset));
            snapping_offset = static_cast<float>(stored_offset);
        }
        coordinate += snapping_offset / dpi_scale;
    };
    constexpr std::size_t header_size =
        sizeof(progpu_native_scene_guideline_set);
    snap_axis(
        target_x,
        header_size,
        guidelines.guideline_x_count,
        0U,
        dpi_scale_x_);
    snap_axis(
        target_y,
        header_size +
            static_cast<std::size_t>(guidelines.guideline_x_count) *
                sizeof(double),
        guidelines.guideline_y_count,
        guidelines.guideline_x_count,
        dpi_scale_y_);
}

progpu_native_scene_state semantic_state_cursor::resolve_guidelines(
    progpu_native_scene_state state) const noexcept {
    if ((state.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) == 0U ||
        !std::isfinite(dpi_scale_x_) || dpi_scale_x_ <= 0.0F ||
        !std::isfinite(dpi_scale_y_) || dpi_scale_y_ <= 0.0F) {
        return state;
    }
    progpu_native_scene_resource resource{};
    std::memcpy(
        &resource,
        bytes_ + header_.resource_offset +
            static_cast<std::size_t>(state.guideline_resource_index) *
                header_.resource_stride,
        sizeof(resource));
    progpu_native_scene_guideline_set guidelines{};
    std::memcpy(
        &guidelines,
        bytes_ + resource.payload_offset,
        sizeof(guidelines));
    if ((guidelines.flags & PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT) != 0U) {
        return state;
    }
    progpu_native_point translation{};
    if (try_uniform_guideline_translation(
            {bytes_ + resource.payload_offset, static_cast<std::size_t>(resource.payload_size)},
            dpi_scale_x_, dpi_scale_y_, translation)) {
        state.transform.m31 += translation.x;
        state.transform.m32 += translation.y;
        return state;
    }
    std::size_t offset = resource.payload_offset + sizeof(guidelines);
    const bool explicit_offsets = (guidelines.flags &
        PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS) != 0U;
    const std::size_t coordinate_count =
        static_cast<std::size_t>(guidelines.guideline_x_count) +
        guidelines.guideline_y_count;
    const std::size_t explicit_offset_base =
        resource.payload_offset + sizeof(guidelines) +
        coordinate_count * sizeof(double);
    if (guidelines.guideline_x_count != 0U) {
        double coordinate = 0.0;
        std::memcpy(&coordinate, bytes_ + offset, sizeof(coordinate));
        const float physical =
            static_cast<float>(coordinate) * dpi_scale_x_;
        float snapping_offset = wpf_guideline_offset(physical);
        if (explicit_offsets) {
            double stored_offset = 0.0;
            std::memcpy(
                &stored_offset,
                bytes_ + explicit_offset_base,
                sizeof(stored_offset));
            snapping_offset = static_cast<float>(stored_offset);
        }
        state.transform.m31 += snapping_offset / dpi_scale_x_;
        offset += static_cast<std::size_t>(guidelines.guideline_x_count) * sizeof(coordinate);
    }
    if (guidelines.guideline_y_count != 0U) {
        double coordinate = 0.0;
        std::memcpy(&coordinate, bytes_ + offset, sizeof(coordinate));
        const float physical =
            static_cast<float>(coordinate) * dpi_scale_y_;
        float snapping_offset = wpf_guideline_offset(physical);
        if (explicit_offsets) {
            double stored_offset = 0.0;
            std::memcpy(
                &stored_offset,
                bytes_ + explicit_offset_base +
                    static_cast<std::size_t>(guidelines.guideline_x_count) *
                        sizeof(double),
                sizeof(stored_offset));
            snapping_offset = static_cast<float>(stored_offset);
        }
        state.transform.m32 += snapping_offset / dpi_scale_y_;
    }
    return state;
}

progpu_native_scene_state semantic_state_cursor::read_state(
    std::uint32_t index) const noexcept {
    progpu_native_scene_resource resource{};
    std::memcpy(
        &resource,
        bytes_ + header_.resource_offset +
            static_cast<std::size_t>(index) * header_.resource_stride,
        sizeof(resource));
    progpu_native_scene_state state{};
    std::memcpy(&state, bytes_ + resource.payload_offset, sizeof(state));
    return state;
}

void apply_semantic_state(
    progpu_native_analytic_primitive& primitive,
    const progpu_native_scene_state& state) noexcept {
    apply_semantic_transform(primitive, state);
    primitive.color.a *= state.opacity;
}

void apply_semantic_transform(
    progpu_native_analytic_primitive& primitive,
    const progpu_native_scene_state& state) noexcept {
    primitive.transform = compose_affine(primitive.transform, state.transform);
}

void apply_semantic_state(
    progpu_native_geometry_primitive& primitive,
    const progpu_native_scene_state& state) noexcept {
    apply_semantic_transform(primitive, state);
    primitive.color.a *= state.opacity;
}

void apply_semantic_transform(
    progpu_native_geometry_primitive& primitive,
    const progpu_native_scene_state& state) noexcept {
    primitive.transform = compose_affine(primitive.transform, state.transform);
}

void apply_semantic_state(
    progpu_native_scene_point_batch& batch,
    const progpu_native_scene_state& state) noexcept {
    apply_semantic_transform(batch, state);
    batch.color.a *= state.opacity;
}

void apply_semantic_transform(
    progpu_native_scene_point_batch& batch,
    const progpu_native_scene_state& state) noexcept {
    batch.transform = compose_affine(batch.transform, state.transform);
}

void apply_semantic_transform(
    progpu_native_scene_vertex_mesh& mesh,
    const progpu_native_scene_state& state) noexcept {
    mesh.transform = compose_affine(mesh.transform, state.transform);
}

void apply_semantic_transform(
    progpu_native_scene_stroke& stroke,
    const progpu_native_scene_state& state) noexcept {
    stroke.transform = compose_affine(stroke.transform, state.transform);
}

void apply_semantic_state(
    progpu_native_scene_path_fill& path,
    const progpu_native_scene_state& state) noexcept {
    apply_semantic_transform(path, state);
    path.color.a *= state.opacity;
}

void apply_semantic_transform(
    progpu_native_scene_path_fill& path,
    const progpu_native_scene_state& state) noexcept {
    path.transform = compose_affine(path.transform, state.transform);
}

void apply_semantic_state(
    progpu_native_path_fill& path,
    const progpu_native_scene_state& state) noexcept {
    apply_semantic_transform(path, state);
    path.color.a *= state.opacity;
}

void apply_semantic_transform(
    progpu_native_path_fill& path,
    const progpu_native_scene_state& state) noexcept {
    path.transform = compose_affine(path.transform, state.transform);
}

void apply_semantic_state(
    progpu_native_positioned_glyph& glyph,
    const progpu_native_scene_state& state) noexcept {
    apply_semantic_transform(glyph, state);
    glyph.color.a *= state.opacity;
}

void apply_semantic_transform(
    progpu_native_positioned_glyph& glyph,
    const progpu_native_scene_state& state) noexcept {
    transform_point(
        state.transform,
        glyph.position.x,
        glyph.position.y,
        glyph.position.x,
        glyph.position.y);
    transform_vector(
        state.transform,
        glyph.basis_x.x,
        glyph.basis_x.y,
        glyph.basis_x.x,
        glyph.basis_x.y);
    transform_vector(
        state.transform,
        glyph.basis_y.x,
        glyph.basis_y.y,
        glyph.basis_y.x,
        glyph.basis_y.y);
}

void apply_semantic_state(
    progpu_native_scene_image_draw& image,
    const progpu_native_scene_state& state) noexcept {
    image.transform = compose_affine(image.transform, state.transform);
    image.opacity *= state.opacity;
}

void snap_semantic_image_point(
    float& x,
    float& y,
    float dpi_scale) noexcept {
    x = round_to_even(x * dpi_scale) / dpi_scale;
    y = round_to_even(y * dpi_scale) / dpi_scale;
}

scissor resolve_semantic_scissor(
    const progpu_native_scene_state& state,
    std::uint32_t target_width,
    std::uint32_t target_height,
    float dpi_scale) noexcept {
    return resolve_semantic_scissor(state, target_width, target_height,
        {sizeof(progpu_native_scene_presentation), 0U, 0U,
            target_width, target_height, dpi_scale, dpi_scale, 0U});
}

scissor resolve_semantic_scissor(const progpu_native_scene_state& state,
    std::uint32_t target_width, std::uint32_t target_height,
    const progpu_native_scene_presentation& presentation) noexcept {
    const scissor viewport{presentation.viewport_x, presentation.viewport_y,
        presentation.viewport_width, presentation.viewport_height, true};
    scissor result = intersect_semantic_scissors(viewport,
        {0U, 0U, target_width, target_height, true});
    if ((state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) == 0U) {
        return result;
    }
    const auto& clip = state.clip_rect;
    const auto edges = presentation_clip_edges(clip, presentation);
    const float left = std::clamp(
        edges[0],
        0.0F,
        static_cast<float>(target_width));
    const float top = std::clamp(
        edges[1],
        0.0F,
        static_cast<float>(target_height));
    const float right = std::clamp(
        edges[2],
        0.0F,
        static_cast<float>(target_width));
    const float bottom = std::clamp(
        edges[3],
        0.0F,
        static_cast<float>(target_height));
    const float snapped_left = snap_scissor_coordinate(left);
    const float snapped_top = snap_scissor_coordinate(top);
    const float snapped_right = snap_scissor_coordinate(right);
    const float snapped_bottom = snap_scissor_coordinate(bottom);
    if (snapped_right <= snapped_left || snapped_bottom <= snapped_top) {
        result.width = 0U;
        result.height = 0U;
        result.drawable = false;
        return result;
    }
    result.x = static_cast<std::uint32_t>(std::floor(snapped_left));
    result.y = static_cast<std::uint32_t>(std::floor(snapped_top));
    result.width = static_cast<std::uint32_t>(
        std::ceil(snapped_right)) - result.x;
    result.height = static_cast<std::uint32_t>(
        std::ceil(snapped_bottom)) - result.y;
    return intersect_semantic_scissors(result, viewport);
}

progpu_native_scene_layer semantic_default_layer() noexcept {
    progpu_native_scene_layer layer{};
    layer.struct_size = sizeof(layer);
    layer.opacity = 1.0F;
    layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    layer.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    return layer;
}

scissor resolve_semantic_layer_scissor(
    const progpu_native_scene_layer& layer,
    std::uint32_t target_width,
    std::uint32_t target_height,
    float dpi_scale) noexcept {
    return resolve_semantic_layer_scissor(layer, target_width, target_height,
        {sizeof(progpu_native_scene_presentation), 0U, 0U,
            target_width, target_height, dpi_scale, dpi_scale, 0U});
}

scissor resolve_semantic_layer_scissor(const progpu_native_scene_layer& layer,
    std::uint32_t target_width, std::uint32_t target_height,
    const progpu_native_scene_presentation& presentation) noexcept {
    auto state = semantic_identity_state();
    if ((layer.flags & PROGPU_NATIVE_SCENE_LAYER_BOUNDS) != 0U) {
        state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
        state.clip_rect = layer.bounds;
    }
    return resolve_semantic_scissor(
        state,
        target_width,
        target_height,
        presentation);
}

scissor intersect_semantic_scissors(
    const scissor& first,
    const scissor& second) noexcept {
    const std::uint32_t left = std::max(first.x, second.x);
    const std::uint32_t top = std::max(first.y, second.y);
    const std::uint64_t right = std::min(
        static_cast<std::uint64_t>(first.x) + first.width,
        static_cast<std::uint64_t>(second.x) + second.width);
    const std::uint64_t bottom = std::min(
        static_cast<std::uint64_t>(first.y) + first.height,
        static_cast<std::uint64_t>(second.y) + second.height);
    if (!first.drawable || !second.drawable || right <= left ||
        bottom <= top) {
        return {left, top, 0U, 0U, false};
    }
    return {
        left,
        top,
        static_cast<std::uint32_t>(right - left),
        static_cast<std::uint32_t>(bottom - top),
        true};
}

scissor resolve_semantic_target_scissor(
    const progpu_native_scene_state& state,
    const scissor& target,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    float dpi_scale) noexcept {
    // A local bitmap-cache page may be larger than the presentation frame
    // (RenderAtScale, or an offscreen subtree). Clip against that page's
    // coordinate domain before localizing, not against the root window.
    auto clipped = intersect_semantic_scissors(
        resolve_semantic_scissor(
            state,
            target_domain_extent(frame_width, target.x, target.width),
            target_domain_extent(frame_height, target.y, target.height),
            dpi_scale),
        target);
    if (!clipped.drawable) {
        return {0U, 0U, 0U, 0U, false};
    }
    clipped.x -= target.x;
    clipped.y -= target.y;
    return clipped;
}

progpu_native_scene_state localize_semantic_state(
    progpu_native_scene_state state,
    const scissor& target,
    float dpi_scale) noexcept {
    state.transform.m31 -= static_cast<float>(target.x) / dpi_scale;
    state.transform.m32 -= static_cast<float>(target.y) / dpi_scale;
    return state;
}

scissor resolve_semantic_target_scissor(const progpu_native_scene_state& state,
    const scissor& target, std::uint32_t frame_width, std::uint32_t frame_height,
    const progpu_native_scene_presentation& presentation) noexcept {
    auto clipped = intersect_semantic_scissors(resolve_semantic_scissor(state,
        target_domain_extent(frame_width, target.x, target.width),
        target_domain_extent(frame_height, target.y, target.height), presentation), target);
    if (!clipped.drawable) return {0U, 0U, 0U, 0U, false};
    clipped.x -= target.x;
    clipped.y -= target.y;
    return clipped;
}

void localize_semantic_point(float& x, float& y, const scissor& target,
    const progpu_native_scene_presentation& presentation, float raster_dpi) noexcept {
    if (presentation.dpi_scale_x == raster_dpi && presentation.dpi_scale_y == raster_dpi &&
        presentation.viewport_x == 0U && presentation.viewport_y == 0U) {
        x -= static_cast<float>(target.x) / raster_dpi;
        y -= static_cast<float>(target.y) / raster_dpi;
        return;
    }
    // Subtract integer physical origins before narrowing, retaining small local
    // displacements even when both origins exceed exact float integer range.
    const float offset_x = static_cast<float>(static_cast<double>(presentation.viewport_x) - target.x) / raster_dpi;
    const float offset_y = static_cast<float>(static_cast<double>(presentation.viewport_y) - target.y) / raster_dpi;
    const auto mapped = scale_translate_four({x, y, 0.0F, 0.0F},
        {presentation.dpi_scale_x / raster_dpi, presentation.dpi_scale_y / raster_dpi, 1.0F, 1.0F},
        {offset_x, offset_y, 0.0F, 0.0F});
    x = mapped[0];
    y = mapped[1];
}

progpu_native_scene_state localize_semantic_state(progpu_native_scene_state state,
    const scissor& target, const progpu_native_scene_presentation& presentation,
    float raster_dpi) noexcept {
    if (presentation.dpi_scale_x == raster_dpi && presentation.dpi_scale_y == raster_dpi &&
        presentation.viewport_x == 0U && presentation.viewport_y == 0U)
        return localize_semantic_state(state, target, raster_dpi);
    const auto linear = scale_translate_four(
        {state.transform.m11, state.transform.m12, state.transform.m21, state.transform.m22},
        {presentation.dpi_scale_x / raster_dpi, presentation.dpi_scale_y / raster_dpi,
            presentation.dpi_scale_x / raster_dpi, presentation.dpi_scale_y / raster_dpi}, {});
    state.transform.m11 = linear[0];
    state.transform.m12 = linear[1];
    state.transform.m21 = linear[2];
    state.transform.m22 = linear[3];
    localize_semantic_point(state.transform.m31, state.transform.m32, target, presentation, raster_dpi);
    return state;
}

semantic_layer_target_cursor::semantic_layer_target_cursor(
    const std::byte* bytes,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    float dpi_scale) noexcept
    : semantic_layer_target_cursor(bytes, frame_width, frame_height,
        {sizeof(progpu_native_scene_presentation), 0U, 0U,
            frame_width, frame_height, dpi_scale, dpi_scale, 0U}) {
}

semantic_layer_target_cursor::semantic_layer_target_cursor(
    const std::byte* bytes, std::uint32_t frame_width, std::uint32_t frame_height,
    const progpu_native_scene_presentation& presentation) noexcept
    : bytes_(bytes),
      frame_extent_{presentation.viewport_x, presentation.viewport_y,
          presentation.viewport_width, presentation.viewport_height, true},
      frame_width_(frame_width),
      frame_height_(frame_height),
      frame_presentation_(presentation) {
}

scissor semantic_layer_target_cursor::advance(
    const progpu_native_scene_command& command) noexcept {
    if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
        auto layer = semantic_default_layer();
        if (command.payload_size != 0U) {
            std::memcpy(
                &layer,
                bytes_ + command.payload_offset,
                sizeof(layer));
        }
        const bool materialized = scene::layer_requires_materialization(layer);
        scope_materialized_[scope_depth_++] = materialized;
        if (materialized) {
            auto presentation = current_presentation();
            const bool local_cache =
                (layer.flags &
                    PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE) != 0U;
            if (local_cache) {
                const auto local_extent = [](float value, float dpi_scale) noexcept {
                    const double pixels = std::ceil(
                        static_cast<double>(value) * dpi_scale);
                    return pixels >= static_cast<double>(
                            std::numeric_limits<std::uint32_t>::max())
                        ? std::numeric_limits<std::uint32_t>::max()
                        : static_cast<std::uint32_t>(pixels);
                };
                const std::uint32_t width = local_extent(
                    layer.bounds.width, presentation.dpi_scale_x);
                const std::uint32_t height = local_extent(
                    layer.bounds.height, presentation.dpi_scale_y);
                extents_[materialized_depth_] = {
                    0U, 0U, width, height, width != 0U && height != 0U};
                // A local cache page is a new device domain. Window placement
                // must not be inherited by its child clips or nested layers.
                presentation.viewport_x = 0U;
                presentation.viewport_y = 0U;
                presentation.viewport_width = width;
                presentation.viewport_height = height;
            } else {
                const auto parent = current();
                const auto declared = resolve_semantic_layer_scissor(
                    layer,
                    target_domain_extent(frame_width_, parent.x, parent.width),
                    target_domain_extent(frame_height_, parent.y, parent.height),
                    presentation);
                extents_[materialized_depth_] =
                    intersect_semantic_scissors(parent, declared);
            }
            presentations_[materialized_depth_++] = presentation;
        }
    } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
        if (scope_materialized_[--scope_depth_]) {
            --materialized_depth_;
        }
    }
    return current();
}

scissor semantic_layer_target_cursor::current() const noexcept {
    return materialized_depth_ == 0U
        ? frame_extent_
        : extents_[materialized_depth_ - 1U];
}

progpu_native_scene_presentation semantic_layer_target_cursor::current_presentation() const noexcept {
    return materialized_depth_ == 0U
        ? frame_presentation_
        : presentations_[materialized_depth_ - 1U];
}

} // namespace progpu::native::semantic
