#include "progpu_native_path_core.hpp"
#include "progpu_native_geometry_primitives.hpp"

#include <bit>
#include <cmath>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <numbers>
#include <span>
#include <utility>
#include <vector>

// Path recording and the canonical segment transports of the Geometry module,
// moved from the Direct2D path implementation (its recording geometry sink and
// native fill/query/stroke geometry builders), which is now a client.

namespace progpu::native::geometry {
namespace {

[[nodiscard]] bool finite_point(point_2f point) noexcept
{
    return std::isfinite(point.x) && std::isfinite(point.y);
}

[[nodiscard]] bool same_point(point_2f left, point_2f right) noexcept
{
    return left.x == right.x && left.y == right.y;
}

// Algorithm: Stream canonical native fill segments into a path sink. Full
// ellipses split into two endpoint arcs; no flattening or CPU pixels are
// introduced here. Time/space: O(S) retained path records.
status append_native_query_segment(path_sink& output,
    const progpu_native_path_segment& segment, path_segment flags, bool stroke, point_2f& end) noexcept
{
    path_sink* const sink = &output;
    sink->set_segment_flags(flags);
    switch (segment.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        end = {segment.p1.x, segment.p1.y};
        sink->add_line(end);
        break;
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC: {
        end = {segment.p2.x, segment.p2.y};
        const quadratic_bezier_segment_f curve{{segment.p1.x, segment.p1.y}, end};
        sink->add_quadratic_bezier(&curve);
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC: {
        end = {segment.p3.x, segment.p3.y};
        const cubic_bezier_segment_f curve{{segment.p1.x, segment.p1.y}, {segment.p2.x, segment.p2.y}, end};
        sink->add_bezier(&curve);
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
        const float theta = std::bit_cast<float>(segment.pad0);
        const float sweep = std::bit_cast<float>(segment.pad1);
        const float rotation = std::bit_cast<float>(segment.pad2);
        if (!std::isfinite(theta) || !std::isfinite(sweep) || !std::isfinite(rotation) ||
            !finite_point({segment.p2.x, segment.p2.y}) ||
            !std::isfinite(segment.p3.x) || !std::isfinite(segment.p3.y) ||
            segment.p3.x <= 0.0F || segment.p3.y <= 0.0F ||
            std::abs(sweep) > 2.0F * std::numbers::pi_v<float> + 0.000001F) return status_code::invalid_argument;
        end = {segment.p1.x, segment.p1.y};
        const std::uint32_t pieces = std::abs(sweep) > std::numbers::pi_v<float> ? 2U : 1U;
        for (std::uint32_t index = 1U; index <= pieces; ++index) {
            point_2f endpoint = end;
            if (index != pieces) {
                const float angle = theta + sweep * (static_cast<float>(index) / static_cast<float>(pieces));
                const float x = segment.p3.x * std::cos(angle);
                const float y = segment.p3.y * std::sin(angle);
                endpoint = {segment.p2.x + x * std::cos(rotation) - y * std::sin(rotation),
                    segment.p2.y + x * std::sin(rotation) + y * std::cos(rotation)};
            }
            const arc_segment_f arc{endpoint, {segment.p3.x, segment.p3.y},
                rotation * (180.0F / std::numbers::pi_v<float>),
                sweep < 0.0F ? arc_sweep_direction::counter_clockwise : arc_sweep_direction::clockwise,
                arc_size_kind::small_value};
            sink->add_arc(&arc);
            // An internal split in one smooth arc must not become a
            // caller-selected bevel/miter join.
            if (stroke) sink->set_segment_flags(static_cast<path_segment>(static_cast<std::uint32_t>(flags) |
                    static_cast<std::uint32_t>(path_segment::force_round_line_join)));
        }
        break;
    }
    default:
        return status_code::invalid_argument;
    }
    if (!finite_point(end)) return status_code::invalid_argument;
    return status_code::ok;
}

// A fresh path in the open state, recorded by the caller.
[[nodiscard]] status open_path(std::shared_ptr<path_data>& path) noexcept
{
    try {
        path = std::make_shared<path_data>();
    } catch (const std::bad_alloc&) {
        return status_code::out_of_memory;
    } catch (...) {
        return status_code::failure;
    }
    path->state.store(path_state::open, std::memory_order_release);
    return status_code::ok;
}

} // namespace

path_recorder::path_recorder(std::shared_ptr<path_data> data) noexcept
    : data_(std::move(data))
{
}

void path_recorder::set_fill_mode(fill_mode value) noexcept
{
    const std::lock_guard lock(data_->mutex);
    if (!can_record() || data_->figure_open || !data_->figures.empty()) {
        fail(status_code::wrong_state);
        return;
    }
    if (value != fill_mode::alternate && value != fill_mode::winding) {
        fail(status_code::invalid_argument);
        return;
    }
    data_->mode = value;
}

void path_recorder::set_segment_flags(path_segment value) noexcept
{
    constexpr std::uint32_t supported = 3U;
    const std::lock_guard lock(data_->mutex);
    if (!can_record()) {
        fail(status_code::wrong_state);
        return;
    }
    if ((static_cast<std::uint32_t>(value) & ~supported) != 0U) {
        fail(status_code::invalid_argument);
        return;
    }
    data_->current_flags = value;
}

void path_recorder::begin_figure(
    point_2f start,
    figure_begin begin) noexcept
{
    const std::lock_guard lock(data_->mutex);
    if (!can_record() || data_->figure_open) {
        fail(status_code::wrong_state);
        return;
    }
    if (!finite_point(start) ||
        (begin != figure_begin::filled && begin != figure_begin::hollow)) {
        fail(status_code::invalid_argument);
        return;
    }
    if (data_->figures.size() ==
        (std::numeric_limits<std::uint32_t>::max)()) {
        fail(status_code::out_of_memory);
        return;
    }
    try {
        stored_figure figure{};
        figure.start = start;
        figure.begin = begin;
        figure.end = figure_end::open;
        figure.first_segment =
            static_cast<std::uint32_t>(data_->segments.size());
        data_->figures.push_back(figure);
    } catch (const std::bad_alloc&) {
        fail(status_code::out_of_memory);
        return;
    } catch (...) {
        fail(status_code::failure);
        return;
    }
    data_->figure_open = true;
}

void path_recorder::add_lines(
    const point_2f* points,
    std::uint32_t point_count) noexcept
{
    const std::lock_guard lock(data_->mutex);
    if (!can_record() || !data_->figure_open) {
        fail(status_code::wrong_state);
        return;
    }
    if (point_count != 0U && points == nullptr) {
        fail(status_code::invalid_argument);
        return;
    }
    for (std::uint32_t index = 0U; index < point_count; ++index) {
        if (!finite_point(points[index])) {
            fail(status_code::invalid_argument);
            return;
        }
        stored_segment segment{};
        segment.kind = segment_kind::line;
        segment.flags = data_->current_flags;
        segment.end = points[index];
        if (!append(segment)) {
            return;
        }
    }
}

void path_recorder::add_beziers(
    const cubic_bezier_segment_f* beziers,
    std::uint32_t bezier_count) noexcept
{
    const std::lock_guard lock(data_->mutex);
    if (!can_record() || !data_->figure_open) {
        fail(status_code::wrong_state);
        return;
    }
    if (bezier_count != 0U && beziers == nullptr) {
        fail(status_code::invalid_argument);
        return;
    }
    for (std::uint32_t index = 0U; index < bezier_count; ++index) {
        const auto& value = beziers[index];
        if (!finite_point(value.point1) || !finite_point(value.point2) ||
            !finite_point(value.point3)) {
            fail(status_code::invalid_argument);
            return;
        }
        stored_segment segment{};
        segment.kind = segment_kind::cubic;
        segment.flags = data_->current_flags;
        segment.control1 = value.point1;
        segment.control2 = value.point2;
        segment.end = value.point3;
        if (!append(segment)) {
            return;
        }
    }
}

void path_recorder::end_figure(figure_end end) noexcept
{
    const std::lock_guard lock(data_->mutex);
    if (!can_record() || !data_->figure_open) {
        fail(status_code::wrong_state);
        return;
    }
    if (end != figure_end::open && end != figure_end::closed) {
        fail(status_code::invalid_argument);
        return;
    }
    if (end == figure_end::closed && data_->public_segment_count ==
            (std::numeric_limits<std::uint32_t>::max)()) {
        fail(status_code::out_of_memory);
        return;
    }
    data_->figures.back().end = end;
    if (end == figure_end::closed) {
        ++data_->public_segment_count;
    }
    data_->figure_open = false;
}

status path_recorder::close() noexcept
{
    const std::lock_guard lock(data_->mutex);
    if (data_->state.load(std::memory_order_relaxed) != path_state::open) {
        return status_code::wrong_state;
    }
    if (data_->figure_open) {
        fail(status_code::wrong_state);
    }
    const status result = data_->recording_failure;
    data_->state.store(
        succeeded(result) ? path_state::closed : path_state::failed,
        std::memory_order_release);
    return result;
}

void path_recorder::add_line(point_2f point) noexcept
{
    add_lines(&point, 1U);
}

void path_recorder::add_bezier(
    const cubic_bezier_segment_f* bezier) noexcept
{
    add_beziers(bezier, bezier == nullptr ? 0U : 1U);
    if (bezier == nullptr) {
        const std::lock_guard lock(data_->mutex);
        fail(status_code::invalid_argument);
    }
}

void path_recorder::add_quadratic_bezier(
    const quadratic_bezier_segment_f* bezier) noexcept
{
    add_quadratic_beziers(bezier, bezier == nullptr ? 0U : 1U);
    if (bezier == nullptr) {
        const std::lock_guard lock(data_->mutex);
        fail(status_code::invalid_argument);
    }
}

void path_recorder::add_quadratic_beziers(
    const quadratic_bezier_segment_f* beziers,
    std::uint32_t bezier_count) noexcept
{
    const std::lock_guard lock(data_->mutex);
    if (!can_record() || !data_->figure_open) {
        fail(status_code::wrong_state);
        return;
    }
    if (bezier_count != 0U && beziers == nullptr) {
        fail(status_code::invalid_argument);
        return;
    }
    for (std::uint32_t index = 0U; index < bezier_count; ++index) {
        const auto& value = beziers[index];
        if (!finite_point(value.point1) || !finite_point(value.point2)) {
            fail(status_code::invalid_argument);
            return;
        }
        stored_segment segment{};
        segment.kind = segment_kind::quadratic;
        segment.flags = data_->current_flags;
        segment.control1 = value.point1;
        segment.end = value.point2;
        if (!append(segment)) {
            return;
        }
    }
}

void path_recorder::add_arc(const arc_segment_f* arc) noexcept
{
    const std::lock_guard lock(data_->mutex);
    if (!can_record() || !data_->figure_open) {
        fail(status_code::wrong_state);
        return;
    }
    if (arc == nullptr || !valid_arc_segment(*arc)) {
        fail(status_code::invalid_argument);
        return;
    }
    stored_segment segment{};
    segment.kind = segment_kind::arc;
    segment.flags = data_->current_flags;
    segment.end = arc->point;
    segment.arc = *arc;
    static_cast<void>(append(segment));
}

path_recorder::~path_recorder()
{
    const std::lock_guard lock(data_->mutex);
    if (data_->state.load(std::memory_order_relaxed) == path_state::open) {
        fail(status_code::wrong_state);
        data_->state.store(path_state::failed, std::memory_order_release);
    }
}

bool path_recorder::can_record() const noexcept
{
    return data_->state.load(std::memory_order_relaxed) ==
            path_state::open &&
        succeeded(data_->recording_failure);
}

void path_recorder::fail(status value) noexcept
{
    if (succeeded(data_->recording_failure)) {
        data_->recording_failure = value;
    }
}

bool path_recorder::append(const stored_segment& segment) noexcept
{
    if (data_->segments.size() ==
            (std::numeric_limits<std::uint32_t>::max)() ||
        data_->public_segment_count ==
            (std::numeric_limits<std::uint32_t>::max)()) {
        fail(status_code::out_of_memory);
        return false;
    }
    try {
        data_->segments.push_back(segment);
    } catch (const std::bad_alloc&) {
        fail(status_code::out_of_memory);
        return false;
    } catch (...) {
        fail(status_code::failure);
        return false;
    }
    ++data_->figures.back().segment_count;
    ++data_->public_segment_count;
    return true;
}

bool valid_native_path_arguments(
    std::span<const progpu_native_path_segment> segments,
    fill_mode mode,
    std::span<const std::uint8_t> smooth_joins,
    bool filled) noexcept
{
    return !(segments.size() > (1U << 20U) ||
        (mode != fill_mode::alternate && mode != fill_mode::winding) ||
        (!filled && smooth_joins.size() != segments.size()));
}

status write_native_path(
    std::span<const progpu_native_path_segment> segments,
    fill_mode mode,
    std::span<const std::uint8_t> smooth_joins,
    bool filled,
    bool closed,
    path_sink& output) noexcept
{
    path_sink* const sink = &output;
    sink->set_fill_mode(mode);
    bool open = false;
    point_2f start{}, previous{};
    for (std::size_t segment_index = 0U; segment_index < segments.size(); ++segment_index) {
        const auto& segment = segments[segment_index];
        const point_2f first{segment.p0.x, segment.p0.y};
        if (!finite_point(first)) return status_code::invalid_argument;
        if (!open || !same_point(previous, first)) {
            if (!filled && open) return status_code::invalid_argument;
            if (open) sink->end_figure(figure_end::closed);
            sink->begin_figure(first, filled ? figure_begin::filled : figure_begin::hollow);
            start = first;
            open = true;
        }
        point_2f end{};
        path_segment flags = path_segment::none;
        if (!filled && (segment_index != 0U || closed) &&
            smooth_joins[segment_index == 0U ? segments.size() - 1U : segment_index - 1U] != 0U)
            flags = path_segment::force_round_line_join;
        const status result = append_native_query_segment(*sink, segment, flags, !filled, end);
        if (failed(result)) return result;
        previous = end;
        if (filled && same_point(end, start)) {
            sink->end_figure(figure_end::closed);
            open = false;
        }
    }
    if (open) sink->end_figure(closed ? figure_end::closed : figure_end::open);
    return status_code::ok;
}

bool valid_native_query_arguments(
    std::span<const progpu_native_geometry_query_figure> figures,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> flags) noexcept
{
    if (figures.size() > (1U << 20U) || segments.size() > (1U << 20U) ||
        flags.size() != segments.size()) return false;
    std::size_t next = 0U;
    for (const auto& figure : figures) {
        if (figure.first_segment != next || figure.segment_count > segments.size() - next ||
            figure.flags > 3U || !finite_point({figure.start.x, figure.start.y})) return false;
        next += figure.segment_count;
    }
    return next == segments.size();
}

status write_native_query_path(
    std::span<const progpu_native_geometry_query_figure> figures,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> flags,
    path_sink& output) noexcept
{
    path_sink* const sink = &output;
    sink->set_fill_mode(fill_mode::winding);
    for (const auto& figure : figures) {
        point_2f current{figure.start.x, figure.start.y};
        sink->begin_figure(current, (figure.flags & 2U) != 0U ? figure_begin::filled : figure_begin::hollow);
        for (std::size_t index = figure.first_segment; index < figure.first_segment + figure.segment_count; ++index) {
            const auto& segment = segments[index];
            if (flags[index] > 3U || !same_point(current, {segment.p0.x, segment.p0.y})) return status_code::invalid_argument;
            const auto state = static_cast<path_segment>(
                ((flags[index] & 1U) == 0U ? static_cast<std::uint32_t>(path_segment::force_unstroked) : 0U) |
                ((flags[index] & 2U) != 0U ? static_cast<std::uint32_t>(path_segment::force_round_line_join) : 0U));
            const status result = append_native_query_segment(*sink, segment, state, true, current);
            if (failed(result)) return result;
        }
        if ((figure.flags & 1U) != 0U && figure.segment_count != 0U &&
            !same_point(current, {figure.start.x, figure.start.y})) return status_code::invalid_argument;
        sink->end_figure((figure.flags & 1U) != 0U ? figure_end::closed : figure_end::open);
    }
    return status_code::ok;
}

namespace {

[[nodiscard]] status build_native_path(
    std::span<const progpu_native_path_segment> segments,
    fill_mode mode,
    std::span<const std::uint8_t> smooth_joins,
    bool filled,
    bool closed,
    std::shared_ptr<path_data>& path) noexcept
{
    if (!valid_native_path_arguments(segments, mode, smooth_joins, filled))
        return status_code::invalid_argument;
    std::shared_ptr<path_data> data;
    status result = open_path(data);
    if (failed(result)) return result;
    path_recorder sink(data);
    result = write_native_path(segments, mode, smooth_joins, filled, closed, sink);
    if (failed(result)) return result;
    result = sink.close();
    if (failed(result)) return result;
    path = std::move(data);
    return status_code::ok;
}

} // namespace

status build_native_fill_path(
    std::span<const progpu_native_path_segment> segments,
    fill_mode mode,
    std::shared_ptr<path_data>& path) noexcept
{
    return build_native_path(segments, mode, {}, true, true, path);
}

status build_native_stroke_path(
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> smooth_joins,
    bool closed,
    std::shared_ptr<path_data>& path) noexcept
{
    return build_native_path(segments, fill_mode::winding, smooth_joins,
        false, closed, path);
}

status build_native_query_path(
    std::span<const progpu_native_geometry_query_figure> figures,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> flags,
    std::shared_ptr<path_data>& path) noexcept
{
    if (!valid_native_query_arguments(figures, segments, flags))
        return status_code::invalid_argument;
    std::shared_ptr<path_data> data;
    status result = open_path(data);
    if (failed(result)) return result;
    path_recorder sink(data);
    result = write_native_query_path(figures, segments, flags, sink);
    if (failed(result)) return result;
    result = sink.close();
    if (failed(result)) return result;
    path = std::move(data);
    return status_code::ok;
}

} // namespace progpu::native::geometry
