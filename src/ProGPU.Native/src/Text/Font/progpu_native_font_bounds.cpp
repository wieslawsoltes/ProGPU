#include "progpu_native_text.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

// Direct native port provenance: ProGPU-owned TtfFont.TryGetGlyphBounds and
// PathGeometry.TryGetBounds at repository checkpoints 2f1fdc472 and 36df47ac.
// Existing native outline decoders provide caller-owned CFF/CFF2 and varied
// TrueType storage; this unit only selects the source and reduces its path.

namespace progpu::native::text {
namespace {

constexpr auto glyf_tag = open_type_tag::from_chars('g', 'l', 'y', 'f');
constexpr auto loca_tag = open_type_tag::from_chars('l', 'o', 'c', 'a');
constexpr auto gvar_tag = open_type_tag::from_chars('g', 'v', 'a', 'r');
constexpr auto cff1_tag = open_type_tag::from_chars('C', 'F', 'F', ' ');
constexpr auto cff2_tag = open_type_tag::from_chars('C', 'F', 'F', '2');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) *destination = value;
}

bool has_nonzero_coordinate(
    std::span<const std::int16_t> coordinates) noexcept {
    return std::any_of(
        coordinates.begin(), coordinates.end(),
        [](std::int16_t value) { return value != 0; });
}

std::int16_t floor_i16(float value) noexcept {
    return static_cast<std::int16_t>(std::clamp(
        std::floor(value),
        static_cast<float>(std::numeric_limits<std::int16_t>::min()),
        static_cast<float>(std::numeric_limits<std::int16_t>::max())));
}

std::int16_t ceiling_i16(float value) noexcept {
    return static_cast<std::int16_t>(std::clamp(
        std::ceil(value),
        static_cast<float>(std::numeric_limits<std::int16_t>::min()),
        static_cast<float>(std::numeric_limits<std::int16_t>::max())));
}

bool reduce_path_bounds(
    std::span<const progpu_native_path_segment> segments,
    sfnt_glyph_bounds& result,
    bool& has_bounds) noexcept {
    result = {};
    has_bounds = false;
    float min_x = std::numeric_limits<float>::infinity();
    float min_y = std::numeric_limits<float>::infinity();
    float max_x = -std::numeric_limits<float>::infinity();
    float max_y = -std::numeric_limits<float>::infinity();
    const auto update = [&](progpu_native_point point) {
        if (!std::isfinite(point.x) || !std::isfinite(point.y)) return;
        min_x = std::min(min_x, point.x);
        min_y = std::min(min_y, point.y);
        max_x = std::max(max_x, point.x);
        max_y = std::max(max_y, point.y);
        has_bounds = true;
    };
    for (const auto& segment : segments) {
        update(segment.p0);
        update(segment.p1);
        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC ||
            segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
            update(segment.p2);
        }
        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
            update(segment.p3);
        }
    }
    if (!has_bounds) return true;
    result = sfnt_glyph_bounds{
        floor_i16(min_x),
        floor_i16(min_y),
        ceiling_i16(max_x),
        ceiling_i16(max_y)};
    has_bounds = result.x_max > result.x_min && result.y_max > result.y_min;
    if (!has_bounds) result = {};
    return true;
}

} // namespace

bool sfnt_font_view::try_get_outline_bounds_requirements(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    sfnt_glyph_outline_bounds_requirements& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    std::uint16_t glyph_count = 0U;
    if (!try_get_glyph_count(glyph_count) || glyph_index >= glyph_count) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }

    sfnt_table_view glyf{};
    sfnt_table_view loca{};
    if (try_get_table(glyf_tag, glyf) && try_get_table(loca_tag, loca)) {
        sfnt_table_view gvar{};
        if (!has_nonzero_coordinate(normalized_coordinates) ||
            !try_get_table(gvar_tag, gvar)) {
            result.source = sfnt_glyph_outline_source::true_type_static;
            return true;
        }
        if (!try_get_varied_glyph_requirements(
                glyph_index, result.varied, error)) {
            result = {};
            return false;
        }
        result.source = sfnt_glyph_outline_source::true_type_varied;
        result.point_count = result.varied.outline.point_count;
        result.path_segment_count = result.varied.outline.path_segment_count;
        return true;
    }

    sfnt_table_view table{};
    if (try_get_table(cff1_tag, table)) {
        sfnt_cff1_font_view cff{};
        sfnt_cff1_outline_requirements outline{};
        if (!try_get_cff1_font(glyph_count, cff, error) ||
            !sfnt_cff_data::try_get_outline_requirements(
                cff, glyph_index, outline, error)) {
            result = {};
            return false;
        }
        result.source = sfnt_glyph_outline_source::cff1;
        result.path_segment_count = outline.path_segment_count;
        return true;
    }
    if (try_get_table(cff2_tag, table)) {
        sfnt_cff2_font_view cff{};
        sfnt_cff2_outline_requirements outline{};
        if (!try_get_cff2_font(glyph_count, cff, error) ||
            !sfnt_cff_data::try_get_outline_requirements(
                cff,
                glyph_index,
                normalized_coordinates,
                outline,
                error)) {
            result = {};
            return false;
        }
        result.source = sfnt_glyph_outline_source::cff2;
        result.path_segment_count = outline.path_segment_count;
    }
    return true;
}

bool sfnt_font_view::try_get_outline_bounds(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    sfnt_glyph_outline_bounds_scratch scratch,
    sfnt_glyph_bounds& result,
    bool& has_bounds,
    font_error* error) const noexcept {
    result = {};
    has_bounds = false;
    set_error(error, font_error::none);
    sfnt_glyph_outline_bounds_requirements requirements{};
    if (!try_get_outline_bounds_requirements(
            glyph_index, normalized_coordinates, requirements, error)) {
        return false;
    }
    if (requirements.source == sfnt_glyph_outline_source::none) return true;
    if (requirements.source ==
            sfnt_glyph_outline_source::true_type_static) {
        has_bounds = try_get_glyph_bounds(glyph_index, result);
        return true;
    }
    if (scratch.points.size() < requirements.point_count ||
        scratch.path_segments.size() < requirements.path_segment_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    std::uint32_t points_written = 0U;
    std::uint32_t segments_written = 0U;
    if (requirements.source ==
            sfnt_glyph_outline_source::true_type_varied) {
        if (!try_decode_varied_glyph_outline(
                glyph_index,
                normalized_coordinates,
                scratch.varied,
                scratch.points,
                scratch.path_segments,
                points_written,
                segments_written,
                error)) {
            return false;
        }
    } else {
        std::uint16_t glyph_count = 0U;
        if (!try_get_glyph_count(glyph_count)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        if (requirements.source == sfnt_glyph_outline_source::cff1) {
            sfnt_cff1_font_view cff{};
            if (!try_get_cff1_font(glyph_count, cff, error) ||
                !sfnt_cff_data::try_decode_outline(
                    cff,
                    glyph_index,
                    scratch.path_segments,
                    segments_written,
                    error)) {
                return false;
            }
        } else {
            sfnt_cff2_font_view cff{};
            if (!try_get_cff2_font(glyph_count, cff, error) ||
                !sfnt_cff_data::try_decode_outline(
                    cff,
                    glyph_index,
                    normalized_coordinates,
                    scratch.path_segments,
                    segments_written,
                    error)) {
                return false;
            }
        }
    }
    return reduce_path_bounds(
        scratch.path_segments.first(segments_written), result, has_bounds);
}

} // namespace progpu::native::text
