#pragma once

#include "../Mil/progpu_native_mil_curve_dash.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstdint>
#include <new>
#include <span>
#include <vector>

namespace progpu::native::semantic_path_stroke {

enum class result {
    success,
    invalid,
    capacity_exceeded
};

struct style {
    progpu_native_affine_2d transform{};
    float thickness{};
    float miter_limit{1.0F};
    double dash_offset{};
    std::uint32_t start_cap{};
    std::uint32_t end_cap{};
    std::uint32_t dash_cap{};
    std::uint32_t line_join{};
    std::uint32_t primitive_flags{};
};

// smooth_joins[i] describes the join from segments[i] to segments[i + 1].
// For a closed run, the last entry describes the closing seam. Direct2D's
// FORCE_ROUND_LINE_JOIN flag belongs to the incoming segment instead, so its
// adapter deliberately shifts that flag while constructing semantic runs.

inline progpu_native_point segment_end(
    const progpu_native_path_segment& segment) noexcept {
    return segment.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC
        ? segment.p2
        : segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC
            ? segment.p3
            : segment.p1;
}

inline bool try_tangent(
    const progpu_native_path_segment& segment,
    bool at_start,
    progpu_native_point& tangent) noexcept {
    const auto subtract = [](progpu_native_point first,
                             progpu_native_point second) noexcept {
        return progpu_native_point{
            first.x - second.x,
            first.y - second.y};
    };
    const auto nonzero = [](progpu_native_point value) noexcept {
        return value.x != 0.0F || value.y != 0.0F;
    };
    switch (segment.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        tangent = subtract(segment.p1, segment.p0);
        return nonzero(tangent);
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
        tangent = at_start
            ? subtract(segment.p1, segment.p0)
            : subtract(segment.p2, segment.p1);
        if (!nonzero(tangent)) {
            tangent = subtract(segment.p2, segment.p0);
        }
        return nonzero(tangent);
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC: {
        const std::array candidates = at_start
            ? std::array{
                  subtract(segment.p1, segment.p0),
                  subtract(segment.p2, segment.p0),
                  subtract(segment.p3, segment.p0)}
            : std::array{
                  subtract(segment.p3, segment.p2),
                  subtract(segment.p3, segment.p1),
                  subtract(segment.p3, segment.p0)};
        for (const auto candidate : candidates) {
            if (nonzero(candidate)) {
                tangent = candidate;
                return true;
            }
        }
        return false;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
        const float theta = std::bit_cast<float>(segment.pad0) +
            (at_start ? 0.0F : std::bit_cast<float>(segment.pad1));
        const float direction = std::bit_cast<float>(segment.pad1) < 0.0F
            ? -1.0F
            : 1.0F;
        const float rotation = std::bit_cast<float>(segment.pad2);
        const float cosine_rotation = std::cos(rotation);
        const float sine_rotation = std::sin(rotation);
        const progpu_native_point axis_x{
            segment.p3.x * cosine_rotation,
            segment.p3.x * sine_rotation};
        const progpu_native_point axis_y{
            -segment.p3.y * sine_rotation,
            segment.p3.y * cosine_rotation};
        tangent = {
            direction * (-axis_x.x * std::sin(theta) +
                axis_y.x * std::cos(theta)),
            direction * (-axis_x.y * std::sin(theta) +
                axis_y.y * std::cos(theta))};
        return nonzero(tangent);
    }
    default:
        return false;
    }
}

inline bool make_primitive(
    const progpu_native_path_segment& segment,
    const style& stroke,
    progpu_native_geometry_primitive& primitive) noexcept {
    primitive = {};
    primitive.flags = stroke.primitive_flags;
    primitive.stroke_thickness = stroke.thickness;
    primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
    primitive.transform = stroke.transform;
    switch (segment.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        primitive.kind = PROGPU_NATIVE_GEOMETRY_LINE;
        primitive.p0 = segment.p0;
        primitive.p1 = segment.p1;
        return true;
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
        primitive.kind = PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER;
        primitive.p0 = segment.p0;
        primitive.p1 = segment.p1;
        primitive.p2 = segment.p2;
        return true;
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC:
        primitive.kind = PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER;
        primitive.p0 = segment.p0;
        primitive.p1 = segment.p1;
        primitive.p2 = segment.p2;
        primitive.p3 = segment.p3;
        return true;
    case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
        primitive.kind = PROGPU_NATIVE_GEOMETRY_ARC;
        primitive.p0 = segment.p2;
        const float rotation = std::bit_cast<float>(segment.pad2);
        const float cosine_rotation = std::cos(rotation);
        const float sine_rotation = std::sin(rotation);
        primitive.p1 = {
            segment.p3.x * cosine_rotation,
            segment.p3.x * sine_rotation};
        primitive.p2 = {
            -segment.p3.y * sine_rotation,
            segment.p3.y * cosine_rotation};
        primitive.p3 = {
            std::bit_cast<float>(segment.pad0),
            std::bit_cast<float>(segment.pad1)};
        return true;
    }
    default:
        return false;
    }
}

inline result compile(
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> smooth_joins,
    bool closed,
    std::span<const double> dash_intervals,
    const style& stroke,
    std::uint32_t brush_index,
    mil::curve_dash::run_buffer& dash_scratch,
    std::vector<progpu_native_geometry_primitive>& primitives,
    std::vector<std::uint32_t>& brushes) {
    constexpr std::uint32_t allowed_primitive_flags =
        PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE;
    const bool finite_transform =
        std::isfinite(stroke.transform.m11) &&
        std::isfinite(stroke.transform.m12) &&
        std::isfinite(stroke.transform.m21) &&
        std::isfinite(stroke.transform.m22) &&
        std::isfinite(stroke.transform.m31) &&
        std::isfinite(stroke.transform.m32);
    if (segments.empty() || smooth_joins.size() != segments.size() ||
        !std::isfinite(stroke.thickness) || stroke.thickness <= 0.0F ||
        !std::isfinite(stroke.miter_limit) || stroke.miter_limit < 1.0F ||
        !std::isfinite(stroke.dash_offset) || !finite_transform ||
        stroke.start_cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
        stroke.end_cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
        stroke.dash_cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
        stroke.line_join > PROGPU_NATIVE_STROKE_JOIN_ROUND ||
        (stroke.primitive_flags & ~allowed_primitive_flags) != 0U ||
        (stroke.primitive_flags &
            (PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
                PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)) ==
            (PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
                PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)) {
        return result::invalid;
    }
    const std::size_t primitive_start = primitives.size();
    const std::size_t brush_start = brushes.size();
    const auto rollback = [&](result value) noexcept {
        primitives.resize(primitive_start);
        brushes.resize(brush_start);
        return value;
    };
    try {
        const auto append_cap = [&](
            const progpu_native_path_segment& segment,
            std::uint32_t cap,
            bool at_start) {
            if (cap == PROGPU_NATIVE_STROKE_CAP_FLAT) {
                return true;
            }
            progpu_native_point tangent{};
            if (!try_tangent(segment, at_start, tangent)) {
                return false;
            }
            progpu_native_geometry_primitive primitive{};
            primitive.kind = PROGPU_NATIVE_GEOMETRY_PATH_CAP;
            primitive.flags = stroke.primitive_flags |
                (cap << PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT);
            primitive.p0 = at_start ? segment.p0 : segment_end(segment);
            primitive.p1 = tangent;
            primitive.p2.x = at_start ? 1.0F : 0.0F;
            primitive.stroke_thickness = stroke.thickness;
            primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
            primitive.transform = stroke.transform;
            primitives.push_back(primitive);
            brushes.push_back(brush_index);
            return true;
        };
        const auto append_join = [&](
            const progpu_native_path_segment& incoming,
            const progpu_native_path_segment& outgoing,
            bool smooth_join) {
            const auto join_point = segment_end(incoming);
            if (join_point.x != outgoing.p0.x ||
                join_point.y != outgoing.p0.y) {
                return false;
            }
            progpu_native_point incoming_tangent{};
            progpu_native_point outgoing_tangent{};
            if (!try_tangent(incoming, false, incoming_tangent) ||
                !try_tangent(outgoing, true, outgoing_tangent)) {
                return false;
            }
            progpu_native_geometry_primitive primitive{};
            primitive.kind = PROGPU_NATIVE_GEOMETRY_PATH_JOIN;
            primitive.flags = stroke.primitive_flags |
                ((smooth_join
                    ? static_cast<std::uint32_t>(
                        PROGPU_NATIVE_STROKE_JOIN_ROUND)
                    : stroke.line_join) <<
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT);
            primitive.p0 = join_point;
            primitive.p1 = incoming_tangent;
            primitive.p2 = outgoing_tangent;
            primitive.p3.x = stroke.miter_limit;
            primitive.stroke_thickness = stroke.thickness;
            primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
            primitive.transform = stroke.transform;
            primitives.push_back(primitive);
            brushes.push_back(brush_index);
            return true;
        };
        const auto append_run = [&](
            std::span<const progpu_native_path_segment> run_segments,
            std::span<const std::uint8_t> run_smooth_joins,
            bool run_closed,
            bool closing_smooth_join,
            std::uint32_t run_start_cap,
            std::uint32_t run_end_cap) {
            if (run_segments.empty() ||
                run_smooth_joins.size() + 1U != run_segments.size()) {
                return false;
            }
            if (!run_closed && !append_cap(
                    run_segments.front(), run_start_cap, true)) {
                return false;
            }
            for (std::size_t index = 0U;
                 index < run_segments.size();
                 ++index) {
                if (index != 0U && !append_join(
                        run_segments[index - 1U],
                        run_segments[index],
                        run_smooth_joins[index - 1U] != 0U)) {
                    return false;
                }
                progpu_native_geometry_primitive primitive{};
                if (!make_primitive(run_segments[index], stroke, primitive)) {
                    return false;
                }
                primitives.push_back(primitive);
                brushes.push_back(brush_index);
            }
            if (run_closed && !append_join(
                    run_segments.back(),
                    run_segments.front(),
                    closing_smooth_join)) {
                return false;
            }
            return run_closed || append_cap(
                run_segments.back(), run_end_cap, false);
        };

        if (!dash_intervals.empty()) {
            const auto dash_result = mil::curve_dash::try_create_runs(
                segments,
                smooth_joins,
                closed,
                dash_intervals,
                stroke.dash_offset,
                stroke.thickness,
                dash_scratch);
            if (dash_result ==
                mil::curve_dash::result::capacity_exceeded) {
                return rollback(result::capacity_exceeded);
            }
            if (dash_result != mil::curve_dash::result::success) {
                return rollback(result::invalid);
            }
            for (const auto& run : dash_scratch.runs) {
                const std::uint32_t run_start_cap =
                    run.starts_at_source_start
                    ? stroke.start_cap
                    : stroke.dash_cap;
                const std::uint32_t run_end_cap = run.ends_at_source_end
                    ? stroke.end_cap
                    : stroke.dash_cap;
                if (!append_run(
                        dash_scratch.segments_for(run),
                        dash_scratch.smooth_joins_for(run),
                        run.closed,
                        run.closing_smooth_join,
                        run_start_cap,
                        run_end_cap)) {
                    return rollback(result::invalid);
                }
            }
            return result::success;
        }
        if (!append_run(
                segments,
                smooth_joins.first(smooth_joins.size() - 1U),
                closed,
                smooth_joins.back() != 0U,
                stroke.start_cap,
                stroke.end_cap)) {
            return rollback(result::invalid);
        }
        return result::success;
    } catch (const std::bad_alloc&) {
        return rollback(result::capacity_exceeded);
    } catch (...) {
        return rollback(result::invalid);
    }
}

} // namespace progpu::native::semantic_path_stroke
