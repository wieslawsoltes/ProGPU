#include "progpu_native_text.hpp"
#include "progpu_native_true_type_composite_internal.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>

// Direct native port of ProGPU.Text.TtfFont.BuildCompositeGlyph. The native
// form preflights caller buffers and writes points/path records in place.
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

bool checked_add(
    std::uint32_t left,
    std::uint32_t right,
    std::uint32_t& result) noexcept {
    if (right > std::numeric_limits<std::uint32_t>::max() - left) {
        return false;
    }
    result = left + right;
    return true;
}

bool is_ancestor(
    std::span<const std::uint16_t> ancestors,
    std::uint16_t glyph_index) noexcept {
    for (const auto ancestor : ancestors) {
        if (ancestor == glyph_index) {
            return true;
        }
    }
    return false;
}

bool measure_expanded_glyph(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    std::span<std::uint16_t> ancestors,
    std::size_t depth,
    sfnt_expanded_glyph_requirements& result) noexcept {
    result = {};
    std::uint16_t glyph_count = 0U;
    if (!font.try_get_glyph_count(glyph_count)) {
        return false;
    }
    if (glyph_index >= glyph_count || depth > maximum_composite_depth ||
        is_ancestor(ancestors.first(depth), glyph_index)) {
        return true;
    }
    ancestors[depth] = glyph_index;

    sfnt_glyph_decode_requirements glyph_requirements{};
    if (!font.try_get_glyph_decode_requirements(
            glyph_index, glyph_requirements)) {
        return false;
    }
    if (glyph_requirements.kind == sfnt_glyph_kind::empty) {
        return true;
    }
    if (glyph_requirements.kind == sfnt_glyph_kind::simple) {
        result = sfnt_expanded_glyph_requirements{
            glyph_requirements.point_count,
            glyph_requirements.path_segment_count,
            glyph_requirements.point_count,
            glyph_requirements.contour_count};
        return true;
    }

    sfnt_composite_glyph_decode_requirements composite_requirements{};
    sfnt_glyph_data_view glyph{};
    if (!font.try_get_composite_glyph_decode_requirements(
            glyph_index, composite_requirements) ||
        !font.try_get_glyph_data(glyph_index, glyph)) {
        return false;
    }
    auto cursor = static_cast<std::size_t>(10U);
    for (std::uint32_t component_index = 0U;
        component_index < composite_requirements.component_count;
        ++component_index) {
        sfnt_composite_component component{};
        if (!read_composite_component(glyph.bytes, cursor, &component)) {
            return false;
        }
        sfnt_expanded_glyph_requirements child{};
        if (!measure_expanded_glyph(
                font,
                component.glyph_index,
                ancestors,
                depth + 1U,
                child)) {
            return false;
        }
        if (child.point_count == 0U && child.path_segment_count == 0U) {
            continue;
        }
        if ((component.flags & composite_arguments_are_xy_values) == 0U &&
            (component.argument1 < 0 || component.argument2 < 0 ||
                static_cast<std::uint32_t>(component.argument1) >=
                    result.point_count ||
                static_cast<std::uint32_t>(component.argument2) >=
                    child.point_count)) {
            continue;
        }
        if (!checked_add(
                result.point_count,
                child.point_count,
                result.point_count) ||
            !checked_add(
                result.path_segment_count,
                child.path_segment_count,
                result.path_segment_count)) {
            return false;
        }
        result.simple_point_scratch_count = std::max(
            result.simple_point_scratch_count,
            child.simple_point_scratch_count);
        result.simple_contour_scratch_count = std::max(
            result.simple_contour_scratch_count,
            child.simple_contour_scratch_count);
    }
    return true;
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
    return progpu_native_point{
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

bool decode_expanded_glyph(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    std::span<std::uint16_t> ancestors,
    std::size_t depth,
    std::span<std::uint16_t> contour_scratch,
    std::span<sfnt_outline_point> point_scratch,
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
            contour_scratch.size() < requirements.contour_count ||
            point_scratch.size() < requirements.point_count ||
            points.size() - point_cursor < requirements.point_count ||
            segments.size() - segment_cursor <
                requirements.path_segment_count) {
            return false;
        }
        const auto contours = contour_scratch.first(
            requirements.contour_count);
        const auto decoded_points = point_scratch.first(
            requirements.point_count);
        if (!font.try_decode_simple_glyph(
                glyph_index, contours, decoded_points)) {
            return false;
        }
        std::uint32_t written = 0U;
        if (!sfnt_simple_glyph_path::try_write_segments(
                contours,
                decoded_points,
                segments.subspan(segment_cursor),
                written) ||
            written != requirements.path_segment_count) {
            return false;
        }
        for (const auto& point : decoded_points) {
            points[point_cursor++] = progpu_native_point{
                static_cast<float>(point.x),
                static_cast<float>(point.y)};
        }
        segment_cursor += written;
        return true;
    }

    sfnt_composite_glyph_decode_requirements composite_requirements{};
    sfnt_glyph_data_view glyph{};
    if (!font.try_get_composite_glyph_decode_requirements(
            glyph_index, composite_requirements) ||
        !font.try_get_glyph_data(glyph_index, glyph)) {
        return false;
    }
    const auto parent_point_start = point_cursor;
    auto component_cursor = static_cast<std::size_t>(10U);
    for (std::uint32_t component_index = 0U;
        component_index < composite_requirements.component_count;
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
            if (!measure_expanded_glyph(
                    font,
                    component.glyph_index,
                    ancestors,
                    depth + 1U,
                    child_requirements)) {
                return false;
            }
            if (component.argument2 < 0 ||
                static_cast<std::uint32_t>(component.argument2) >=
                    child_requirements.point_count) {
                continue;
            }
        }
        if (!decode_expanded_glyph(
                font,
                component.glyph_index,
                ancestors,
                depth + 1U,
                contour_scratch,
                point_scratch,
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
            translate_x = static_cast<float>(component.argument1);
            translate_y = static_cast<float>(component.argument2);
            if ((component.flags &
                    composite_scaled_component_offset) != 0U) {
                const auto transformed = transform_point(
                    progpu_native_point{translate_x, translate_y},
                    component,
                    0.0F,
                    0.0F);
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
            const auto component_point = transform_point(
                points[child_point_start +
                    static_cast<std::uint32_t>(component.argument2)],
                component,
                0.0F,
                0.0F);
            translate_x = parent.x - component_point.x;
            translate_y = parent.y - component_point.y;
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

} // namespace

bool sfnt_font_view::try_get_expanded_glyph_requirements(
    std::uint16_t glyph_index,
    sfnt_expanded_glyph_requirements& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    std::uint16_t glyph_count = 0U;
    if (!try_get_glyph_count(glyph_count) || glyph_index >= glyph_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::array<std::uint16_t, maximum_composite_depth + 1U> ancestors{};
    if (!measure_expanded_glyph(*this, glyph_index, ancestors, 0U, result)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    return true;
}

bool sfnt_font_view::try_decode_glyph_outline(
    std::uint16_t glyph_index,
    std::span<std::uint16_t> simple_contour_scratch,
    std::span<sfnt_outline_point> simple_point_scratch,
    std::span<progpu_native_point> points,
    std::span<progpu_native_path_segment> segments,
    std::uint32_t& points_written,
    std::uint32_t& segments_written,
    font_error* error) const noexcept {
    points_written = 0U;
    segments_written = 0U;
    set_error(error, font_error::none);
    sfnt_expanded_glyph_requirements requirements{};
    if (!try_get_expanded_glyph_requirements(
            glyph_index, requirements, error)) {
        return false;
    }
    if (simple_contour_scratch.size() <
            requirements.simple_contour_scratch_count ||
        simple_point_scratch.size() <
            requirements.simple_point_scratch_count ||
        points.size() < requirements.point_count ||
        segments.size() < requirements.path_segment_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::array<std::uint16_t, maximum_composite_depth + 1U> ancestors{};
    std::uint32_t point_cursor = 0U;
    std::uint32_t segment_cursor = 0U;
    if (!decode_expanded_glyph(
            *this,
            glyph_index,
            ancestors,
            0U,
            simple_contour_scratch,
            simple_point_scratch,
            points,
            segments,
            point_cursor,
            segment_cursor) ||
        point_cursor != requirements.point_count ||
        segment_cursor != requirements.path_segment_count) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    points_written = point_cursor;
    segments_written = segment_cursor;
    return true;
}

} // namespace progpu::native::text
