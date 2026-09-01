#include "progpu_native_direct2d_compat.hpp"
#include "progpu_native_direct2d_drawing_state.hpp"
#include "progpu_native_direct2d_ellipse.hpp"
#include "progpu_native_direct2d_geometry_group.hpp"
#include "progpu_native_direct2d_path.hpp"
#include "progpu_native_direct2d_render_target.hpp"
#include "progpu_native_direct2d_rounded_rectangle.hpp"
#include "progpu_native_direct2d_stroke_style.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <mutex>
#include <new>

namespace progpu::native::direct2d::compat {
namespace {

class portable_factory;

[[nodiscard]] bool axis_preserving_transform(
    const matrix_3x2_f* transform) noexcept
{
    return transform == nullptr ||
        (transform->m12 == 0.0F && transform->m21 == 0.0F) ||
        (transform->m11 == 0.0F && transform->m22 == 0.0F);
}

[[nodiscard]] com::result get_axis_aligned_rectangle(
    factory* owner,
    geometry* candidate,
    const matrix_3x2_f* transform,
    std::uint32_t depth,
    rectangle_f* rectangle) noexcept
{
    if (candidate == nullptr || rectangle == nullptr) {
        return com::pointer_error;
    }
    *rectangle = {};
    if (depth > 16U || !core::valid_transform(transform)) {
        return depth > 16U ? not_implemented : com::invalid_argument;
    }
    factory* raw_factory = nullptr;
    candidate->GetFactory(&raw_factory);
    com::pointer<factory> candidate_factory;
    candidate_factory.attach(raw_factory);
    if (candidate_factory.get() != owner) {
        return wrong_factory;
    }

    rectangle_geometry* raw_rectangle = nullptr;
    const com::result rectangle_query = candidate->QueryInterface(
        rectangle_geometry_interface_id,
        reinterpret_cast<void**>(&raw_rectangle));
    com::pointer<rectangle_geometry> rectangle_geometry_value;
    rectangle_geometry_value.attach(raw_rectangle);
    if (com::succeeded(rectangle_query) && rectangle_geometry_value) {
        if (!axis_preserving_transform(transform)) {
            return not_implemented;
        }
        rectangle_f source{};
        rectangle_geometry_value->GetRect(&source);
        return core::rectangle_geometry(source).bounds(transform, rectangle);
    }
    if (com::failed(rectangle_query) && rectangle_query != com::no_interface) {
        return rectangle_query;
    }

    transformed_geometry* raw_transformed = nullptr;
    const com::result transformed_query = candidate->QueryInterface(
        transformed_geometry_interface_id,
        reinterpret_cast<void**>(&raw_transformed));
    com::pointer<transformed_geometry> transformed;
    transformed.attach(raw_transformed);
    if (com::failed(transformed_query) || !transformed) {
        return com::failed(transformed_query) &&
                transformed_query != com::no_interface
            ? transformed_query
            : not_implemented;
    }
    matrix_3x2_f local{};
    transformed->GetTransform(&local);
    matrix_3x2_f composed{};
    const com::result compose_result = core::compose_transform(
        local, transform, &composed);
    if (com::failed(compose_result)) {
        return compose_result;
    }
    geometry* raw_source = nullptr;
    transformed->GetSourceGeometry(&raw_source);
    com::pointer<geometry> source;
    source.attach(raw_source);
    return source
        ? get_axis_aligned_rectangle(
              owner, source.get(), &composed, depth + 1U, rectangle)
        : failure;
}

[[nodiscard]] com::result compare_rectangle_with_geometry(
    factory* owner,
    const rectangle_f& rectangle,
    geometry* candidate,
    const matrix_3x2_f* candidate_transform,
    float flattening_tolerance,
    geometry_relation* relation) noexcept
{
    if (relation == nullptr) {
        return com::pointer_error;
    }
    *relation = geometry_relation::unknown;
    if (candidate == nullptr || !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        !core::valid_transform(candidate_transform)) {
        return com::invalid_argument;
    }
    if (rectangle.right <= rectangle.left ||
        rectangle.bottom <= rectangle.top) {
        return not_implemented;
    }
    rectangle_f other{};
    const com::result rectangle_result = get_axis_aligned_rectangle(
        owner, candidate, candidate_transform, 0U, &other);
    if (com::failed(rectangle_result)) {
        return rectangle_result;
    }
    if (other.right <= other.left || other.bottom <= other.top) {
        return not_implemented;
    }
    if (other.left <= rectangle.left && other.top <= rectangle.top &&
        other.right >= rectangle.right &&
        other.bottom >= rectangle.bottom) {
        *relation = geometry_relation::is_contained;
    } else if (rectangle.left <= other.left &&
        rectangle.top <= other.top && rectangle.right >= other.right &&
        rectangle.bottom >= other.bottom) {
        *relation = geometry_relation::contains;
    } else if (rectangle.right < other.left ||
        other.right < rectangle.left || rectangle.bottom < other.top ||
        other.bottom < rectangle.top) {
        *relation = geometry_relation::disjoint;
    } else {
        *relation = geometry_relation::overlap;
    }
    return com::ok;
}

struct orthogonal_edge final {
    std::uint8_t start_x = 0U;
    std::uint8_t start_y = 0U;
    std::uint8_t end_x = 0U;
    std::uint8_t end_y = 0U;
    bool used = false;
};

[[nodiscard]] com::result combine_rectangle_with_geometry(
    factory* owner,
    const rectangle_f& rectangle,
    geometry* candidate,
    combine_mode mode,
    const matrix_3x2_f* candidate_transform,
    float flattening_tolerance,
    simplified_geometry_sink* sink) noexcept
{
    if (sink == nullptr) {
        return com::pointer_error;
    }
    if (candidate == nullptr ||
        (mode != combine_mode::union_value &&
            mode != combine_mode::intersect && mode != combine_mode::xor_value &&
            mode != combine_mode::exclude) ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        !core::valid_transform(candidate_transform)) {
        return com::invalid_argument;
    }
    if (rectangle.right <= rectangle.left ||
        rectangle.bottom <= rectangle.top) {
        return not_implemented;
    }
    rectangle_f other{};
    const com::result rectangle_result = get_axis_aligned_rectangle(
        owner, candidate, candidate_transform, 0U, &other);
    if (com::failed(rectangle_result)) {
        return rectangle_result;
    }
    if (other.right <= other.left || other.bottom <= other.top) {
        return not_implemented;
    }

    std::array<float, 4U> x_values{
        rectangle.left, rectangle.right, other.left, other.right};
    std::array<float, 4U> y_values{
        rectangle.top, rectangle.bottom, other.top, other.bottom};
    std::sort(x_values.begin(), x_values.end());
    std::sort(y_values.begin(), y_values.end());
    const std::size_t x_count = static_cast<std::size_t>(
        std::unique(x_values.begin(), x_values.end()) - x_values.begin());
    const std::size_t y_count = static_cast<std::size_t>(
        std::unique(y_values.begin(), y_values.end()) - y_values.begin());
    std::array<std::array<bool, 3U>, 3U> selected{};
    for (std::size_t y = 0U; y + 1U < y_count; ++y) {
        for (std::size_t x = 0U; x + 1U < x_count; ++x) {
            const double center_x =
                (static_cast<double>(x_values[x]) + x_values[x + 1U]) * 0.5;
            const double center_y =
                (static_cast<double>(y_values[y]) + y_values[y + 1U]) * 0.5;
            const bool in_first =
                center_x > rectangle.left && center_x < rectangle.right &&
                center_y > rectangle.top && center_y < rectangle.bottom;
            const bool in_second =
                center_x > other.left && center_x < other.right &&
                center_y > other.top && center_y < other.bottom;
            switch (mode) {
            case combine_mode::union_value:
                selected[y][x] = in_first || in_second;
                break;
            case combine_mode::intersect:
                selected[y][x] = in_first && in_second;
                break;
            case combine_mode::xor_value:
                selected[y][x] = in_first != in_second;
                break;
            case combine_mode::exclude:
                selected[y][x] = in_first && !in_second;
                break;
            }
        }
    }

    std::array<std::array<std::int8_t, 3U>, 3U> labels{};
    for (auto& row : labels) {
        row.fill(-1);
    }
    std::array<std::uint8_t, 9U> queue_x{};
    std::array<std::uint8_t, 9U> queue_y{};
    std::int8_t component_count = 0;
    constexpr std::array<std::int8_t, 4U> offsets_x{-1, 1, 0, 0};
    constexpr std::array<std::int8_t, 4U> offsets_y{0, 0, -1, 1};
    for (std::size_t start_y = 0U; start_y + 1U < y_count; ++start_y) {
        for (std::size_t start_x = 0U; start_x + 1U < x_count; ++start_x) {
            if (!selected[start_y][start_x] ||
                labels[start_y][start_x] >= 0) {
                continue;
            }
            std::size_t queue_begin = 0U;
            std::size_t queue_end = 1U;
            queue_x[0U] = static_cast<std::uint8_t>(start_x);
            queue_y[0U] = static_cast<std::uint8_t>(start_y);
            labels[start_y][start_x] = component_count;
            while (queue_begin < queue_end) {
                const std::int8_t x = static_cast<std::int8_t>(
                    queue_x[queue_begin]);
                const std::int8_t y = static_cast<std::int8_t>(
                    queue_y[queue_begin]);
                ++queue_begin;
                for (std::size_t direction = 0U;
                     direction < offsets_x.size();
                     ++direction) {
                    const std::int8_t next_x = x + offsets_x[direction];
                    const std::int8_t next_y = y + offsets_y[direction];
                    if (next_x < 0 || next_y < 0 ||
                        next_x >= static_cast<std::int8_t>(x_count - 1U) ||
                        next_y >= static_cast<std::int8_t>(y_count - 1U) ||
                        !selected[static_cast<std::size_t>(next_y)]
                            [static_cast<std::size_t>(next_x)] ||
                        labels[static_cast<std::size_t>(next_y)]
                            [static_cast<std::size_t>(next_x)] >= 0) {
                        continue;
                    }
                    labels[static_cast<std::size_t>(next_y)]
                        [static_cast<std::size_t>(next_x)] = component_count;
                    queue_x[queue_end] = static_cast<std::uint8_t>(next_x);
                    queue_y[queue_end] = static_cast<std::uint8_t>(next_y);
                    ++queue_end;
                }
            }
            ++component_count;
        }
    }

    sink->SetFillMode(fill_mode::alternate);
    sink->SetSegmentFlags(path_segment::force_unstroked);
    for (std::int8_t component = 0; component < component_count; ++component) {
        std::array<orthogonal_edge, 36U> edges{};
        std::size_t edge_count = 0U;
        const auto append_edge = [&](std::size_t start_x,
                                     std::size_t start_y,
                                     std::size_t end_x,
                                     std::size_t end_y) {
            edges[edge_count++] = {
                static_cast<std::uint8_t>(start_x),
                static_cast<std::uint8_t>(start_y),
                static_cast<std::uint8_t>(end_x),
                static_cast<std::uint8_t>(end_y),
                false};
        };
        for (std::size_t y = 0U; y + 1U < y_count; ++y) {
            for (std::size_t x = 0U; x + 1U < x_count; ++x) {
                if (labels[y][x] != component) {
                    continue;
                }
                if (y == 0U || labels[y - 1U][x] != component) {
                    append_edge(x, y, x + 1U, y);
                }
                if (x + 2U > x_count - 1U ||
                    labels[y][x + 1U] != component) {
                    append_edge(x + 1U, y, x + 1U, y + 1U);
                }
                if (y + 2U > y_count - 1U ||
                    labels[y + 1U][x] != component) {
                    append_edge(x + 1U, y + 1U, x, y + 1U);
                }
                if (x == 0U || labels[y][x - 1U] != component) {
                    append_edge(x, y + 1U, x, y);
                }
            }
        }
        for (std::size_t first_edge = 0U;
             first_edge < edge_count;
             ++first_edge) {
            if (edges[first_edge].used) {
                continue;
            }
            orthogonal_edge* current = &edges[first_edge];
            current->used = true;
            const std::uint8_t first_x = current->start_x;
            const std::uint8_t first_y = current->start_y;
            std::array<std::array<std::uint8_t, 2U>, 36U> vertices{};
            std::size_t vertex_count = 1U;
            vertices[0U] = {first_x, first_y};
            while (current->end_x != first_x || current->end_y != first_y) {
                vertices[vertex_count++] = {
                    current->end_x, current->end_y};
                orthogonal_edge* next = nullptr;
                for (std::size_t candidate_edge = 0U;
                     candidate_edge < edge_count;
                     ++candidate_edge) {
                    if (!edges[candidate_edge].used &&
                        edges[candidate_edge].start_x == current->end_x &&
                        edges[candidate_edge].start_y == current->end_y) {
                        next = &edges[candidate_edge];
                        break;
                    }
                }
                if (next == nullptr) {
                    return failure;
                }
                next->used = true;
                current = next;
            }
            std::array<point_2f, 36U> compact{};
            std::size_t compact_count = 0U;
            for (std::size_t index = 0U; index < vertex_count; ++index) {
                const auto& previous = vertices[
                    (index + vertex_count - 1U) % vertex_count];
                const auto& value = vertices[index];
                const auto& next = vertices[(index + 1U) % vertex_count];
                const bool vertical =
                    previous[0U] == value[0U] &&
                    value[0U] == next[0U];
                const bool horizontal =
                    previous[1U] == value[1U] &&
                    value[1U] == next[1U];
                if (!vertical && !horizontal) {
                    compact[compact_count++] = {
                        x_values[value[0U]], y_values[value[1U]]};
                }
            }
            if (compact_count < 4U) {
                return failure;
            }
            sink->BeginFigure(compact[0U], figure_begin::filled);
            sink->AddLines(
                compact.data() + 1U,
                static_cast<std::uint32_t>(compact_count - 1U));
            sink->AddLines(compact.data(), 1U);
            sink->EndFigure(figure_end::closed);
        }
    }
    return com::ok;
}

[[nodiscard]] com::result get_rectangle_widened_bounds(
    factory* owner,
    const rectangle_f& rectangle,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    rectangle_f* bounds) noexcept
{
    if (bounds == nullptr) {
        return com::pointer_error;
    }
    *bounds = {};
    if (!std::isfinite(stroke_width) || stroke_width < 0.0F ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        !core::valid_transform(world_transform)) {
        return com::invalid_argument;
    }
    if (style != nullptr) {
        factory* raw_factory = nullptr;
        style->GetFactory(&raw_factory);
        com::pointer<factory> style_factory;
        style_factory.attach(raw_factory);
        if (style_factory.get() != owner) {
            return wrong_factory;
        }
        if (style->GetDashStyle() != dash_style::solid) {
            return not_implemented;
        }
    }
    if (rectangle.right <= rectangle.left ||
        rectangle.bottom <= rectangle.top) {
        return not_implemented;
    }
    const double half_width = static_cast<double>(stroke_width) * 0.5;
    const std::array<double, 4U> expanded{
        static_cast<double>(rectangle.left) - half_width,
        static_cast<double>(rectangle.top) - half_width,
        static_cast<double>(rectangle.right) + half_width,
        static_cast<double>(rectangle.bottom) + half_width};
    constexpr double maximum =
        static_cast<double>(std::numeric_limits<float>::max());
    for (const double value : expanded) {
        if (!std::isfinite(value) || std::abs(value) > maximum) {
            return com::invalid_argument;
        }
    }
    return core::rectangle_geometry({
        static_cast<float>(expanded[0U]),
        static_cast<float>(expanded[1U]),
        static_cast<float>(expanded[2U]),
        static_cast<float>(expanded[3U])})
        .bounds(world_transform, bounds);
}

[[nodiscard]] bool convex_rectangle_contains(
    const std::array<point_2f, 4U>& points,
    point_2f point,
    bool include_boundary) noexcept
{
    bool positive = false;
    bool negative = false;
    bool boundary = false;
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
        boundary = boundary || cross == 0.0;
    }
    return !(positive && negative) && (include_boundary || !boundary);
}

[[nodiscard]] com::result rectangle_stroke_contains_point(
    const rectangle_f& rectangle,
    point_2f point,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::int32_t* contains) noexcept
{
    if (contains == nullptr) {
        return com::pointer_error;
    }
    *contains = 0;
    if (!std::isfinite(point.x) || !std::isfinite(point.y) ||
        !std::isfinite(stroke_width) || stroke_width < 0.0F ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        !core::valid_transform(world_transform)) {
        return com::invalid_argument;
    }
    if (style != nullptr || rectangle.right <= rectangle.left ||
        rectangle.bottom <= rectangle.top) {
        return not_implemented;
    }
    if (world_transform != nullptr) {
        const double determinant =
            static_cast<double>(world_transform->m11) *
                world_transform->m22 -
            static_cast<double>(world_transform->m12) *
                world_transform->m21;
        if (determinant == 0.0) {
            return not_implemented;
        }
    }
    const double half_width = static_cast<double>(stroke_width) * 0.5;
    const std::array<double, 4U> outer_values{
        static_cast<double>(rectangle.left) - half_width,
        static_cast<double>(rectangle.top) - half_width,
        static_cast<double>(rectangle.right) + half_width,
        static_cast<double>(rectangle.bottom) + half_width};
    constexpr double maximum =
        static_cast<double>(std::numeric_limits<float>::max());
    for (const double value : outer_values) {
        if (!std::isfinite(value) || std::abs(value) > maximum) {
            return com::invalid_argument;
        }
    }
    std::array<point_2f, 4U> outer{};
    const com::result outer_result = core::rectangle_geometry({
        static_cast<float>(outer_values[0U]),
        static_cast<float>(outer_values[1U]),
        static_cast<float>(outer_values[2U]),
        static_cast<float>(outer_values[3U])})
        .vertices(world_transform, outer);
    if (com::failed(outer_result)) {
        return outer_result;
    }
    if (!convex_rectangle_contains(outer, point, true)) {
        return com::ok;
    }
    const double inner_left =
        static_cast<double>(rectangle.left) + half_width;
    const double inner_top =
        static_cast<double>(rectangle.top) + half_width;
    const double inner_right =
        static_cast<double>(rectangle.right) - half_width;
    const double inner_bottom =
        static_cast<double>(rectangle.bottom) - half_width;
    if (inner_right <= inner_left || inner_bottom <= inner_top) {
        *contains = 1;
        return com::ok;
    }
    std::array<point_2f, 4U> inner{};
    const com::result inner_result = core::rectangle_geometry({
        static_cast<float>(inner_left),
        static_cast<float>(inner_top),
        static_cast<float>(inner_right),
        static_cast<float>(inner_bottom)})
        .vertices(world_transform, inner);
    if (com::failed(inner_result)) {
        return inner_result;
    }
    *contains = convex_rectangle_contains(inner, point, false) ? 0 : 1;
    return com::ok;
}

[[nodiscard]] com::result widen_rectangle(
    const rectangle_f& rectangle,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_geometry_sink* sink) noexcept
{
    if (sink == nullptr) {
        return com::pointer_error;
    }
    if (!std::isfinite(stroke_width) || stroke_width < 0.0F ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        !core::valid_transform(world_transform)) {
        return com::invalid_argument;
    }
    if (style != nullptr || rectangle.right <= rectangle.left ||
        rectangle.bottom <= rectangle.top) {
        return not_implemented;
    }
    if (stroke_width == 0.0F) {
        return not_implemented;
    }
    const double half_width = static_cast<double>(stroke_width) * 0.5;
    const std::array<double, 4U> outer_values{
        static_cast<double>(rectangle.left) - half_width,
        static_cast<double>(rectangle.top) - half_width,
        static_cast<double>(rectangle.right) + half_width,
        static_cast<double>(rectangle.bottom) + half_width};
    constexpr double maximum =
        static_cast<double>(std::numeric_limits<float>::max());
    for (const double value : outer_values) {
        if (!std::isfinite(value) || std::abs(value) > maximum) {
            return com::invalid_argument;
        }
    }
    std::array<point_2f, 4U> outer{};
    const com::result outer_result = core::rectangle_geometry({
        static_cast<float>(outer_values[0U]),
        static_cast<float>(outer_values[1U]),
        static_cast<float>(outer_values[2U]),
        static_cast<float>(outer_values[3U])})
        .vertices(world_transform, outer);
    if (com::failed(outer_result)) {
        return outer_result;
    }
    sink->SetFillMode(fill_mode::alternate);
    sink->SetSegmentFlags(path_segment::force_unstroked);
    sink->BeginFigure(outer[0U], figure_begin::filled);
    sink->AddLines(outer.data() + 1U, 3U);
    sink->EndFigure(figure_end::closed);

    const double inner_left =
        static_cast<double>(rectangle.left) + half_width;
    const double inner_top =
        static_cast<double>(rectangle.top) + half_width;
    const double inner_right =
        static_cast<double>(rectangle.right) - half_width;
    const double inner_bottom =
        static_cast<double>(rectangle.bottom) - half_width;
    if (inner_right <= inner_left || inner_bottom <= inner_top) {
        return com::ok;
    }
    std::array<point_2f, 4U> inner{};
    const com::result inner_result = core::rectangle_geometry({
        static_cast<float>(inner_left),
        static_cast<float>(inner_top),
        static_cast<float>(inner_right),
        static_cast<float>(inner_bottom)})
        .vertices(world_transform, inner);
    if (com::failed(inner_result)) {
        return inner_result;
    }
    sink->BeginFigure(inner[0U], figure_begin::filled);
    sink->AddLines(inner.data() + 1U, 3U);
    sink->EndFigure(figure_end::closed);
    return com::ok;
}

[[nodiscard]] com::result widen_transformed_rectangle(
    const rectangle_f& rectangle,
    float stroke_width,
    stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_geometry_sink* sink) noexcept
{
    if (sink == nullptr) {
        return com::pointer_error;
    }
    if (!std::isfinite(stroke_width) || stroke_width <= 0.0F ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        !core::valid_transform(world_transform)) {
        return stroke_width == 0.0F
            ? not_implemented
            : com::invalid_argument;
    }
    if (style != nullptr || rectangle.right <= rectangle.left ||
        rectangle.bottom <= rectangle.top) {
        return not_implemented;
    }
    const double half_width = static_cast<double>(stroke_width) * 0.5;
    const double outer_left =
        static_cast<double>(rectangle.left) - half_width;
    const double outer_top =
        static_cast<double>(rectangle.top) - half_width;
    const double outer_right =
        static_cast<double>(rectangle.right) + half_width;
    const double outer_bottom =
        static_cast<double>(rectangle.bottom) + half_width;
    const double inner_left =
        static_cast<double>(rectangle.left) + half_width;
    const double inner_top =
        static_cast<double>(rectangle.top) + half_width;
    const double inner_right =
        static_cast<double>(rectangle.right) - half_width;
    const double inner_bottom =
        static_cast<double>(rectangle.bottom) - half_width;
    constexpr double maximum =
        static_cast<double>(std::numeric_limits<float>::max());
    const std::array<double, 8U> bounds{
        outer_left,
        outer_top,
        outer_right,
        outer_bottom,
        inner_left,
        inner_top,
        inner_right,
        inner_bottom};
    for (const double value : bounds) {
        if (!std::isfinite(value) || std::abs(value) > maximum) {
            return com::invalid_argument;
        }
    }
    if (inner_right <= inner_left || inner_bottom <= inner_top) {
        return not_implemented;
    }

    const double left = static_cast<double>(rectangle.left);
    const double top = static_cast<double>(rectangle.top);
    const double right = static_cast<double>(rectangle.right);
    const double bottom = static_cast<double>(rectangle.bottom);
    std::array<point_2f, 27U> points{{
        {static_cast<float>(left), static_cast<float>(inner_top)},
        {static_cast<float>(left), static_cast<float>(outer_top)},
        {static_cast<float>(right), static_cast<float>(outer_top)},
        {static_cast<float>(outer_right), static_cast<float>(outer_top)},
        {static_cast<float>(outer_right), static_cast<float>(top)},
        {static_cast<float>(outer_right), static_cast<float>(bottom)},
        {static_cast<float>(outer_right), static_cast<float>(outer_bottom)},
        {static_cast<float>(right), static_cast<float>(outer_bottom)},
        {static_cast<float>(left), static_cast<float>(outer_bottom)},
        {static_cast<float>(outer_left), static_cast<float>(outer_bottom)},
        {static_cast<float>(outer_left), static_cast<float>(bottom)},
        {static_cast<float>(outer_left), static_cast<float>(top)},
        {static_cast<float>(outer_left), static_cast<float>(outer_top)},
        {static_cast<float>(left), static_cast<float>(outer_top)},
        {static_cast<float>(left), static_cast<float>(inner_top)},
        {static_cast<float>(left), static_cast<float>(top)},
        {static_cast<float>(inner_left), static_cast<float>(top)},
        {static_cast<float>(inner_left), static_cast<float>(bottom)},
        {static_cast<float>(left), static_cast<float>(bottom)},
        {static_cast<float>(left), static_cast<float>(inner_bottom)},
        {static_cast<float>(right), static_cast<float>(inner_bottom)},
        {static_cast<float>(right), static_cast<float>(bottom)},
        {static_cast<float>(inner_right), static_cast<float>(bottom)},
        {static_cast<float>(inner_right), static_cast<float>(top)},
        {static_cast<float>(right), static_cast<float>(top)},
        {static_cast<float>(right), static_cast<float>(inner_top)},
        {static_cast<float>(left), static_cast<float>(inner_top)},
    }};
    if (world_transform != nullptr) {
        for (point_2f& point : points) {
            const double x =
                static_cast<double>(point.x) * world_transform->m11 +
                static_cast<double>(point.y) * world_transform->m21 +
                world_transform->m31;
            const double y =
                static_cast<double>(point.x) * world_transform->m12 +
                static_cast<double>(point.y) * world_transform->m22 +
                world_transform->m32;
            if (!std::isfinite(x) || !std::isfinite(y) ||
                std::abs(x) > maximum || std::abs(y) > maximum) {
                return com::invalid_argument;
            }
            point = {static_cast<float>(x), static_cast<float>(y)};
        }
    }
    sink->SetFillMode(fill_mode::winding);
    sink->SetSegmentFlags(path_segment::force_unstroked);
    sink->BeginFigure(points[0U], figure_begin::filled);
    sink->AddLines(points.data() + 1U, 26U);
    sink->EndFigure(figure_end::open);
    return com::ok;
}

class portable_solid_color_brush final : public solid_color_brush {
public:
    portable_solid_color_brush(
        factory* owner,
        color_f color,
        brush_properties properties) noexcept
        : owner_(owner),
          color_(color),
          opacity_(properties.opacity),
          transform_(properties.transform)
    {
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, brush_interface_id) ||
            com::guid_equal(interface_id, solid_color_brush_interface_id)) {
            *value = static_cast<solid_color_brush*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    void PROGPU_NATIVE_COM_CALL SetOpacity(float opacity) noexcept override
    {
        if (!valid_opacity(opacity)) {
            return;
        }
        const std::lock_guard lock(mutex_);
        opacity_ = opacity;
    }

    void PROGPU_NATIVE_COM_CALL SetTransform(
        const matrix_3x2_f* transform) noexcept override
    {
        if (transform == nullptr || !core::valid_transform(transform)) {
            return;
        }
        const std::lock_guard lock(mutex_);
        transform_ = *transform;
    }

    float PROGPU_NATIVE_COM_CALL GetOpacity() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return opacity_;
    }

    void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept override
    {
        if (transform == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        *transform = transform_;
    }

    void PROGPU_NATIVE_COM_CALL SetColor(
        const color_f* color) noexcept override
    {
        if (color == nullptr || !valid_color(*color)) {
            return;
        }
        const std::lock_guard lock(mutex_);
        color_ = *color;
    }

    color_f PROGPU_NATIVE_COM_CALL GetColor() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return color_;
    }

    [[nodiscard]] static bool valid_color(const color_f& color) noexcept
    {
        return std::isfinite(color.red) && std::isfinite(color.green) &&
            std::isfinite(color.blue) && std::isfinite(color.alpha);
    }

    [[nodiscard]] static bool valid_opacity(float opacity) noexcept
    {
        return std::isfinite(opacity) && opacity >= 0.0F && opacity <= 1.0F;
    }

private:
    friend class com::atomic_reference_count<portable_solid_color_brush>;
    ~portable_solid_color_brush() = default;

    com::atomic_reference_count<portable_solid_color_brush> reference_count_;
    com::pointer<factory> owner_;
    mutable std::mutex mutex_;
    color_f color_{};
    float opacity_ = 1.0F;
    matrix_3x2_f transform_{};
};

class portable_rectangle_geometry final : public rectangle_geometry {
public:
    portable_rectangle_geometry(
        factory* owner,
        rectangle_f rectangle) noexcept
        : owner_(owner), geometry_(rectangle)
    {
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, geometry_interface_id) ||
            com::guid_equal(interface_id, rectangle_geometry_interface_id)) {
            *value = static_cast<rectangle_geometry*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    com::result PROGPU_NATIVE_COM_CALL GetBounds(
        const matrix_3x2_f* world_transform,
        rectangle_f* bounds) const noexcept override
    {
        return geometry_.bounds(world_transform, bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL GetWidenedBounds(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        rectangle_f* bounds) const noexcept override
    {
        return get_rectangle_widened_bounds(
            owner_.get(),
            geometry_.rectangle(),
            stroke_width,
            style,
            world_transform,
            flattening_tolerance,
            bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL StrokeContainsPoint(
        point_2f point,
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept override
    {
        return rectangle_stroke_contains_point(
            geometry_.rectangle(),
            point,
            stroke_width,
            style,
            world_transform,
            flattening_tolerance,
            contains);
    }

    com::result PROGPU_NATIVE_COM_CALL FillContainsPoint(
        point_2f point,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept override
    {
        if (contains == nullptr) {
            return com::pointer_error;
        }
        std::uint32_t result = 0U;
        const com::result status = geometry_.fill_contains_point(
            point, world_transform, flattening_tolerance, &result);
        *contains = result == 0U ? 0 : 1;
        return status;
    }

    com::result PROGPU_NATIVE_COM_CALL CompareWithGeometry(
        geometry* candidate,
        const matrix_3x2_f* candidate_transform,
        float flattening_tolerance,
        geometry_relation* relation) const noexcept override
    {
        return compare_rectangle_with_geometry(
            owner_.get(),
            geometry_.rectangle(),
            candidate,
            candidate_transform,
            flattening_tolerance,
            relation);
    }

    com::result PROGPU_NATIVE_COM_CALL Simplify(
        geometry_simplification_option option,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        if ((option != geometry_simplification_option::cubics_and_lines &&
                option != geometry_simplification_option::lines) ||
            !std::isfinite(flattening_tolerance) ||
            flattening_tolerance <= 0.0F) {
            return com::invalid_argument;
        }
        std::array<point_2f, 4U> points{};
        const com::result status = geometry_.vertices(
            world_transform, points);
        if (com::failed(status)) {
            return status;
        }
        sink->SetFillMode(fill_mode::winding);
        sink->SetSegmentFlags(path_segment::none);
        sink->BeginFigure(points[0U], figure_begin::filled);
        sink->AddLines(points.data() + 1U, 3U);
        sink->EndFigure(figure_end::closed);
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL Tessellate(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        tessellation_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        std::array<triangle, 2U> triangles{};
        const com::result status = geometry_.tessellate(
            world_transform, flattening_tolerance, &triangles);
        if (com::failed(status)) {
            return status;
        }
        sink->AddTriangles(
            triangles.data(), static_cast<std::uint32_t>(triangles.size()));
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CombineWithGeometry(
        geometry* candidate,
        combine_mode mode,
        const matrix_3x2_f* candidate_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        return combine_rectangle_with_geometry(
            owner_.get(),
            geometry_.rectangle(),
            candidate,
            mode,
            candidate_transform,
            flattening_tolerance,
            sink);
    }

    com::result PROGPU_NATIVE_COM_CALL Outline(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        if (!std::isfinite(flattening_tolerance) ||
            flattening_tolerance <= 0.0F) {
            return com::invalid_argument;
        }
        std::array<point_2f, 4U> points{};
        const com::result status = geometry_.vertices(
            world_transform, points);
        if (com::failed(status)) {
            return status;
        }
        sink->SetFillMode(fill_mode::alternate);
        sink->SetSegmentFlags(path_segment::none);
        sink->BeginFigure(points[0U], figure_begin::filled);
        sink->AddLines(points.data() + 1U, 3U);
        sink->AddLines(points.data(), 1U);
        sink->EndFigure(figure_end::closed);
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeArea(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* area) const noexcept override
    {
        return geometry_.area(world_transform, flattening_tolerance, area);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeLength(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* length) const noexcept override
    {
        return geometry_.length(
            world_transform, flattening_tolerance, length);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputePointAtLength(
        float length,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        point_2f* point,
        point_2f* unit_tangent) const noexcept override
    {
        return geometry_.point_at_length(
            length,
            world_transform,
            flattening_tolerance,
            point,
            unit_tangent);
    }

    com::result PROGPU_NATIVE_COM_CALL Widen(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        return widen_rectangle(
            geometry_.rectangle(),
            stroke_width,
            style,
            world_transform,
            flattening_tolerance,
            sink);
    }

    void PROGPU_NATIVE_COM_CALL GetRect(rectangle_f* rectangle) const
        noexcept override
    {
        if (rectangle != nullptr) {
            *rectangle = geometry_.rectangle();
        }
    }

private:
    friend class com::atomic_reference_count<portable_rectangle_geometry>;
    ~portable_rectangle_geometry() = default;

    com::atomic_reference_count<portable_rectangle_geometry> reference_count_;
    com::pointer<factory> owner_;
    core::rectangle_geometry geometry_;
};

class portable_transformed_geometry final : public transformed_geometry {
public:
    portable_transformed_geometry(
        factory* owner,
        geometry* source,
        matrix_3x2_f transform) noexcept
        : owner_(owner), source_(source), transform_(transform)
    {
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, geometry_interface_id) ||
            com::guid_equal(interface_id, transformed_geometry_interface_id)) {
            *value = static_cast<transformed_geometry*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    com::result PROGPU_NATIVE_COM_CALL GetBounds(
        const matrix_3x2_f* world_transform,
        rectangle_f* bounds) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->GetBounds(&composed, bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL GetWidenedBounds(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        rectangle_f* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return com::pointer_error;
        }
        *bounds = {};
        rectangle_f transformed_rectangle{};
        const com::result rectangle_result =
            get_axis_preserving_rectangle(&transformed_rectangle);
        return com::failed(rectangle_result)
            ? rectangle_result
            : get_rectangle_widened_bounds(
                  owner_.get(),
                  transformed_rectangle,
                  stroke_width,
                  style,
                  world_transform,
                  flattening_tolerance,
                  bounds);
    }

    com::result PROGPU_NATIVE_COM_CALL StrokeContainsPoint(
        point_2f point,
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept override
    {
        if (contains == nullptr) {
            return com::pointer_error;
        }
        *contains = 0;
        rectangle_f transformed_rectangle{};
        const com::result rectangle_result =
            get_axis_preserving_rectangle(&transformed_rectangle);
        return com::failed(rectangle_result)
            ? rectangle_result
            : rectangle_stroke_contains_point(
                  transformed_rectangle,
                  point,
                  stroke_width,
                  style,
                  world_transform,
                  flattening_tolerance,
                  contains);
    }

    com::result PROGPU_NATIVE_COM_CALL FillContainsPoint(
        point_2f point,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        std::int32_t* contains) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->FillContainsPoint(
                  point, &composed, flattening_tolerance, contains);
    }

    com::result PROGPU_NATIVE_COM_CALL CompareWithGeometry(
        geometry* candidate,
        const matrix_3x2_f* candidate_transform,
        float flattening_tolerance,
        geometry_relation* relation) const noexcept override
    {
        if (relation == nullptr) {
            return com::pointer_error;
        }
        *relation = geometry_relation::unknown;
        rectangle_f transformed_rectangle{};
        const com::result rectangle_result =
            get_axis_preserving_rectangle(&transformed_rectangle);
        return com::failed(rectangle_result)
            ? rectangle_result
            : compare_rectangle_with_geometry(
                  owner_.get(),
                  transformed_rectangle,
                  candidate,
                  candidate_transform,
                  flattening_tolerance,
                  relation);
    }

    com::result PROGPU_NATIVE_COM_CALL Simplify(
        geometry_simplification_option option,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->Simplify(
                  option, &composed, flattening_tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL Tessellate(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        tessellation_sink* sink) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->Tessellate(&composed, flattening_tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL CombineWithGeometry(
        geometry* candidate,
        combine_mode mode,
        const matrix_3x2_f* candidate_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        rectangle_f transformed_rectangle{};
        const com::result rectangle_result =
            get_axis_preserving_rectangle(&transformed_rectangle);
        return com::failed(rectangle_result)
            ? rectangle_result
            : combine_rectangle_with_geometry(
                  owner_.get(),
                  transformed_rectangle,
                  candidate,
                  mode,
                  candidate_transform,
                  flattening_tolerance,
                  sink);
    }

    com::result PROGPU_NATIVE_COM_CALL Outline(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->Outline(&composed, flattening_tolerance, sink);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeArea(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* area) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->ComputeArea(
                  &composed, flattening_tolerance, area);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeLength(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* length) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->ComputeLength(
                  &composed, flattening_tolerance, length);
    }

    com::result PROGPU_NATIVE_COM_CALL ComputePointAtLength(
        float length,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        point_2f* point,
        point_2f* unit_tangent) const noexcept override
    {
        matrix_3x2_f composed{};
        const com::result status = compose(world_transform, &composed);
        return com::failed(status)
            ? status
            : source_->ComputePointAtLength(
                  length,
                  &composed,
                  flattening_tolerance,
                  point,
                  unit_tangent);
    }

    com::result PROGPU_NATIVE_COM_CALL Widen(
        float stroke_width,
        stroke_style* style,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        simplified_geometry_sink* sink) const noexcept override
    {
        if (transform_.m12 != 0.0F || transform_.m21 != 0.0F ||
            transform_.m11 <= 0.0F || transform_.m22 <= 0.0F) {
            return not_implemented;
        }
        rectangle_f transformed_rectangle{};
        const com::result rectangle_result =
            get_axis_preserving_rectangle(&transformed_rectangle);
        return com::failed(rectangle_result)
            ? rectangle_result
            : widen_transformed_rectangle(
                  transformed_rectangle,
                  stroke_width,
                  style,
                  world_transform,
                  flattening_tolerance,
                  sink);
    }

    void PROGPU_NATIVE_COM_CALL GetSourceGeometry(geometry** source) const
        noexcept override
    {
        if (source == nullptr) {
            return;
        }
        *source = source_.get();
        if (*source != nullptr) {
            (*source)->AddRef();
        }
    }

    void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept override
    {
        if (transform != nullptr) {
            *transform = transform_;
        }
    }

private:
    [[nodiscard]] com::result get_axis_preserving_rectangle(
        rectangle_f* transformed_rectangle) const noexcept
    {
        if (transformed_rectangle == nullptr) {
            return com::pointer_error;
        }
        *transformed_rectangle = {};
        const bool axis_preserving =
            (transform_.m12 == 0.0F && transform_.m21 == 0.0F) ||
            (transform_.m11 == 0.0F && transform_.m22 == 0.0F);
        if (!axis_preserving) {
            return not_implemented;
        }
        rectangle_geometry* raw_rectangle = nullptr;
        const com::result query = source_->QueryInterface(
            rectangle_geometry_interface_id,
            reinterpret_cast<void**>(&raw_rectangle));
        com::pointer<rectangle_geometry> rectangle;
        rectangle.attach(raw_rectangle);
        if (com::failed(query) || !rectangle) {
            return com::failed(query) && query != com::no_interface
                ? query
                : not_implemented;
        }
        rectangle_f source_rectangle{};
        rectangle->GetRect(&source_rectangle);
        return core::rectangle_geometry(source_rectangle).bounds(
            &transform_, transformed_rectangle);
    }

    [[nodiscard]] com::result compose(
        const matrix_3x2_f* world_transform,
        matrix_3x2_f* composed) const noexcept
    {
        return core::compose_transform(
            transform_, world_transform, composed);
    }

    friend class com::atomic_reference_count<portable_transformed_geometry>;
    ~portable_transformed_geometry() = default;

    com::atomic_reference_count<portable_transformed_geometry>
        reference_count_;
    com::pointer<factory> owner_;
    com::pointer<geometry> source_;
    matrix_3x2_f transform_{};
};

class portable_factory final :
    public factory,
    public factory_native,
    public scene_factory_native {
public:
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(interface_id, factory_interface_id)) {
            *value = static_cast<factory*>(this);
        } else if (com::guid_equal(
                interface_id, factory_native_interface_id)) {
            *value = static_cast<factory_native*>(this);
        } else if (com::guid_equal(
                interface_id, scene_factory_native_interface_id)) {
            *value = static_cast<scene_factory_native*>(this);
        } else {
            return com::no_interface;
        }
        AddRef();
        return com::ok;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    com::result PROGPU_NATIVE_COM_CALL ReloadSystemMetrics()
        noexcept override
    {
        return com::ok;
    }

    void PROGPU_NATIVE_COM_CALL GetDesktopDpi(
        float* dpi_x,
        float* dpi_y) noexcept override
    {
        if (dpi_x != nullptr) {
            *dpi_x = 96.0F;
        }
        if (dpi_y != nullptr) {
            *dpi_y = 96.0F;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL CreateRectangleGeometry(
        const rectangle_f* rectangle,
        rectangle_geometry** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (rectangle == nullptr ||
            !core::rectangle_geometry::valid_rectangle(*rectangle)) {
            return com::invalid_argument;
        }
        auto* created = new (std::nothrow) portable_rectangle_geometry(
            this, *rectangle);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CreateRoundedRectangleGeometry(
        const rounded_rectangle* rectangle,
        rounded_rectangle_geometry** value) noexcept override
    {
        return detail::create_rounded_rectangle_geometry(
            this, rectangle, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateEllipseGeometry(
        const ellipse* ellipse_value,
        ellipse_geometry** value) noexcept override
    {
        return detail::create_ellipse_geometry(this, ellipse_value, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateGeometryGroup(
        fill_mode mode,
        geometry** geometries,
        std::uint32_t geometry_count,
        geometry_group** value) noexcept override
    {
        return detail::create_geometry_group(
            this, mode, geometries, geometry_count, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateTransformedGeometry(
        geometry* source,
        const matrix_3x2_f* transform,
        transformed_geometry** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (source == nullptr || transform == nullptr ||
            !core::valid_transform(transform)) {
            return com::invalid_argument;
        }
        com::pointer<factory> source_factory;
        factory* raw_source_factory = nullptr;
        source->GetFactory(&raw_source_factory);
        source_factory.attach(raw_source_factory);
        if (!source_factory || source_factory.get() != this) {
            return wrong_factory;
        }
        auto* created = new (std::nothrow) portable_transformed_geometry(
            this, source, *transform);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CreatePathGeometry(
        path_geometry** value) noexcept override
    {
        return detail::create_path_geometry(this, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateStrokeStyle(
        const stroke_style_properties* properties,
        const float* dashes,
        std::uint32_t dash_count,
        stroke_style** value) noexcept override
    {
        return detail::create_stroke_style(
            this, properties, dashes, dash_count, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateDrawingStateBlock(
        const drawing_state_description* description,
        rendering_parameters* text_rendering_parameters,
        drawing_state_block** value) noexcept override
    {
        return detail::create_drawing_state_block(
            this, description, text_rendering_parameters, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateWicBitmapRenderTarget(
        com::unknown*,
        const render_target_properties*,
        render_target** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateHwndRenderTarget(
        const render_target_properties*,
        const hwnd_render_target_properties*,
        hwnd_render_target** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateDxgiSurfaceRenderTarget(
        com::unknown*,
        const render_target_properties*,
        render_target** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateDCRenderTarget(
        const render_target_properties*,
        dc_render_target** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateSolidColorBrush(
        const color_f* color,
        const brush_properties* properties,
        solid_color_brush** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (color == nullptr ||
            !portable_solid_color_brush::valid_color(*color)) {
            return com::invalid_argument;
        }
        constexpr matrix_3x2_f identity_transform{
            1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
        brush_properties actual_properties{1.0F, identity_transform};
        if (properties != nullptr) {
            if (!portable_solid_color_brush::valid_opacity(
                    properties->opacity) ||
                !core::valid_transform(&properties->transform)) {
                return com::invalid_argument;
            }
            actual_properties = *properties;
        }
        auto* created = new (std::nothrow) portable_solid_color_brush(
            this, *color, actual_properties);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CreateSceneRenderTarget(
        const scene_render_target_properties* properties,
        render_target** value) noexcept override
    {
        return detail::create_scene_render_target(this, properties, value);
    }

private:
    template<typename Interface>
    [[nodiscard]] static com::result unsupported_output(
        Interface** value) noexcept
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        return not_implemented;
    }

    friend class com::atomic_reference_count<portable_factory>;
    ~portable_factory() = default;

    com::atomic_reference_count<portable_factory> reference_count_;
};

} // namespace

com::result create_factory(factory** value) noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = new (std::nothrow) portable_factory();
    return *value == nullptr ? com::out_of_memory : com::ok;
}

} // namespace progpu::native::direct2d::compat
