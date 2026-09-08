#include "progpu_native.h"
#include "../Direct2D/progpu_native_direct2d_path.hpp"

#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <type_traits>
#include <vector>

struct progpu_native_geometry_outline final {
    std::unique_ptr<progpu_native_point[]> points;
    std::vector<std::uint32_t> offsets;
};

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_geometry_stroke_query(
    const progpu_native_geometry_query_figure* figures, std::uint32_t figure_count,
    const progpu_native_path_segment* segments, const std::uint8_t* segment_flags, std::uint32_t segment_count,
    const progpu_native_geometry_query_pen* pen, const float* dashes, std::uint32_t dash_count,
    const progpu_native_affine_2d* world_transform, const progpu_native_point* point, float tolerance,
    progpu_native_image_rect* bounds, std::uint32_t* has_bounds, std::uint32_t* contains)
{
    if (bounds == nullptr || has_bounds == nullptr || contains == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *bounds = {}; *has_bounds = 0U; *contains = 0U;
    if (pen == nullptr || (figure_count != 0U && figures == nullptr) ||
        (segment_count != 0U && (segments == nullptr || segment_flags == nullptr)) ||
        (dash_count != 0U && dashes == nullptr) || figure_count > (1U << 20U) ||
        segment_count > (1U << 20U) || dash_count > (1U << 20U) ||
        !std::isfinite(tolerance) || tolerance <= 0.0F ||
        !std::isfinite(pen->thickness) || pen->thickness < 0.0F ||
        !std::isfinite(pen->miter_limit) || pen->miter_limit < 1.0F ||
        !std::isfinite(pen->dash_offset) || pen->start_cap > 3U || pen->end_cap > 3U ||
        pen->dash_cap > 3U || pen->line_join > 2U) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    namespace d2d = progpu::native::direct2d::compat;
    namespace com = progpu::native::com;
    try {
        d2d::matrix_3x2_f matrix{1, 0, 0, 1, 0, 0};
        if (world_transform != nullptr) matrix = {world_transform->m11, world_transform->m12,
            world_transform->m21, world_transform->m22, world_transform->m31, world_transform->m32};
        com::pointer<d2d::factory> owner;
        com::pointer<d2d::path_geometry> path;
        com::pointer<d2d::stroke_style> style;
        com::result status = d2d::create_factory(owner.put());
        if (!com::failed(status)) status = d2d::detail::create_native_query_geometry(owner.get(),
            {figures, figure_count}, {segments, segment_count}, {segment_flags, segment_count}, path.put());
        const d2d::stroke_style_properties properties{
            static_cast<d2d::cap_style>(pen->start_cap), static_cast<d2d::cap_style>(pen->end_cap),
            static_cast<d2d::cap_style>(pen->dash_cap), static_cast<d2d::line_join>(pen->line_join),
            pen->miter_limit, dash_count == 0U ? d2d::dash_style::solid : d2d::dash_style::custom, pen->dash_offset};
        if (!com::failed(status)) status = owner->CreateStrokeStyle(&properties,
            dash_count == 0U ? nullptr : dashes, dash_count, style.put());
        d2d::rectangle_f measured{};
        bool emitted = false;
        std::int32_t hit = 0;
        if (!com::failed(status)) status = point == nullptr
            ? d2d::detail::get_widened_outline_bounds(path.get(), pen->thickness, style.get(),
                &matrix, tolerance, measured, emitted)
            : path->StrokeContainsPoint({point->x, point->y}, pen->thickness, style.get(), &matrix, tolerance, &hit);
        if (com::failed(status)) {
            if (status == com::out_of_memory) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
            if (status == com::invalid_argument || status == com::pointer_error) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            if (status == d2d::not_implemented) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        }
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
    namespace d2d = progpu::native::direct2d::compat;
    namespace com = progpu::native::com;
    try {
        com::pointer<d2d::factory> owner;
        com::pointer<d2d::path_geometry> path;
        com::result status = d2d::create_factory(owner.put());
        if (!com::failed(status)) status = d2d::detail::create_native_fill_geometry(owner.get(),
            {segments, segment_count}, fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                ? d2d::fill_mode::alternate : d2d::fill_mode::winding, path.put());
        std::int32_t result = 0;
        if (!com::failed(status)) status = path->FillContainsPoint(
            {point->x, point->y}, nullptr, tolerance, &result);
        if (com::failed(status)) {
            if (status == com::out_of_memory) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
            if (status == com::invalid_argument || status == com::pointer_error) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            if (status == d2d::not_implemented) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
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

    namespace d2d = progpu::native::direct2d::compat;
    namespace com = progpu::native::com;
    static_assert(static_cast<std::uint32_t>(d2d::combine_mode::union_value) == 0U);
    static_assert(static_cast<std::uint32_t>(d2d::combine_mode::intersect) == 1U);
    static_assert(static_cast<std::uint32_t>(d2d::combine_mode::xor_value) == 2U);
    static_assert(static_cast<std::uint32_t>(d2d::combine_mode::exclude) == 3U);
    try {
        std::vector<std::vector<d2d::point_2f>> contours;
        const auto fill = [](std::uint32_t value) {
            return value == PROGPU_NATIVE_FILL_RULE_EVEN_ODD ? d2d::fill_mode::alternate : d2d::fill_mode::winding;
        };
        const com::result status = d2d::detail::combine_native_fill_contours(
            {first, first_count}, fill(first_fill), {second, second_count}, fill(second_fill),
            static_cast<d2d::combine_mode>(mode), tolerance, contours);
        if (com::failed(status)) {
            if (status == com::out_of_memory) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
            if (status == com::invalid_argument || status == com::pointer_error) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            if (status == d2d::not_implemented) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
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
        static_assert(sizeof(d2d::point_2f) == sizeof(progpu_native_point));
        static_assert(offsetof(d2d::point_2f, x) == offsetof(progpu_native_point, x));
        static_assert(offsetof(d2d::point_2f, y) == offsetof(progpu_native_point, y));
        static_assert(std::is_trivially_copyable_v<d2d::point_2f>);
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
