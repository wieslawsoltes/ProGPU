#include "progpu_native_draw_state.hpp"

#include "progpu_native_geometry_base.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace {

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

void normalize_group_mask_radii_impl(
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
            (chain.flags &
                ~PROGPU_NATIVE_CLIP_CHAIN_STAGED_SIGNED_WINDING) == 0U &&
            chain.paths != nullptr &&
            chain.path_count > 0U && chain.path_count <= (1U << 16U) &&
            chain.segments != nullptr && chain.segment_count > 0U &&
            chain.segment_count <= (1U << 24U) &&
            chain.boolean_node_count <= (1U << 22U) &&
            (chain.boolean_node_count == 0U ||
                chain.boolean_nodes != nullptr);
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
    normalize_group_mask_radii_impl(resolved);
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
         requested->kind != PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW &&
         requested->kind != PROGPU_NATIVE_GROUP_EFFECT_BOX_BLUR) ||
        requested->flags != 0U || requested->revision == 0U ||
        requested->reserved != 0U || requested->reserved2 != 0U ||
        !std::isfinite(requested->sigma_x) ||
        !std::isfinite(requested->sigma_y) ||
        requested->sigma_x < 0.0F || requested->sigma_y < 0.0F) {
        return false;
    }
    if ((requested->kind == PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR ||
         requested->kind == PROGPU_NATIVE_GROUP_EFFECT_BOX_BLUR) &&
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
    const float maximum_physical_extent =
        requested->kind == PROGPU_NATIVE_GROUP_EFFECT_BOX_BLUR
            ? 128.0F
            : 128.0F / 3.0F;
    if (requested->sigma_x * dpi_scale > maximum_physical_extent ||
        requested->sigma_y * dpi_scale > maximum_physical_extent) {
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

} // namespace

void normalize_group_mask_radii(
    progpu_native_group_mask& mask) noexcept {
    normalize_group_mask_radii_impl(mask);
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
