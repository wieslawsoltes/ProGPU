#pragma once

#include "progpu_native_mil.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <new>
#include <numbers>
#include <span>
#include <utility>
#include <vector>

namespace progpu::native::mil::curve_dash {

// Clean-room C++ port of ProGPU's owned managed DashPattern,
// BezierSegmentGeometry, ArcSegmentGeometry, and dashed Compositor path lane.
// The sampled tables approximate only distance-to-parameter inversion; emitted
// Bézier and arc spans remain exact native curve primitives.

inline constexpr float epsilon = 0.0001F;
inline constexpr std::size_t bezier_length_segment_count = 32U;
inline constexpr std::size_t maximum_arc_length_segment_count = 64U;

struct run {
    std::size_t segment_offset{};
    std::size_t segment_count{};
    std::size_t smooth_join_offset{};
    bool closed{};
    bool starts_at_source_start{};
    bool ends_at_source_end{};
    bool closing_smooth_join{};
};

struct run_buffer {
    std::vector<run> runs;
    std::vector<progpu_native_path_segment> segments;
    // Each run owns segment_count - 1 entries beginning at smooth_join_offset.
    std::vector<std::uint8_t> smooth_joins;

    void clear() noexcept {
        runs.clear();
        segments.clear();
        smooth_joins.clear();
    }

    [[nodiscard]] std::span<const progpu_native_path_segment> segments_for(
        const run& value) const noexcept {
        return std::span<const progpu_native_path_segment>(segments).subspan(
            value.segment_offset,
            value.segment_count);
    }

    [[nodiscard]] std::span<const std::uint8_t> smooth_joins_for(
        const run& value) const noexcept {
        return std::span<const std::uint8_t>(smooth_joins).subspan(
            value.smooth_join_offset,
            value.segment_count - 1U);
    }
};

enum class result { success, invalid, capacity_exceeded };

namespace detail {

struct pattern_state {
    std::span<const double> source;
    std::size_t effective_count{};
    std::size_t index{};
    float distance{};
    float thickness{};
};

inline bool finite(
    progpu_native_point point) noexcept {
    return std::isfinite(point.x) && std::isfinite(point.y);
}

inline progpu_native_point lerp(
    progpu_native_point first,
    progpu_native_point second,
    float amount) noexcept {
    return {first.x + (second.x - first.x) * amount,
        first.y + (second.y - first.y) * amount};
}

inline float distance(
    progpu_native_point first,
    progpu_native_point second) noexcept {
    return std::hypot(second.x - first.x, second.y - first.y);
}

inline progpu_native_point metric_point(
    progpu_native_point point,
    const progpu_native_affine_2d* transform) noexcept {
    if (transform == nullptr) {
        return point;
    }
    // Translation cannot change arc length. Excluding it also avoids losing
    // dash precision when a small curve is drawn at a very large world offset.
    return {
        point.x * transform->m11 + point.y * transform->m21,
        point.x * transform->m12 + point.y * transform->m22};
}

inline bool points_near(
    progpu_native_point first,
    progpu_native_point second) noexcept {
    const float x = first.x - second.x;
    const float y = first.y - second.y;
    return x * x + y * y <= epsilon * epsilon;
}

inline progpu_native_point segment_end(
    const progpu_native_path_segment& segment) noexcept {
    return segment.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC ? segment.p2
           : segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC   ? segment.p3
                                                                : segment.p1;
}

inline progpu_native_point evaluate_quadratic(
    const progpu_native_path_segment& segment,
    float parameter) noexcept {
    const float inverse = 1.0F - parameter;
    return {inverse * inverse * segment.p0.x +
                2.0F * inverse * parameter * segment.p1.x +
                parameter * parameter * segment.p2.x,
        inverse * inverse * segment.p0.y +
            2.0F * inverse * parameter * segment.p1.y +
            parameter * parameter * segment.p2.y};
}

inline progpu_native_point evaluate_cubic(
    const progpu_native_path_segment& segment,
    float parameter) noexcept {
    const float inverse = 1.0F - parameter;
    return {inverse * inverse * inverse * segment.p0.x +
                3.0F * inverse * inverse * parameter * segment.p1.x +
                3.0F * inverse * parameter * parameter * segment.p2.x +
                parameter * parameter * parameter * segment.p3.x,
        inverse * inverse * inverse * segment.p0.y +
            3.0F * inverse * inverse * parameter * segment.p1.y +
            3.0F * inverse * parameter * parameter * segment.p2.y +
            parameter * parameter * parameter * segment.p3.y};
}

inline progpu_native_point evaluate_arc(
    const progpu_native_path_segment& segment,
    float parameter) noexcept {
    const float theta = std::bit_cast<float>(segment.pad0) +
                        std::bit_cast<float>(segment.pad1) * parameter;
    const float rotation = std::bit_cast<float>(segment.pad2);
    const float cosine_rotation = std::cos(rotation);
    const float sine_rotation = std::sin(rotation);
    const progpu_native_point axis_x{
        segment.p3.x * cosine_rotation, segment.p3.x * sine_rotation};
    const progpu_native_point axis_y{
        -segment.p3.y * sine_rotation, segment.p3.y * cosine_rotation};
    return {
        segment.p2.x + axis_x.x * std::cos(theta) + axis_y.x * std::sin(theta),
        segment.p2.y + axis_x.y * std::cos(theta) + axis_y.y * std::sin(theta)};
}

inline float resolved_interval(
    const pattern_state& pattern,
    std::size_t index) noexcept {
    const double scaled =
        pattern.source[index % pattern.source.size()] * pattern.thickness;
    if (!std::isfinite(scaled) || scaled < 0.0) {
        return std::numeric_limits<float>::quiet_NaN();
    }
    const float interval = static_cast<float>(scaled);
    return interval <= epsilon
               ? std::max(epsilon * 2.0F, pattern.thickness * 0.001F)
               : interval;
}

inline bool try_create_pattern(
    std::span<const double> source,
    double offset,
    float thickness,
    pattern_state& pattern) noexcept {
    pattern = {};
    if (source.empty() ||
        source.size() > std::numeric_limits<std::size_t>::max() / 2U ||
        !std::isfinite(offset) || !std::isfinite(thickness) ||
        thickness <= 0.0F) {
        return false;
    }
    pattern.source = source;
    pattern.effective_count =
        (source.size() & 1U) == 0U ? source.size() : source.size() * 2U;
    pattern.thickness = thickness;
    float pattern_length = 0.0F;
    for (std::size_t index = 0U; index < pattern.effective_count; ++index) {
        const float interval = resolved_interval(pattern, index);
        if (!std::isfinite(interval) || interval <= epsilon ||
            pattern_length > std::numeric_limits<float>::max() - interval) {
            return false;
        }
        pattern_length += interval;
    }
    const double scaled_offset = offset * thickness;
    if (!std::isfinite(scaled_offset) || !std::isfinite(pattern_length) ||
        pattern_length <= epsilon) {
        return false;
    }
    pattern.distance =
        std::fmod(static_cast<float>(scaled_offset), pattern_length);
    if (!std::isfinite(pattern.distance)) {
        return false;
    }
    if (pattern.distance < 0.0F) {
        pattern.distance += pattern_length;
    }
    while (pattern.distance >= resolved_interval(pattern, pattern.index)) {
        pattern.distance -= resolved_interval(pattern, pattern.index);
        pattern.index = (pattern.index + 1U) % pattern.effective_count;
    }
    return true;
}

inline void advance_pattern(
    pattern_state& pattern,
    float remaining,
    float step) noexcept {
    if (step >= remaining - epsilon) {
        pattern.distance = 0.0F;
        pattern.index = (pattern.index + 1U) % pattern.effective_count;
    } else {
        pattern.distance += step;
    }
}

struct length_table {
    std::array<float, maximum_arc_length_segment_count + 1U> cumulative{};
    std::size_t segment_count{};
    float total{};
};

inline bool build_length_table(
    const progpu_native_path_segment& segment,
    const progpu_native_affine_2d* metric_transform,
    length_table& table) noexcept {
    table = {};
    if (!finite(segment.p0) || !finite(segment_end(segment))) {
        return false;
    }
    switch (segment.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        table.segment_count = 1U;
        table.total = distance(
            metric_point(segment.p0, metric_transform),
            metric_point(segment.p1, metric_transform));
        table.cumulative[1U] = table.total;
        break;
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC: {
        if (!finite(segment.p1)) {
            return false;
        }
        table.segment_count = bezier_length_segment_count;
        auto previous = metric_point(segment.p0, metric_transform);
        for (std::size_t index = 1U; index <= table.segment_count; ++index) {
            const auto current = metric_point(
                evaluate_quadratic(segment,
                    static_cast<float>(index) /
                        static_cast<float>(table.segment_count)),
                metric_transform);
            table.total += distance(previous, current);
            table.cumulative[index] = table.total;
            previous = current;
        }
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC: {
        if (!finite(segment.p1) || !finite(segment.p2)) {
            return false;
        }
        table.segment_count = bezier_length_segment_count;
        auto previous = metric_point(segment.p0, metric_transform);
        for (std::size_t index = 1U; index <= table.segment_count; ++index) {
            const auto current = metric_point(
                evaluate_cubic(segment,
                    static_cast<float>(index) /
                        static_cast<float>(table.segment_count)),
                metric_transform);
            table.total += distance(previous, current);
            table.cumulative[index] = table.total;
            previous = current;
        }
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
        const float sweep = std::bit_cast<float>(segment.pad1);
        const float start = std::bit_cast<float>(segment.pad0);
        const float rotation = std::bit_cast<float>(segment.pad2);
        if (!finite(segment.p2) || !finite(segment.p3) ||
            !std::isfinite(start) || !std::isfinite(sweep) ||
            !std::isfinite(rotation)) {
            return false;
        }
        const float maximum_angle = std::numbers::pi_v<float> / 64.0F;
        const float requested_segment_count =
            std::ceil(std::abs(sweep) / maximum_angle);
        if (!std::isfinite(requested_segment_count)) {
            return false;
        }
        table.segment_count =
            static_cast<std::size_t>(std::clamp(requested_segment_count,
                1.0F,
                static_cast<float>(maximum_arc_length_segment_count)));
        auto previous = metric_point(segment.p0, metric_transform);
        for (std::size_t index = 1U; index <= table.segment_count; ++index) {
            const auto local_current =
                index == table.segment_count
                    ? segment.p1
                    : evaluate_arc(segment,
                          static_cast<float>(index) /
                              static_cast<float>(table.segment_count));
            const auto current = metric_point(local_current, metric_transform);
            table.total += distance(previous, current);
            table.cumulative[index] = table.total;
            previous = current;
        }
        break;
    }
    default:
        return false;
    }
    return std::isfinite(table.total);
}

inline float parameter_at_distance(
    const length_table& table,
    float target) noexcept {
    if (target <= 0.0F) {
        return 0.0F;
    }
    if (target >= table.total) {
        return 1.0F;
    }
    for (std::size_t index = 1U; index <= table.segment_count; ++index) {
        const float segment_end_distance = table.cumulative[index];
        if (target > segment_end_distance) {
            continue;
        }
        const float segment_start_distance = table.cumulative[index - 1U];
        const float segment_length =
            segment_end_distance - segment_start_distance;
        const float local =
            segment_length > epsilon
                ? (target - segment_start_distance) / segment_length
                : 0.0F;
        return (static_cast<float>(index - 1U) + local) /
               static_cast<float>(table.segment_count);
    }
    return 1.0F;
}

inline void split_quadratic(
    progpu_native_point p0,
    progpu_native_point p1,
    progpu_native_point p2,
    float parameter,
    std::array<progpu_native_point, 3U>& left,
    std::array<progpu_native_point, 3U>& right) noexcept {
    const auto p01 = lerp(p0, p1, parameter);
    const auto p12 = lerp(p1, p2, parameter);
    const auto p012 = lerp(p01, p12, parameter);
    left = {p0, p01, p012};
    right = {p012, p12, p2};
}

inline void split_cubic(
    progpu_native_point p0,
    progpu_native_point p1,
    progpu_native_point p2,
    progpu_native_point p3,
    float parameter,
    std::array<progpu_native_point, 4U>& left,
    std::array<progpu_native_point, 4U>& right) noexcept {
    const auto p01 = lerp(p0, p1, parameter);
    const auto p12 = lerp(p1, p2, parameter);
    const auto p23 = lerp(p2, p3, parameter);
    const auto p012 = lerp(p01, p12, parameter);
    const auto p123 = lerp(p12, p23, parameter);
    const auto p0123 = lerp(p012, p123, parameter);
    left = {p0, p01, p012, p0123};
    right = {p0123, p123, p23, p3};
}

inline bool try_create_subsegment(
    const progpu_native_path_segment& source,
    float start_parameter,
    float end_parameter,
    progpu_native_path_segment& result) noexcept {
    if (!std::isfinite(start_parameter) || !std::isfinite(end_parameter) ||
        end_parameter <= start_parameter) {
        return false;
    }
    start_parameter = std::clamp(start_parameter, 0.0F, 1.0F);
    end_parameter = std::clamp(end_parameter, 0.0F, 1.0F);
    if (end_parameter <= start_parameter + epsilon) {
        return false;
    }
    result = source;
    switch (source.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE: {
        const auto delta = progpu_native_point{
            source.p1.x - source.p0.x, source.p1.y - source.p0.y};
        result.p0 =
            start_parameter <= epsilon
                ? source.p0
                : progpu_native_point{source.p0.x + delta.x * start_parameter,
                      source.p0.y + delta.y * start_parameter};
        result.p1 =
            end_parameter >= 1.0F - epsilon
                ? source.p1
                : progpu_native_point{source.p0.x + delta.x * end_parameter,
                      source.p0.y + delta.y * end_parameter};
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC: {
        std::array<progpu_native_point, 3U> left{};
        std::array<progpu_native_point, 3U> unused{};
        split_quadratic(
            source.p0, source.p1, source.p2, end_parameter, left, unused);
        if (start_parameter <= epsilon) {
            result.p0 = left[0U];
            result.p1 = left[1U];
            result.p2 = left[2U];
        } else {
            std::array<progpu_native_point, 3U> right{};
            split_quadratic(left[0U],
                left[1U],
                left[2U],
                start_parameter / end_parameter,
                unused,
                right);
            result.p0 = right[0U];
            result.p1 = right[1U];
            result.p2 = right[2U];
        }
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC: {
        std::array<progpu_native_point, 4U> left{};
        std::array<progpu_native_point, 4U> unused{};
        split_cubic(source.p0,
            source.p1,
            source.p2,
            source.p3,
            end_parameter,
            left,
            unused);
        if (start_parameter <= epsilon) {
            result.p0 = left[0U];
            result.p1 = left[1U];
            result.p2 = left[2U];
            result.p3 = left[3U];
        } else {
            std::array<progpu_native_point, 4U> right{};
            split_cubic(left[0U],
                left[1U],
                left[2U],
                left[3U],
                start_parameter / end_parameter,
                unused,
                right);
            result.p0 = right[0U];
            result.p1 = right[1U];
            result.p2 = right[2U];
            result.p3 = right[3U];
        }
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
        const float source_start = std::bit_cast<float>(source.pad0);
        const float source_sweep = std::bit_cast<float>(source.pad1);
        const float sub_start = source_start + source_sweep * start_parameter;
        const float sub_sweep =
            source_sweep * (end_parameter - start_parameter);
        result.p0 = start_parameter <= epsilon
                        ? source.p0
                        : evaluate_arc(source, start_parameter);
        result.p1 = end_parameter >= 1.0F - epsilon
                        ? source.p1
                        : evaluate_arc(source, end_parameter);
        result.pad0 = std::bit_cast<std::uint32_t>(sub_start);
        result.pad1 = std::bit_cast<std::uint32_t>(sub_sweep);
        break;
    }
    default:
        return false;
    }
    return finite(result.p0) && finite(segment_end(result)) &&
           !points_near(result.p0, segment_end(result));
}

inline result append_segment(
    run_buffer& output,
    std::size_t& active_run,
    const progpu_native_path_segment& segment,
    bool smooth_join) {
    constexpr std::size_t no_run = std::numeric_limits<std::size_t>::max();
    try {
        if (active_run != no_run && active_run < output.runs.size()) {
            auto& current = output.runs[active_run];
            const std::size_t current_end =
                current.segment_offset + current.segment_count;
            if (current.segment_count != 0U &&
                current_end == output.segments.size() &&
                current.smooth_join_offset + current.segment_count - 1U ==
                    output.smooth_joins.size() &&
                points_near(segment_end(output.segments.back()), segment.p0)) {
                progpu_native_path_segment connected = segment;
                connected.p0 = segment_end(output.segments.back());
                output.smooth_joins.push_back(smooth_join ? 1U : 0U);
                try {
                    output.segments.push_back(connected);
                } catch (...) {
                    output.smooth_joins.pop_back();
                    throw;
                }
                ++current.segment_count;
                return result::success;
            }
        }
        const std::size_t segment_offset = output.segments.size();
        output.segments.push_back(segment);
        try {
            output.runs.push_back(run{
                segment_offset,
                1U,
                output.smooth_joins.size()});
        } catch (...) {
            output.segments.pop_back();
            throw;
        }
        active_run = output.runs.size() - 1U;
        return result::success;
    } catch (const std::bad_alloc&) {
        return result::capacity_exceeded;
    }
}

inline bool run_touches_start(
    const run_buffer& output,
    const run& value,
    progpu_native_point source_start) noexcept {
    return value.segment_count != 0U &&
           points_near(output.segments[value.segment_offset].p0, source_start);
}

inline bool run_touches_end(
    const run_buffer& output,
    const run& value,
    progpu_native_point source_end) noexcept {
    return value.segment_count != 0U &&
           points_near(segment_end(output.segments[
                    value.segment_offset + value.segment_count - 1U]),
               source_end);
}

inline result merge_closed_seam(
    run_buffer& output,
    bool closing_smooth_join) {
    if (output.runs.size() < 2U) {
        return result::invalid;
    }
    const run first = output.runs.front();
    const std::size_t last_index = output.runs.size() - 1U;
    const run last = output.runs[last_index];
    if (first.segment_count == 0U || last.segment_count == 0U ||
        last.segment_offset + last.segment_count != output.segments.size() ||
        last.smooth_join_offset + last.segment_count - 1U !=
            output.smooth_joins.size()) {
        return result::invalid;
    }
    try {
        output.segments.reserve(
            output.segments.size() + first.segment_count);
        output.smooth_joins.reserve(
            output.smooth_joins.size() + first.segment_count);
        for (std::size_t index = 0U; index < first.segment_count; ++index) {
            progpu_native_path_segment connected =
                output.segments[first.segment_offset + index];
            connected.p0 = segment_end(output.segments.back());
            output.segments.push_back(connected);
        }
        output.smooth_joins.push_back(closing_smooth_join ? 1U : 0U);
        for (std::size_t index = 0U; index + 1U < first.segment_count;
            ++index) {
            output.smooth_joins.push_back(
                output.smooth_joins[first.smooth_join_offset + index]);
        }
        output.runs[last_index].segment_count += first.segment_count;
        output.runs.erase(output.runs.begin());
        return result::success;
    } catch (const std::bad_alloc&) {
        return result::capacity_exceeded;
    }
}

} // namespace detail

inline result try_create_runs(
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> smooth_joins,
    bool closed,
    std::span<const double> source_intervals,
    double offset,
    float thickness,
    run_buffer& output,
    const progpu_native_affine_2d* metric_transform = nullptr) {
    output.clear();
    if (segments.empty() || smooth_joins.size() != segments.size()) {
        return result::invalid;
    }
    detail::pattern_state pattern{};
    if (!detail::try_create_pattern(
            source_intervals, offset, thickness, pattern)) {
        return result::invalid;
    }
    constexpr std::size_t no_run = std::numeric_limits<std::size_t>::max();
    std::size_t active_run = no_run;
    for (std::size_t segment_index = 0U; segment_index < segments.size();
        ++segment_index) {
        const auto& source = segments[segment_index];
        detail::length_table table{};
        if (!detail::build_length_table(source, metric_transform, table)) {
            return result::invalid;
        }
        if (table.total <= epsilon) {
            continue;
        }
        float traveled = 0.0F;
        while (traveled < table.total - epsilon) {
            const float interval =
                detail::resolved_interval(pattern, pattern.index);
            if (!std::isfinite(interval) || interval <= epsilon) {
                return result::invalid;
            }
            const float remaining = interval - pattern.distance;
            const float step = std::min(remaining, table.total - traveled);
            const bool visible = (pattern.index & 1U) == 0U;
            const bool starts_at_segment_start = traveled <= epsilon;
            if (visible && step > epsilon) {
                progpu_native_path_segment dash_segment{};
                if (!detail::try_create_subsegment(source,
                        detail::parameter_at_distance(table, traveled),
                        detail::parameter_at_distance(table, traveled + step),
                        dash_segment)) {
                    return result::invalid;
                }
                const bool smooth_join = starts_at_segment_start &&
                                         segment_index != 0U &&
                                         smooth_joins[segment_index - 1U] != 0U;
                const result append_result = detail::append_segment(
                    output, active_run, dash_segment, smooth_join);
                if (append_result != result::success) {
                    return append_result;
                }
            } else if (!visible) {
                active_run = no_run;
            }
            const bool pattern_ends = step >= remaining - epsilon;
            detail::advance_pattern(pattern, remaining, step);
            if (visible && pattern_ends) {
                active_run = no_run;
            }
            traveled += step;
        }
    }
    if (output.runs.empty()) {
        return result::success;
    }
    const auto source_start = segments.front().p0;
    const auto source_end = detail::segment_end(segments.back());
    if (!closed) {
        output.runs.front().starts_at_source_start =
            detail::run_touches_start(
                output,
                output.runs.front(),
                source_start);
        output.runs.back().ends_at_source_end = detail::run_touches_end(
            output,
            output.runs.back(),
            source_end);
        return result::success;
    }
    const bool first_touches_seam =
        detail::run_touches_start(
            output,
            output.runs.front(),
            source_start);
    const bool last_touches_seam = detail::run_touches_end(
        output,
        output.runs.back(),
        source_end);
    if (!first_touches_seam || !last_touches_seam) {
        return result::success;
    }
    if (output.runs.size() == 1U) {
        output.runs.front().closed = true;
        output.runs.front().closing_smooth_join =
            smooth_joins.back() != 0U;
        return result::success;
    }
    return detail::merge_closed_seam(
        output,
        smooth_joins.back() != 0U);
}

} // namespace progpu::native::mil::curve_dash
