#pragma once

#include "progpu_native_scene_builder.hpp"

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
    std::uint32_t brush_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t stack_depth = 0U;
    std::uint32_t maximum_stack_depth = 0U;
    std::uint64_t arena_reserve = 0U;
    scene_build_error error = scene_build_error::none;

    bool fail(scene_build_error value) noexcept {
        error = value;
        return false;
    }

    bool valid_state_index(std::uint32_t index) const noexcept {
        return index == PROGPU_NATIVE_SCENE_NO_INDEX ||
            (index < resources.size() && resources[index].record.kind ==
                PROGPU_NATIVE_SCENE_RESOURCE_STATE);
    }
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
