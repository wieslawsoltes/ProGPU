#include "progpu_native_direct2d_core.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace progpu::native::direct2d::core {
namespace {

[[nodiscard]] bool finite_point(
    const progpu_native_direct2d_point_2f& point) noexcept
{
    return std::isfinite(point.x) && std::isfinite(point.y);
}

[[nodiscard]] bool valid_tolerance(float value) noexcept
{
    return std::isfinite(value) && value > 0.0F;
}

[[nodiscard]] double twice_area(
    const std::array<progpu_native_direct2d_point_2f, 4U>& points) noexcept
{
    double result = 0.0;
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        result += static_cast<double>(start.x) * end.y -
            static_cast<double>(start.y) * end.x;
    }
    return result;
}

} // namespace

bool valid_transform(
    const progpu_native_direct2d_matrix_3x2_f* transform) noexcept
{
    return transform == nullptr ||
        (std::isfinite(transform->m11) &&
            std::isfinite(transform->m12) &&
            std::isfinite(transform->m21) &&
            std::isfinite(transform->m22) &&
            std::isfinite(transform->m31) &&
            std::isfinite(transform->m32));
}

com::result compose_transform(
    const progpu_native_direct2d_matrix_3x2_f& first,
    const progpu_native_direct2d_matrix_3x2_f* second,
    progpu_native_direct2d_matrix_3x2_f* result) noexcept
{
    if (result == nullptr) {
        return com::pointer_error;
    }
    *result = {};
    if (!valid_transform(&first) || !valid_transform(second)) {
        return com::invalid_argument;
    }
    const progpu_native_direct2d_matrix_3x2_f identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const auto& right = second == nullptr ? identity : *second;
    const std::array<double, 6U> values{{
        static_cast<double>(first.m11) * right.m11 +
            static_cast<double>(first.m12) * right.m21,
        static_cast<double>(first.m11) * right.m12 +
            static_cast<double>(first.m12) * right.m22,
        static_cast<double>(first.m21) * right.m11 +
            static_cast<double>(first.m22) * right.m21,
        static_cast<double>(first.m21) * right.m12 +
            static_cast<double>(first.m22) * right.m22,
        static_cast<double>(first.m31) * right.m11 +
            static_cast<double>(first.m32) * right.m21 + right.m31,
        static_cast<double>(first.m31) * right.m12 +
            static_cast<double>(first.m32) * right.m22 + right.m32}};
    constexpr double maximum =
        static_cast<double>((std::numeric_limits<float>::max)());
    if (!std::all_of(values.begin(), values.end(), [&](double value) {
            return std::isfinite(value) && value >= -maximum &&
                value <= maximum;
        })) {
        return com::invalid_argument;
    }
    *result = {
        static_cast<float>(values[0U]),
        static_cast<float>(values[1U]),
        static_cast<float>(values[2U]),
        static_cast<float>(values[3U]),
        static_cast<float>(values[4U]),
        static_cast<float>(values[5U])};
    return com::ok;
}

bool valid_arc_segment(const arc_segment_f& arc) noexcept
{
    return finite_point(arc.point) && std::isfinite(arc.size.width) &&
        std::isfinite(arc.size.height) && arc.size.width >= 0.0F &&
        arc.size.height >= 0.0F && std::isfinite(arc.rotation_angle) &&
        (arc.sweep == arc_sweep_direction::counter_clockwise ||
            arc.sweep == arc_sweep_direction::clockwise) &&
        (arc.size_kind == arc_size_kind::small_value ||
            arc.size_kind == arc_size_kind::large_value);
}

com::result arc_to_cubics(
    progpu_native_direct2d_point_2f start,
    const arc_segment_f& arc,
    std::array<cubic_bezier_segment_f, 4U>* cubics,
    std::uint32_t* cubic_count) noexcept
{
    if (cubics == nullptr || cubic_count == nullptr) {
        return com::pointer_error;
    }
    *cubics = {};
    *cubic_count = 0U;
    if (!finite_point(start) || !valid_arc_segment(arc)) {
        return com::invalid_argument;
    }
    if ((start.x == arc.point.x && start.y == arc.point.y) ||
        arc.size.width == 0.0F || arc.size.height == 0.0F) {
        return com::ok;
    }

    constexpr double pi = 3.141592653589793238462643383279502884;
    const double phi = std::remainder(
        static_cast<double>(arc.rotation_angle), 360.0) * pi / 180.0;
    const double cosine = std::cos(phi);
    const double sine = std::sin(phi);
    const double half_dx =
        (static_cast<double>(start.x) - arc.point.x) * 0.5;
    const double half_dy =
        (static_cast<double>(start.y) - arc.point.y) * 0.5;
    const double x1_prime = cosine * half_dx + sine * half_dy;
    const double y1_prime = -sine * half_dx + cosine * half_dy;
    double radius_x = std::abs(static_cast<double>(arc.size.width));
    double radius_y = std::abs(static_cast<double>(arc.size.height));
    double radius_x_squared = radius_x * radius_x;
    double radius_y_squared = radius_y * radius_y;
    const double scale =
        x1_prime * x1_prime / radius_x_squared +
        y1_prime * y1_prime / radius_y_squared;
    if (!std::isfinite(scale)) {
        return com::invalid_argument;
    }
    if (scale > 1.0) {
        const double factor = std::sqrt(scale);
        radius_x *= factor;
        radius_y *= factor;
        radius_x_squared = radius_x * radius_x;
        radius_y_squared = radius_y * radius_y;
    }

    const bool large = arc.size_kind == arc_size_kind::large_value;
    const bool clockwise =
        arc.sweep == arc_sweep_direction::clockwise;
    const double numerator = std::max(
        0.0,
        radius_x_squared * radius_y_squared -
            radius_x_squared * y1_prime * y1_prime -
            radius_y_squared * x1_prime * x1_prime);
    const double denominator =
        radius_x_squared * y1_prime * y1_prime +
        radius_y_squared * x1_prime * x1_prime;
    const double sign = large == clockwise ? -1.0 : 1.0;
    const double coefficient = denominator == 0.0
        ? 0.0
        : sign * std::sqrt(numerator / denominator);
    const double center_x_prime =
        coefficient * radius_x * y1_prime / radius_y;
    const double center_y_prime =
        -coefficient * radius_y * x1_prime / radius_x;
    const double center_x = cosine * center_x_prime -
        sine * center_y_prime +
        (static_cast<double>(start.x) + arc.point.x) * 0.5;
    const double center_y = sine * center_x_prime +
        cosine * center_y_prime +
        (static_cast<double>(start.y) + arc.point.y) * 0.5;

    const auto vector_angle = [](double ux, double uy, double vx, double vy) {
        return std::atan2(ux * vy - uy * vx, ux * vx + uy * vy);
    };
    const double ux = (x1_prime - center_x_prime) / radius_x;
    const double uy = (y1_prime - center_y_prime) / radius_y;
    const double vx = (-x1_prime - center_x_prime) / radius_x;
    const double vy = (-y1_prime - center_y_prime) / radius_y;
    const double start_angle = std::atan2(uy, ux);
    double delta = vector_angle(ux, uy, vx, vy);
    if (!clockwise && delta > 0.0) {
        delta -= 2.0 * pi;
    } else if (clockwise && delta < 0.0) {
        delta += 2.0 * pi;
    }
    *cubic_count = static_cast<std::uint32_t>(std::clamp(
        std::ceil(std::abs(delta) / (pi * 0.5)),
        1.0,
        4.0));
    const double step = delta / *cubic_count;
    progpu_native_direct2d_point_2f current = start;
    constexpr double maximum =
        static_cast<double>((std::numeric_limits<float>::max)());
    for (std::uint32_t index = 0U; index < *cubic_count; ++index) {
        const double angle0 = start_angle + step * index;
        const double angle1 = angle0 + step;
        const double alpha = 4.0 / 3.0 * std::tan(step * 0.25);
        const auto evaluate = [&](double angle) {
            const double local_x = radius_x * std::cos(angle);
            const double local_y = radius_y * std::sin(angle);
            return std::array<double, 2U>{
                center_x + cosine * local_x - sine * local_y,
                center_y + sine * local_x + cosine * local_y};
        };
        const auto derivative = [&](double angle) {
            const double local_x = -radius_x * std::sin(angle);
            const double local_y = radius_y * std::cos(angle);
            return std::array<double, 2U>{
                cosine * local_x - sine * local_y,
                sine * local_x + cosine * local_y};
        };
        const auto evaluated_end = evaluate(angle1);
        const std::array<double, 2U> end = index + 1U == *cubic_count
            ? std::array<double, 2U>{arc.point.x, arc.point.y}
            : evaluated_end;
        const auto tangent0 = derivative(angle0);
        const auto tangent1 = derivative(angle1);
        const std::array<std::array<double, 2U>, 3U> values{{
            {static_cast<double>(current.x) + alpha * tangent0[0U],
                static_cast<double>(current.y) + alpha * tangent0[1U]},
            {end[0U] - alpha * tangent1[0U],
                end[1U] - alpha * tangent1[1U]},
            end}};
        if (!std::all_of(values.begin(), values.end(), [&](const auto& point) {
                return std::isfinite(point[0U]) &&
                    std::isfinite(point[1U]) &&
                    std::abs(point[0U]) <= maximum &&
                    std::abs(point[1U]) <= maximum;
            })) {
            *cubics = {};
            *cubic_count = 0U;
            return com::invalid_argument;
        }
        (*cubics)[index] = {
            {static_cast<float>(values[0U][0U]),
                static_cast<float>(values[0U][1U])},
            {static_cast<float>(values[1U][0U]),
                static_cast<float>(values[1U][1U])},
            {static_cast<float>(values[2U][0U]),
                static_cast<float>(values[2U][1U])}};
        current = (*cubics)[index].point3;
    }
    return com::ok;
}

bool valid_ellipse(const ellipse_f& ellipse) noexcept
{
    if (!finite_point(ellipse.point) || !std::isfinite(ellipse.radius_x) ||
        ellipse.radius_x < 0.0F || !std::isfinite(ellipse.radius_y) ||
        ellipse.radius_y < 0.0F) {
        return false;
    }
    const std::array<double, 4U> edges{{
        static_cast<double>(ellipse.point.x) - ellipse.radius_x,
        static_cast<double>(ellipse.point.y) - ellipse.radius_y,
        static_cast<double>(ellipse.point.x) + ellipse.radius_x,
        static_cast<double>(ellipse.point.y) + ellipse.radius_y}};
    constexpr double maximum =
        static_cast<double>((std::numeric_limits<float>::max)());
    return std::all_of(edges.begin(), edges.end(), [&](double value) {
        return std::isfinite(value) && value >= -maximum &&
            value <= maximum;
    });
}

com::result ellipse_to_cubics(
    const ellipse_f& ellipse,
    progpu_native_direct2d_point_2f* start,
    std::array<cubic_bezier_segment_f, 4U>* cubics) noexcept
{
    if (start == nullptr || cubics == nullptr) {
        return com::pointer_error;
    }
    *start = {};
    *cubics = {};
    if (!valid_ellipse(ellipse)) {
        return com::invalid_argument;
    }
    constexpr double control_scale = 0.5522847498307936;
    const double center_x = ellipse.point.x;
    const double center_y = ellipse.point.y;
    const double radius_x = ellipse.radius_x;
    const double radius_y = ellipse.radius_y;
    const double control_x = radius_x * control_scale;
    const double control_y = radius_y * control_scale;
    const std::array<std::array<double, 2U>, 13U> points{{
        {center_x + radius_x, center_y},
        {center_x + radius_x, center_y + control_y},
        {center_x + control_x, center_y + radius_y},
        {center_x, center_y + radius_y},
        {center_x - control_x, center_y + radius_y},
        {center_x - radius_x, center_y + control_y},
        {center_x - radius_x, center_y},
        {center_x - radius_x, center_y - control_y},
        {center_x - control_x, center_y - radius_y},
        {center_x, center_y - radius_y},
        {center_x + control_x, center_y - radius_y},
        {center_x + radius_x, center_y - control_y},
        {center_x + radius_x, center_y}}};
    constexpr double maximum =
        static_cast<double>((std::numeric_limits<float>::max)());
    if (!std::all_of(points.begin(), points.end(), [&](const auto& point) {
            return std::isfinite(point[0U]) &&
                std::isfinite(point[1U]) &&
                std::abs(point[0U]) <= maximum &&
                std::abs(point[1U]) <= maximum;
        })) {
        return com::invalid_argument;
    }
    const auto convert = [](const std::array<double, 2U>& point) {
        return progpu_native_direct2d_point_2f{
            static_cast<float>(point[0U]),
            static_cast<float>(point[1U])};
    };
    *start = convert(points[0U]);
    for (std::size_t index = 0U; index < cubics->size(); ++index) {
        (*cubics)[index] = {
            convert(points[index * 3U + 1U]),
            convert(points[index * 3U + 2U]),
            convert(points[index * 3U + 3U])};
    }
    return com::ok;
}

com::result ellipse_fill_contains_point(
    const ellipse_f& ellipse,
    progpu_native_direct2d_point_2f point,
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::uint32_t* contains) noexcept
{
    if (contains == nullptr) {
        return com::pointer_error;
    }
    *contains = 0U;
    if (!valid_ellipse(ellipse) || !finite_point(point) ||
        !valid_tolerance(flattening_tolerance) ||
        !valid_transform(world_transform)) {
        return com::invalid_argument;
    }
    const progpu_native_direct2d_matrix_3x2_f identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const auto& matrix = world_transform == nullptr
        ? identity
        : *world_transform;
    const double determinant =
        static_cast<double>(matrix.m11) * matrix.m22 -
        static_cast<double>(matrix.m12) * matrix.m21;
    if (determinant == 0.0 || ellipse.radius_x == 0.0F ||
        ellipse.radius_y == 0.0F) {
        return com::ok;
    }
    const double translated_x =
        static_cast<double>(point.x) - matrix.m31;
    const double translated_y =
        static_cast<double>(point.y) - matrix.m32;
    const double local_x =
        (translated_x * matrix.m22 - translated_y * matrix.m21) /
        determinant;
    const double local_y =
        (-translated_x * matrix.m12 + translated_y * matrix.m11) /
        determinant;
    const double normalized_x =
        (local_x - ellipse.point.x) / ellipse.radius_x;
    const double normalized_y =
        (local_y - ellipse.point.y) / ellipse.radius_y;
    const double distance_squared =
        normalized_x * normalized_x + normalized_y * normalized_y;
    if (!std::isfinite(distance_squared)) {
        return com::invalid_argument;
    }
    *contains = distance_squared <= 1.0 ? 1U : 0U;
    return com::ok;
}

bool valid_rounded_rectangle(
    const rounded_rectangle_f& rectangle) noexcept
{
    return rectangle_geometry::valid_rectangle(rectangle.rectangle) &&
        std::isfinite(rectangle.radius_x) && rectangle.radius_x >= 0.0F &&
        std::isfinite(rectangle.radius_y) && rectangle.radius_y >= 0.0F;
}

com::result rounded_rectangle_to_path(
    const rounded_rectangle_f& rectangle,
    progpu_native_direct2d_point_2f* start,
    std::array<progpu_native_direct2d_point_2f, 4U>* line_ends,
    std::array<cubic_bezier_segment_f, 4U>* corners) noexcept
{
    if (start == nullptr || line_ends == nullptr || corners == nullptr) {
        return com::pointer_error;
    }
    *start = {};
    *line_ends = {};
    *corners = {};
    if (!valid_rounded_rectangle(rectangle)) {
        return com::invalid_argument;
    }
    const double width =
        static_cast<double>(rectangle.rectangle.right) -
        rectangle.rectangle.left;
    const double height =
        static_cast<double>(rectangle.rectangle.bottom) -
        rectangle.rectangle.top;
    const double radius_x = std::min(
        static_cast<double>(rectangle.radius_x), width * 0.5);
    const double radius_y = std::min(
        static_cast<double>(rectangle.radius_y), height * 0.5);
    constexpr double control_scale = 0.5522847498307936;
    const double control_x = radius_x * control_scale;
    const double control_y = radius_y * control_scale;
    const double left = rectangle.rectangle.left;
    const double top = rectangle.rectangle.top;
    const double right = rectangle.rectangle.right;
    const double bottom = rectangle.rectangle.bottom;
    const std::array<std::array<double, 2U>, 17U> points{{
        {left + radius_x, top},
        {right - radius_x, top},
        {right - radius_x + control_x, top},
        {right, top + radius_y - control_y},
        {right, top + radius_y},
        {right, bottom - radius_y},
        {right, bottom - radius_y + control_y},
        {right - radius_x + control_x, bottom},
        {right - radius_x, bottom},
        {left + radius_x, bottom},
        {left + radius_x - control_x, bottom},
        {left, bottom - radius_y + control_y},
        {left, bottom - radius_y},
        {left, top + radius_y},
        {left, top + radius_y - control_y},
        {left + radius_x - control_x, top},
        {left + radius_x, top}}};
    constexpr double maximum =
        static_cast<double>((std::numeric_limits<float>::max)());
    if (!std::all_of(points.begin(), points.end(), [&](const auto& point) {
            return std::isfinite(point[0U]) &&
                std::isfinite(point[1U]) &&
                std::abs(point[0U]) <= maximum &&
                std::abs(point[1U]) <= maximum;
        })) {
        return com::invalid_argument;
    }
    const auto convert = [](const std::array<double, 2U>& point) {
        return progpu_native_direct2d_point_2f{
            static_cast<float>(point[0U]),
            static_cast<float>(point[1U])};
    };
    *start = convert(points[0U]);
    for (std::size_t index = 0U; index < line_ends->size(); ++index) {
        const std::size_t base = index * 4U;
        (*line_ends)[index] = convert(points[base + 1U]);
        (*corners)[index] = {
            convert(points[base + 2U]),
            convert(points[base + 3U]),
            convert(points[base + 4U])};
    }
    return com::ok;
}

com::result rounded_rectangle_fill_contains_point(
    const rounded_rectangle_f& rectangle,
    progpu_native_direct2d_point_2f point,
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::uint32_t* contains) noexcept
{
    if (contains == nullptr) {
        return com::pointer_error;
    }
    *contains = 0U;
    if (!valid_rounded_rectangle(rectangle) || !finite_point(point) ||
        !valid_tolerance(flattening_tolerance) ||
        !valid_transform(world_transform)) {
        return com::invalid_argument;
    }
    const progpu_native_direct2d_matrix_3x2_f identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const auto& matrix = world_transform == nullptr
        ? identity
        : *world_transform;
    const double determinant =
        static_cast<double>(matrix.m11) * matrix.m22 -
        static_cast<double>(matrix.m12) * matrix.m21;
    if (determinant == 0.0) {
        return com::ok;
    }
    const double translated_x =
        static_cast<double>(point.x) - matrix.m31;
    const double translated_y =
        static_cast<double>(point.y) - matrix.m32;
    const double local_x =
        (translated_x * matrix.m22 - translated_y * matrix.m21) /
        determinant;
    const double local_y =
        (-translated_x * matrix.m12 + translated_y * matrix.m11) /
        determinant;
    const auto& edges = rectangle.rectangle;
    if (local_x < edges.left || local_x > edges.right ||
        local_y < edges.top || local_y > edges.bottom) {
        return com::ok;
    }
    const double width = static_cast<double>(edges.right) - edges.left;
    const double height = static_cast<double>(edges.bottom) - edges.top;
    const double radius_x = std::min(
        static_cast<double>(rectangle.radius_x), width * 0.5);
    const double radius_y = std::min(
        static_cast<double>(rectangle.radius_y), height * 0.5);
    if (radius_x == 0.0 || radius_y == 0.0 ||
        (local_x >= edges.left + radius_x &&
            local_x <= edges.right - radius_x) ||
        (local_y >= edges.top + radius_y &&
            local_y <= edges.bottom - radius_y)) {
        *contains = 1U;
        return com::ok;
    }
    const double center_x = local_x < edges.left + radius_x
        ? edges.left + radius_x
        : edges.right - radius_x;
    const double center_y = local_y < edges.top + radius_y
        ? edges.top + radius_y
        : edges.bottom - radius_y;
    const double normalized_x = (local_x - center_x) / radius_x;
    const double normalized_y = (local_y - center_y) / radius_y;
    const double distance_squared =
        normalized_x * normalized_x + normalized_y * normalized_y;
    if (!std::isfinite(distance_squared)) {
        return com::invalid_argument;
    }
    *contains = distance_squared <= 1.0 ? 1U : 0U;
    return com::ok;
}

rectangle_geometry::rectangle_geometry(
    rectangle_edges_f rectangle) noexcept
    : rectangle_(rectangle)
{
}

bool rectangle_geometry::valid_rectangle(
    const rectangle_edges_f& rectangle) noexcept
{
    return std::isfinite(rectangle.left) &&
        std::isfinite(rectangle.top) && std::isfinite(rectangle.right) &&
        std::isfinite(rectangle.bottom) &&
        rectangle.right >= rectangle.left &&
        rectangle.bottom >= rectangle.top;
}

com::result rectangle_geometry::vertices(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    std::array<progpu_native_direct2d_point_2f, 4U>& value) const noexcept
{
    value = {};
    if (!valid_rectangle(rectangle_) ||
        !valid_transform(world_transform)) {
        return com::invalid_argument;
    }
    const progpu_native_direct2d_matrix_3x2_f identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const auto& matrix = world_transform == nullptr
        ? identity
        : *world_transform;
    const std::array<progpu_native_direct2d_point_2f, 4U> source{{
        {rectangle_.left, rectangle_.top},
        {rectangle_.right, rectangle_.top},
        {rectangle_.right, rectangle_.bottom},
        {rectangle_.left, rectangle_.bottom}}};
    constexpr double maximum =
        static_cast<double>(std::numeric_limits<float>::max());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        const double x = static_cast<double>(source[index].x) * matrix.m11 +
            static_cast<double>(source[index].y) * matrix.m21 + matrix.m31;
        const double y = static_cast<double>(source[index].x) * matrix.m12 +
            static_cast<double>(source[index].y) * matrix.m22 + matrix.m32;
        if (!std::isfinite(x) || !std::isfinite(y) ||
            std::abs(x) > maximum || std::abs(y) > maximum) {
            value = {};
            return com::invalid_argument;
        }
        value[index] = {
            static_cast<float>(x), static_cast<float>(y)};
    }
    return com::ok;
}

com::result rectangle_geometry::bounds(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    rectangle_edges_f* value) const noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = {};
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result result = vertices(world_transform, points);
    if (com::failed(result)) {
        return result;
    }
    float left = points[0U].x;
    float top = points[0U].y;
    float right = left;
    float bottom = top;
    for (std::size_t index = 1U; index < points.size(); ++index) {
        left = std::min(left, points[index].x);
        top = std::min(top, points[index].y);
        right = std::max(right, points[index].x);
        bottom = std::max(bottom, points[index].y);
    }
    *value = {left, top, right, bottom};
    return com::ok;
}

com::result rectangle_geometry::fill_contains_point(
    progpu_native_direct2d_point_2f point,
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::uint32_t* contains) const noexcept
{
    if (contains == nullptr) {
        return com::pointer_error;
    }
    *contains = 0U;
    if (!finite_point(point) || !valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result result = vertices(world_transform, points);
    if (com::failed(result)) {
        return result;
    }
    if (twice_area(points) == 0.0) {
        return com::ok;
    }
    bool positive = false;
    bool negative = false;
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        const double cross =
            (static_cast<double>(end.x) - start.x) *
                (static_cast<double>(point.y) - start.y) -
            (static_cast<double>(end.y) - start.y) *
                (static_cast<double>(point.x) - start.x);
        positive = positive || cross > 0.0;
        negative = negative || cross < 0.0;
    }
    *contains = positive && negative ? 0U : 1U;
    return com::ok;
}

com::result rectangle_geometry::tessellate(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::array<progpu_native_direct2d_triangle, 2U>* triangles) const
    noexcept
{
    if (triangles == nullptr) {
        return com::pointer_error;
    }
    *triangles = {};
    if (!valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result result = vertices(world_transform, points);
    if (com::failed(result)) {
        return result;
    }
    *triangles = {{
        {points[0U], points[1U], points[2U]},
        {points[0U], points[2U], points[3U]}}};
    return com::ok;
}

com::result rectangle_geometry::area(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    float* value) const noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = 0.0F;
    if (!valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result status = vertices(world_transform, points);
    if (com::failed(status)) {
        return status;
    }
    const double result = std::abs(twice_area(points)) * 0.5;
    if (!std::isfinite(result) ||
        result > std::numeric_limits<float>::max()) {
        return com::invalid_argument;
    }
    *value = static_cast<float>(result);
    return com::ok;
}

com::result rectangle_geometry::length(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    float* value) const noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = 0.0F;
    if (!valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result status = vertices(world_transform, points);
    if (com::failed(status)) {
        return status;
    }
    double result = 0.0;
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        result += std::hypot(
            static_cast<double>(end.x) - start.x,
            static_cast<double>(end.y) - start.y);
    }
    if (!std::isfinite(result) ||
        result > std::numeric_limits<float>::max()) {
        return com::invalid_argument;
    }
    *value = static_cast<float>(result);
    return com::ok;
}

com::result rectangle_geometry::point_at_length(
    float length_value,
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    progpu_native_direct2d_point_2f* point,
    progpu_native_direct2d_point_2f* unit_tangent) const noexcept
{
    if (point == nullptr && unit_tangent == nullptr) {
        return com::pointer_error;
    }
    if (point != nullptr) {
        *point = {};
    }
    if (unit_tangent != nullptr) {
        *unit_tangent = {};
    }
    if (!std::isfinite(length_value) || length_value < 0.0F ||
        !valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result status = vertices(world_transform, points);
    if (com::failed(status)) {
        return status;
    }
    std::array<double, 4U> edge_lengths{};
    double perimeter = 0.0;
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        edge_lengths[index] = std::hypot(
            static_cast<double>(end.x) - start.x,
            static_cast<double>(end.y) - start.y);
        perimeter += edge_lengths[index];
    }
    if (perimeter == 0.0) {
        if (point != nullptr) {
            *point = points[0U];
        }
        return com::ok;
    }
    double remaining = std::min(
        static_cast<double>(length_value), perimeter);
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        const double edge_length = edge_lengths[index];
        if (edge_length == 0.0) {
            continue;
        }
        if (remaining <= edge_length || index + 1U == points.size()) {
            const double ratio = std::min(remaining / edge_length, 1.0);
            if (point != nullptr) {
                point->x = static_cast<float>(
                    start.x + (end.x - start.x) * ratio);
                point->y = static_cast<float>(
                    start.y + (end.y - start.y) * ratio);
            }
            if (unit_tangent != nullptr) {
                unit_tangent->x = static_cast<float>(
                    (end.x - start.x) / edge_length);
                unit_tangent->y = static_cast<float>(
                    (end.y - start.y) / edge_length);
            }
            return com::ok;
        }
        remaining -= edge_length;
    }
    return com::invalid_argument;
}

const rectangle_edges_f& rectangle_geometry::rectangle() const
    noexcept
{
    return rectangle_;
}

} // namespace progpu::native::direct2d::core
