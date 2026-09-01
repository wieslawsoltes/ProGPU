#include "progpu_native_direct2d_path.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <utility>
#include <vector>

namespace progpu::native::direct2d::compat::detail {
namespace {

enum class path_state : std::uint32_t {
    fresh,
    open,
    closed,
    failed
};

enum class segment_kind : std::uint8_t {
    line,
    cubic,
    quadratic,
    arc
};

struct stored_segment final {
    segment_kind kind = segment_kind::line;
    path_segment flags = path_segment::none;
    point_2f end{};
    point_2f control1{};
    point_2f control2{};
    arc_segment arc{};
};

struct stored_figure final {
    point_2f start{};
    figure_begin begin = figure_begin::filled;
    figure_end end = figure_end::open;
    std::uint32_t first_segment = 0U;
    std::uint32_t segment_count = 0U;
};

struct path_data final {
    mutable std::mutex mutex;
    std::vector<stored_figure> figures;
    std::vector<stored_segment> segments;
    std::atomic<path_state> state{path_state::fresh};
    fill_mode mode = fill_mode::alternate;
    path_segment current_flags = path_segment::none;
    std::uint32_t public_segment_count = 0U;
    com::result recording_failure = com::ok;
    bool figure_open = false;
};

[[nodiscard]] bool finite_point(point_2f point) noexcept
{
    return std::isfinite(point.x) && std::isfinite(point.y);
}

[[nodiscard]] bool valid_tolerance(float tolerance) noexcept
{
    return std::isfinite(tolerance) && tolerance > 0.0F;
}

[[nodiscard]] bool valid_arc(const arc_segment& arc) noexcept
{
    return core::valid_arc_segment(arc);
}

[[nodiscard]] com::result transform_point(
    point_2f point,
    const matrix_3x2_f* transform,
    point_2f* result) noexcept
{
    if (result == nullptr) {
        return com::pointer_error;
    }
    *result = {};
    if (!finite_point(point) || !core::valid_transform(transform)) {
        return com::invalid_argument;
    }
    const matrix_3x2_f identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const auto& matrix = transform == nullptr ? identity : *transform;
    const double x = static_cast<double>(point.x) * matrix.m11 +
        static_cast<double>(point.y) * matrix.m21 + matrix.m31;
    const double y = static_cast<double>(point.x) * matrix.m12 +
        static_cast<double>(point.y) * matrix.m22 + matrix.m32;
    constexpr double maximum =
        static_cast<double>((std::numeric_limits<float>::max)());
    if (!std::isfinite(x) || !std::isfinite(y) ||
        std::abs(x) > maximum || std::abs(y) > maximum) {
        return com::invalid_argument;
    }
    *result = {static_cast<float>(x), static_cast<float>(y)};
    return com::ok;
}

[[nodiscard]] double cubic_coordinate(
    double p0,
    double p1,
    double p2,
    double p3,
    double t) noexcept
{
    const double one_minus_t = 1.0 - t;
    return one_minus_t * one_minus_t * one_minus_t * p0 +
        3.0 * one_minus_t * one_minus_t * t * p1 +
        3.0 * one_minus_t * t * t * p2 + t * t * t * p3;
}

void include_cubic_bounds(
    point_2f start,
    point_2f control1,
    point_2f control2,
    point_2f end,
    rectangle_f& bounds) noexcept
{
    bounds.left = std::min(bounds.left, std::min(start.x, end.x));
    bounds.top = std::min(bounds.top, std::min(start.y, end.y));
    bounds.right = std::max(bounds.right, std::max(start.x, end.x));
    bounds.bottom = std::max(bounds.bottom, std::max(start.y, end.y));
    const auto include_axis = [&](double p0,
                                  double p1,
                                  double p2,
                                  double p3,
                                  bool x_axis) {
        const double quadratic = 3.0 * (-p0 + 3.0 * p1 - 3.0 * p2 + p3);
        const double linear = 2.0 * (3.0 * p0 - 6.0 * p1 + 3.0 * p2);
        const double constant = -3.0 * p0 + 3.0 * p1;
        const double scale = std::max(
            {1.0, std::abs(quadratic), std::abs(linear),
                std::abs(constant)});
        const double epsilon =
            (std::numeric_limits<double>::epsilon)() * scale * 16.0;
        std::array<double, 2U> roots{};
        std::uint32_t count = 0U;
        if (std::abs(quadratic) <= epsilon) {
            if (std::abs(linear) > epsilon) {
                roots[count++] = -constant / linear;
            }
        } else {
            const double discriminant =
                linear * linear - 4.0 * quadratic * constant;
            if (discriminant >= 0.0) {
                const double root = std::sqrt(discriminant);
                roots[count++] = (-linear + root) / (2.0 * quadratic);
                roots[count++] = (-linear - root) / (2.0 * quadratic);
            }
        }
        for (std::uint32_t index = 0U; index < count; ++index) {
            const double t = roots[index];
            if (!(t > 0.0 && t < 1.0)) {
                continue;
            }
            const float value = static_cast<float>(cubic_coordinate(
                p0, p1, p2, p3, t));
            if (x_axis) {
                bounds.left = std::min(bounds.left, value);
                bounds.right = std::max(bounds.right, value);
            } else {
                bounds.top = std::min(bounds.top, value);
                bounds.bottom = std::max(bounds.bottom, value);
            }
        }
    };
    include_axis(start.x, control1.x, control2.x, end.x, true);
    include_axis(start.y, control1.y, control2.y, end.y, false);
}

[[nodiscard]] bool same_point(point_2f left, point_2f right) noexcept
{
    return left.x == right.x && left.y == right.y;
}

[[nodiscard]] double point_line_distance_squared(
    point_2f point,
    point_2f start,
    point_2f end) noexcept
{
    const double dx = static_cast<double>(end.x) - start.x;
    const double dy = static_cast<double>(end.y) - start.y;
    const double length_squared = dx * dx + dy * dy;
    if (length_squared == 0.0) {
        const double px = static_cast<double>(point.x) - start.x;
        const double py = static_cast<double>(point.y) - start.y;
        return px * px + py * py;
    }
    const double cross =
        (static_cast<double>(point.x) - start.x) * dy -
        (static_cast<double>(point.y) - start.y) * dx;
    return cross * cross / length_squared;
}

template<typename Callback>
[[nodiscard]] bool flatten_cubic(
    point_2f start,
    point_2f control1,
    point_2f control2,
    point_2f end,
    double tolerance_squared,
    std::uint32_t depth,
    Callback& callback)
{
    if (depth == 20U ||
        (point_line_distance_squared(control1, start, end) <=
                tolerance_squared &&
            point_line_distance_squared(control2, start, end) <=
                tolerance_squared)) {
        return callback(start, end);
    }
    const point_2f p01{
        (start.x + control1.x) * 0.5F,
        (start.y + control1.y) * 0.5F};
    const point_2f p12{
        (control1.x + control2.x) * 0.5F,
        (control1.y + control2.y) * 0.5F};
    const point_2f p23{
        (control2.x + end.x) * 0.5F,
        (control2.y + end.y) * 0.5F};
    const point_2f p012{
        (p01.x + p12.x) * 0.5F,
        (p01.y + p12.y) * 0.5F};
    const point_2f p123{
        (p12.x + p23.x) * 0.5F,
        (p12.y + p23.y) * 0.5F};
    const point_2f midpoint{
        (p012.x + p123.x) * 0.5F,
        (p012.y + p123.y) * 0.5F};
    return flatten_cubic(
               start,
               p01,
               p012,
               midpoint,
               tolerance_squared,
               depth + 1U,
               callback) &&
        flatten_cubic(
            midpoint,
            p123,
            p23,
            end,
            tolerance_squared,
            depth + 1U,
            callback);
}

template<typename BeginCallback, typename LineCallback,
    typename CubicCallback, typename EndCallback>
[[nodiscard]] bool visit_path(
    const path_data& data,
    const matrix_3x2_f* transform,
    bool flatten,
    float tolerance,
    BeginCallback&& begin_callback,
    LineCallback&& line_callback,
    CubicCallback&& cubic_callback,
    EndCallback&& end_callback)
{
    const double tolerance_squared =
        static_cast<double>(tolerance) * tolerance;
    std::uint32_t public_segment_base = 0U;
    for (std::size_t figure_offset = 0U;
         figure_offset < data.figures.size();
         ++figure_offset) {
        const std::uint32_t figure_index =
            static_cast<std::uint32_t>(figure_offset);
        const auto& figure = data.figures[figure_offset];
        point_2f transformed_start{};
        if (com::failed(transform_point(
                figure.start, transform, &transformed_start)) ||
            !begin_callback(transformed_start, figure_index, figure)) {
            return false;
        }
        point_2f current_source = figure.start;
        point_2f current_target = transformed_start;
        for (std::uint32_t local_index = 0U;
             local_index < figure.segment_count;
             ++local_index) {
            const auto& segment =
                data.segments[figure.first_segment + local_index];
            const std::uint32_t segment_index =
                public_segment_base + local_index;
            point_2f end_target{};
            if (com::failed(transform_point(
                    segment.end, transform, &end_target))) {
                return false;
            }
            if (segment.kind == segment_kind::line ||
                (segment.kind == segment_kind::arc &&
                    (segment.arc.size.width == 0.0F ||
                        segment.arc.size.height == 0.0F))) {
                if (!line_callback(
                        current_target,
                        end_target,
                        segment_index,
                        figure_index,
                        segment.flags)) {
                    return false;
                }
            } else {
                std::array<core::cubic_bezier_segment_f, 4U> cubics{};
                std::uint32_t cubic_count = 1U;
                if (segment.kind == segment_kind::cubic ||
                    segment.kind == segment_kind::quadratic) {
                    point_2f control1 = segment.control1;
                    point_2f control2 = segment.control2;
                    if (segment.kind == segment_kind::quadratic) {
                        control1 = {
                            current_source.x +
                                (segment.control1.x - current_source.x) *
                                    (2.0F / 3.0F),
                            current_source.y +
                                (segment.control1.y - current_source.y) *
                                    (2.0F / 3.0F)};
                        control2 = {
                            segment.end.x +
                                (segment.control1.x - segment.end.x) *
                                    (2.0F / 3.0F),
                            segment.end.y +
                                (segment.control1.y - segment.end.y) *
                                    (2.0F / 3.0F)};
                    }
                    cubics[0U] = {control1, control2, segment.end};
                } else if (com::failed(core::arc_to_cubics(
                               current_source,
                               segment.arc,
                               &cubics,
                               &cubic_count))) {
                    return false;
                }
                point_2f cubic_start = current_target;
                for (std::uint32_t cubic_index = 0U;
                     cubic_index < cubic_count;
                     ++cubic_index) {
                    point_2f control1_target{};
                    point_2f control2_target{};
                    point_2f cubic_end_target{};
                    if (com::failed(transform_point(
                            cubics[cubic_index].point1,
                            transform,
                            &control1_target)) ||
                        com::failed(transform_point(
                            cubics[cubic_index].point2,
                            transform,
                            &control2_target)) ||
                        com::failed(transform_point(
                            cubics[cubic_index].point3,
                            transform,
                            &cubic_end_target))) {
                        return false;
                    }
                    if (flatten) {
                        auto callback = [&](point_2f line_start,
                                            point_2f line_end) {
                            return line_callback(
                                line_start,
                                line_end,
                                segment_index,
                                figure_index,
                                segment.flags);
                        };
                        if (!flatten_cubic(
                                cubic_start,
                                control1_target,
                                control2_target,
                                cubic_end_target,
                                tolerance_squared,
                                0U,
                                callback)) {
                            return false;
                        }
                    } else if (!cubic_callback(
                                   cubic_start,
                                   control1_target,
                                   control2_target,
                                   cubic_end_target,
                                   segment_index,
                                   figure_index,
                                   segment.flags)) {
                        return false;
                    }
                    cubic_start = cubic_end_target;
                }
            }
            current_source = segment.end;
            current_target = end_target;
        }
        if (!end_callback(
                current_target,
                transformed_start,
                figure_index,
                figure)) {
            return false;
        }
        public_segment_base += figure.segment_count;
        if (figure.end == figure_end::closed) {
            ++public_segment_base;
        }
    }
    return true;
}

struct flat_edge final {
    point_2f start{};
    point_2f end{};
    std::uint32_t segment_index = 0U;
    std::uint32_t figure_index = 0U;
};

class portable_geometry_sink final : public geometry_sink {
public:
    explicit portable_geometry_sink(std::shared_ptr<path_data> data) noexcept
        : data_(std::move(data))
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
            com::guid_equal(
                interface_id, simplified_geometry_sink_interface_id) ||
            com::guid_equal(interface_id, geometry_sink_interface_id)) {
            *value = static_cast<geometry_sink*>(this);
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

    void PROGPU_NATIVE_COM_CALL SetFillMode(fill_mode value)
        noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record() || data_->figure_open || !data_->figures.empty()) {
            fail(wrong_state);
            return;
        }
        if (value != fill_mode::alternate && value != fill_mode::winding) {
            fail(com::invalid_argument);
            return;
        }
        data_->mode = value;
    }

    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(path_segment value)
        noexcept override
    {
        constexpr std::uint32_t supported = 3U;
        const std::lock_guard lock(data_->mutex);
        if (!can_record()) {
            fail(wrong_state);
            return;
        }
        if ((static_cast<std::uint32_t>(value) & ~supported) != 0U) {
            fail(com::invalid_argument);
            return;
        }
        data_->current_flags = value;
    }

    void PROGPU_NATIVE_COM_CALL BeginFigure(
        point_2f start,
        figure_begin begin) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record() || data_->figure_open) {
            fail(wrong_state);
            return;
        }
        if (!finite_point(start) ||
            (begin != figure_begin::filled && begin != figure_begin::hollow)) {
            fail(com::invalid_argument);
            return;
        }
        if (data_->figures.size() ==
            (std::numeric_limits<std::uint32_t>::max)()) {
            fail(com::out_of_memory);
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
            fail(com::out_of_memory);
            return;
        } catch (...) {
            fail(failure);
            return;
        }
        data_->figure_open = true;
    }

    void PROGPU_NATIVE_COM_CALL AddLines(
        const point_2f* points,
        std::uint32_t point_count) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record() || !data_->figure_open) {
            fail(wrong_state);
            return;
        }
        if (point_count != 0U && points == nullptr) {
            fail(com::invalid_argument);
            return;
        }
        for (std::uint32_t index = 0U; index < point_count; ++index) {
            if (!finite_point(points[index])) {
                fail(com::invalid_argument);
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

    void PROGPU_NATIVE_COM_CALL AddBeziers(
        const bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record() || !data_->figure_open) {
            fail(wrong_state);
            return;
        }
        if (bezier_count != 0U && beziers == nullptr) {
            fail(com::invalid_argument);
            return;
        }
        for (std::uint32_t index = 0U; index < bezier_count; ++index) {
            const auto& value = beziers[index];
            if (!finite_point(value.point1) || !finite_point(value.point2) ||
                !finite_point(value.point3)) {
                fail(com::invalid_argument);
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

    void PROGPU_NATIVE_COM_CALL EndFigure(figure_end end) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record() || !data_->figure_open) {
            fail(wrong_state);
            return;
        }
        if (end != figure_end::open && end != figure_end::closed) {
            fail(com::invalid_argument);
            return;
        }
        if (end == figure_end::closed && data_->public_segment_count ==
                (std::numeric_limits<std::uint32_t>::max)()) {
            fail(com::out_of_memory);
            return;
        }
        data_->figures.back().end = end;
        if (end == figure_end::closed) {
            ++data_->public_segment_count;
        }
        data_->figure_open = false;
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (data_->state.load(std::memory_order_relaxed) != path_state::open) {
            return wrong_state;
        }
        if (data_->figure_open) {
            fail(wrong_state);
        }
        const com::result result = data_->recording_failure;
        data_->state.store(
            com::succeeded(result) ? path_state::closed : path_state::failed,
            std::memory_order_release);
        return result;
    }

    void PROGPU_NATIVE_COM_CALL AddLine(point_2f point) noexcept override
    {
        AddLines(&point, 1U);
    }

    void PROGPU_NATIVE_COM_CALL AddBezier(
        const bezier_segment* bezier) noexcept override
    {
        AddBeziers(bezier, bezier == nullptr ? 0U : 1U);
        if (bezier == nullptr) {
            const std::lock_guard lock(data_->mutex);
            fail(com::invalid_argument);
        }
    }

    void PROGPU_NATIVE_COM_CALL AddQuadraticBezier(
        const quadratic_bezier_segment* bezier) noexcept override
    {
        AddQuadraticBeziers(bezier, bezier == nullptr ? 0U : 1U);
        if (bezier == nullptr) {
            const std::lock_guard lock(data_->mutex);
            fail(com::invalid_argument);
        }
    }

    void PROGPU_NATIVE_COM_CALL AddQuadraticBeziers(
        const quadratic_bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record() || !data_->figure_open) {
            fail(wrong_state);
            return;
        }
        if (bezier_count != 0U && beziers == nullptr) {
            fail(com::invalid_argument);
            return;
        }
        for (std::uint32_t index = 0U; index < bezier_count; ++index) {
            const auto& value = beziers[index];
            if (!finite_point(value.point1) || !finite_point(value.point2)) {
                fail(com::invalid_argument);
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

    void PROGPU_NATIVE_COM_CALL AddArc(const arc_segment* arc)
        noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record() || !data_->figure_open) {
            fail(wrong_state);
            return;
        }
        if (arc == nullptr || !valid_arc(*arc)) {
            fail(com::invalid_argument);
            return;
        }
        stored_segment segment{};
        segment.kind = segment_kind::arc;
        segment.flags = data_->current_flags;
        segment.end = arc->point;
        segment.arc = *arc;
        static_cast<void>(append(segment));
    }

private:
    friend class com::atomic_reference_count<portable_geometry_sink>;
    ~portable_geometry_sink()
    {
        const std::lock_guard lock(data_->mutex);
        if (data_->state.load(std::memory_order_relaxed) == path_state::open) {
            fail(wrong_state);
            data_->state.store(path_state::failed, std::memory_order_release);
        }
    }

    [[nodiscard]] bool can_record() const noexcept
    {
        return data_->state.load(std::memory_order_relaxed) ==
                path_state::open &&
            com::succeeded(data_->recording_failure);
    }

    void fail(com::result value) noexcept
    {
        if (com::succeeded(data_->recording_failure)) {
            data_->recording_failure = value;
        }
    }

    [[nodiscard]] bool append(const stored_segment& segment) noexcept
    {
        if (data_->segments.size() ==
                (std::numeric_limits<std::uint32_t>::max)() ||
            data_->public_segment_count ==
                (std::numeric_limits<std::uint32_t>::max)()) {
            fail(com::out_of_memory);
            return false;
        }
        try {
            data_->segments.push_back(segment);
        } catch (const std::bad_alloc&) {
            fail(com::out_of_memory);
            return false;
        } catch (...) {
            fail(failure);
            return false;
        }
        ++data_->figures.back().segment_count;
        ++data_->public_segment_count;
        return true;
    }

    com::atomic_reference_count<portable_geometry_sink> reference_count_;
    std::shared_ptr<path_data> data_;
};

class portable_path_geometry final : public path_geometry {
public:
    explicit portable_path_geometry(factory* owner)
        : owner_(owner), data_(std::make_shared<path_data>())
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
            com::guid_equal(interface_id, path_geometry_interface_id)) {
            *value = static_cast<path_geometry*>(this);
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
        if (bounds == nullptr) {
            return com::pointer_error;
        }
        *bounds = {};
        if (!closed()) {
            return wrong_state;
        }
        if (!core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        rectangle_f result{
            (std::numeric_limits<float>::max)(),
            (std::numeric_limits<float>::max)(),
            -(std::numeric_limits<float>::max)(),
            -(std::numeric_limits<float>::max)()};
        bool has_bounds = false;
        for (const auto& figure : data_->figures) {
            point_2f current_source = figure.start;
            point_2f current{};
            if (com::failed(transform_point(
                    figure.start, world_transform, &current))) {
                return com::invalid_argument;
            }
            result.left = std::min(result.left, current.x);
            result.top = std::min(result.top, current.y);
            result.right = std::max(result.right, current.x);
            result.bottom = std::max(result.bottom, current.y);
            has_bounds = true;
            for (std::uint32_t offset = 0U;
                 offset < figure.segment_count;
                 ++offset) {
                const auto& segment =
                    data_->segments[figure.first_segment + offset];
                point_2f end{};
                if (com::failed(transform_point(
                        segment.end, world_transform, &end))) {
                    return com::invalid_argument;
                }
                if (segment.kind == segment_kind::line ||
                    (segment.kind == segment_kind::arc &&
                        (segment.arc.size.width == 0.0F ||
                            segment.arc.size.height == 0.0F))) {
                    result.left = std::min(result.left, end.x);
                    result.top = std::min(result.top, end.y);
                    result.right = std::max(result.right, end.x);
                    result.bottom = std::max(result.bottom, end.y);
                } else if (segment.kind == segment_kind::cubic ||
                           segment.kind == segment_kind::quadratic) {
                    point_2f control1_source = segment.control1;
                    point_2f control2_source = segment.control2;
                    if (segment.kind == segment_kind::quadratic) {
                        control1_source = {
                            current_source.x +
                                (segment.control1.x - current_source.x) *
                                    (2.0F / 3.0F),
                            current_source.y +
                                (segment.control1.y - current_source.y) *
                                    (2.0F / 3.0F)};
                        control2_source = {
                            segment.end.x +
                                (segment.control1.x - segment.end.x) *
                                    (2.0F / 3.0F),
                            segment.end.y +
                                (segment.control1.y - segment.end.y) *
                                    (2.0F / 3.0F)};
                    }
                    point_2f control1{};
                    point_2f control2{};
                    if (com::failed(transform_point(
                            control1_source, world_transform, &control1)) ||
                        com::failed(transform_point(
                            control2_source, world_transform, &control2))) {
                        return com::invalid_argument;
                    }
                    include_cubic_bounds(
                        current, control1, control2, end, result);
                } else {
                    std::array<core::cubic_bezier_segment_f, 4U> cubics{};
                    std::uint32_t cubic_count = 0U;
                    const com::result arc_status = core::arc_to_cubics(
                        current_source,
                        segment.arc,
                        &cubics,
                        &cubic_count);
                    if (com::failed(arc_status)) {
                        return arc_status;
                    }
                    point_2f cubic_start = current;
                    for (std::uint32_t cubic_index = 0U;
                         cubic_index < cubic_count;
                         ++cubic_index) {
                        point_2f control1{};
                        point_2f control2{};
                        point_2f cubic_end{};
                        if (com::failed(transform_point(
                                cubics[cubic_index].point1,
                                world_transform,
                                &control1)) ||
                            com::failed(transform_point(
                                cubics[cubic_index].point2,
                                world_transform,
                                &control2)) ||
                            com::failed(transform_point(
                                cubics[cubic_index].point3,
                                world_transform,
                                &cubic_end))) {
                            return com::invalid_argument;
                        }
                        include_cubic_bounds(
                            cubic_start,
                            control1,
                            control2,
                            cubic_end,
                            result);
                        cubic_start = cubic_end;
                    }
                }
                current_source = segment.end;
                current = end;
            }
        }
        if (has_bounds) {
            *bounds = result;
        }
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetWidenedBounds(
        float,
        stroke_style*,
        const matrix_3x2_f*,
        float,
        rectangle_f* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return com::pointer_error;
        }
        *bounds = {};
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL StrokeContainsPoint(
        point_2f,
        float,
        stroke_style*,
        const matrix_3x2_f*,
        float,
        std::int32_t* contains) const noexcept override
    {
        if (contains == nullptr) {
            return com::pointer_error;
        }
        *contains = 0;
        return not_implemented;
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
        *contains = 0;
        if (!closed()) {
            return wrong_state;
        }
        if (!finite_point(point) ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        std::vector<flat_edge> edges;
        const com::result edge_status = collect_flat_edges(
            world_transform,
            flattening_tolerance,
            true,
            edges);
        if (com::failed(edge_status)) {
            return edge_status;
        }
        std::int64_t winding = 0;
        bool alternate = false;
        bool boundary = false;
        const double tolerance_squared =
            static_cast<double>(flattening_tolerance) *
            flattening_tolerance;
        for (const auto& edge : edges) {
            if (data_->figures[edge.figure_index].begin !=
                figure_begin::filled) {
                continue;
            }
            const double dx =
                static_cast<double>(edge.end.x) - edge.start.x;
            const double dy =
                static_cast<double>(edge.end.y) - edge.start.y;
            const double px =
                static_cast<double>(point.x) - edge.start.x;
            const double py =
                static_cast<double>(point.y) - edge.start.y;
            const double projection = px * dx + py * dy;
            const double length_squared = dx * dx + dy * dy;
            if (projection >= 0.0 && projection <= length_squared &&
                point_line_distance_squared(
                    point, edge.start, edge.end) <= tolerance_squared) {
                boundary = true;
                break;
            }
            const bool upward = edge.start.y <= point.y &&
                edge.end.y > point.y;
            const bool downward = edge.start.y > point.y &&
                edge.end.y <= point.y;
            if (!upward && !downward) {
                continue;
            }
            const double cross = dx * py - dy * px;
            if ((upward && cross > 0.0) ||
                (downward && cross < 0.0)) {
                alternate = !alternate;
                winding += upward ? 1 : -1;
            }
        }
        *contains = boundary ||
                (data_->mode == fill_mode::alternate
                    ? alternate
                    : winding != 0)
            ? 1
            : 0;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CompareWithGeometry(
        geometry*,
        const matrix_3x2_f*,
        float,
        geometry_relation* relation) const noexcept override
    {
        if (relation == nullptr) {
            return com::pointer_error;
        }
        *relation = geometry_relation::unknown;
        return not_implemented;
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
        if (!closed()) {
            return wrong_state;
        }
        if ((option != geometry_simplification_option::cubics_and_lines &&
                option != geometry_simplification_option::lines) ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        sink->SetFillMode(data_->mode);
        path_segment current_flags = static_cast<path_segment>(
            (std::numeric_limits<std::uint32_t>::max)());
        const bool flatten =
            option == geometry_simplification_option::lines;
        const bool visited = visit_path(
            *data_,
            world_transform,
            flatten,
            flattening_tolerance,
            [&](point_2f start,
                std::uint32_t,
                const stored_figure& figure) {
                sink->BeginFigure(start, figure.begin);
                return true;
            },
            [&](point_2f,
                point_2f end,
                std::uint32_t,
                std::uint32_t,
                path_segment flags) {
                if (current_flags != flags) {
                    sink->SetSegmentFlags(flags);
                    current_flags = flags;
                }
                sink->AddLines(&end, 1U);
                return true;
            },
            [&](point_2f,
                point_2f control1,
                point_2f control2,
                point_2f end,
                std::uint32_t,
                std::uint32_t,
                path_segment flags) {
                if (current_flags != flags) {
                    sink->SetSegmentFlags(flags);
                    current_flags = flags;
                }
                const bezier_segment bezier{control1, control2, end};
                sink->AddBeziers(&bezier, 1U);
                return true;
            },
            [&](point_2f,
                point_2f,
                std::uint32_t,
                const stored_figure& figure) {
                sink->EndFigure(figure.end);
                return true;
            });
        return visited ? com::ok : com::invalid_argument;
    }

    com::result PROGPU_NATIVE_COM_CALL Tessellate(
        const matrix_3x2_f*,
        float,
        tessellation_sink*) const noexcept override
    {
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL CombineWithGeometry(
        geometry*, combine_mode, const matrix_3x2_f*, float,
        simplified_geometry_sink*) const noexcept override
    {
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL Outline(
        const matrix_3x2_f*, float, simplified_geometry_sink*) const
        noexcept override
    {
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeArea(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* area) const noexcept override
    {
        if (area == nullptr) {
            return com::pointer_error;
        }
        *area = 0.0F;
        if (!closed()) {
            return wrong_state;
        }
        if (!valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        std::vector<flat_edge> edges;
        const com::result edge_status = collect_flat_edges(
            world_transform,
            flattening_tolerance,
            true,
            edges);
        if (com::failed(edge_status)) {
            return edge_status;
        }
        try {
            std::vector<double> signed_areas(
                data_->figures.size(), 0.0);
            for (const auto& edge : edges) {
                if (data_->figures[edge.figure_index].begin ==
                    figure_begin::filled) {
                    signed_areas[edge.figure_index] +=
                        static_cast<double>(edge.start.x) * edge.end.y -
                        static_cast<double>(edge.start.y) * edge.end.x;
                }
            }
            double result = 0.0;
            if (data_->mode == fill_mode::alternate) {
                for (double value : signed_areas) {
                    result += std::abs(value) * 0.5;
                }
            } else {
                for (double value : signed_areas) {
                    result += value * 0.5;
                }
                result = std::abs(result);
            }
            if (!std::isfinite(result) ||
                result > (std::numeric_limits<float>::max)()) {
                return com::invalid_argument;
            }
            *area = static_cast<float>(result);
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL ComputeLength(
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        float* length) const noexcept override
    {
        if (length == nullptr) {
            return com::pointer_error;
        }
        *length = 0.0F;
        if (!closed()) {
            return wrong_state;
        }
        if (!valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        std::vector<flat_edge> edges;
        const com::result edge_status = collect_flat_edges(
            world_transform,
            flattening_tolerance,
            false,
            edges);
        if (com::failed(edge_status)) {
            return edge_status;
        }
        double result = 0.0;
        for (const auto& edge : edges) {
            result += std::hypot(
                static_cast<double>(edge.end.x) - edge.start.x,
                static_cast<double>(edge.end.y) - edge.start.y);
        }
        if (!std::isfinite(result) ||
            result > (std::numeric_limits<float>::max)()) {
            return com::invalid_argument;
        }
        *length = static_cast<float>(result);
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL ComputePointAtLength(
        float length,
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        point_2f* point,
        point_2f* tangent) const noexcept override
    {
        if (point == nullptr && tangent == nullptr) {
            return com::pointer_error;
        }
        if (point != nullptr) {
            *point = {};
        }
        if (tangent != nullptr) {
            *tangent = {};
        }
        if (!closed()) {
            return wrong_state;
        }
        if (!std::isfinite(length) ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        std::vector<flat_edge> edges;
        const com::result edge_status = collect_flat_edges(
            world_transform,
            flattening_tolerance,
            false,
            edges);
        return com::failed(edge_status)
            ? edge_status
            : point_at_length(
                edges,
                std::max(length, 0.0F),
                point,
                tangent);
    }

    com::result PROGPU_NATIVE_COM_CALL Widen(
        float,
        stroke_style*,
        const matrix_3x2_f*,
        float,
        simplified_geometry_sink*) const noexcept override
    {
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL Open(
        geometry_sink** sink) noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        *sink = nullptr;
        const std::lock_guard lock(data_->mutex);
        if (data_->state.load(std::memory_order_relaxed) != path_state::fresh) {
            return wrong_state;
        }
        auto* created = new (std::nothrow) portable_geometry_sink(data_);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        data_->state.store(path_state::open, std::memory_order_release);
        *sink = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL Stream(
        geometry_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
        if (!closed()) {
            return wrong_state;
        }
        sink->SetFillMode(data_->mode);
        path_segment current_flags = static_cast<path_segment>(
            (std::numeric_limits<std::uint32_t>::max)());
        for (const auto& figure : data_->figures) {
            sink->BeginFigure(figure.start, figure.begin);
            for (std::uint32_t offset = 0U;
                 offset < figure.segment_count;
                 ++offset) {
                const auto& segment =
                    data_->segments[figure.first_segment + offset];
                if (segment.flags != current_flags) {
                    sink->SetSegmentFlags(segment.flags);
                    current_flags = segment.flags;
                }
                switch (segment.kind) {
                case segment_kind::line:
                    sink->AddLine(segment.end);
                    break;
                case segment_kind::cubic: {
                    const bezier_segment value{
                        segment.control1, segment.control2, segment.end};
                    sink->AddBezier(&value);
                    break;
                }
                case segment_kind::quadratic: {
                    const quadratic_bezier_segment value{
                        segment.control1, segment.end};
                    sink->AddQuadraticBezier(&value);
                    break;
                }
                case segment_kind::arc:
                    sink->AddArc(&segment.arc);
                    break;
                }
            }
            sink->EndFigure(figure.end);
        }
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetSegmentCount(
        std::uint32_t* count) const noexcept override
    {
        if (count == nullptr) {
            return com::pointer_error;
        }
        *count = 0U;
        if (!closed()) {
            return wrong_state;
        }
        *count = data_->public_segment_count;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetFigureCount(
        std::uint32_t* count) const noexcept override
    {
        if (count == nullptr) {
            return com::pointer_error;
        }
        *count = 0U;
        if (!closed()) {
            return wrong_state;
        }
        *count = static_cast<std::uint32_t>(data_->figures.size());
        return com::ok;
    }

private:
    [[nodiscard]] com::result collect_flat_edges(
        const matrix_3x2_f* transform,
        float tolerance,
        bool close_open_filled_figures,
        std::vector<flat_edge>& edges) const noexcept
    {
        edges.clear();
        if (!closed()) {
            return wrong_state;
        }
        try {
            edges.reserve(
                data_->segments.size() * 2U + data_->figures.size());
            std::uint32_t public_segment_base = 0U;
            const bool visited = visit_path(
                *data_,
                transform,
                true,
                tolerance,
                [](point_2f,
                   std::uint32_t,
                   const stored_figure&) { return true; },
                [&](point_2f start,
                    point_2f end,
                    std::uint32_t segment_index,
                    std::uint32_t figure_index,
                    path_segment) {
                    edges.push_back(
                        {start, end, segment_index, figure_index});
                    return true;
                },
                [](point_2f,
                   point_2f,
                   point_2f,
                   point_2f,
                   std::uint32_t,
                   std::uint32_t,
                   path_segment) { return true; },
                [&](point_2f current,
                    point_2f start,
                    std::uint32_t figure_index,
                    const stored_figure& figure) {
                    const bool close =
                        figure.end == figure_end::closed ||
                        (close_open_filled_figures &&
                            figure.begin == figure_begin::filled);
                    if (close && !same_point(current, start)) {
                        const std::uint32_t segment_index =
                            public_segment_base + figure.segment_count;
                        edges.push_back(
                            {current, start, segment_index, figure_index});
                    }
                    public_segment_base += figure.segment_count;
                    if (figure.end == figure_end::closed) {
                        ++public_segment_base;
                    }
                    return true;
                });
            return visited ? com::ok : com::invalid_argument;
        } catch (const std::bad_alloc&) {
            edges.clear();
            return com::out_of_memory;
        } catch (...) {
            edges.clear();
            return failure;
        }
    }

    [[nodiscard]] static com::result point_at_length(
        const std::vector<flat_edge>& edges,
        float length,
        point_2f* point,
        point_2f* tangent) noexcept
    {
        if (edges.empty()) {
            return com::invalid_argument;
        }
        double remaining = length;
        std::size_t last_eligible =
            (std::numeric_limits<std::size_t>::max)();
        for (std::size_t index = 0U; index < edges.size(); ++index) {
            const auto& edge = edges[index];
            const double dx =
                static_cast<double>(edge.end.x) - edge.start.x;
            const double dy =
                static_cast<double>(edge.end.y) - edge.start.y;
            const double edge_length = std::hypot(dx, dy);
            if (edge_length == 0.0) {
                continue;
            }
            last_eligible = index;
            if (remaining <= edge_length) {
                const double ratio = remaining / edge_length;
                if (point != nullptr) {
                    point->x = static_cast<float>(
                        edge.start.x + dx * ratio);
                    point->y = static_cast<float>(
                        edge.start.y + dy * ratio);
                }
                if (tangent != nullptr) {
                    tangent->x = static_cast<float>(dx / edge_length);
                    tangent->y = static_cast<float>(dy / edge_length);
                }
                return com::ok;
            }
            remaining -= edge_length;
        }
        if (last_eligible ==
            (std::numeric_limits<std::size_t>::max)()) {
            return com::invalid_argument;
        }
        const auto& edge = edges[last_eligible];
        const double dx = static_cast<double>(edge.end.x) - edge.start.x;
        const double dy = static_cast<double>(edge.end.y) - edge.start.y;
        const double edge_length = std::hypot(dx, dy);
        if (point != nullptr) {
            *point = edge.end;
        }
        if (tangent != nullptr && edge_length != 0.0) {
            tangent->x = static_cast<float>(dx / edge_length);
            tangent->y = static_cast<float>(dy / edge_length);
        }
        return com::ok;
    }

    [[nodiscard]] bool closed() const noexcept
    {
        return data_->state.load(std::memory_order_acquire) ==
            path_state::closed;
    }

    friend class com::atomic_reference_count<portable_path_geometry>;
    ~portable_path_geometry() = default;

    com::atomic_reference_count<portable_path_geometry> reference_count_;
    com::pointer<factory> owner_;
    std::shared_ptr<path_data> data_;
};

} // namespace

com::result create_path_geometry(
    factory* owner,
    path_geometry** value) noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = nullptr;
    if (owner == nullptr) {
        return com::invalid_argument;
    }
    try {
        auto* created = new (std::nothrow) portable_path_geometry(owner);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
}

} // namespace progpu::native::direct2d::compat::detail
