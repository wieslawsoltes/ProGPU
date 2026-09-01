#include "progpu_native_direct2d_core.hpp"

#include <array>
#include <cmath>
#include <cstdint>
#include <limits>

namespace core = progpu::native::direct2d::core;
namespace com = progpu::native::com;

namespace {

[[nodiscard]] bool approximately_equal(float left, float right) noexcept
{
    return std::abs(left - right) <= 0.0001F;
}

} // namespace

int main()
{
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
    return 0;
}
