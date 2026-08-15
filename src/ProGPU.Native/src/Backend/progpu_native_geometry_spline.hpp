#pragma once

#include "progpu_native_geometry_dash.hpp"

namespace progpu::native {

struct spline_homogeneous_point {
    float x;
    float y;
    float weight;
};

inline bool try_get_spline_domain(
    const progpu_native_spline& spline,
    const double* knots,
    double& start_knot,
    double& end_knot) noexcept {
    start_knot = 0.0;
    end_knot = 0.0;
    const std::size_t degree = spline.degree;
    const std::size_t control_count = spline.stroke.point_count;
    if (knots == nullptr || control_count < 2U ||
        degree > std::numeric_limits<std::size_t>::max() - control_count - 1U ||
        spline.knot_count < control_count + degree + 1U ||
        degree >= spline.knot_count) {
        return false;
    }
    const std::size_t end_index = spline.knot_count - degree - 1U;
    if (end_index <= degree || end_index >= spline.knot_count) {
        return false;
    }
    start_knot = knots[degree];
    end_knot = knots[end_index];
    return std::isfinite(start_knot) && std::isfinite(end_knot) &&
        end_knot > start_knot;
}

inline bool try_get_spline_segment_count(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    std::size_t& segment_count) noexcept {
    segment_count = 0U;
    if (control_points == nullptr || spline.stroke.point_count == 0U ||
        !is_finite(spline.stroke.transform)) {
        return false;
    }
    float minimum_x = std::numeric_limits<float>::max();
    float minimum_y = std::numeric_limits<float>::max();
    float maximum_x = std::numeric_limits<float>::lowest();
    float maximum_y = std::numeric_limits<float>::lowest();
    for (std::size_t index = 0U;
         index < spline.stroke.point_count;
         ++index) {
        if (!is_finite(control_points[index])) {
            return false;
        }
        const progpu_native_point screen = transformed_point(
            spline.stroke.transform,
            control_points[index]);
        if (!is_finite(screen)) {
            return false;
        }
        minimum_x = std::min(minimum_x, screen.x);
        minimum_y = std::min(minimum_y, screen.y);
        maximum_x = std::max(maximum_x, screen.x);
        maximum_y = std::max(maximum_y, screen.y);
    }
    const float extent = std::hypot(
        maximum_x - minimum_x,
        maximum_y - minimum_y);
    if (!std::isfinite(extent)) {
        return false;
    }
    if (extent < 2.0F) {
        return true;
    }
    segment_count = extent < 20.0F
        ? 10U
        : extent < 80.0F
            ? 25U
            : extent < 250.0F ? 50U : 100U;
    return true;
}

inline bool try_evaluate_spline_point(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    const double* knots,
    const double* weights,
    double parameter,
    std::vector<spline_homogeneous_point>& work,
    progpu_native_point& output) {
    const std::size_t degree = spline.degree;
    const std::size_t end_index = spline.knot_count - degree - 1U;
    parameter = std::clamp(parameter, knots[degree], knots[end_index]);
    std::size_t span = std::numeric_limits<std::size_t>::max();
    for (std::size_t index = degree;
         index + 1U < spline.knot_count;
         ++index) {
        if (parameter >= knots[index] && parameter <= knots[index + 1U]) {
            span = index;
            break;
        }
    }
    if (span == std::numeric_limits<std::size_t>::max()) {
        span = spline.knot_count - degree - 2U;
    }

    work.resize(degree + 1U);
    for (std::size_t index = 0U; index <= degree; ++index) {
        const std::ptrdiff_t control_index =
            static_cast<std::ptrdiff_t>(span) -
            static_cast<std::ptrdiff_t>(degree) +
            static_cast<std::ptrdiff_t>(index);
        if (control_index >= 0 &&
            static_cast<std::size_t>(control_index) <
                spline.stroke.point_count) {
            float weight = 1.0F;
            if (weights != nullptr &&
                static_cast<std::size_t>(control_index) <
                    spline.weight_count) {
                weight = static_cast<float>(weights[control_index]);
            }
            const auto& control = control_points[control_index];
            work[index] = {
                control.x * weight,
                control.y * weight,
                weight
            };
        } else {
            work[index] = {};
        }
    }

    for (std::size_t level = 1U; level <= degree; ++level) {
        for (std::size_t index = degree; index >= level; --index) {
            const std::size_t knot_index = span - degree + index;
            const double denominator =
                knots[knot_index + degree + 1U - level] -
                knots[knot_index];
            const float alpha = denominator > 1.0e-9
                ? static_cast<float>(
                    (parameter - knots[knot_index]) / denominator)
                : 0.0F;
            const float inverse = 1.0F - alpha;
            work[index] = {
                inverse * work[index - 1U].x + alpha * work[index].x,
                inverse * work[index - 1U].y + alpha * work[index].y,
                inverse * work[index - 1U].weight +
                    alpha * work[index].weight
            };
        }
    }

    const auto final = work[degree];
    output = std::abs(final.weight) > 1.0e-9F
        ? progpu_native_point{
            final.x / final.weight,
            final.y / final.weight}
        : progpu_native_point{final.x, final.y};
    return is_finite(output);
}

inline bool spline_capacity(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    const double* knots,
    std::size_t& segment_count,
    std::size_t& vertex_count,
    std::size_t& index_count) noexcept {
    if (spline.reserved != 0U || spline.degree > (1U << 20U)) {
        return false;
    }
    if (spline.stroke.point_count < 2U || spline.knot_count == 0U) {
        segment_count = 0U;
        vertex_count = 0U;
        index_count = 0U;
        return true;
    }
    double start_knot = 0.0;
    double end_knot = 0.0;
    if (!try_get_spline_domain(
            spline,
            knots,
            start_knot,
            end_knot)) {
        segment_count = spline.stroke.point_count - 1U;
        return polyline_capacity(spline.stroke, vertex_count, index_count);
    }
    if (!try_get_spline_segment_count(
            spline,
            control_points,
            segment_count)) {
        return false;
    }
    if (segment_count == 0U) {
        vertex_count = 0U;
        index_count = 0U;
        return true;
    }
    progpu_native_polyline sampled_stroke = spline.stroke;
    sampled_stroke.point_offset = 0U;
    sampled_stroke.point_count = segment_count + 1U;
    return polyline_capacity(sampled_stroke, vertex_count, index_count);
}

inline bool spline_capacity(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    const double* knots,
    const double* weights,
    const progpu_native_dash_style* dash_styles,
    std::size_t dash_style_count,
    const double* doubles,
    std::size_t double_count,
    std::size_t& segment_count,
    std::array<progpu_native_point, 101U>& sampled_points,
    std::vector<spline_homogeneous_point>& work,
    std::size_t& vertex_count,
    std::size_t& index_count) {
    if (!spline_capacity(
            spline,
            control_points,
            knots,
            segment_count,
            vertex_count,
            index_count)) {
        return false;
    }
    if (spline.stroke.dash_style == 0U || segment_count == 0U) {
        return true;
    }
    double start_knot = 0.0;
    double end_knot = 0.0;
    if (!try_get_spline_domain(spline, knots, start_knot, end_knot)) {
        return polyline_capacity(
            spline.stroke,
            control_points,
            dash_styles,
            dash_style_count,
            doubles,
            double_count,
            vertex_count,
            index_count);
    }
    if (segment_count >= sampled_points.size()) {
        return false;
    }
    const double delta =
        (end_knot - start_knot) / static_cast<double>(segment_count);
    for (std::size_t index = 0U; index <= segment_count; ++index) {
        if (!try_evaluate_spline_point(
                spline,
                control_points,
                knots,
                weights,
                start_knot + static_cast<double>(index) * delta,
                work,
                sampled_points[index])) {
            return false;
        }
    }
    progpu_native_polyline sampled_stroke = spline.stroke;
    sampled_stroke.point_offset = 0U;
    sampled_stroke.point_count = segment_count + 1U;
    return polyline_capacity(
        sampled_stroke,
        sampled_points.data(),
        dash_styles,
        dash_style_count,
        doubles,
        double_count,
        vertex_count,
        index_count);
}

inline bool append_spline(
    const progpu_native_spline& spline,
    const progpu_native_point* control_points,
    const double* knots,
    const double* weights,
    std::size_t segment_count,
    float brush_index,
    std::array<progpu_native_point, 101U>& sampled_points,
    std::vector<spline_homogeneous_point>& work,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices,
    const progpu_native_dash_style* dash_styles = nullptr,
    std::size_t dash_style_count = 0U,
    const double* doubles = nullptr,
    std::size_t double_count = 0U) {
    double start_knot = 0.0;
    double end_knot = 0.0;
    if (!try_get_spline_domain(
            spline,
            knots,
            start_knot,
            end_knot)) {
        return append_polyline(
            spline.stroke,
            control_points,
            brush_index,
            vertices,
            indices,
            dash_styles,
            dash_style_count,
            doubles,
            double_count);
    }
    if (segment_count == 0U || segment_count >= sampled_points.size()) {
        return segment_count == 0U;
    }
    const double delta =
        (end_knot - start_knot) / static_cast<double>(segment_count);
    for (std::size_t index = 0U; index <= segment_count; ++index) {
        if (!try_evaluate_spline_point(
                spline,
                control_points,
                knots,
                weights,
                start_knot + static_cast<double>(index) * delta,
                work,
                sampled_points[index])) {
            return false;
        }
    }
    progpu_native_polyline sampled_stroke = spline.stroke;
    sampled_stroke.point_offset = 0U;
    sampled_stroke.point_count = segment_count + 1U;
    return append_polyline(
        sampled_stroke,
        sampled_points.data(),
        brush_index,
        vertices,
        indices,
        dash_styles,
        dash_style_count,
        doubles,
        double_count);
}

} // namespace progpu::native
