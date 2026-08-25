#include "progpu_native_semantic_state.hpp"

#include "progpu_native_geometry.hpp"
#include "progpu_native_scene.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

namespace progpu::native::semantic {

namespace {

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

float wpf_guideline_offset(float value) noexcept {
    // CFloatFPU::OffsetToRounded resolves every half-integer toward the
    // numerically larger integer and leaves floats with no fractional bits
    // unchanged.
    constexpr float minimum_float_without_fraction = 8388608.0F;
    if (!(std::abs(value) < minimum_float_without_fraction)) {
        return 0.0F;
    }
    const float rounded = std::floor(value + 0.5F);
    float offset = rounded - value;
    if (offset <= -0.5F) {
        offset += 1.0F;
    }
    return offset;
}

} // namespace

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
    : bytes_(bytes),
      header_(header),
      dpi_scale_(dpi_scale),
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

progpu_native_scene_state semantic_state_cursor::resolve_guidelines(
    progpu_native_scene_state state) const noexcept {
    if ((state.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) == 0U ||
        !std::isfinite(dpi_scale_) || dpi_scale_ <= 0.0F) {
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
    std::size_t offset = resource.payload_offset + sizeof(guidelines);
    if (guidelines.guideline_x_count != 0U) {
        double coordinate = 0.0;
        std::memcpy(&coordinate, bytes_ + offset, sizeof(coordinate));
        const float physical =
            static_cast<float>(coordinate) * dpi_scale_;
        state.transform.m31 += wpf_guideline_offset(physical) / dpi_scale_;
        offset += sizeof(coordinate);
    }
    if (guidelines.guideline_y_count != 0U) {
        double coordinate = 0.0;
        std::memcpy(&coordinate, bytes_ + offset, sizeof(coordinate));
        const float physical =
            static_cast<float>(coordinate) * dpi_scale_;
        state.transform.m32 += wpf_guideline_offset(physical) / dpi_scale_;
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
    scissor result{0U, 0U, target_width, target_height, true};
    if ((state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) == 0U) {
        return result;
    }
    const auto& clip = state.clip_rect;
    const float left = std::clamp(
        clip.x * dpi_scale,
        0.0F,
        static_cast<float>(target_width));
    const float top = std::clamp(
        clip.y * dpi_scale,
        0.0F,
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
    return result;
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
    auto state = semantic_identity_state();
    if ((layer.flags & PROGPU_NATIVE_SCENE_LAYER_BOUNDS) != 0U) {
        state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
        state.clip_rect = layer.bounds;
    }
    return resolve_semantic_scissor(
        state,
        target_width,
        target_height,
        dpi_scale);
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
    auto clipped = intersect_semantic_scissors(
        resolve_semantic_scissor(
            state,
            frame_width,
            frame_height,
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

semantic_layer_target_cursor::semantic_layer_target_cursor(
    const std::byte* bytes,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    float dpi_scale) noexcept
    : bytes_(bytes),
      frame_extent_{0U, 0U, frame_width, frame_height, true},
      frame_width_(frame_width),
      frame_height_(frame_height),
      dpi_scale_(dpi_scale) {
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
            const bool local_cache =
                (layer.flags &
                    PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE) != 0U;
            if (local_cache) {
                const auto local_extent = [&](float value) noexcept {
                    const double pixels = std::ceil(
                        static_cast<double>(value) * dpi_scale_);
                    return pixels >= static_cast<double>(
                            std::numeric_limits<std::uint32_t>::max())
                        ? std::numeric_limits<std::uint32_t>::max()
                        : static_cast<std::uint32_t>(pixels);
                };
                const std::uint32_t width = local_extent(
                    layer.bounds.width);
                const std::uint32_t height = local_extent(
                    layer.bounds.height);
                extents_[materialized_depth_++] = {
                    0U, 0U, width, height, width != 0U && height != 0U};
            } else {
                const auto declared = resolve_semantic_layer_scissor(
                    layer,
                    frame_width_,
                    frame_height_,
                    dpi_scale_);
                extents_[materialized_depth_++] =
                    intersect_semantic_scissors(current(), declared);
            }
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

} // namespace progpu::native::semantic
