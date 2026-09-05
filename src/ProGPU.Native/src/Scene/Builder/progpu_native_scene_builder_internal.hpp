#pragma once

#include "progpu_native_scene_builder.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <span>
#include <vector>

namespace progpu::native {

struct semantic_scene_builder::implementation final {
    struct resource_entry final {
        progpu_native_scene_resource record{};
        std::vector<std::byte> payload{};
        std::vector<std::byte> auxiliary{};
        bool brush_table = false;
        bool text_style_table = false;
        bool rgba8_image = false;
        bool bgra8_image = false;
        bool r8_image = false;
        std::uint32_t image_width = 0U;
        std::uint32_t image_height = 0U;
        std::uint32_t image_row_bytes = 0U;
        std::uint32_t glyph_outline_count = 0U;
    };

    struct command_entry final {
        progpu_native_scene_command record{};
        std::vector<std::byte> payload{};
    };

    std::uint64_t scene_id = 0U;
    std::uint64_t generation = 0U;
    std::vector<resource_entry> resources{};
    std::vector<command_entry> commands{};
    std::vector<progpu_native_scene_brush> brushes{};
    std::vector<progpu_native_scene_gradient_stop> gradient_stops{};
    std::vector<progpu_native_scene_text_style> text_styles{};
    std::uint32_t brush_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t text_style_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t stack_depth = 0U;
    std::uint32_t materialized_layer_depth = 0U;
    std::uint32_t maximum_stack_depth = 0U;
    std::array<std::uint8_t, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH>
        stack_kinds{};
    std::uint64_t arena_reserve = 0U;
    scene_build_error error = scene_build_error::none;

    bool fail(scene_build_error value) noexcept {
        error = value;
        return false;
    }

    bool valid_state_index(
        std::uint32_t index,
        bool allow_per_point = false) const noexcept {
        if (index == PROGPU_NATIVE_SCENE_NO_INDEX) {
            return true;
        }
        if (index >= resources.size() || resources[index].record.kind !=
                PROGPU_NATIVE_SCENE_RESOURCE_STATE ||
            resources[index].payload.size() !=
                sizeof(progpu_native_scene_state)) {
            return false;
        }
        progpu_native_scene_state state{};
        std::memcpy(
            &state,
            resources[index].payload.data(),
            sizeof(state));
        if ((state.flags & PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) == 0U) {
            return true;
        }
        if (state.guideline_resource_index >= resources.size()) {
            return false;
        }
        const auto& guidelines = resources[state.guideline_resource_index];
        if (guidelines.record.kind !=
                PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET ||
            guidelines.payload.size() <
                sizeof(progpu_native_scene_guideline_set)) {
            return false;
        }
        progpu_native_scene_guideline_set header{};
        std::memcpy(&header, guidelines.payload.data(), sizeof(header));
        return (header.flags &
                PROGPU_NATIVE_SCENE_GUIDELINE_COMPOSITE_ONLY) == 0U &&
            (allow_per_point || (header.flags &
                PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT) == 0U);
    }

    bool try_merge_image_draw(
        std::uint32_t image_resource_index,
        const progpu_native_scene_image_draw& image,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index,
        const progpu_native_scene_image_sampling_options*
            sampling_options);
};

namespace scene_builder_detail {

inline bool finite_color(const progpu_native_color& color) noexcept {
    return std::isfinite(color.r) && std::isfinite(color.g) &&
        std::isfinite(color.b) && std::isfinite(color.a);
}

inline bool finite_transform(const progpu_native_affine_2d& value) noexcept {
    return std::isfinite(value.m11) && std::isfinite(value.m12) &&
        std::isfinite(value.m21) && std::isfinite(value.m22) &&
        std::isfinite(value.m31) && std::isfinite(value.m32);
}

inline bool finite_rect(const progpu_native_image_rect& value) noexcept {
    return std::isfinite(value.x) && std::isfinite(value.y) &&
        std::isfinite(value.width) && std::isfinite(value.height) &&
        value.width >= 0.0F && value.height >= 0.0F;
}

inline bool finite_primitive(
    const progpu_native_analytic_primitive& value) noexcept {
    return value.kind <= PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE &&
        (value.flags & ~(PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
            PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
            PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE |
            PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK |
            PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK)) == 0U &&
        std::isfinite(value.x) && std::isfinite(value.y) &&
        std::isfinite(value.width) && std::isfinite(value.height) &&
        value.width >= 0.0F && value.height >= 0.0F &&
        std::isfinite(value.corner_radius) && value.corner_radius >= 0.0F &&
        std::isfinite(value.stroke_thickness) &&
        value.stroke_thickness >= 0.0F && finite_color(value.color) &&
        finite_transform(value.transform);
}

template<typename T>
std::vector<std::byte> copy_bytes(std::span<const T> values) {
    std::vector<std::byte> result(values.size_bytes());
    if (!result.empty()) {
        std::memcpy(result.data(), values.data(), values.size_bytes());
    }
    return result;
}

inline bool same_color(
    const progpu_native_color& left,
    const progpu_native_color& right) noexcept {
    return std::bit_cast<std::uint32_t>(left.r) ==
            std::bit_cast<std::uint32_t>(right.r) &&
        std::bit_cast<std::uint32_t>(left.g) ==
            std::bit_cast<std::uint32_t>(right.g) &&
        std::bit_cast<std::uint32_t>(left.b) ==
            std::bit_cast<std::uint32_t>(right.b) &&
        std::bit_cast<std::uint32_t>(left.a) ==
            std::bit_cast<std::uint32_t>(right.a);
}

} // namespace scene_builder_detail
} // namespace progpu::native
