#pragma once

#include "progpu_native_geometry_stroke.hpp"

namespace progpu::native {

struct dash_pattern_state {
    const double* intervals = nullptr;
    std::size_t source_count = 0U;
    std::size_t effective_count = 0U;
    std::size_t index = 0U;
    float distance = 0.0F;
    float thickness = 0.0F;
    std::uint32_t cap = PROGPU_NATIVE_STROKE_CAP_FLAT;
};

inline float resolved_dash_interval(
    const dash_pattern_state& pattern,
    std::size_t index) noexcept {
    constexpr float epsilon = 0.0001F;
    const double scaled =
        pattern.intervals[index % pattern.source_count] * pattern.thickness;
    if (!std::isfinite(scaled) || scaled < 0.0) {
        return std::numeric_limits<float>::quiet_NaN();
    }
    const float interval = static_cast<float>(scaled);
    return interval <= epsilon
        ? std::max(epsilon * 2.0F, pattern.thickness * 0.001F)
        : interval;
}

inline bool try_create_dash_pattern(
    const progpu_native_polyline& polyline,
    const progpu_native_dash_style* styles,
    std::size_t style_count,
    const double* doubles,
    std::size_t double_count,
    dash_pattern_state& pattern) noexcept {
    constexpr float epsilon = 0.0001F;
    pattern = {};
    if (polyline.dash_style == 0U) {
        return false;
    }
    const std::size_t style_index =
        static_cast<std::size_t>(polyline.dash_style - 1U);
    if (styles == nullptr || style_index >= style_count || doubles == nullptr) {
        return false;
    }
    const auto& style = styles[style_index];
    if (style.interval_count == 0U ||
        style.interval_offset > double_count ||
        style.interval_count > double_count - style.interval_offset ||
        style.interval_count > std::numeric_limits<std::size_t>::max() / 2U ||
        !std::isfinite(style.offset) ||
        style.cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
        style.reserved != 0U) {
        return false;
    }
    const bool hairline =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE) != 0U;
    const float thickness = hairline ? 1.0F : polyline.stroke_thickness;
    if (!std::isfinite(thickness) || thickness <= 0.0F) {
        return false;
    }
    pattern.intervals = doubles + style.interval_offset;
    pattern.source_count = style.interval_count;
    pattern.effective_count = (style.interval_count & 1U) == 0U
        ? style.interval_count
        : style.interval_count * 2U;
    pattern.thickness = thickness;
    pattern.cap = style.cap;
    float pattern_length = 0.0F;
    for (std::size_t index = 0U; index < pattern.effective_count; ++index) {
        const float interval = resolved_dash_interval(pattern, index);
        if (!std::isfinite(interval) || interval <= epsilon ||
            pattern_length > std::numeric_limits<float>::max() - interval) {
            return false;
        }
        pattern_length += interval;
    }
    const double scaled_offset = style.offset * thickness;
    if (!std::isfinite(scaled_offset) ||
        !std::isfinite(pattern_length) || pattern_length <= epsilon) {
        return false;
    }
    float distance = std::fmod(
        static_cast<float>(scaled_offset),
        pattern_length);
    if (!std::isfinite(distance)) {
        return false;
    }
    if (distance < 0.0F) {
        distance += pattern_length;
    }
    while (distance >= resolved_dash_interval(pattern, pattern.index)) {
        distance -= resolved_dash_interval(pattern, pattern.index);
        pattern.index = (pattern.index + 1U) % pattern.effective_count;
    }
    pattern.distance = distance;
    return true;
}

inline void advance_dash_pattern(
    dash_pattern_state& pattern,
    float remaining,
    float step) noexcept {
    constexpr float epsilon = 0.0001F;
    if (step >= remaining - epsilon) {
        pattern.distance = 0.0F;
        pattern.index = (pattern.index + 1U) % pattern.effective_count;
    } else {
        pattern.distance += step;
    }
}

template <typename Body, typename Cap, typename Join>
inline bool walk_dashed_polyline(
    const progpu_native_polyline& polyline,
    const progpu_native_point* points,
    dash_pattern_state pattern,
    Body&& append_body,
    Cap&& append_cap,
    Join&& append_join) {
    constexpr float epsilon = 0.0001F;
    const bool closed =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U;
    const bool device_space =
        (polyline.flags & (PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE |
            PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE)) != 0U;
    const std::size_t segment_count = closed
        ? polyline.point_count
        : polyline.point_count - 1U;
    const auto point_at = [&](std::size_t index) {
        return device_space
            ? transformed_point(polyline.transform, points[index])
            : points[index];
    };

    bool first_visible = false;
    bool last_visible = false;
    bool continuing_run = false;
    bool have_first_direction = false;
    bool have_previous_direction = false;
    progpu_native_point first_direction{};
    progpu_native_point previous_direction{};
    progpu_native_point first_visible_direction{};
    progpu_native_point last_visible_direction{};
    progpu_native_point first_visible_point{};
    progpu_native_point last_visible_point{};

    for (std::size_t segment = 0U; segment < segment_count; ++segment) {
        const std::size_t next = (segment + 1U) % polyline.point_count;
        const progpu_native_point start = point_at(segment);
        const progpu_native_point end = point_at(next);
        const progpu_native_point delta{end.x - start.x, end.y - start.y};
        const float length = std::hypot(delta.x, delta.y);
        if (!std::isfinite(length)) {
            return false;
        }
        if (length <= epsilon) {
            continue;
        }
        const progpu_native_point direction{
            delta.x / length,
            delta.y / length
        };
        if (!have_first_direction) {
            first_direction = direction;
            have_first_direction = true;
        }
        if (continuing_run && have_previous_direction) {
            if (!append_join(start, previous_direction, direction)) {
                return false;
            }
        }

        float distance = 0.0F;
        bool segment_ends_visible = false;
        bool segment_continues = false;
        while (distance < length - epsilon) {
            const float interval = resolved_dash_interval(pattern, pattern.index);
            if (!std::isfinite(interval) || interval <= epsilon) {
                return false;
            }
            const float remaining = interval - pattern.distance;
            const float step = std::min(remaining, length - distance);
            const bool on = (pattern.index & 1U) == 0U;
            const bool at_segment_start = distance <= epsilon;
            const bool at_segment_end = distance + step >= length - epsilon;
            const bool pattern_ends = step >= remaining - epsilon;
            if (on && step > epsilon) {
                const progpu_native_point body_start{
                    start.x + direction.x * distance,
                    start.y + direction.y * distance
                };
                const progpu_native_point body_end{
                    start.x + direction.x * (distance + step),
                    start.y + direction.y * (distance + step)
                };
                if (!append_body(body_start, body_end)) {
                    return false;
                }
                const bool starts_new_run =
                    !(continuing_run && at_segment_start);
                if (starts_new_run) {
                    const bool source_start = !closed && segment == 0U &&
                        at_segment_start;
                    const std::uint32_t cap = source_start
                        ? (polyline.flags & PROGPU_NATIVE_POLYLINE_START_CAP_MASK) >>
                            PROGPU_NATIVE_POLYLINE_START_CAP_SHIFT
                        : pattern.cap;
                    if (closed && segment == 0U && at_segment_start) {
                        first_visible = true;
                        first_visible_point = body_start;
                        first_visible_direction = direction;
                    } else if (!append_cap(
                            cap,
                            body_start,
                            direction,
                            true)) {
                        return false;
                    }
                }

                const bool source_end = !closed &&
                    segment + 1U == segment_count && at_segment_end;
                if (source_end) {
                    const std::uint32_t cap =
                        (polyline.flags & PROGPU_NATIVE_POLYLINE_END_CAP_MASK) >>
                        PROGPU_NATIVE_POLYLINE_END_CAP_SHIFT;
                    if (!append_cap(cap, body_end, direction, false)) {
                        return false;
                    }
                } else if (closed && segment + 1U == segment_count &&
                    at_segment_end) {
                    last_visible = true;
                    last_visible_point = body_end;
                    last_visible_direction = direction;
                } else if (pattern_ends) {
                    if (!append_cap(
                            pattern.cap,
                            body_end,
                            direction,
                            false)) {
                        return false;
                    }
                }
                segment_ends_visible = at_segment_end;
                segment_continues = at_segment_end && !pattern_ends &&
                    !source_end;
            } else if (at_segment_end) {
                segment_ends_visible = false;
                segment_continues = false;
            }
            advance_dash_pattern(pattern, remaining, step);
            distance += step;
        }
        continuing_run = segment_ends_visible && segment_continues;
        previous_direction = direction;
        have_previous_direction = true;
    }

    if (closed) {
        if (first_visible && last_visible && have_first_direction &&
            have_previous_direction) {
            if (!append_join(
                    first_visible_point,
                    last_visible_direction,
                    first_visible_direction)) {
                return false;
            }
        } else {
            if (first_visible && !append_cap(
                    pattern.cap,
                    first_visible_point,
                    first_visible_direction,
                    true)) {
                return false;
            }
            if (last_visible && !append_cap(
                    pattern.cap,
                    last_visible_point,
                    last_visible_direction,
                    false)) {
                return false;
            }
        }
    }
    return true;
}

inline bool polyline_capacity(
    const progpu_native_polyline& polyline,
    std::size_t& vertex_count,
    std::size_t& index_count) noexcept {
    const bool closed =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U;
    if (polyline.point_count < 2U ||
        (closed && polyline.point_count < 3U)) {
        return false;
    }
    const std::size_t segment_count = closed
        ? polyline.point_count
        : polyline.point_count - 1U;
    const std::size_t join_count = closed
        ? polyline.point_count
        : polyline.point_count - 2U;
    const std::size_t cap_count = closed
        ? 0U
        : ((polyline.flags & PROGPU_NATIVE_POLYLINE_START_CAP_MASK) != 0U
            ? 1U : 0U) +
          ((polyline.flags & PROGPU_NATIVE_POLYLINE_END_CAP_MASK) != 0U
            ? 1U : 0U);
    if (segment_count > std::numeric_limits<std::size_t>::max() / 4U ||
        join_count > std::numeric_limits<std::size_t>::max() / 32U ||
        cap_count > std::numeric_limits<std::size_t>::max() / 32U) {
        return false;
    }
    vertex_count = segment_count * 4U + join_count * 32U + cap_count * 32U;
    index_count = segment_count * 6U + join_count * 48U + cap_count * 48U;
    return true;
}

inline bool polyline_capacity(
    const progpu_native_polyline& polyline,
    const progpu_native_point* points,
    const progpu_native_dash_style* dash_styles,
    std::size_t dash_style_count,
    const double* doubles,
    std::size_t double_count,
    std::size_t& vertex_count,
    std::size_t& index_count) {
    if (polyline.dash_style == 0U) {
        return polyline_capacity(polyline, vertex_count, index_count);
    }
    if (points == nullptr) {
        return false;
    }
    dash_pattern_state pattern{};
    if (!try_create_dash_pattern(
            polyline,
            dash_styles,
            dash_style_count,
            doubles,
            double_count,
            pattern)) {
        return false;
    }
    vertex_count = 0U;
    index_count = 0U;
    bool valid = true;
    const auto add = [&](std::size_t vertices_to_add,
                         std::size_t indices_to_add) {
        if (vertex_count > std::numeric_limits<std::size_t>::max() -
                vertices_to_add ||
            index_count > std::numeric_limits<std::size_t>::max() -
                indices_to_add) {
            valid = false;
            return false;
        }
        vertex_count += vertices_to_add;
        index_count += indices_to_add;
        return true;
    };
    const auto body = [&](const progpu_native_point&,
                          const progpu_native_point&) {
        return add(4U, 6U);
    };
    const auto cap = [&](std::uint32_t cap_kind,
                         const progpu_native_point&,
                         const progpu_native_point&,
                         bool) {
        return cap_kind == PROGPU_NATIVE_STROKE_CAP_FLAT || add(32U, 48U);
    };
    const auto join = [&](const progpu_native_point&,
                          const progpu_native_point&,
                          const progpu_native_point&) {
        return add(32U, 48U);
    };
    return walk_dashed_polyline(
        polyline,
        points,
        pattern,
        body,
        cap,
        join) && valid;
}

inline bool append_polyline(
    const progpu_native_polyline& polyline,
    const progpu_native_point* points,
    float brush_index,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices,
    const progpu_native_dash_style* dash_styles = nullptr,
    std::size_t dash_style_count = 0U,
    const double* doubles = nullptr,
    std::size_t double_count = 0U,
    bool capacity_prevalidated = false) {
    constexpr std::uint32_t all_flags =
        PROGPU_NATIVE_POLYLINE_FLAG_EDGE_ALIASED |
        PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE |
        PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE |
        PROGPU_NATIVE_POLYLINE_START_CAP_MASK |
        PROGPU_NATIVE_POLYLINE_END_CAP_MASK |
        PROGPU_NATIVE_POLYLINE_JOIN_MASK |
        PROGPU_NATIVE_POLYLINE_FLAG_CLOSED |
        PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS;
    const std::uint32_t join =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_JOIN_MASK) >>
        PROGPU_NATIVE_POLYLINE_JOIN_SHIFT;
    const bool closed =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U;
    const bool hairline =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE) != 0U;
    const bool fixed_device =
        (polyline.flags &
            PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE) != 0U;
    const bool use_wpf_join_semantics =
        (polyline.flags &
            PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS) != 0U;
    if (points == nullptr || polyline.point_count < 2U ||
        (closed && polyline.point_count < 3U) ||
        (polyline.flags & ~all_flags) != 0U ||
        join > PROGPU_NATIVE_STROKE_JOIN_ROUND ||
        !is_finite(polyline.color) || !is_finite(polyline.transform) ||
        !std::isfinite(polyline.stroke_thickness) ||
        !std::isfinite(polyline.miter_limit) ||
        (hairline && fixed_device) ||
        (use_wpf_join_semantics && (hairline || fixed_device)) ||
        (hairline && polyline.stroke_thickness != 0.0F) ||
        (!hairline && polyline.stroke_thickness <= 0.0F)) {
        return false;
    }
    for (std::size_t index = 0U; index < polyline.point_count; ++index) {
        if (!is_finite(points[index])) {
            return false;
        }
    }

    std::size_t capacity_vertices = 0U;
    std::size_t capacity_indices = 0U;
    if ((!capacity_prevalidated && !polyline_capacity(
            polyline,
            points,
            dash_styles,
            dash_style_count,
            doubles,
            double_count,
            capacity_vertices,
            capacity_indices)) ||
        vertices.size() > std::numeric_limits<std::uint32_t>::max() -
            capacity_vertices ||
        vertices.size() > std::numeric_limits<std::size_t>::max() -
            capacity_vertices ||
        indices.size() > std::numeric_limits<std::size_t>::max() -
            capacity_indices) {
        return false;
    }
    const std::size_t initial_vertex_count = vertices.size();
    const std::size_t initial_index_count = indices.size();
    const bool aliased =
        (polyline.flags & PROGPU_NATIVE_POLYLINE_FLAG_EDGE_ALIASED) != 0U;
    float maximum_scale = 0.0F;
    float minimum_scale = 0.0F;
    if (!try_get_stroke_scales(
            polyline.transform,
            maximum_scale,
            minimum_scale)) {
        return false;
    }
    const bool affine_outline = !hairline && !fixed_device &&
        requires_affine_stroke_geometry(polyline.transform);
    const float encoded_thickness = hairline
        ? -1.0F
        : fixed_device
            ? -std::max(
                polyline.stroke_thickness + 1.0F,
                std::nextafter(1.0F, 2.0F))
            : polyline.stroke_thickness * maximum_scale;
    const std::uint32_t primitive_flags =
        (aliased
            ? static_cast<std::uint32_t>(PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
            : 0U) |
        (hairline
            ? static_cast<std::uint32_t>(PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE)
            : 0U) |
        (fixed_device
            ? static_cast<std::uint32_t>(PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)
            : 0U);

    const auto make_segment = [&](std::size_t first, std::size_t second) {
        progpu_native_geometry_primitive segment{};
        segment.kind = PROGPU_NATIVE_GEOMETRY_LINE;
        segment.flags = primitive_flags;
        segment.p0 = points[first];
        segment.p1 = points[second];
        segment.stroke_thickness = polyline.stroke_thickness;
        segment.color = polyline.color;
        segment.transform = polyline.transform;
        return segment;
    };
    const auto rollback = [&]() {
        vertices.resize(initial_vertex_count);
        indices.resize(initial_index_count);
        return false;
    };

    if (polyline.dash_style != 0U) {
        dash_pattern_state pattern{};
        if (!try_create_dash_pattern(
                polyline,
                dash_styles,
                dash_style_count,
                doubles,
                double_count,
                pattern)) {
            return rollback();
        }
        const progpu_native_affine_2d identity{
            1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F
        };
        const auto& body_transform = hairline || fixed_device
            ? identity
            : polyline.transform;
        const auto append_body = [&](const progpu_native_point& start,
                                     const progpu_native_point& end) {
            return append_connected_line_body(
                start,
                end,
                body_transform,
                polyline.color,
                polyline.stroke_thickness,
                encoded_thickness,
                affine_outline,
                brush_index,
                aliased,
                vertices,
                indices);
        };
        const auto append_cap_at = [&](std::uint32_t cap,
                                       const progpu_native_point& center,
                                       const progpu_native_point& direction,
                                       bool is_start) {
            if (hairline || fixed_device) {
                append_device_cap(
                    cap,
                    center,
                    direction,
                    is_start,
                    encoded_thickness,
                    brush_index,
                    aliased,
                    vertices,
                    indices);
                return true;
            }
            if (affine_outline) {
                return append_cpu_cap(
                    cap,
                    polyline.stroke_thickness,
                    center,
                    direction,
                    is_start,
                    &polyline.transform,
                    brush_index,
                    aliased,
                    vertices,
                    indices);
            }
            return append_cpu_cap(
                cap,
                polyline.stroke_thickness * maximum_scale,
                transformed_point(polyline.transform, center),
                transformed_direction(polyline.transform, direction),
                is_start,
                nullptr,
                brush_index,
                aliased,
                vertices,
                indices);
        };
        const auto append_join_at_points = [&] (
            const progpu_native_point& point,
            const progpu_native_point& incoming,
            const progpu_native_point& outgoing) {
            if (hairline || fixed_device) {
                append_device_join(
                    join,
                    polyline.miter_limit,
                    point,
                    incoming,
                    outgoing,
                    encoded_thickness,
                    brush_index,
                    aliased,
                    vertices,
                    indices);
                return true;
            }
            if (affine_outline) {
                return append_cpu_join(
                    join,
                    polyline.stroke_thickness,
                    polyline.miter_limit,
                    point,
                    incoming,
                    outgoing,
                    &polyline.transform,
                    brush_index,
                    aliased,
                    vertices,
                    indices,
                    use_wpf_join_semantics);
            }
            return append_cpu_join(
                join,
                polyline.stroke_thickness * maximum_scale,
                polyline.miter_limit,
                transformed_point(polyline.transform, point),
                transformed_direction(polyline.transform, incoming),
                transformed_direction(polyline.transform, outgoing),
                nullptr,
                brush_index,
                aliased,
                vertices,
                indices,
                use_wpf_join_semantics);
        };
        if (!walk_dashed_polyline(
                polyline,
                points,
                pattern,
                append_body,
                append_cap_at,
                append_join_at_points)) {
            return rollback();
        }
        return true;
    }

    const auto append_join_at = [&](std::size_t previous,
                                    std::size_t current,
                                    std::size_t next) {
        const progpu_native_point incoming{
            points[current].x - points[previous].x,
            points[current].y - points[previous].y
        };
        const progpu_native_point outgoing{
            points[next].x - points[current].x,
            points[next].y - points[current].y
        };
        if (hairline || fixed_device) {
            append_device_join(
                join,
                polyline.miter_limit,
                transformed_point(polyline.transform, points[current]),
                transformed_direction(polyline.transform, incoming),
                transformed_direction(polyline.transform, outgoing),
                encoded_thickness,
                brush_index,
                aliased,
                vertices,
                indices);
            return true;
        }
        if (affine_outline) {
            return append_cpu_join(
                join,
                polyline.stroke_thickness,
                polyline.miter_limit,
                points[current],
                incoming,
                outgoing,
                &polyline.transform,
                brush_index,
                aliased,
                vertices,
                indices,
                use_wpf_join_semantics);
        }
        return append_cpu_join(
            join,
            polyline.stroke_thickness * maximum_scale,
            polyline.miter_limit,
            transformed_point(polyline.transform, points[current]),
            transformed_direction(polyline.transform, incoming),
            transformed_direction(polyline.transform, outgoing),
            nullptr,
            brush_index,
            aliased,
            vertices,
            indices,
            use_wpf_join_semantics);
    };

    if (!closed) {
        auto start = make_segment(0U, 1U);
        start.flags |= polyline.flags & PROGPU_NATIVE_POLYLINE_START_CAP_MASK;
        if (!append_primitive_caps(
                start,
                hairline,
                fixed_device,
                affine_outline,
                maximum_scale,
                encoded_thickness,
                brush_index,
                aliased,
                1U,
                vertices,
                indices)) {
            return rollback();
        }
    }

    const std::size_t segment_count = closed
        ? polyline.point_count
        : polyline.point_count - 1U;
    for (std::size_t index = 0U; index < segment_count; ++index) {
        const std::size_t next = (index + 1U) % polyline.point_count;
        if (!append_connected_line_body(
                points[index],
                points[next],
                polyline.transform,
                polyline.color,
                polyline.stroke_thickness,
                encoded_thickness,
                affine_outline,
                brush_index,
                aliased,
                vertices,
                indices)) {
            return rollback();
        }
        if (closed || next + 1U < polyline.point_count) {
            const std::size_t after = (next + 1U) % polyline.point_count;
            if (!append_join_at(index, next, after)) {
                return rollback();
            }
        }
    }

    if (!closed) {
        auto end = make_segment(
            polyline.point_count - 2U,
            polyline.point_count - 1U);
        end.flags |= polyline.flags & PROGPU_NATIVE_POLYLINE_END_CAP_MASK;
        if (!append_primitive_caps(
                end,
                hairline,
                fixed_device,
                affine_outline,
                maximum_scale,
                encoded_thickness,
                brush_index,
                aliased,
                2U,
                vertices,
                indices)) {
            return rollback();
        }
    }
    return true;
}

} // namespace progpu::native
