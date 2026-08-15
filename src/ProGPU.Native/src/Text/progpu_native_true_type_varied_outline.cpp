#include "progpu_native_text.hpp"
#include "progpu_native_true_type_composite_internal.hpp"

#include <algorithm>
#include <array>
#include <cmath>

// Direct native port provenance: ProGPU-owned TtfFont.BuildCompositeGlyph and
// OpenTypeVariationData glyph-delta application at checkpoint 6ca97ba7. The
// recursive decoder keeps all point, tuple, and component storage caller-owned.
namespace progpu::native::text {
namespace {

using detail::composite_arguments_are_xy_values;
using detail::composite_round_xy_to_grid;
using detail::composite_scaled_component_offset;
using detail::read_composite_component;

constexpr std::size_t maximum_composite_depth = 32U;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool is_ancestor(
    std::span<const std::uint16_t> ancestors,
    std::uint16_t glyph_index) noexcept {
    return std::find(ancestors.begin(), ancestors.end(), glyph_index) !=
        ancestors.end();
}

float round_to_even(float value) noexcept {
    const auto lower = std::floor(value);
    const auto fraction = value - lower;
    if (fraction < 0.5F) {
        return lower;
    }
    if (fraction > 0.5F) {
        return lower + 1.0F;
    }
    return std::fmod(lower, 2.0F) == 0.0F ? lower : lower + 1.0F;
}

progpu_native_point transform_point(
    progpu_native_point point,
    const sfnt_composite_component& component,
    float translate_x,
    float translate_y) noexcept {
    return {
        point.x * component.m00 + point.y * component.m01 + translate_x,
        point.x * component.m10 + point.y * component.m11 + translate_y};
}

void transform_segment(
    progpu_native_path_segment& segment,
    const sfnt_composite_component& component,
    float translate_x,
    float translate_y) noexcept {
    segment.p0 = transform_point(
        segment.p0, component, translate_x, translate_y);
    segment.p1 = transform_point(
        segment.p1, component, translate_x, translate_y);
    if (segment.kind != PROGPU_NATIVE_PATH_SEGMENT_LINE) {
        segment.p2 = transform_point(
            segment.p2, component, translate_x, translate_y);
    }
}

bool decode_varied_glyph(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<std::uint16_t> ancestors,
    std::size_t depth,
    std::uint32_t component_offset_base,
    sfnt_varied_glyph_scratch scratch,
    std::span<progpu_native_point> points,
    std::span<progpu_native_path_segment> segments,
    std::uint32_t& point_cursor,
    std::uint32_t& segment_cursor) noexcept {
    std::uint16_t glyph_count = 0U;
    if (!font.try_get_glyph_count(glyph_count)) {
        return false;
    }
    if (glyph_index >= glyph_count || depth > maximum_composite_depth ||
        is_ancestor(ancestors.first(depth), glyph_index)) {
        return true;
    }
    ancestors[depth] = glyph_index;

    sfnt_glyph_decode_requirements requirements{};
    if (!font.try_get_glyph_decode_requirements(glyph_index, requirements)) {
        return false;
    }
    if (requirements.kind == sfnt_glyph_kind::empty) {
        return true;
    }
    if (requirements.kind == sfnt_glyph_kind::simple) {
        if (point_cursor > points.size() ||
            segment_cursor > segments.size() ||
            points.size() - point_cursor < requirements.point_count ||
            segments.size() - segment_cursor <
                requirements.path_segment_count) {
            return false;
        }
        const auto contours = scratch.simple_contour_end_points.first(
            requirements.contour_count);
        const auto original = scratch.simple_points.first(
            requirements.point_count);
        const auto varied = scratch.varied_simple_points.first(
            requirements.point_count);
        if (!font.try_decode_simple_glyph(
                glyph_index, contours, original) ||
            !font.try_apply_simple_glyph_variations(
                glyph_index,
                normalized_coordinates,
                contours,
                original,
                varied,
                scratch.simple_variation)) {
            return false;
        }
        std::uint32_t written = 0U;
        if (!sfnt_simple_glyph_path::try_write_varied_segments(
                contours,
                original,
                varied,
                segments.subspan(segment_cursor),
                written) ||
            written != requirements.path_segment_count) {
            return false;
        }
        std::copy(varied.begin(), varied.end(),
            points.begin() + point_cursor);
        point_cursor += requirements.point_count;
        segment_cursor += written;
        return true;
    }

    sfnt_composite_glyph_decode_requirements composite{};
    sfnt_glyph_data_view glyph{};
    if (!font.try_get_composite_glyph_decode_requirements(
            glyph_index, composite) ||
        !font.try_get_glyph_data(glyph_index, glyph) ||
        component_offset_base > scratch.component_offsets.size() ||
        scratch.component_offsets.size() - component_offset_base <
            composite.component_count) {
        return false;
    }
    auto component_offsets = scratch.component_offsets.subspan(
        component_offset_base, composite.component_count);
    if (!font.try_get_composite_glyph_variation_offsets(
            glyph_index,
            normalized_coordinates,
            composite.component_count,
            component_offsets,
            scratch.composite_variation)) {
        return false;
    }
    const auto child_offset_base =
        component_offset_base + composite.component_count;
    const auto parent_point_start = point_cursor;
    auto component_cursor = static_cast<std::size_t>(10U);
    for (std::uint32_t component_index = 0U;
        component_index < composite.component_count;
        ++component_index) {
        sfnt_composite_component component{};
        if (!read_composite_component(
                glyph.bytes, component_cursor, &component)) {
            return false;
        }
        const auto child_point_start = point_cursor;
        const auto child_segment_start = segment_cursor;
        if ((component.flags & composite_arguments_are_xy_values) == 0U) {
            const auto parent_count = child_point_start - parent_point_start;
            if (component.argument1 < 0 ||
                static_cast<std::uint32_t>(component.argument1) >=
                    parent_count) {
                continue;
            }
            sfnt_expanded_glyph_requirements child_requirements{};
            if (!font.try_get_expanded_glyph_requirements(
                    component.glyph_index, child_requirements) ||
                component.argument2 < 0 ||
                static_cast<std::uint32_t>(component.argument2) >=
                    child_requirements.point_count) {
                continue;
            }
        }
        if (!decode_varied_glyph(
                font,
                component.glyph_index,
                normalized_coordinates,
                ancestors,
                depth + 1U,
                child_offset_base,
                scratch,
                points,
                segments,
                point_cursor,
                segment_cursor)) {
            return false;
        }
        const auto child_point_count = point_cursor - child_point_start;
        if (child_point_count == 0U &&
            segment_cursor == child_segment_start) {
            continue;
        }

        float translate_x = 0.0F;
        float translate_y = 0.0F;
        if ((component.flags & composite_arguments_are_xy_values) != 0U) {
            translate_x = static_cast<float>(component.argument1) +
                component_offsets[component_index].x;
            translate_y = static_cast<float>(component.argument2) +
                component_offsets[component_index].y;
            if ((component.flags & composite_scaled_component_offset) != 0U) {
                const auto transformed = transform_point(
                    {translate_x, translate_y}, component, 0.0F, 0.0F);
                translate_x = transformed.x;
                translate_y = transformed.y;
            }
        } else {
            const auto parent_count = child_point_start - parent_point_start;
            if (static_cast<std::uint32_t>(component.argument1) >=
                    parent_count ||
                static_cast<std::uint32_t>(component.argument2) >=
                    child_point_count) {
                point_cursor = child_point_start;
                segment_cursor = child_segment_start;
                continue;
            }
            const auto parent = points[
                parent_point_start +
                static_cast<std::uint32_t>(component.argument1)];
            const auto child = transform_point(
                points[child_point_start +
                    static_cast<std::uint32_t>(component.argument2)],
                component,
                0.0F,
                0.0F);
            translate_x = parent.x - child.x;
            translate_y = parent.y - child.y;
        }
        if ((component.flags & composite_round_xy_to_grid) != 0U) {
            translate_x = round_to_even(translate_x);
            translate_y = round_to_even(translate_y);
        }
        for (auto index = child_point_start; index < point_cursor; ++index) {
            points[index] = transform_point(
                points[index], component, translate_x, translate_y);
        }
        for (auto index = child_segment_start;
            index < segment_cursor;
            ++index) {
            transform_segment(
                segments[index], component, translate_x, translate_y);
        }
    }
    return true;
}

bool has_required_scratch(
    const sfnt_varied_glyph_requirements& required,
    const sfnt_varied_glyph_scratch& actual) noexcept {
    const auto& simple = required.simple_variation;
    const auto& simple_actual = actual.simple_variation;
    const auto& composite = required.composite_variation;
    const auto& composite_actual = actual.composite_variation;
    return actual.simple_contour_end_points.size() >=
            required.outline.simple_contour_scratch_count &&
        actual.simple_points.size() >=
            required.outline.simple_point_scratch_count &&
        actual.varied_simple_points.size() >=
            required.varied_simple_point_count &&
        actual.component_offsets.size() >= required.component_offset_count &&
        simple_actual.tuple_headers.size() >= simple.tuple_header_count &&
        simple_actual.region_coordinates.size() >=
            simple.region_coordinate_count &&
        simple_actual.shared_point_numbers.size() >= simple.point_number_count &&
        simple_actual.private_point_numbers.size() >= simple.point_number_count &&
        simple_actual.x_deltas.size() >= simple.delta_count &&
        simple_actual.y_deltas.size() >= simple.delta_count &&
        simple_actual.tuple_x.size() >= simple.tuple_point_count &&
        simple_actual.tuple_y.size() >= simple.tuple_point_count &&
        simple_actual.touched.size() >= simple.tuple_point_count &&
        composite_actual.tuple_headers.size() >=
            composite.tuple_header_count &&
        composite_actual.region_coordinates.size() >=
            composite.region_coordinate_count &&
        composite_actual.shared_point_numbers.size() >=
            composite.point_number_count &&
        composite_actual.private_point_numbers.size() >=
            composite.point_number_count &&
        composite_actual.x_deltas.size() >= composite.delta_count &&
        composite_actual.y_deltas.size() >= composite.delta_count;
}

} // namespace

bool sfnt_font_view::try_decode_varied_glyph_outline(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    sfnt_varied_glyph_scratch scratch,
    std::span<progpu_native_point> points,
    std::span<progpu_native_path_segment> segments,
    std::uint32_t& points_written,
    std::uint32_t& segments_written,
    font_error* error) const noexcept {
    points_written = 0U;
    segments_written = 0U;
    set_error(error, font_error::none);
    sfnt_gvar_header gvar{};
    sfnt_varied_glyph_requirements requirements{};
    if (!try_get_gvar_header(gvar, error) ||
        normalized_coordinates.size() < gvar.axis_count ||
        !try_get_varied_glyph_requirements(
            glyph_index, requirements, error)) {
        if (normalized_coordinates.size() < gvar.axis_count) {
            set_error(error, font_error::insufficient_buffer);
        }
        return false;
    }
    if (!has_required_scratch(requirements, scratch) ||
        points.size() < requirements.outline.point_count ||
        segments.size() < requirements.outline.path_segment_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::array<std::uint16_t, maximum_composite_depth + 1U> ancestors{};
    std::uint32_t point_cursor = 0U;
    std::uint32_t segment_cursor = 0U;
    if (!decode_varied_glyph(
            *this,
            glyph_index,
            normalized_coordinates.first(gvar.axis_count),
            ancestors,
            0U,
            0U,
            scratch,
            points,
            segments,
            point_cursor,
            segment_cursor) ||
        point_cursor != requirements.outline.point_count ||
        segment_cursor != requirements.outline.path_segment_count) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    points_written = point_cursor;
    segments_written = segment_cursor;
    return true;
}

} // namespace progpu::native::text
