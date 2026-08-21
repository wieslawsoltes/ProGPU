#include "progpu_native_semantic_brush.hpp"

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

namespace progpu::native::semantic {
namespace {

template<typename T>
T read_record(const std::byte* bytes, std::size_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes + offset, sizeof(value));
    return value;
}

bool finite_color(const progpu_native_color& color) noexcept {
    return std::isfinite(color.r) && std::isfinite(color.g) &&
        std::isfinite(color.b) && std::isfinite(color.a);
}

bool finite_point(const progpu_native_point& point) noexcept {
    return std::isfinite(point.x) && std::isfinite(point.y);
}

bool finite_array(const float* values, std::size_t count) noexcept {
    for (std::size_t index = 0U; index < count; ++index) {
        if (!std::isfinite(values[index])) {
            return false;
        }
    }
    return true;
}

bool supported_brush_kind(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_BRUSH_SOLID ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_HATCH_PATTERN ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_CROSS_HATCH ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_SWEEP_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE;
}

bool gradient_brush_kind(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_SWEEP_GRADIENT;
}

} // namespace

std::uint32_t semantic_brush_stored_stop_count(
    const progpu_native_scene_brush& brush) noexcept {
    if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE) {
        return brush.stop_count == 0U ||
                brush.color_interpolation_mode ==
                    PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB
            ? 0U
            : static_cast<std::uint32_t>(
                PROGPU_NATIVE_SCENE_PERLIN_TABLE_RECORDS);
    }
    return gradient_brush_kind(brush.type) ? brush.stop_count : 0U;
}

bool is_valid_semantic_brush(
    const progpu_native_scene_brush& brush,
    std::span<const progpu_native_scene_gradient_stop> stops) noexcept {
    if (stops.size() > std::numeric_limits<std::uint32_t>::max()) {
        return false;
    }
    const std::uint32_t stop_count =
        static_cast<std::uint32_t>(stops.size());
    const auto* stop_bytes =
        reinterpret_cast<const std::byte*>(stops.data());
    const std::uint32_t spread = brush.spread_method & 0x7fffffffU;
    const bool outside_color =
        (brush.spread_method & 0x80000000U) != 0U;
    if (!supported_brush_kind(brush.type) ||
        !std::isfinite(brush.opacity) || brush.opacity < 0.0F ||
        brush.opacity > 1.0F || !finite_point(brush.start_point) ||
        !finite_point(brush.end_point) || !finite_point(brush.center) ||
        !std::isfinite(brush.radius) || !std::isfinite(brush.radius_y) ||
        spread > PROGPU_NATIVE_SCENE_GRADIENT_DECAL ||
        brush.color_interpolation_mode >
            PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SCRGB ||
        (outside_color && brush.type !=
            PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT) ||
        brush.reserved0 != 0U || brush.reserved1 != 0U ||
        !finite_array(brush.offsets0, 4U) ||
        !finite_array(brush.offsets1, 4U) ||
        !finite_array(brush.coordinate_transform0, 4U) ||
        !finite_array(brush.coordinate_transform1, 4U) ||
        brush.coordinate_transform0[3] != 0.0F ||
        brush.coordinate_transform1[3] != 0.0F) {
        return false;
    }
    for (const auto& color : brush.colors) {
        if (!finite_color(color)) {
            return false;
        }
    }

    if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE) {
        const std::uint32_t table_count =
            semantic_brush_stored_stop_count(brush);
        if (outside_color || spread > 1U ||
            brush.stop_count > PROGPU_NATIVE_SCENE_MAX_PERLIN_OCTAVES ||
            (table_count == 0U && brush.stop_offset != 0U) ||
            brush.stop_offset > stop_count ||
            table_count > stop_count - brush.stop_offset) {
            return false;
        }
        for (std::uint32_t index = 0U; index < table_count; ++index) {
            const auto record =
                read_record<progpu_native_scene_gradient_stop>(
                    stop_bytes,
                    static_cast<std::size_t>(brush.stop_offset + index) *
                        sizeof(progpu_native_scene_gradient_stop));
            if (!finite_color(record.color) ||
                !std::isfinite(record.offset) ||
                record.reserved0 != 0U || record.reserved1 != 0U ||
                record.reserved2 != 0U) {
                return false;
            }
        }
        return true;
    }

    if ((brush.type == PROGPU_NATIVE_SCENE_BRUSH_HATCH_PATTERN ||
            brush.type == PROGPU_NATIVE_SCENE_BRUSH_CROSS_HATCH) &&
        (brush.center.x <= 0.0F || brush.center.y < 0.0F)) {
        return false;
    }
    if (!gradient_brush_kind(brush.type)) {
        return brush.stop_count == 0U && brush.stop_offset == 0U &&
            brush.spread_method == 0U &&
            brush.color_interpolation_mode ==
                PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
    }
    if (brush.stop_count == 0U || brush.stop_offset > stop_count ||
        brush.stop_count > stop_count - brush.stop_offset) {
        return false;
    }
    if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT &&
        (brush.radius < 0.0F || brush.radius_y < 0.0F ||
            (brush.radius == 0.0F && brush.radius_y == 0.0F))) {
        return false;
    }
    if (brush.type ==
            PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT &&
        (brush.radius < 0.0F || brush.radius_y < 0.0F)) {
        return false;
    }

    float previous_offset = -std::numeric_limits<float>::infinity();
    for (std::uint32_t index = 0U; index < brush.stop_count; ++index) {
        const auto stop = read_record<progpu_native_scene_gradient_stop>(
            stop_bytes,
            static_cast<std::size_t>(brush.stop_offset + index) *
                sizeof(progpu_native_scene_gradient_stop));
        if (!finite_color(stop.color) || !std::isfinite(stop.offset) ||
            stop.offset < previous_offset || stop.reserved0 != 0U ||
            stop.reserved1 != 0U || stop.reserved2 != 0U) {
            return false;
        }
        previous_offset = stop.offset;
    }
    return true;
}

} // namespace progpu::native::semantic
