#include "progpu_native.h"
#include "progpu_native_geometry_primitives.hpp"
#include "progpu_native_path_core.hpp"

#include <cmath>
#include <cstddef>
#include <cstring>
#include <limits>
#include <memory>
#include <type_traits>
#include <vector>

struct progpu_native_geometry_outline final {
    std::unique_ptr<progpu_native_point[]> points;
    std::vector<std::uint32_t> offsets;
};

struct progpu_native_geometry_path final {
    std::vector<progpu_native_path_segment> segments;
};

namespace {

namespace geometry = progpu::native::geometry;
namespace status_code = progpu::native::geometry::status_code;

[[nodiscard]] progpu_native_status native_status(geometry::status status) noexcept
{
    if (status == status_code::out_of_memory) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    if (status == status_code::invalid_argument || status == status_code::pointer_error) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (status == status_code::not_implemented) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
}

// The figure/segment transport of the query functions (stroke_query).
[[nodiscard]] bool valid_query_transport(const progpu_native_geometry_query_figure* figures,
    std::uint32_t figure_count, const progpu_native_path_segment* segments,
    const std::uint8_t* segment_flags, std::uint32_t segment_count) noexcept
{
    return !((figure_count != 0U && figures == nullptr) ||
        (segment_count != 0U && (segments == nullptr || segment_flags == nullptr)) ||
        figure_count > (1U << 20U) || segment_count > (1U << 20U));
}

[[nodiscard]] bool valid_pen(const progpu_native_geometry_query_pen* pen, const float* dashes,
    std::uint32_t dash_count) noexcept
{
    return !(pen == nullptr || (dash_count != 0U && dashes == nullptr) || dash_count > (1U << 20U) ||
        !std::isfinite(pen->thickness) || pen->thickness < 0.0F ||
        !std::isfinite(pen->miter_limit) || pen->miter_limit < 1.0F ||
        !std::isfinite(pen->dash_offset) || pen->start_cap > 3U || pen->end_cap > 3U ||
        pen->dash_cap > 3U || pen->line_join > PROGPU_NATIVE_STROKE_JOIN_MITER_OR_BEVEL);
}

[[nodiscard]] geometry::matrix_3x2_f world_matrix(const progpu_native_affine_2d* transform) noexcept
{
    if (transform == nullptr) return {1, 0, 0, 1, 0, 0};
    return {transform->m11, transform->m12, transform->m21, transform->m22, transform->m31, transform->m32};
}

// The recorded query path of the transport.
[[nodiscard]] geometry::status create_query_path(const progpu_native_geometry_query_figure* figures,
    std::uint32_t figure_count, const progpu_native_path_segment* segments,
    const std::uint8_t* segment_flags, std::uint32_t segment_count,
    std::shared_ptr<geometry::path_data>& path) noexcept
{
    return geometry::build_native_query_path(
        {figures, figure_count}, {segments, segment_count}, {segment_flags, segment_count}, path);
}

// The stroke style of the pen, validated as stroke style creation validates
// it. The dashes stay borrowed from the caller.
[[nodiscard]] geometry::status create_pen_style(const progpu_native_geometry_query_pen& pen,
    const float* dashes, std::uint32_t dash_count, geometry::stroke_style& style) noexcept
{
    const geometry::stroke_style_properties_f properties{
        static_cast<geometry::cap_style>(pen.start_cap), static_cast<geometry::cap_style>(pen.end_cap),
        static_cast<geometry::cap_style>(pen.dash_cap), static_cast<geometry::line_join>(pen.line_join),
        pen.miter_limit, dash_count == 0U ? geometry::dash_style::solid : geometry::dash_style::custom, pen.dash_offset};
    const float* const style_dashes = dash_count == 0U ? nullptr : dashes;
    if (!geometry::valid_stroke_style(properties, style_dashes, dash_count))
        return status_code::invalid_argument;
    style.properties = properties;
    style.dashes = style_dashes;
    style.dash_count = dash_count;
    return status_code::ok;
}

} // namespace

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_geometry_stroke_query(
    const progpu_native_geometry_query_figure* figures, std::uint32_t figure_count,
    const progpu_native_path_segment* segments, const std::uint8_t* segment_flags, std::uint32_t segment_count,
    const progpu_native_geometry_query_pen* pen, const float* dashes, std::uint32_t dash_count,
    const progpu_native_affine_2d* world_transform, const progpu_native_point* point, float tolerance,
    progpu_native_image_rect* bounds, std::uint32_t* has_bounds, std::uint32_t* contains)
{
    if (bounds == nullptr || has_bounds == nullptr || contains == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *bounds = {}; *has_bounds = 0U; *contains = 0U;
    if (!valid_query_transport(figures, figure_count, segments, segment_flags, segment_count) ||
        !valid_pen(pen, dashes, dash_count) || !std::isfinite(tolerance) || tolerance <= 0.0F)
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        const geometry::matrix_3x2_f matrix = world_matrix(world_transform);
        std::shared_ptr<geometry::path_data> path;
        geometry::stroke_style style{};
        geometry::status status = create_query_path(figures, figure_count, segments, segment_flags, segment_count, path);
        if (!geometry::failed(status)) status = create_pen_style(*pen, dashes, dash_count, style);
        geometry::rectangle_edges_f measured{};
        bool emitted = false;
        std::int32_t hit = 0;
        if (!geometry::failed(status)) status = point == nullptr
            ? geometry::widened_outline_bounds(*path, pen->thickness, &style,
                &matrix, tolerance, measured, emitted)
            : geometry::path_stroke_contains_point(*path, {point->x, point->y}, pen->thickness, &style, &matrix, tolerance, &hit);
        if (geometry::failed(status)) return native_status(status);
        if (emitted) {
            const float width = measured.right - measured.left, height = measured.bottom - measured.top;
            if (!std::isfinite(measured.left) || !std::isfinite(measured.top) ||
                !std::isfinite(width) || !std::isfinite(height) || width < 0.0F || height < 0.0F)
                return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            *bounds = {measured.left, measured.top, width, height};
            *has_bounds = 1U;
        }
        *contains = hit != 0 ? 1U : 0U;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_geometry_fill_contains(
    const progpu_native_path_segment* segments, std::uint32_t segment_count,
    std::uint32_t fill_rule, const progpu_native_point* point, float tolerance,
    std::uint32_t* contains)
{
    if (contains == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *contains = 0U;
    if ((segment_count != 0U && segments == nullptr) || segment_count > (1U << 20U) ||
        fill_rule > PROGPU_NATIVE_FILL_RULE_EVEN_ODD || point == nullptr ||
        !std::isfinite(point->x) || !std::isfinite(point->y) ||
        !std::isfinite(tolerance) || tolerance <= 0.0F) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        std::shared_ptr<geometry::path_data> path;
        geometry::status status = geometry::build_native_fill_path(
            {segments, segment_count}, fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                ? geometry::fill_mode::alternate : geometry::fill_mode::winding, path);
        std::int32_t result = 0;
        if (!geometry::failed(status)) status = geometry::path_fill_contains_point(
            *path, {point->x, point->y}, nullptr, tolerance, &result);
        if (geometry::failed(status)) {
            if (status == status_code::out_of_memory) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
            if (status == status_code::invalid_argument || status == status_code::pointer_error) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            if (status == status_code::not_implemented) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        }
        *contains = result != 0 ? 1U : 0U;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_geometry_compare_fill(
    const progpu_native_path_segment* first, std::uint32_t first_count, std::uint32_t first_fill,
    const progpu_native_path_segment* second, std::uint32_t second_count, std::uint32_t second_fill,
    float tolerance, std::uint32_t* relation)
{
    if (relation == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *relation = PROGPU_NATIVE_GEOMETRY_RELATION_UNKNOWN;
    if ((first_count != 0U && first == nullptr) || (second_count != 0U && second == nullptr) ||
        first_count > (1U << 20U) || second_count > (1U << 20U) ||
        first_fill > PROGPU_NATIVE_FILL_RULE_EVEN_ODD || second_fill > PROGPU_NATIVE_FILL_RULE_EVEN_ODD ||
        !std::isfinite(tolerance) || tolerance <= 0.0F) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto fill = [](std::uint32_t value) {
        return value == PROGPU_NATIVE_FILL_RULE_EVEN_ODD ? geometry::fill_mode::alternate : geometry::fill_mode::winding;
    };
    geometry::geometry_relation result = geometry::geometry_relation::unknown;
    const geometry::status status = geometry::compare_native_fill_contours(
        {first, first_count}, fill(first_fill), {second, second_count}, fill(second_fill), tolerance, result);
    if (geometry::failed(status)) {
        if (status == status_code::out_of_memory) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
        if (status == status_code::invalid_argument || status == status_code::pointer_error) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        if (status == status_code::not_implemented) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
    switch (result) {
    case geometry::geometry_relation::disjoint: *relation = PROGPU_NATIVE_GEOMETRY_RELATION_DISJOINT; break;
    case geometry::geometry_relation::is_contained: *relation = PROGPU_NATIVE_GEOMETRY_RELATION_IS_CONTAINED; break;
    case geometry::geometry_relation::contains: *relation = PROGPU_NATIVE_GEOMETRY_RELATION_CONTAINS; break;
    case geometry::geometry_relation::overlap: *relation = PROGPU_NATIVE_GEOMETRY_RELATION_OVERLAP; break;
    default: return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_geometry_combine(
    const progpu_native_path_segment* first, std::uint32_t first_count, std::uint32_t first_fill,
    const progpu_native_path_segment* second, std::uint32_t second_count, std::uint32_t second_fill,
    std::uint32_t mode, float tolerance, progpu_native_geometry_outline** result,
    const progpu_native_point** points, std::uint32_t* point_count,
    const std::uint32_t** contour_offsets, std::uint32_t* contour_count)
{
    if (result == nullptr || points == nullptr || point_count == nullptr ||
        contour_offsets == nullptr || contour_count == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *result = nullptr;
    *points = nullptr;
    *point_count = 0U;
    *contour_offsets = nullptr;
    *contour_count = 0U;
    if ((first_count != 0U && first == nullptr) || (second_count != 0U && second == nullptr) ||
        first_count > (1U << 20U) || second_count > (1U << 20U) ||
        first_fill > PROGPU_NATIVE_FILL_RULE_EVEN_ODD || second_fill > PROGPU_NATIVE_FILL_RULE_EVEN_ODD ||
        mode > 3U || !std::isfinite(tolerance) || tolerance <= 0.0F) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;

    static_assert(static_cast<std::uint32_t>(geometry::combine_mode::union_value) == 0U);
    static_assert(static_cast<std::uint32_t>(geometry::combine_mode::intersect) == 1U);
    static_assert(static_cast<std::uint32_t>(geometry::combine_mode::xor_value) == 2U);
    static_assert(static_cast<std::uint32_t>(geometry::combine_mode::exclude) == 3U);
    try {
        std::vector<std::vector<geometry::point_2f>> contours;
        const auto fill = [](std::uint32_t value) {
            return value == PROGPU_NATIVE_FILL_RULE_EVEN_ODD ? geometry::fill_mode::alternate : geometry::fill_mode::winding;
        };
        const geometry::status status = geometry::combine_native_fill_contours(
            {first, first_count}, fill(first_fill), {second, second_count}, fill(second_fill),
            static_cast<geometry::combine_mode>(mode), tolerance, contours);
        if (geometry::failed(status)) {
            if (status == status_code::out_of_memory) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
            if (status == status_code::invalid_argument || status == status_code::pointer_error) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            if (status == status_code::not_implemented) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        }

        // One bounded ownership copy of the existing core's result. Bulk copies
        // use compiler/runtime intrinsic memcpy, not a new scalar point kernel.
        constexpr auto maximum = (std::numeric_limits<std::uint32_t>::max)();
        if (contours.size() >= maximum) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
        auto snapshot = std::make_unique<progpu_native_geometry_outline>();
        snapshot->offsets.reserve(contours.size() + 1U);
        snapshot->offsets.push_back(0U);
        std::uint32_t total = 0U;
        for (const auto& contour : contours) {
            if (contour.size() > maximum - total) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
            total += static_cast<std::uint32_t>(contour.size());
            snapshot->offsets.push_back(total);
        }
        snapshot->points = std::make_unique_for_overwrite<progpu_native_point[]>(total);
        static_assert(sizeof(geometry::point_2f) == sizeof(progpu_native_point));
        static_assert(offsetof(geometry::point_2f, x) == offsetof(progpu_native_point, x));
        static_assert(offsetof(geometry::point_2f, y) == offsetof(progpu_native_point, y));
        static_assert(std::is_trivially_copyable_v<geometry::point_2f>);
        static_assert(std::is_trivially_copyable_v<progpu_native_point>);
        for (std::size_t index = 0U; index < contours.size(); ++index) {
            const auto& contour = contours[index];
            if (!contour.empty()) std::memcpy(snapshot->points.get() + snapshot->offsets[index],
                contour.data(), contour.size() * sizeof(progpu_native_point));
        }
        *points = snapshot->points.get();
        *point_count = total;
        *contour_offsets = snapshot->offsets.data();
        *contour_count = static_cast<std::uint32_t>(contours.size());
        *result = snapshot.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

extern "C" PROGPU_NATIVE_API void progpu_native_geometry_outline_destroy(
    progpu_native_geometry_outline* result)
{
    delete result;
}

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_geometry_bounds(
    const progpu_native_geometry_query_figure* figures, std::uint32_t figure_count,
    const progpu_native_path_segment* segments, const std::uint8_t* segment_flags, std::uint32_t segment_count,
    const progpu_native_affine_2d* world_transform, progpu_native_image_rect* bounds, std::uint32_t* has_bounds)
{
    if (bounds == nullptr || has_bounds == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *bounds = {}; *has_bounds = 0U;
    if (!valid_query_transport(figures, figure_count, segments, segment_flags, segment_count))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        const geometry::matrix_3x2_f matrix = world_matrix(world_transform);
        std::shared_ptr<geometry::path_data> path;
        geometry::status status = create_query_path(figures, figure_count, segments, segment_flags, segment_count, path);
        geometry::rectangle_edges_f measured{};
        if (!geometry::failed(status)) status = geometry::path_bounds(*path, &matrix, &measured);
        if (geometry::failed(status)) return native_status(status);
        // Every figure contributes at least its start point; a geometry without
        // figures has no bounds.
        if (figure_count == 0U) return PROGPU_NATIVE_STATUS_SUCCESS;
        const float width = measured.right - measured.left, height = measured.bottom - measured.top;
        if (!std::isfinite(measured.left) || !std::isfinite(measured.top) ||
            !std::isfinite(width) || !std::isfinite(height))
            return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        *bounds = {measured.left, measured.top, width, height};
        *has_bounds = 1U;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_geometry_widen(
    const progpu_native_geometry_query_figure* figures, std::uint32_t figure_count,
    const progpu_native_path_segment* segments, const std::uint8_t* segment_flags, std::uint32_t segment_count,
    const progpu_native_geometry_query_pen* pen, const float* dashes, std::uint32_t dash_count,
    const progpu_native_affine_2d* world_transform, float tolerance,
    progpu_native_geometry_path** result, const progpu_native_path_segment** outline,
    std::uint32_t* outline_count, std::uint32_t* fill_rule)
{
    if (result == nullptr || outline == nullptr || outline_count == nullptr || fill_rule == nullptr)
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *result = nullptr; *outline = nullptr; *outline_count = 0U; *fill_rule = PROGPU_NATIVE_FILL_RULE_NON_ZERO;
    if (!valid_query_transport(figures, figure_count, segments, segment_flags, segment_count) ||
        !valid_pen(pen, dashes, dash_count) || !std::isfinite(tolerance) || tolerance <= 0.0F)
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        const geometry::matrix_3x2_f matrix = world_matrix(world_transform);
        std::shared_ptr<geometry::path_data> path;
        geometry::stroke_style style{};
        geometry::status status = create_query_path(figures, figure_count, segments, segment_flags, segment_count, path);
        if (!geometry::failed(status)) status = create_pen_style(*pen, dashes, dash_count, style);
        auto snapshot = std::make_unique<progpu_native_geometry_path>();
        geometry::fill_mode mode = geometry::fill_mode::winding;
        if (!geometry::failed(status)) status = geometry::widened_outline_segments(
            *path, pen->thickness, &style, &matrix, tolerance, snapshot->segments, mode);
        if (geometry::failed(status)) return native_status(status);
        if (snapshot->segments.size() > (std::numeric_limits<std::uint32_t>::max)())
            return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
        *outline = snapshot->segments.data();
        *outline_count = static_cast<std::uint32_t>(snapshot->segments.size());
        *fill_rule = mode == geometry::fill_mode::alternate ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD : PROGPU_NATIVE_FILL_RULE_NON_ZERO;
        *result = snapshot.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

extern "C" PROGPU_NATIVE_API void progpu_native_geometry_path_destroy(progpu_native_geometry_path* path)
{
    delete path;
}
