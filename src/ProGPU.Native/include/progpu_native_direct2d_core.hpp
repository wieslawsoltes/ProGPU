#pragma once

#include "progpu_native_com.hpp"
#include "progpu_native_direct2d.h"

#include <array>
#include <cstdint>

namespace progpu::native::direct2d::core {

inline constexpr float default_flattening_tolerance = 0.25F;

/* Direct2D rectangles use edge coordinates rather than x/y/width/height.
 * Keeping that representation in the behavior core preserves valid spans
 * whose mathematical width is larger than FLT_MAX. */
struct rectangle_edges_f final {
    float left;
    float top;
    float right;
    float bottom;
};

class rectangle_geometry final {
public:
    explicit rectangle_geometry(rectangle_edges_f rectangle) noexcept;

    [[nodiscard]] static bool valid_rectangle(
        const rectangle_edges_f& rectangle) noexcept;

    [[nodiscard]] com::result vertices(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        std::array<progpu_native_direct2d_point_2f, 4U>& value) const
        noexcept;

    [[nodiscard]] com::result bounds(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        rectangle_edges_f* value) const noexcept;

    [[nodiscard]] com::result fill_contains_point(
        progpu_native_direct2d_point_2f point,
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::uint32_t* contains) const noexcept;

    [[nodiscard]] com::result tessellate(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::array<progpu_native_direct2d_triangle, 2U>* triangles) const
        noexcept;

    [[nodiscard]] com::result area(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* value) const noexcept;

    [[nodiscard]] com::result length(
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* value) const noexcept;

    [[nodiscard]] com::result point_at_length(
        float length,
        const progpu_native_direct2d_matrix_3x2_f* world_transform,
        float flattening_tolerance,
        progpu_native_direct2d_point_2f* point,
        progpu_native_direct2d_point_2f* unit_tangent) const noexcept;

    [[nodiscard]] const rectangle_edges_f& rectangle() const
        noexcept;

private:
    rectangle_edges_f rectangle_{};
};

} // namespace progpu::native::direct2d::core
