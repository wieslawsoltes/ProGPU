#include "progpu_native_direct2d_core.hpp"

#include <array>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>

namespace core = progpu::native::direct2d::core;
namespace com = progpu::native::com;

namespace {

[[nodiscard]] bool approximately_equal(float left, float right) noexcept
{
    return std::abs(left - right) <= 0.0001F;
}

// Deliberately scalar test oracle for the original four-corner algorithm.
core::rectangle_edges_f scalar_viewport_bounds(
    const progpu_native_direct2d_matrix_3x2_f& inverse, double width, double height,
    double target_x = 0.0, double target_y = 0.0)
{
    const double origin_x = target_x * inverse.m11 + target_y * inverse.m21 + inverse.m31;
    const double origin_y = target_x * inverse.m12 + target_y * inverse.m22 + inverse.m32;
    double left = origin_x, top = origin_y, right = left, bottom = top;
    for (const double x : {0.0, width}) {
        for (const double y : {0.0, height}) {
            const double local_x = x * inverse.m11 + y * inverse.m21 + origin_x;
            const double local_y = x * inverse.m12 + y * inverse.m22 + origin_y;
            left = std::min(left, local_x);
            right = std::max(right, local_x);
            top = std::min(top, local_y);
            bottom = std::max(bottom, local_y);
        }
    }
    const auto outward = [](double value, bool upper) {
        float rounded = static_cast<float>(value);
        if (upper ? double{rounded} < value : double{rounded} > value)
            rounded = std::nextafter(rounded, upper ? std::numeric_limits<float>::infinity()
                : -std::numeric_limits<float>::infinity());
        return rounded;
    };
    core::rectangle_edges_f result{outward(left, false), outward(top, false),
        outward(right, true), outward(bottom, true)};
    result.right = result.left + outward(double{result.right} - result.left, true);
    result.bottom = result.top + outward(double{result.bottom} - result.top, true);
    return result;
}

bool viewport_bounds_contract()
{
    const std::array inverses{
        progpu_native_direct2d_matrix_3x2_f{1, 0, 0, 1, 0, 0},
        progpu_native_direct2d_matrix_3x2_f{0, -1, 1, 0, -20, 10},
        progpu_native_direct2d_matrix_3x2_f{0.5F, 0.25F, -0.75F, 2, 17, -23},
        progpu_native_direct2d_matrix_3x2_f{0.6F, -0.8F, 0.8F, 0.6F, -30, 10}};
    for (const auto& inverse : inverses) {
        for (const double width : {640.0, 640.0 * 96.0 / 144.0, 7.25}) {
            for (const double height : {480.0, 480.0 * 96.0 / 192.0, 3.125}) {
                core::rectangle_edges_f result{};
                const auto expected = scalar_viewport_bounds(inverse, width, height);
                if (core::viewport_coverage_bounds(inverse, width, height, &result) != com::ok ||
                    std::memcmp(&result, &expected, sizeof(result)) != 0) return false;
                for (const auto origin : {std::array<double, 2U>{0, 0},
                         std::array<double, 2U>{-31.25, 17.125}, std::array<double, 2U>{4096.5, -2048.75}}) {
                    const auto rectangular = scalar_viewport_bounds(inverse, width, height, origin[0], origin[1]);
                    if (core::rectangular_coverage_bounds(inverse, origin[0], origin[1], width, height, &result) != com::ok ||
                        std::memcmp(&result, &rectangular, sizeof(result)) != 0) return false;
                }
            }
        }
    }
    core::rectangle_edges_f result{1, 2, 3, 4};
    const auto identity = inverses[0];
    if (core::rectangular_coverage_bounds(identity, 1, 2, 3, 4, nullptr) != com::pointer_error) return false;
    for (const double origin : {std::numeric_limits<double>::infinity(),
             std::numeric_limits<double>::quiet_NaN(), std::numeric_limits<double>::max()}) {
        if (core::rectangular_coverage_bounds(identity, origin, 0, 1, 1, &result) != com::invalid_argument ||
            result.left != 0 || result.top != 0 || result.right != 0 || result.bottom != 0 ||
            core::rectangular_coverage_bounds(identity, 0, origin, 1, 1, &result) != com::invalid_argument) return false;
    }
    if (core::viewport_coverage_bounds(identity, 1, 1, nullptr) != com::pointer_error) return false;
    for (const double width : {0.0, -1.0, std::numeric_limits<double>::infinity(),
             std::numeric_limits<double>::quiet_NaN(), std::numeric_limits<double>::max()}) {
        if (core::viewport_coverage_bounds(identity, width, 1, &result) != com::invalid_argument ||
            result.left != 0 || result.top != 0 || result.right != 0 || result.bottom != 0) return false;
    }
    auto invalid = identity;
    invalid.m21 = std::numeric_limits<float>::quiet_NaN();
    return core::viewport_coverage_bounds(invalid, 1, 1, &result) == com::invalid_argument &&
        core::viewport_coverage_bounds(identity, 1, 0, &result) == com::invalid_argument;
}

} // namespace

int main()
{
    const progpu_native_direct2d_target_extent target{sizeof(target), 640U, 480U, 0U, 144.0F, 192.0F};
    if (!core::valid_target_extent(&target) || core::valid_target_extent(nullptr)) return 25;
    for (std::uint32_t field = 0U; field < 6U; ++field) {
        auto invalid = target;
        switch (field) {
        case 0U: invalid.struct_size -= 1U; break;
        case 1U: invalid.pixel_width = 0U; break;
        case 2U: invalid.pixel_height = 0U; break;
        case 3U: invalid.reserved = 1U; break;
        case 4U: invalid.dpi_x = std::numeric_limits<float>::quiet_NaN(); break;
        default: invalid.dpi_y = std::numeric_limits<float>::infinity(); break;
        }
        if (core::valid_target_extent(&invalid)) return 25;
    }
    for (const float value : {0.0F, -1.0F}) {
        auto invalid = target;
        invalid.dpi_x = value;
        if (core::valid_target_extent(&invalid)) return 25;
        invalid = target;
        invalid.dpi_y = value;
        if (core::valid_target_extent(&invalid)) return 25;
    }
    if (!viewport_bounds_contract()) return 24;
    const core::rectangle_geometry rectangle({1.0F, 2.0F, 4.0F, 6.0F});
    const progpu_native_direct2d_matrix_3x2_f transform{
        0.0F, 2.0F, -3.0F, 0.0F, 10.0F, 20.0F};

    core::rectangle_edges_f bounds{};
    if (rectangle.bounds(&transform, &bounds) != com::ok ||
        !approximately_equal(bounds.left, -8.0F) ||
        !approximately_equal(bounds.top, 22.0F) ||
        !approximately_equal(bounds.right, 4.0F) ||
        !approximately_equal(bounds.bottom, 28.0F)) {
        return 1;
    }

    float area = 0.0F;
    float length = 0.0F;
    if (rectangle.area(
            &transform, core::default_flattening_tolerance, &area) !=
            com::ok ||
        rectangle.length(
            &transform, core::default_flattening_tolerance, &length) !=
            com::ok ||
        !approximately_equal(area, 72.0F) ||
        !approximately_equal(length, 36.0F)) {
        return 2;
    }

    std::uint32_t contains = 0U;
    if (rectangle.fill_contains_point(
            {-2.0F, 25.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1U) {
        return 3;
    }
    if (rectangle.fill_contains_point(
            {20.0F, 25.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 0U) {
        return 4;
    }

    std::array<progpu_native_direct2d_triangle, 2U> triangles{};
    if (rectangle.tessellate(
            &transform,
            core::default_flattening_tolerance,
            &triangles) != com::ok ||
        !approximately_equal(triangles[0U].point1.x, 4.0F) ||
        !approximately_equal(triangles[1U].point3.y, 22.0F)) {
        return 5;
    }

    progpu_native_direct2d_point_2f point{};
    progpu_native_direct2d_point_2f tangent{};
    if (rectangle.point_at_length(
            6.0F,
            &transform,
            core::default_flattening_tolerance,
            &point,
            &tangent) != com::ok ||
        !approximately_equal(point.x, 4.0F) ||
        !approximately_equal(point.y, 28.0F) ||
        !approximately_equal(tangent.x, 0.0F) ||
        !approximately_equal(tangent.y, 1.0F)) {
        return 6;
    }

    const core::rectangle_geometry invalid(
        {0.0F, 0.0F, -1.0F, 1.0F});
    if (invalid.bounds(nullptr, &bounds) != com::invalid_argument ||
        rectangle.bounds(nullptr, nullptr) != com::pointer_error) {
        return 7;
    }
    const float infinity = std::numeric_limits<float>::infinity();
    const progpu_native_direct2d_matrix_3x2_f invalid_transform{
        infinity, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    if (rectangle.bounds(&invalid_transform, &bounds) !=
        com::invalid_argument) {
        return 8;
    }

    const core::rectangle_geometry degenerate({3.0F, 4.0F, 3.0F, 4.0F});
    if (degenerate.point_at_length(
            0.0F,
            nullptr,
            core::default_flattening_tolerance,
            &point,
            &tangent) != com::ok ||
        !approximately_equal(point.x, 3.0F) ||
        !approximately_equal(point.y, 4.0F) ||
        !approximately_equal(tangent.x, 0.0F) ||
        !approximately_equal(tangent.y, 0.0F)) {
        return 9;
    }

    const progpu_native_direct2d_matrix_3x2_f first{
        2.0F, 0.0F, 0.0F, 3.0F, 10.0F, -4.0F};
    const progpu_native_direct2d_matrix_3x2_f second{
        0.0F, 1.0F, -1.0F, 0.0F, 5.0F, 6.0F};
    progpu_native_direct2d_matrix_3x2_f composed{};
    if (core::compose_transform(first, &second, &composed) != com::ok ||
        !approximately_equal(composed.m11, 0.0F) ||
        !approximately_equal(composed.m12, 2.0F) ||
        !approximately_equal(composed.m21, -3.0F) ||
        !approximately_equal(composed.m22, 0.0F) ||
        !approximately_equal(composed.m31, 9.0F) ||
        !approximately_equal(composed.m32, 16.0F) ||
        core::compose_transform(first, &second, nullptr) !=
            com::pointer_error ||
        core::compose_transform(first, &invalid_transform, &composed) !=
            com::invalid_argument) {
        return 10;
    }

    const core::arc_segment_f arc{
        {2.0F, 0.0F},
        {1.0F, 1.0F},
        0.0F,
        core::arc_sweep_direction::clockwise,
        core::arc_size_kind::small_value};
    std::array<core::cubic_bezier_segment_f, 4U> arc_cubics{};
    std::uint32_t arc_cubic_count = 0U;
    if (!core::valid_arc_segment(arc) ||
        core::arc_to_cubics(
            {0.0F, 0.0F}, arc, &arc_cubics, &arc_cubic_count) != com::ok ||
        arc_cubic_count != 2U ||
        !approximately_equal(arc_cubics[0U].point3.x, 1.0F) ||
        !approximately_equal(arc_cubics[0U].point3.y, -1.0F) ||
        !approximately_equal(arc_cubics[1U].point3.x, 2.0F) ||
        !approximately_equal(arc_cubics[1U].point3.y, 0.0F)) {
        return 11;
    }
    if (core::arc_to_cubics(
            {0.0F, 0.0F}, arc, nullptr, &arc_cubic_count) !=
            com::pointer_error ||
        core::arc_to_cubics(
            {0.0F, 0.0F}, arc, &arc_cubics, nullptr) !=
            com::pointer_error) {
        return 12;
    }
    core::arc_segment_f invalid_arc = arc;
    invalid_arc.sweep = static_cast<core::arc_sweep_direction>(99U);
    arc_cubic_count = 99U;
    if (core::valid_arc_segment(invalid_arc) ||
        core::arc_to_cubics(
            {0.0F, 0.0F},
            invalid_arc,
            &arc_cubics,
            &arc_cubic_count) != com::invalid_argument ||
        arc_cubic_count != 0U) {
        return 13;
    }

    const core::ellipse_f ellipse{{2.0F, 3.0F}, 4.0F, 2.0F};
    if (!core::valid_ellipse(ellipse)) {
        return 14;
    }
    if (core::ellipse_fill_contains_point(
            ellipse,
            {1.0F, 24.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1U ||
        core::ellipse_fill_contains_point(
            ellipse,
            {8.0F, 24.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 0U) {
        return 15;
    }
    std::array<core::cubic_bezier_segment_f, 4U> ellipse_cubics{};
    if (core::ellipse_to_cubics(
            ellipse, &point, &ellipse_cubics) != com::ok ||
        !approximately_equal(point.x, 6.0F) ||
        !approximately_equal(point.y, 3.0F) ||
        !approximately_equal(ellipse_cubics[0U].point3.x, 2.0F) ||
        !approximately_equal(ellipse_cubics[0U].point3.y, 5.0F) ||
        !approximately_equal(ellipse_cubics[3U].point3.x, 6.0F) ||
        !approximately_equal(ellipse_cubics[3U].point3.y, 3.0F)) {
        return 16;
    }
    core::ellipse_f invalid_ellipse = ellipse;
    invalid_ellipse.radius_x = -1.0F;
    if (core::valid_ellipse(invalid_ellipse)) {
        return 17;
    }

    const core::rounded_rectangle_f rounded_rectangle{
        {0.0F, 0.0F, 10.0F, 8.0F}, 3.0F, 2.0F};
    progpu_native_direct2d_point_2f rounded_start{};
    std::array<progpu_native_direct2d_point_2f, 4U> rounded_lines{};
    std::array<core::cubic_bezier_segment_f, 4U> rounded_corners{};
    if (!core::valid_rounded_rectangle(rounded_rectangle) ||
        core::rounded_rectangle_to_path(
            rounded_rectangle,
            &rounded_start,
            &rounded_lines,
            &rounded_corners) != com::ok ||
        !approximately_equal(rounded_start.x, 3.0F) ||
        !approximately_equal(rounded_start.y, 0.0F) ||
        !approximately_equal(rounded_lines[0U].x, 7.0F) ||
        !approximately_equal(rounded_lines[1U].y, 6.0F) ||
        !approximately_equal(rounded_corners[3U].point3.x, 3.0F) ||
        !approximately_equal(rounded_corners[3U].point3.y, 0.0F)) {
        return 18;
    }
    if (core::rounded_rectangle_fill_contains_point(
            rounded_rectangle,
            {-2.0F, 30.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1U ||
        core::rounded_rectangle_fill_contains_point(
            rounded_rectangle,
            {9.7F, 20.2F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 0U) {
        return 19;
    }
    core::rounded_rectangle_f invalid_rounded_rectangle = rounded_rectangle;
    invalid_rounded_rectangle.radius_x = -1.0F;
    if (core::valid_rounded_rectangle(invalid_rounded_rectangle)) {
        return 20;
    }

    const core::stroke_style_properties_f stroke_properties{
        core::cap_style::round,
        core::cap_style::square,
        core::cap_style::triangle,
        core::line_join::bevel,
        4.0F,
        core::dash_style::custom,
        0.5F};
    const std::array<float, 4U> dashes{2.0F, 1.0F, 0.5F, 1.0F};
    if (!core::valid_stroke_style(
            stroke_properties,
            dashes.data(),
            static_cast<std::uint32_t>(dashes.size()))) {
        return 21;
    }
    core::stroke_style_properties_f invalid_stroke = stroke_properties;
    invalid_stroke.miter_limit = 0.0F;
    if (core::valid_stroke_style(
            invalid_stroke,
            dashes.data(),
            static_cast<std::uint32_t>(dashes.size())) ||
        core::valid_stroke_style(stroke_properties, nullptr, 0U)) {
        return 22;
    }
    const std::array<float, 2U> empty_dashes{0.0F, 0.0F};
    if (core::valid_stroke_style(
            stroke_properties,
            empty_dashes.data(),
            static_cast<std::uint32_t>(empty_dashes.size()))) {
        return 23;
    }
    return 0;
}
