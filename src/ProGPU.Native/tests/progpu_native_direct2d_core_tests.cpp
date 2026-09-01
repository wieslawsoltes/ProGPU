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
    return 0;
}
