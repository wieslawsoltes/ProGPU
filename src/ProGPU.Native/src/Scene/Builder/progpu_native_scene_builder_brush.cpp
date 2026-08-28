#include "progpu_native_scene_builder.hpp"
#include "progpu_native_scene_builder_internal.hpp"

#include <algorithm>
#include <bit>
#include <cmath>
#include <limits>
#include <new>
#include <utility>

// Direct native port provenance: ProGPU-owned NativeBrushTableBuilder at
// checkpoint 8bf3cd44. The standalone C++ builder retains one canonical brush
// and gradient-stop page, validates before mutation, and rewrites local stop
// offsets once per changed scene generation.
namespace progpu::native {
namespace {

using scene_builder_detail::finite_color;
using scene_builder_detail::same_color;

bool finite_point(progpu_native_point value) noexcept {
    return std::isfinite(value.x) && std::isfinite(value.y);
}

template<std::size_t Size>
bool finite_values(const float (&values)[Size]) noexcept {
    return std::all_of(
        std::begin(values), std::end(values),
        [](float value) { return std::isfinite(value); });
}

bool gradient_kind(std::uint32_t kind) noexcept {
    return kind == PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT ||
        kind == PROGPU_NATIVE_SCENE_BRUSH_SWEEP_GRADIENT;
}

bool valid_gradient_stop(
    const progpu_native_scene_gradient_stop& stop) noexcept {
    return finite_color(stop.color) && std::isfinite(stop.offset) &&
        stop.reserved0 == 0U && stop.reserved1 == 0U &&
        stop.reserved2 == 0U;
}

float hatch_dash(
    const progpu_native_scene_gradient_stop& record2,
    const progpu_native_scene_gradient_stop& record3,
    std::uint32_t index) noexcept {
    switch (index) {
        case 0U: return record2.color.r;
        case 1U: return record2.color.g;
        case 2U: return record2.color.b;
        case 3U: return record2.color.a;
        case 4U: return record2.offset;
        default: return record3.color.r;
    }
}

bool valid_hatch_pattern_set(
    const progpu_native_scene_brush& brush,
    std::span<const progpu_native_scene_gradient_stop> records) noexcept {
    // Direct native port provenance: ProGPU-owned four-record family packing
    // and validation in ProGPU.Scene.Native/NativeBrushTableBuilder.cs and
    // ProGPU.Backend.Native/NativeSceneStreamBuilder.cs in this checkpoint.
    const std::uint32_t family_count = brush.spread_method;
    if (family_count == 0U || brush.radius < 0.0F ||
        brush.radius_y != 0.0F ||
        brush.color_interpolation_mode !=
            PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB ||
        family_count > std::numeric_limits<std::uint32_t>::max() /
            PROGPU_NATIVE_SCENE_HATCH_PATTERN_RECORDS_PER_FAMILY ||
        brush.stop_count != family_count *
            PROGPU_NATIVE_SCENE_HATCH_PATTERN_RECORDS_PER_FAMILY ||
        records.size() != brush.stop_count) {
        return false;
    }
    for (std::uint32_t family = 0U; family < family_count; ++family) {
        const std::size_t base = static_cast<std::size_t>(family) *
            PROGPU_NATIVE_SCENE_HATCH_PATTERN_RECORDS_PER_FAMILY;
        const auto& record0 = records[base];
        const auto& record1 = records[base + 1U];
        const auto& record2 = records[base + 2U];
        const auto& record3 = records[base + 3U];
        if (!valid_gradient_stop(record0) || !valid_gradient_stop(record1) ||
            !valid_gradient_stop(record2) || !valid_gradient_stop(record3) ||
            record0.offset <= 0.0F || brush.radius > record0.offset ||
            record1.color.a != 0.0F ||
            record1.offset != 0.0F || record3.color.g != 0.0F ||
            record3.color.b != 0.0F || record3.color.a != 0.0F ||
            record3.offset != 0.0F) {
            return false;
        }
        const float direction_length =
            record0.color.b * record0.color.b +
            record0.color.a * record0.color.a;
        const float dash_count_value = record1.color.b;
        if (std::abs(direction_length - 1.0F) > 0.001F ||
            dash_count_value < 0.0F ||
            dash_count_value > static_cast<float>(
                PROGPU_NATIVE_SCENE_HATCH_PATTERN_MAX_DASHES) ||
            std::floor(dash_count_value) != dash_count_value) {
            return false;
        }
        const auto dash_count = static_cast<std::uint32_t>(dash_count_value);
        float period = 0.0F;
        bool draws = false;
        for (std::uint32_t dash = 0U; dash < dash_count; ++dash) {
            const float value = hatch_dash(record2, record3, dash);
            period += std::abs(value);
            draws = draws || value >= 0.0F;
        }
        for (std::uint32_t dash = dash_count;
             dash < PROGPU_NATIVE_SCENE_HATCH_PATTERN_MAX_DASHES;
             ++dash) {
            if (hatch_dash(record2, record3, dash) != 0.0F) {
                return false;
            }
        }
        const float tolerance = std::max(1.0F, period) * 0.00001F;
        if ((dash_count == 0U && record1.color.g != 0.0F) ||
            (dash_count != 0U && (!draws || period <= 0.0F ||
                std::abs(period - record1.color.g) > tolerance))) {
            return false;
        }
    }
    return true;
}

bool valid_brush_input(
    const progpu_native_scene_brush& brush,
    std::span<const progpu_native_scene_gradient_stop> stops) noexcept {
    const std::uint32_t spread = brush.spread_method & 0x7fffffffU;
    const bool outside = (brush.spread_method & 0x80000000U) != 0U;
    if (brush.type > PROGPU_NATIVE_SCENE_BRUSH_HATCH_PATTERN_SET ||
        !std::isfinite(brush.opacity) || brush.opacity < 0.0F ||
        brush.opacity > 1.0F || !finite_point(brush.start_point) ||
        !finite_point(brush.end_point) || !finite_point(brush.center) ||
        !std::isfinite(brush.radius) || !std::isfinite(brush.radius_y) ||
        (brush.type != PROGPU_NATIVE_SCENE_BRUSH_HATCH_PATTERN_SET &&
            spread > PROGPU_NATIVE_SCENE_GRADIENT_DECAL) ||
        brush.color_interpolation_mode >
            PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SCRGB ||
        (outside && brush.type !=
            PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT) ||
        brush.reserved0 != 0U || brush.reserved1 != 0U ||
        !finite_values(brush.offsets0) || !finite_values(brush.offsets1) ||
        !finite_values(brush.coordinate_transform0) ||
        !finite_values(brush.coordinate_transform1) ||
        brush.coordinate_transform0[3] != 0.0F ||
        brush.coordinate_transform1[3] != 0.0F ||
        stops.size() > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS) {
        return false;
    }
    for (const auto& color : brush.colors) {
        if (!finite_color(color)) {
            return false;
        }
    }
    if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE) {
        const std::size_t expected = brush.stop_count == 0U ||
                brush.color_interpolation_mode ==
                    PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB
            ? std::size_t{0U}
            : static_cast<std::size_t>(
                PROGPU_NATIVE_SCENE_PERLIN_TABLE_RECORDS);
        if (outside || spread > 1U ||
            brush.stop_count > PROGPU_NATIVE_SCENE_MAX_PERLIN_OCTAVES ||
            stops.size() != expected) {
            return false;
        }
        return std::all_of(stops.begin(), stops.end(), valid_gradient_stop);
    }
    if (brush.type == PROGPU_NATIVE_SCENE_BRUSH_HATCH_PATTERN_SET) {
        return !outside && valid_hatch_pattern_set(brush, stops);
    }
    if ((brush.type == PROGPU_NATIVE_SCENE_BRUSH_HATCH_PATTERN ||
            brush.type == PROGPU_NATIVE_SCENE_BRUSH_CROSS_HATCH) &&
        (brush.center.x <= 0.0F || brush.center.y < 0.0F)) {
        return false;
    }
    if (!gradient_kind(brush.type)) {
        return stops.empty() && brush.stop_count == 0U &&
            brush.stop_offset == 0U && brush.spread_method == 0U &&
            brush.color_interpolation_mode ==
                PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
    }
    if (stops.empty() || brush.stop_count != stops.size() ||
        (brush.type == PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT &&
            (brush.radius < 0.0F || brush.radius_y < 0.0F ||
                (brush.radius == 0.0F && brush.radius_y == 0.0F))) ||
        (brush.type == PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT &&
            (brush.radius < 0.0F || brush.radius_y < 0.0F))) {
        return false;
    }
    float previous = -std::numeric_limits<float>::infinity();
    for (const auto& stop : stops) {
        if (!valid_gradient_stop(stop) || stop.offset < previous) {
            return false;
        }
        previous = stop.offset;
    }
    return true;
}

} // namespace

bool semantic_scene_builder::add_solid_brush(
    progpu_native_color color,
    float opacity,
    std::uint32_t& brush_index) noexcept {
    brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!finite_color(color) || !std::isfinite(opacity) ||
        opacity < 0.0F || opacity > 1.0F) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (std::uint32_t index = 0U;
         index < implementation_->brushes.size();
         ++index) {
        const auto& existing = implementation_->brushes[index];
        if (existing.type == PROGPU_NATIVE_SCENE_BRUSH_SOLID &&
            same_color(existing.colors[0], color) &&
            std::bit_cast<std::uint32_t>(existing.opacity) ==
                std::bit_cast<std::uint32_t>(opacity)) {
            brush_index = index;
            implementation_->error = scene_build_error::none;
            return true;
        }
    }
    if (implementation_->brushes.size() >=
        PROGPU_NATIVE_SCENE_MAX_BRUSHES) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        if (implementation_->brush_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX) {
            if (implementation_->resources.size() >=
                PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
                return implementation_->fail(
                    scene_build_error::capacity_exceeded);
            }
            implementation_->resources.reserve(
                implementation_->resources.size() + 1U);
            implementation_->brushes.reserve(
                implementation_->brushes.size() + 1U);
            implementation::resource_entry resource{};
            resource.record.struct_size = sizeof(resource.record);
            resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE;
            resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
            resource.record.resource_id = implementation_->resources.size() + 1U;
            resource.record.generation = implementation_->generation;
            resource.brush_table = true;
            implementation_->brush_resource_index =
                static_cast<std::uint32_t>(implementation_->resources.size());
            implementation_->resources.push_back(std::move(resource));
        } else {
            implementation_->brushes.reserve(
                implementation_->brushes.size() + 1U);
        }
        progpu_native_scene_brush brush{};
        brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
        brush.opacity = opacity;
        brush.colors[0] = color;
        brush.coordinate_transform0[0] = 1.0F;
        brush.coordinate_transform1[1] = 1.0F;
        brush_index = static_cast<std::uint32_t>(
            implementation_->brushes.size());
        implementation_->brushes.push_back(brush);
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

bool semantic_scene_builder::add_brush(
    const progpu_native_scene_brush& source,
    std::span<const progpu_native_scene_gradient_stop> gradient_stops,
    std::uint32_t& brush_index) noexcept {
    brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!valid_brush_input(source, gradient_stops)) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    if (implementation_->brushes.size() >= PROGPU_NATIVE_SCENE_MAX_BRUSHES ||
        gradient_stops.size() > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS -
            implementation_->gradient_stops.size()) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        const bool create_resource = implementation_->brush_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX;
        if (create_resource) {
            if (implementation_->resources.size() >=
                PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
                return implementation_->fail(
                    scene_build_error::capacity_exceeded);
            }
            implementation_->resources.reserve(
                implementation_->resources.size() + 1U);
        }
        implementation_->brushes.reserve(
            implementation_->brushes.size() + 1U);
        implementation_->gradient_stops.reserve(
            implementation_->gradient_stops.size() + gradient_stops.size());
        if (create_resource) {
            implementation::resource_entry resource{};
            resource.record.struct_size = sizeof(resource.record);
            resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE;
            resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
            resource.record.resource_id = implementation_->resources.size() + 1U;
            resource.record.generation = implementation_->generation;
            resource.brush_table = true;
            implementation_->brush_resource_index =
                static_cast<std::uint32_t>(implementation_->resources.size());
            implementation_->resources.push_back(std::move(resource));
        }
        progpu_native_scene_brush brush = source;
        if (!gradient_stops.empty()) {
            brush.stop_offset = static_cast<std::uint32_t>(
                implementation_->gradient_stops.size());
            implementation_->gradient_stops.insert(
                implementation_->gradient_stops.end(),
                gradient_stops.begin(), gradient_stops.end());
        } else {
            brush.stop_offset = 0U;
        }
        brush_index = static_cast<std::uint32_t>(
            implementation_->brushes.size());
        implementation_->brushes.push_back(brush);
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native
