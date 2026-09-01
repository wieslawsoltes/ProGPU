#include "progpu_native_direct2d_path.hpp"
#include "../Mil/progpu_native_mil_curve_dash.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <span>
#include <utility>
#include <vector>

#if defined(__ARM_NEON) || defined(__ARM_NEON__) || defined(_M_ARM64)
#include <arm_neon.h>
#define PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_NEON 1
#elif defined(__SSE2__) || defined(_M_X64) ||                                  \
    (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
#include <emmintrin.h>
#define PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_SSE2 1
#endif

namespace progpu::native::direct2d::compat::detail {
namespace {

namespace curve_dash = progpu::native::mil::curve_dash;

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
    path_segment flags = path_segment::none;
};

[[nodiscard]] double triangle_cross(
    point_2f first,
    point_2f second,
    point_2f third) noexcept
{
    return (static_cast<double>(second.x) - first.x) *
            (static_cast<double>(third.y) - first.y) -
        (static_cast<double>(second.y) - first.y) *
            (static_cast<double>(third.x) - first.x);
}

[[nodiscard]] bool point_in_triangle(
    point_2f point,
    point_2f first,
    point_2f second,
    point_2f third,
    bool counter_clockwise) noexcept
{
    const double first_cross = triangle_cross(first, second, point);
    const double second_cross = triangle_cross(second, third, point);
    const double third_cross = triangle_cross(third, first, point);
    return counter_clockwise
        ? first_cross >= 0.0 && second_cross >= 0.0 && third_cross >= 0.0
        : first_cross <= 0.0 && second_cross <= 0.0 && third_cross <= 0.0;
}

[[nodiscard]] bool segments_intersect(
    point_2f first_start,
    point_2f first_end,
    point_2f second_start,
    point_2f second_end) noexcept
{
    const double a = triangle_cross(first_start, first_end, second_start);
    const double b = triangle_cross(first_start, first_end, second_end);
    const double c = triangle_cross(second_start, second_end, first_start);
    const double d = triangle_cross(second_start, second_end, first_end);
    return ((a > 0.0 && b < 0.0) || (a < 0.0 && b > 0.0)) &&
        ((c > 0.0 && d < 0.0) || (c < 0.0 && d > 0.0));
}

[[nodiscard]] bool polygon_contains_point(
    std::span<const point_2f> polygon,
    point_2f point) noexcept
{
    bool inside = false;
    for (std::size_t index = 0U, previous = polygon.size() - 1U;
         index < polygon.size();
         previous = index++) {
        const point_2f start = polygon[previous];
        const point_2f end = polygon[index];
        if ((start.y > point.y) == (end.y > point.y)) {
            continue;
        }
        const double crossing_x = static_cast<double>(start.x) +
            (static_cast<double>(point.y) - start.y) *
                (static_cast<double>(end.x) - start.x) /
                (static_cast<double>(end.y) - start.y);
        if (crossing_x > point.x) {
            inside = !inside;
        }
    }
    return inside;
}

class single_polygon_sink final : public simplified_geometry_sink {
public:
  com::result PROGPU_NATIVE_COM_CALL
  QueryInterface(com::guid_ref interface_id, void **value) noexcept override {
    if (value == nullptr) {
      return com::pointer_error;
    }
    *value = nullptr;
    if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
        com::guid_equal(interface_id, simplified_geometry_sink_interface_id)) {
      *value = static_cast<simplified_geometry_sink *>(this);
      AddRef();
      return com::ok;
    }
    return com::no_interface;
  }

  com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef() noexcept override {
    return reference_count_.add_ref();
  }

  com::reference_count_value PROGPU_NATIVE_COM_CALL
  Release() noexcept override {
    return reference_count_.release(this);
  }

  void PROGPU_NATIVE_COM_CALL SetFillMode(fill_mode value) noexcept override {
    if (value != fill_mode::alternate && value != fill_mode::winding) {
      fail(com::invalid_argument);
    }
  }

  void PROGPU_NATIVE_COM_CALL SetSegmentFlags(path_segment) noexcept override {}

  void PROGPU_NATIVE_COM_CALL
  BeginFigure(point_2f start, figure_begin begin) noexcept override {
    if (figure_open_ || !finite_point(start) ||
        (begin != figure_begin::filled && begin != figure_begin::hollow)) {
      fail(com::invalid_argument);
      return;
    }
    figure_open_ = true;
    capture_ = begin == figure_begin::filled;
    if (!capture_) {
      return;
    }
    if (filled_figure_seen_) {
      supported_ = false;
      return;
    }
    filled_figure_seen_ = true;
    try {
      points_.push_back(start);
    } catch (const std::bad_alloc &) {
      fail(com::out_of_memory);
    } catch (...) {
      fail(failure);
    }
  }

  void PROGPU_NATIVE_COM_CALL AddLines(
      const point_2f *points, std::uint32_t point_count) noexcept override {
    if (!figure_open_ || (point_count != 0U && points == nullptr)) {
      fail(com::invalid_argument);
      return;
    }
    if (!capture_) {
      return;
    }
    try {
      for (std::uint32_t index = 0U; index < point_count; ++index) {
        if (!finite_point(points[index])) {
          fail(com::invalid_argument);
          return;
        }
        if (points_.empty() || !same_point(points_.back(), points[index])) {
          points_.push_back(points[index]);
        }
      }
    } catch (const std::bad_alloc &) {
      fail(com::out_of_memory);
    } catch (...) {
      fail(failure);
    }
  }

  void PROGPU_NATIVE_COM_CALL AddBeziers(const bezier_segment *,
                                         std::uint32_t) noexcept override {
    fail(com::invalid_argument);
  }

  void PROGPU_NATIVE_COM_CALL EndFigure(figure_end end) noexcept override {
    if (!figure_open_ ||
        (end != figure_end::open && end != figure_end::closed)) {
      fail(com::invalid_argument);
      return;
    }
    figure_open_ = false;
    capture_ = false;
  }

  com::result PROGPU_NATIVE_COM_CALL Close() noexcept override {
    fail(wrong_state);
    return status_;
  }

  [[nodiscard]] com::result status() const noexcept {
    if (com::failed(status_)) {
      return status_;
    }
    return figure_open_ || !filled_figure_seen_ || !supported_ ? not_implemented
                                                               : com::ok;
  }

  [[nodiscard]] const std::vector<point_2f> &points() const noexcept {
    return points_;
  }

private:
  friend class com::atomic_reference_count<single_polygon_sink>;
  ~single_polygon_sink() = default;

  void fail(com::result value) noexcept {
    if (com::succeeded(status_)) {
      status_ = value;
    }
  }

  com::atomic_reference_count<single_polygon_sink> reference_count_;
  std::vector<point_2f> points_;
  com::result status_ = com::ok;
  bool figure_open_ = false;
  bool capture_ = false;
  bool filled_figure_seen_ = false;
  bool supported_ = true;
};

enum class polygon_point_relation : std::uint8_t {
  outside,
  boundary,
  inside,
};

struct polygon_boolean_segment final {
  point_2f start{};
  point_2f end{};
  bool used = false;
};

struct polygon_edge_bounds final {
  std::vector<float> minimum_x;
  std::vector<float> minimum_y;
  std::vector<float> maximum_x;
  std::vector<float> maximum_y;
  std::size_t edge_count = 0U;
};

struct polygon_stroke_edges final {
    std::vector<float> start_x;
    std::vector<float> start_y;
    std::vector<float> delta_x;
    std::vector<float> delta_y;
    std::vector<float> length_squared;
};

[[nodiscard]] polygon_stroke_edges make_polyline_stroke_edges(
    std::span<const point_2f> points,
    bool closed)
{
    polygon_stroke_edges result{};
    const std::size_t edge_count = closed ? points.size() : points.size() - 1U;
    const std::size_t padded_count = (edge_count + 3U) & ~std::size_t{3U};
    result.start_x.resize(padded_count, 0.0F);
    result.start_y.resize(padded_count, 0.0F);
    result.delta_x.resize(padded_count, 0.0F);
    result.delta_y.resize(padded_count, 0.0F);
    result.length_squared.resize(padded_count, 0.0F);
    for (std::size_t index = 0U; index < edge_count; ++index) {
        const point_2f start = points[index];
        const point_2f end = points[(index + 1U) % points.size()];
        const float delta_x = end.x - start.x;
        const float delta_y = end.y - start.y;
        result.start_x[index] = start.x;
        result.start_y[index] = start.y;
        result.delta_x[index] = delta_x;
        result.delta_y[index] = delta_y;
        result.length_squared[index] =
            delta_x * delta_x + delta_y * delta_y;
    }
    return result;
}

[[nodiscard]] polygon_stroke_edges make_dashed_stroke_edges(
    std::span<const progpu_native_path_segment> segments)
{
    polygon_stroke_edges result{};
    const std::size_t padded_count =
        (segments.size() + 3U) & ~std::size_t{3U};
    result.start_x.resize(padded_count, 0.0F);
    result.start_y.resize(padded_count, 0.0F);
    result.delta_x.resize(padded_count, 0.0F);
    result.delta_y.resize(padded_count, 0.0F);
    result.length_squared.resize(padded_count, 0.0F);
    for (std::size_t index = 0U; index < segments.size(); ++index) {
        const auto& segment = segments[index];
        const float delta_x = segment.p1.x - segment.p0.x;
        const float delta_y = segment.p1.y - segment.p0.y;
        result.start_x[index] = segment.p0.x;
        result.start_y[index] = segment.p0.y;
        result.delta_x[index] = delta_x;
        result.delta_y[index] = delta_y;
        result.length_squared[index] =
            delta_x * delta_x + delta_y * delta_y;
    }
    return result;
}

[[nodiscard]] bool polygon_stroke_body_contains(
    const polygon_stroke_edges& edges,
    point_2f point,
    float half_width) noexcept
{
    const float half_width_squared = half_width * half_width;
    for (std::size_t offset = 0U;
         offset < edges.start_x.size();
         offset += 4U) {
#if defined(PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_NEON)
        const float32x4_t delta_x = vld1q_f32(edges.delta_x.data() + offset);
        const float32x4_t delta_y = vld1q_f32(edges.delta_y.data() + offset);
        const float32x4_t length_squared =
            vld1q_f32(edges.length_squared.data() + offset);
        const float32x4_t point_x = vsubq_f32(
            vdupq_n_f32(point.x),
            vld1q_f32(edges.start_x.data() + offset));
        const float32x4_t point_y = vsubq_f32(
            vdupq_n_f32(point.y),
            vld1q_f32(edges.start_y.data() + offset));
        const float32x4_t projection = vaddq_f32(
            vmulq_f32(point_x, delta_x),
            vmulq_f32(point_y, delta_y));
        const float32x4_t cross = vsubq_f32(
            vmulq_f32(delta_x, point_y),
            vmulq_f32(delta_y, point_x));
        const uint32x4_t contained = vandq_u32(
            vandq_u32(
                vcgtq_f32(length_squared, vdupq_n_f32(0.0F)),
                vandq_u32(
                    vcgeq_f32(projection, vdupq_n_f32(0.0F)),
                    vcleq_f32(projection, length_squared))),
            vcleq_f32(
                vmulq_f32(cross, cross),
                vmulq_f32(
                    vdupq_n_f32(half_width_squared), length_squared)));
        if (vmaxvq_u32(contained) != 0U) {
            return true;
        }
#elif defined(PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_SSE2)
        const __m128 delta_x = _mm_loadu_ps(edges.delta_x.data() + offset);
        const __m128 delta_y = _mm_loadu_ps(edges.delta_y.data() + offset);
        const __m128 length_squared =
            _mm_loadu_ps(edges.length_squared.data() + offset);
        const __m128 point_x = _mm_sub_ps(
            _mm_set1_ps(point.x),
            _mm_loadu_ps(edges.start_x.data() + offset));
        const __m128 point_y = _mm_sub_ps(
            _mm_set1_ps(point.y),
            _mm_loadu_ps(edges.start_y.data() + offset));
        const __m128 projection = _mm_add_ps(
            _mm_mul_ps(point_x, delta_x),
            _mm_mul_ps(point_y, delta_y));
        const __m128 cross = _mm_sub_ps(
            _mm_mul_ps(delta_x, point_y),
            _mm_mul_ps(delta_y, point_x));
        const __m128 contained = _mm_and_ps(
            _mm_and_ps(
                _mm_cmpgt_ps(length_squared, _mm_setzero_ps()),
                _mm_and_ps(
                    _mm_cmpge_ps(projection, _mm_setzero_ps()),
                    _mm_cmple_ps(projection, length_squared))),
            _mm_cmple_ps(
                _mm_mul_ps(cross, cross),
                _mm_mul_ps(
                    _mm_set1_ps(half_width_squared), length_squared)));
        if (_mm_movemask_ps(contained) != 0) {
            return true;
        }
#else
        for (std::size_t lane = 0U; lane < 4U; ++lane) {
            const std::size_t index = offset + lane;
            const float length_squared = edges.length_squared[index];
            const float point_x = point.x - edges.start_x[index];
            const float point_y = point.y - edges.start_y[index];
            const float projection = point_x * edges.delta_x[index] +
                point_y * edges.delta_y[index];
            const float cross = edges.delta_x[index] * point_y -
                edges.delta_y[index] * point_x;
            if (length_squared > 0.0F && projection >= 0.0F &&
                projection <= length_squared && cross * cross <=
                    half_width_squared * length_squared) {
                return true;
            }
        }
#endif
    }
    return false;
}

[[nodiscard]] bool transform_to_local_point(
    point_2f point,
    const matrix_3x2_f* transform,
    point_2f& local) noexcept
{
    if (transform == nullptr) {
        local = point;
        return true;
    }
    const double determinant =
        static_cast<double>(transform->m11) * transform->m22 -
        static_cast<double>(transform->m12) * transform->m21;
    if (determinant == 0.0 || !std::isfinite(determinant)) {
        return false;
    }
    const double translated_x =
        static_cast<double>(point.x) - transform->m31;
    const double translated_y =
        static_cast<double>(point.y) - transform->m32;
    const double x =
        (translated_x * transform->m22 -
            translated_y * transform->m21) /
        determinant;
    const double y =
        (translated_y * transform->m11 -
            translated_x * transform->m12) /
        determinant;
    if (!std::isfinite(x) || !std::isfinite(y) ||
        std::abs(x) > (std::numeric_limits<float>::max)() ||
        std::abs(y) > (std::numeric_limits<float>::max)()) {
        return false;
    }
    local = {static_cast<float>(x), static_cast<float>(y)};
    return true;
}

[[nodiscard]] bool point_in_closed_triangle(
    point_2f point,
    point_2f first,
    point_2f second,
    point_2f third) noexcept
{
    const double first_cross = triangle_cross(first, second, point);
    const double second_cross = triangle_cross(second, third, point);
    const double third_cross = triangle_cross(third, first, point);
    const bool negative =
        first_cross < 0.0 || second_cross < 0.0 || third_cross < 0.0;
    const bool positive =
        first_cross > 0.0 || second_cross > 0.0 || third_cross > 0.0;
    return !(negative && positive);
}

[[nodiscard]] bool stroke_cap_contains(
    point_2f endpoint,
    point_2f adjacent,
    point_2f point,
    double half_width,
    cap_style cap) noexcept
{
    if (cap == cap_style::flat) {
        return false;
    }
    const double direction_x =
        static_cast<double>(endpoint.x) - adjacent.x;
    const double direction_y =
        static_cast<double>(endpoint.y) - adjacent.y;
    const double length = std::hypot(direction_x, direction_y);
    if (length == 0.0) {
        return false;
    }
    const double unit_x = direction_x / length;
    const double unit_y = direction_y / length;
    const double point_x = static_cast<double>(point.x) - endpoint.x;
    const double point_y = static_cast<double>(point.y) - endpoint.y;
    if (cap == cap_style::round) {
        return point_x * point_x + point_y * point_y <=
            half_width * half_width;
    }
    const double longitudinal = point_x * unit_x + point_y * unit_y;
    const double lateral = -point_x * unit_y + point_y * unit_x;
    if (cap == cap_style::square) {
        return longitudinal >= 0.0 && longitudinal <= half_width &&
            std::abs(lateral) <= half_width;
    }
    if (cap != cap_style::triangle) {
        return false;
    }
    const point_2f first{
        static_cast<float>(endpoint.x - unit_y * half_width),
        static_cast<float>(endpoint.y + unit_x * half_width)};
    const point_2f second{
        static_cast<float>(endpoint.x + unit_y * half_width),
        static_cast<float>(endpoint.y - unit_x * half_width)};
    const point_2f apex{
        static_cast<float>(endpoint.x + unit_x * half_width),
        static_cast<float>(endpoint.y + unit_y * half_width)};
    return point_in_closed_triangle(point, first, second, apex);
}

[[nodiscard]] bool stroke_join_contains(
    point_2f previous,
    point_2f vertex,
    point_2f next,
    point_2f point,
    double half_width,
    line_join join,
    double miter_limit) noexcept
{
    const double incoming_x = static_cast<double>(vertex.x) - previous.x;
    const double incoming_y = static_cast<double>(vertex.y) - previous.y;
    const double outgoing_x = static_cast<double>(next.x) - vertex.x;
    const double outgoing_y = static_cast<double>(next.y) - vertex.y;
    const double incoming_length = std::hypot(incoming_x, incoming_y);
    const double outgoing_length = std::hypot(outgoing_x, outgoing_y);
    if (incoming_length == 0.0 || outgoing_length == 0.0) {
        return false;
    }
    const double incoming_unit_x = incoming_x / incoming_length;
    const double incoming_unit_y = incoming_y / incoming_length;
    const double outgoing_unit_x = outgoing_x / outgoing_length;
    const double outgoing_unit_y = outgoing_y / outgoing_length;
    const double denominator = incoming_unit_x * outgoing_unit_y -
        incoming_unit_y * outgoing_unit_x;
    if (denominator == 0.0) {
        return false;
    }
    if (join == line_join::round) {
        const double point_x = static_cast<double>(point.x) - vertex.x;
        const double point_y = static_cast<double>(point.y) - vertex.y;
        return point_x * point_x + point_y * point_y <=
            half_width * half_width;
    }
    for (const double side : {-1.0, 1.0}) {
        const point_2f incoming_offset{
            static_cast<float>(
                vertex.x - incoming_unit_y * half_width * side),
            static_cast<float>(
                vertex.y + incoming_unit_x * half_width * side)};
        const point_2f outgoing_offset{
            static_cast<float>(
                vertex.x - outgoing_unit_y * half_width * side),
            static_cast<float>(
                vertex.y + outgoing_unit_x * half_width * side)};
        if (point_in_closed_triangle(
                point, vertex, incoming_offset, outgoing_offset)) {
            return true;
        }
        if (join == line_join::bevel) {
            continue;
        }
        const double offset_x =
            static_cast<double>(outgoing_offset.x) - incoming_offset.x;
        const double offset_y =
            static_cast<double>(outgoing_offset.y) - incoming_offset.y;
        const double parameter =
            (offset_x * outgoing_unit_y - offset_y * outgoing_unit_x) /
            denominator;
        const point_2f miter{
            static_cast<float>(
                incoming_offset.x + incoming_unit_x * parameter),
            static_cast<float>(
                incoming_offset.y + incoming_unit_y * parameter)};
        const double miter_length = std::hypot(
            static_cast<double>(miter.x) - vertex.x,
            static_cast<double>(miter.y) - vertex.y);
        if (miter_length <= miter_limit * half_width &&
            point_in_closed_triangle(
                point, incoming_offset, miter, outgoing_offset)) {
            return true;
        }
        if (join != line_join::miter || miter_length == 0.0 ||
            miter_length <= miter_limit * half_width) {
            continue;
        }
        const double bisector_x =
            (static_cast<double>(miter.x) - vertex.x) / miter_length;
        const double bisector_y =
            (static_cast<double>(miter.y) - vertex.y) / miter_length;
        const double clip_distance = miter_limit * half_width;
        const auto clipped_point = [&](point_2f offset, point_2f& clipped) {
            const double offset_x =
                static_cast<double>(offset.x) - vertex.x;
            const double offset_y =
                static_cast<double>(offset.y) - vertex.y;
            const double projection =
                offset_x * bisector_x + offset_y * bisector_y;
            const double denominator = miter_length - projection;
            if (denominator <= 0.0) {
                return false;
            }
            const double amount =
                (clip_distance - projection) / denominator;
            if (!std::isfinite(amount) || amount < 0.0 || amount > 1.0) {
                return false;
            }
            clipped = {
                static_cast<float>(
                    offset.x + (miter.x - offset.x) * amount),
                static_cast<float>(
                    offset.y + (miter.y - offset.y) * amount)};
            return true;
        };
        point_2f first_clipped{};
        point_2f second_clipped{};
        if (clipped_point(incoming_offset, first_clipped) &&
            clipped_point(outgoing_offset, second_clipped) &&
            (point_in_closed_triangle(
                 point, incoming_offset, first_clipped, second_clipped) ||
             point_in_closed_triangle(
                 point, incoming_offset, second_clipped, outgoing_offset))) {
            return true;
        }
    }
    return false;
}

[[nodiscard]] com::result read_dash_intervals(
    stroke_style& style,
    std::vector<double>& intervals)
{
    intervals.clear();
    switch (style.GetDashStyle()) {
    case dash_style::solid:
        return com::ok;
    case dash_style::dash:
        intervals = {2.0, 2.0};
        return com::ok;
    case dash_style::dot:
        intervals = {0.0, 2.0};
        return com::ok;
    case dash_style::dash_dot:
        intervals = {2.0, 2.0, 0.0, 2.0};
        return com::ok;
    case dash_style::dash_dot_dot:
        intervals = {2.0, 2.0, 0.0, 2.0, 0.0, 2.0};
        return com::ok;
    case dash_style::custom:
        break;
    default:
        return com::invalid_argument;
    }
    const std::uint32_t count = style.GetDashesCount();
    constexpr std::uint32_t maximum_dash_count = 1U << 20U;
    if (count == 0U || count > maximum_dash_count) {
        return com::invalid_argument;
    }
    std::vector<float> source(count);
    style.GetDashes(source.data(), count);
    intervals.reserve(count);
    bool has_positive = false;
    for (const float interval : source) {
        if (!std::isfinite(interval) || interval < 0.0F) {
            return com::invalid_argument;
        }
        has_positive = has_positive || interval > 0.0F;
        intervals.push_back(interval);
    }
    return has_positive ? com::ok : com::invalid_argument;
}

[[nodiscard]] com::result create_dashed_polyline_runs(
    std::span<const point_2f> points,
    bool closed,
    float stroke_width,
    stroke_style& style,
    curve_dash::run_buffer& dash_runs)
{
    std::vector<double> intervals;
    const com::result interval_status = read_dash_intervals(style, intervals);
    if (com::failed(interval_status)) {
        return interval_status;
    }
    if (intervals.empty()) {
        return not_implemented;
    }
    std::vector<progpu_native_path_segment> segments;
    std::vector<std::uint8_t> smooth_joins;
    const std::size_t segment_count = closed ? points.size() : points.size() - 1U;
    segments.reserve(segment_count);
    smooth_joins.resize(segment_count, 0U);
    for (std::size_t index = 0U; index < segment_count; ++index) {
        const point_2f start = points[index];
        const point_2f end = points[(index + 1U) % points.size()];
        progpu_native_path_segment segment{};
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        segment.p0 = {start.x, start.y};
        segment.p1 = {end.x, end.y};
        segments.push_back(segment);
    }
    const curve_dash::result dash_status = curve_dash::try_create_runs(
        segments,
        smooth_joins,
        closed,
        intervals,
        style.GetDashOffset(),
        stroke_width,
        dash_runs);
    if (dash_status == curve_dash::result::capacity_exceeded) {
        return com::out_of_memory;
    }
    return dash_status == curve_dash::result::success
        ? com::ok
        : com::invalid_argument;
}

[[nodiscard]] com::result create_dashed_polygon_runs(
    std::span<const point_2f> polygon,
    float stroke_width,
    stroke_style& style,
    curve_dash::run_buffer& dash_runs)
{
    return create_dashed_polyline_runs(
        polygon, true, stroke_width, style, dash_runs);
}

[[nodiscard]] com::result dashed_polygon_stroke_contains(
    std::span<const point_2f> polygon,
    point_2f point,
    float stroke_width,
    stroke_style& style,
    line_join join,
    double miter_limit,
    std::int32_t& contains)
{
    curve_dash::run_buffer dash_runs;
    const com::result dash_status = create_dashed_polygon_runs(
        polygon, stroke_width, style, dash_runs);
    if (com::failed(dash_status)) {
        return dash_status;
    }
    const double half_width = static_cast<double>(stroke_width) * 0.5;
    const polygon_stroke_edges stroke_edges =
        make_dashed_stroke_edges(dash_runs.segments);
    if (polygon_stroke_body_contains(
            stroke_edges, point, static_cast<float>(half_width))) {
        contains = 1;
        return com::ok;
    }
    for (const curve_dash::run& run : dash_runs.runs) {
        const auto run_segments = dash_runs.segments_for(run);
        for (std::size_t index = 1U; index < run_segments.size(); ++index) {
            const auto& incoming = run_segments[index - 1U];
            const auto& outgoing = run_segments[index];
            if (stroke_join_contains(
                    {incoming.p0.x, incoming.p0.y},
                    {incoming.p1.x, incoming.p1.y},
                    {outgoing.p1.x, outgoing.p1.y},
                    point,
                    half_width,
                    join,
                    miter_limit)) {
                contains = 1;
                return com::ok;
            }
        }
        if (run.closed) {
            const auto& incoming = run_segments.back();
            const auto& outgoing = run_segments.front();
            if (stroke_join_contains(
                    {incoming.p0.x, incoming.p0.y},
                    {incoming.p1.x, incoming.p1.y},
                    {outgoing.p1.x, outgoing.p1.y},
                    point,
                    half_width,
                    join,
                    miter_limit)) {
                contains = 1;
                return com::ok;
            }
            continue;
        }
        const auto& first = run_segments.front();
        const auto& last = run_segments.back();
        const cap_style start_cap = run.starts_at_source_start
            ? style.GetStartCap()
            : style.GetDashCap();
        const cap_style end_cap = run.ends_at_source_end
            ? style.GetEndCap()
            : style.GetDashCap();
        if (stroke_cap_contains(
                {first.p0.x, first.p0.y},
                {first.p1.x, first.p1.y},
                point,
                half_width,
                start_cap) ||
            stroke_cap_contains(
                {last.p1.x, last.p1.y},
                {last.p0.x, last.p0.y},
                point,
                half_width,
                end_cap)) {
            contains = 1;
            return com::ok;
        }
    }
    return com::ok;
}

[[nodiscard]] com::result dashed_open_polyline_stroke_contains(
    std::span<const point_2f> points,
    point_2f point,
    float stroke_width,
    stroke_style& style,
    line_join join,
    double miter_limit,
    std::int32_t& contains)
{
    curve_dash::run_buffer dash_runs;
    const com::result dash_status = create_dashed_polyline_runs(
        points, false, stroke_width, style, dash_runs);
    if (com::failed(dash_status)) {
        return dash_status;
    }
    const double half_width = static_cast<double>(stroke_width) * 0.5;
    const polygon_stroke_edges stroke_edges =
        make_dashed_stroke_edges(dash_runs.segments);
    if (polygon_stroke_body_contains(
            stroke_edges, point, static_cast<float>(half_width))) {
        contains = 1;
        return com::ok;
    }
    for (const curve_dash::run& run : dash_runs.runs) {
        const auto run_segments = dash_runs.segments_for(run);
        if (run_segments.empty() || run.closed) {
            return com::invalid_argument;
        }
        for (std::size_t index = 1U; index < run_segments.size(); ++index) {
            const auto& incoming = run_segments[index - 1U];
            const auto& outgoing = run_segments[index];
            if (stroke_join_contains(
                    {incoming.p0.x, incoming.p0.y},
                    {incoming.p1.x, incoming.p1.y},
                    {outgoing.p1.x, outgoing.p1.y},
                    point,
                    half_width,
                    join,
                    miter_limit)) {
                contains = 1;
                return com::ok;
            }
        }
        const auto& first = run_segments.front();
        const auto& last = run_segments.back();
        const cap_style start_cap = run.starts_at_source_start
            ? style.GetStartCap()
            : style.GetDashCap();
        const cap_style end_cap = run.ends_at_source_end
            ? style.GetEndCap()
            : style.GetDashCap();
        if (stroke_cap_contains(
                {first.p0.x, first.p0.y},
                {first.p1.x, first.p1.y},
                point,
                half_width,
                start_cap) ||
            stroke_cap_contains(
                {last.p1.x, last.p1.y},
                {last.p0.x, last.p0.y},
                point,
                half_width,
                end_cap)) {
            contains = 1;
            return com::ok;
        }
    }
    if (dash_runs.terminal_visible_point &&
        style.GetDashCap() != cap_style::flat) {
        const point_2f endpoint = points.back();
        const point_2f adjacent = points[points.size() - 2U];
        const point_2f opposite_adjacent{
            endpoint.x + (endpoint.x - adjacent.x),
            endpoint.y + (endpoint.y - adjacent.y)};
        if (stroke_cap_contains(
                endpoint,
                opposite_adjacent,
                point,
                half_width,
                style.GetDashCap()) ||
            stroke_cap_contains(
                endpoint,
                adjacent,
                point,
                half_width,
                style.GetEndCap())) {
            contains = 1;
        }
    }
    return com::ok;
}

[[nodiscard]] com::result transformed_point_bounds(
    std::span<const point_2f> points,
    const matrix_3x2_f* transform,
    rectangle_f& bounds) noexcept
{
    if (points.empty()) {
        bounds = {};
        return com::ok;
    }
    const matrix_3x2_f identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const matrix_3x2_f& matrix = transform == nullptr ? identity : *transform;
    float minimum_x = std::numeric_limits<float>::infinity();
    float minimum_y = std::numeric_limits<float>::infinity();
    float maximum_x = -std::numeric_limits<float>::infinity();
    float maximum_y = -std::numeric_limits<float>::infinity();
    std::size_t index = 0U;
#if defined(PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_NEON)
    static_assert(sizeof(point_2f) == sizeof(float) * 2U);
    for (; index + 4U <= points.size(); index += 4U) {
        const float32x4x2_t source = vld2q_f32(
            reinterpret_cast<const float*>(points.data() + index));
        const float32x4_t x = vaddq_f32(
            vaddq_f32(
                vmulq_n_f32(source.val[0], matrix.m11),
                vmulq_n_f32(source.val[1], matrix.m21)),
            vdupq_n_f32(matrix.m31));
        const float32x4_t y = vaddq_f32(
            vaddq_f32(
                vmulq_n_f32(source.val[0], matrix.m12),
                vmulq_n_f32(source.val[1], matrix.m22)),
            vdupq_n_f32(matrix.m32));
        minimum_x = std::min(minimum_x, vminvq_f32(x));
        minimum_y = std::min(minimum_y, vminvq_f32(y));
        maximum_x = std::max(maximum_x, vmaxvq_f32(x));
        maximum_y = std::max(maximum_y, vmaxvq_f32(y));
    }
#elif defined(PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_SSE2)
    static_assert(sizeof(point_2f) == sizeof(float) * 2U);
    alignas(16) float transformed_x[4U]{};
    alignas(16) float transformed_y[4U]{};
    for (; index + 4U <= points.size(); index += 4U) {
        const float* source =
            reinterpret_cast<const float*>(points.data() + index);
        const __m128 first = _mm_loadu_ps(source);
        const __m128 second = _mm_loadu_ps(source + 4U);
        const __m128 source_x =
            _mm_shuffle_ps(first, second, _MM_SHUFFLE(2, 0, 2, 0));
        const __m128 source_y =
            _mm_shuffle_ps(first, second, _MM_SHUFFLE(3, 1, 3, 1));
        const __m128 x = _mm_add_ps(
            _mm_add_ps(
                _mm_mul_ps(source_x, _mm_set1_ps(matrix.m11)),
                _mm_mul_ps(source_y, _mm_set1_ps(matrix.m21))),
            _mm_set1_ps(matrix.m31));
        const __m128 y = _mm_add_ps(
            _mm_add_ps(
                _mm_mul_ps(source_x, _mm_set1_ps(matrix.m12)),
                _mm_mul_ps(source_y, _mm_set1_ps(matrix.m22))),
            _mm_set1_ps(matrix.m32));
        _mm_store_ps(transformed_x, x);
        _mm_store_ps(transformed_y, y);
        for (std::size_t lane = 0U; lane < 4U; ++lane) {
            minimum_x = std::min(minimum_x, transformed_x[lane]);
            minimum_y = std::min(minimum_y, transformed_y[lane]);
            maximum_x = std::max(maximum_x, transformed_x[lane]);
            maximum_y = std::max(maximum_y, transformed_y[lane]);
        }
    }
#endif
    for (; index < points.size(); ++index) {
        point_2f transformed{};
        if (com::failed(transform_point(
                points[index], &matrix, &transformed))) {
            return com::invalid_argument;
        }
        minimum_x = std::min(minimum_x, transformed.x);
        minimum_y = std::min(minimum_y, transformed.y);
        maximum_x = std::max(maximum_x, transformed.x);
        maximum_y = std::max(maximum_y, transformed.y);
    }
    if (!std::isfinite(minimum_x) || !std::isfinite(minimum_y) ||
        !std::isfinite(maximum_x) || !std::isfinite(maximum_y)) {
        return com::invalid_argument;
    }
    bounds = {minimum_x, minimum_y, maximum_x, maximum_y};
    return com::ok;
}

void append_round_support_points(
    point_2f center,
    double radius,
    const matrix_3x2_f* transform,
    std::vector<point_2f>& points)
{
    const matrix_3x2_f identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const matrix_3x2_f& matrix = transform == nullptr ? identity : *transform;
    const auto append_axis = [&](double first, double second) {
        const double length = std::hypot(first, second);
        if (length == 0.0) {
            return;
        }
        const double scale = radius / length;
        const float offset_x = static_cast<float>(first * scale);
        const float offset_y = static_cast<float>(second * scale);
        points.push_back({center.x + offset_x, center.y + offset_y});
        points.push_back({center.x - offset_x, center.y - offset_y});
    };
    points.push_back(center);
    append_axis(matrix.m11, matrix.m21);
    append_axis(matrix.m12, matrix.m22);
}

[[nodiscard]] com::result append_stroke_segment_bounds_points(
    const progpu_native_path_segment& segment,
    double half_width,
    std::vector<point_2f>& points)
{
    if (segment.kind != PROGPU_NATIVE_PATH_SEGMENT_LINE) {
        return not_implemented;
    }
    const double delta_x = static_cast<double>(segment.p1.x) - segment.p0.x;
    const double delta_y = static_cast<double>(segment.p1.y) - segment.p0.y;
    const double length = std::hypot(delta_x, delta_y);
    if (length == 0.0) {
        return not_implemented;
    }
    const double normal_x = -delta_y / length * half_width;
    const double normal_y = delta_x / length * half_width;
    points.push_back({
        static_cast<float>(segment.p0.x + normal_x),
        static_cast<float>(segment.p0.y + normal_y)});
    points.push_back({
        static_cast<float>(segment.p0.x - normal_x),
        static_cast<float>(segment.p0.y - normal_y)});
    points.push_back({
        static_cast<float>(segment.p1.x + normal_x),
        static_cast<float>(segment.p1.y + normal_y)});
    points.push_back({
        static_cast<float>(segment.p1.x - normal_x),
        static_cast<float>(segment.p1.y - normal_y)});
    return com::ok;
}

void append_stroke_cap_bounds_points(
    point_2f endpoint,
    point_2f adjacent,
    double half_width,
    cap_style cap,
    const matrix_3x2_f* transform,
    std::vector<point_2f>& points)
{
    if (cap == cap_style::flat) {
        return;
    }
    if (cap == cap_style::round) {
        append_round_support_points(endpoint, half_width, transform, points);
        return;
    }
    const double direction_x =
        static_cast<double>(endpoint.x) - adjacent.x;
    const double direction_y =
        static_cast<double>(endpoint.y) - adjacent.y;
    const double length = std::hypot(direction_x, direction_y);
    if (length == 0.0) {
        return;
    }
    const double unit_x = direction_x / length;
    const double unit_y = direction_y / length;
    const point_2f extension{
        static_cast<float>(endpoint.x + unit_x * half_width),
        static_cast<float>(endpoint.y + unit_y * half_width)};
    if (cap == cap_style::triangle) {
        points.push_back(extension);
        return;
    }
    if (cap != cap_style::square) {
        return;
    }
    points.push_back({
        static_cast<float>(extension.x - unit_y * half_width),
        static_cast<float>(extension.y + unit_x * half_width)});
    points.push_back({
        static_cast<float>(extension.x + unit_y * half_width),
        static_cast<float>(extension.y - unit_x * half_width)});
}

void append_stroke_join_bounds_points(
    point_2f previous,
    point_2f vertex,
    point_2f next,
    double half_width,
    line_join join,
    double miter_limit,
    const matrix_3x2_f* transform,
    std::vector<point_2f>& points)
{
    if (join == line_join::round) {
        append_round_support_points(vertex, half_width, transform, points);
        return;
    }
    if (join == line_join::bevel) {
        return;
    }
    const double incoming_x = static_cast<double>(vertex.x) - previous.x;
    const double incoming_y = static_cast<double>(vertex.y) - previous.y;
    const double outgoing_x = static_cast<double>(next.x) - vertex.x;
    const double outgoing_y = static_cast<double>(next.y) - vertex.y;
    const double incoming_length = std::hypot(incoming_x, incoming_y);
    const double outgoing_length = std::hypot(outgoing_x, outgoing_y);
    if (incoming_length == 0.0 || outgoing_length == 0.0) {
        return;
    }
    const double incoming_unit_x = incoming_x / incoming_length;
    const double incoming_unit_y = incoming_y / incoming_length;
    const double outgoing_unit_x = outgoing_x / outgoing_length;
    const double outgoing_unit_y = outgoing_y / outgoing_length;
    const double denominator = incoming_unit_x * outgoing_unit_y -
        incoming_unit_y * outgoing_unit_x;
    if (denominator == 0.0) {
        return;
    }
    for (const double side : {-1.0, 1.0}) {
        const point_2f incoming_offset{
            static_cast<float>(
                vertex.x - incoming_unit_y * half_width * side),
            static_cast<float>(
                vertex.y + incoming_unit_x * half_width * side)};
        const point_2f outgoing_offset{
            static_cast<float>(
                vertex.x - outgoing_unit_y * half_width * side),
            static_cast<float>(
                vertex.y + outgoing_unit_x * half_width * side)};
        const double offset_x =
            static_cast<double>(outgoing_offset.x) - incoming_offset.x;
        const double offset_y =
            static_cast<double>(outgoing_offset.y) - incoming_offset.y;
        const double parameter =
            (offset_x * outgoing_unit_y - offset_y * outgoing_unit_x) /
            denominator;
        const point_2f miter{
            static_cast<float>(
                incoming_offset.x + incoming_unit_x * parameter),
            static_cast<float>(
                incoming_offset.y + incoming_unit_y * parameter)};
        const double miter_length = std::hypot(
                static_cast<double>(miter.x) - vertex.x,
                static_cast<double>(miter.y) - vertex.y);
        if (miter_length <= miter_limit * half_width) {
            points.push_back(miter);
            continue;
        }
        if (join != line_join::miter || miter_length == 0.0) {
            continue;
        }
        const double bisector_x =
            (static_cast<double>(miter.x) - vertex.x) / miter_length;
        const double bisector_y =
            (static_cast<double>(miter.y) - vertex.y) / miter_length;
        const double clip_distance = miter_limit * half_width;
        const auto append_clipped = [&](point_2f offset) {
            const double offset_x =
                static_cast<double>(offset.x) - vertex.x;
            const double offset_y =
                static_cast<double>(offset.y) - vertex.y;
            const double projection =
                offset_x * bisector_x + offset_y * bisector_y;
            const double divisor = miter_length - projection;
            if (divisor <= 0.0) {
                return;
            }
            const double amount =
                (clip_distance - projection) / divisor;
            if (std::isfinite(amount) && amount >= 0.0 && amount <= 1.0) {
                points.push_back({
                    static_cast<float>(
                        offset.x + (miter.x - offset.x) * amount),
                    static_cast<float>(
                        offset.y + (miter.y - offset.y) * amount)});
            }
        };
        append_clipped(incoming_offset);
        append_clipped(outgoing_offset);
    }
}

[[nodiscard]] com::result solid_polyline_widened_bounds(
    std::span<const point_2f> points_source,
    bool closed,
    float stroke_width,
    line_join join,
    double miter_limit,
    cap_style start_cap,
    cap_style end_cap,
    const matrix_3x2_f* transform,
    rectangle_f& bounds)
{
    const double half_width = static_cast<double>(stroke_width) * 0.5;
    const std::size_t edge_count =
        closed ? points_source.size() : points_source.size() - 1U;
    std::vector<point_2f> points;
    points.reserve(edge_count * 6U + 8U);
    for (std::size_t index = 0U; index < edge_count; ++index) {
        progpu_native_path_segment segment{};
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        segment.p0 = {
            points_source[index].x,
            points_source[index].y};
        const point_2f end =
            points_source[(index + 1U) % points_source.size()];
        segment.p1 = {end.x, end.y};
        const com::result segment_status =
            append_stroke_segment_bounds_points(
                segment, half_width, points);
        if (com::failed(segment_status)) {
            return segment_status;
        }
    }
    const std::size_t first_join = closed ? 0U : 1U;
    const std::size_t join_end =
        closed ? points_source.size() : points_source.size() - 1U;
    for (std::size_t index = first_join; index < join_end; ++index) {
        append_stroke_join_bounds_points(
            points_source[closed
                ? (index + points_source.size() - 1U) % points_source.size()
                : index - 1U],
            points_source[index],
            points_source[closed
                ? (index + 1U) % points_source.size()
                : index + 1U],
            half_width,
            join,
            miter_limit,
            transform,
            points);
    }
    if (!closed) {
        append_stroke_cap_bounds_points(
            points_source.front(),
            points_source[1U],
            half_width,
            start_cap,
            transform,
            points);
        append_stroke_cap_bounds_points(
            points_source.back(),
            points_source[points_source.size() - 2U],
            half_width,
            end_cap,
            transform,
            points);
    }
    return transformed_point_bounds(points, transform, bounds);
}

[[nodiscard]] com::result dashed_polyline_widened_bounds(
    std::span<const point_2f> polygon,
    bool closed,
    float stroke_width,
    stroke_style& style,
    const matrix_3x2_f* transform,
    rectangle_f& bounds)
{
    curve_dash::run_buffer dash_runs;
    const com::result dash_status = create_dashed_polyline_runs(
        polygon, closed, stroke_width, style, dash_runs);
    if (com::failed(dash_status)) {
        return dash_status;
    }
    const double half_width = static_cast<double>(stroke_width) * 0.5;
    std::vector<point_2f> points;
    points.reserve(dash_runs.segments.size() * 8U);
    points.insert(points.end(), polygon.begin(), polygon.end());
    for (const curve_dash::run& run : dash_runs.runs) {
        const auto segments = dash_runs.segments_for(run);
        if (segments.empty()) {
            return com::invalid_argument;
        }
        for (const auto& segment : segments) {
            const com::result segment_status =
                append_stroke_segment_bounds_points(
                    segment, half_width, points);
            if (com::failed(segment_status)) {
                return segment_status;
            }
        }
        for (std::size_t index = 1U; index < segments.size(); ++index) {
            append_stroke_join_bounds_points(
                {segments[index - 1U].p0.x, segments[index - 1U].p0.y},
                {segments[index - 1U].p1.x, segments[index - 1U].p1.y},
                {segments[index].p1.x, segments[index].p1.y},
                half_width,
                style.GetLineJoin(),
                style.GetMiterLimit(),
                transform,
                points);
        }
        if (run.closed) {
            append_stroke_join_bounds_points(
                {segments.back().p0.x, segments.back().p0.y},
                {segments.back().p1.x, segments.back().p1.y},
                {segments.front().p1.x, segments.front().p1.y},
                half_width,
                style.GetLineJoin(),
                style.GetMiterLimit(),
                transform,
                points);
        } else {
            const cap_style run_start_cap = run.starts_at_source_start
                ? style.GetStartCap()
                : style.GetDashCap();
            const cap_style run_end_cap = run.ends_at_source_end
                ? style.GetEndCap()
                : style.GetDashCap();
            append_stroke_cap_bounds_points(
                {segments.front().p0.x, segments.front().p0.y},
                {segments.front().p1.x, segments.front().p1.y},
                half_width,
                run_start_cap,
                transform,
                points);
            append_stroke_cap_bounds_points(
                {segments.back().p1.x, segments.back().p1.y},
                {segments.back().p0.x, segments.back().p0.y},
                half_width,
                run_end_cap,
                transform,
            points);
        }
    }
    if (!closed && dash_runs.terminal_visible_point &&
        style.GetEndCap() != cap_style::flat) {
        append_stroke_cap_bounds_points(
            polygon.back(),
            polygon[polygon.size() - 2U],
            half_width,
            style.GetEndCap(),
            transform,
            points);
    }
    return transformed_point_bounds(points, transform, bounds);
}

struct dash_side_edge final {
    bool round{};
    point_2f center{};
};

struct dash_side final {
    std::vector<point_2f> points;
    std::vector<dash_side_edge> edges;
};

void append_dash_side_point(
    dash_side& side,
    point_2f point,
    bool round = false,
    point_2f center = {})
{
    if (side.points.empty()) {
        side.points.push_back(point);
        return;
    }
    if (!same_point(side.points.back(), point)) {
        side.edges.push_back({round, center});
        side.points.push_back(point);
    }
}

[[nodiscard]] com::result append_dash_run_side(
    std::span<const progpu_native_path_segment> segments,
    double half_width,
    double side,
    line_join join,
    double miter_limit,
    cap_style start_cap,
    cap_style end_cap,
    dash_side& output)
{
    if (segments.empty()) {
        return com::invalid_argument;
    }
    const auto unit_and_normal = [half_width](
        const progpu_native_path_segment& segment,
        double& unit_x,
        double& unit_y,
        double& normal_x,
        double& normal_y) {
        if (segment.kind != PROGPU_NATIVE_PATH_SEGMENT_LINE) {
            return false;
        }
        const double delta_x =
            static_cast<double>(segment.p1.x) - segment.p0.x;
        const double delta_y =
            static_cast<double>(segment.p1.y) - segment.p0.y;
        const double length = std::hypot(delta_x, delta_y);
        if (length == 0.0) {
            return false;
        }
        unit_x = delta_x / length;
        unit_y = delta_y / length;
        normal_x = -unit_y * half_width;
        normal_y = unit_x * half_width;
        return true;
    };
    double first_unit_x = 0.0;
    double first_unit_y = 0.0;
    double first_normal_x = 0.0;
    double first_normal_y = 0.0;
    if (!unit_and_normal(
            segments.front(),
            first_unit_x,
            first_unit_y,
            first_normal_x,
            first_normal_y)) {
        return not_implemented;
    }
    const double start_extension = start_cap == cap_style::square
        ? half_width
        : 0.0;
    append_dash_side_point(output, {
        static_cast<float>(
            segments.front().p0.x + first_normal_x * side -
            first_unit_x * start_extension),
        static_cast<float>(
            segments.front().p0.y + first_normal_y * side -
            first_unit_y * start_extension)});
    for (std::size_t index = 1U; index < segments.size(); ++index) {
        const auto& incoming = segments[index - 1U];
        const auto& outgoing = segments[index];
        if (!same_point(
                {incoming.p1.x, incoming.p1.y},
                {outgoing.p0.x, outgoing.p0.y})) {
            return not_implemented;
        }
        double incoming_unit_x = 0.0;
        double incoming_unit_y = 0.0;
        double incoming_normal_x = 0.0;
        double incoming_normal_y = 0.0;
        double outgoing_unit_x = 0.0;
        double outgoing_unit_y = 0.0;
        double outgoing_normal_x = 0.0;
        double outgoing_normal_y = 0.0;
        if (!unit_and_normal(
                incoming,
                incoming_unit_x,
                incoming_unit_y,
                incoming_normal_x,
                incoming_normal_y) ||
            !unit_and_normal(
                outgoing,
                outgoing_unit_x,
                outgoing_unit_y,
                outgoing_normal_x,
                outgoing_normal_y)) {
            return not_implemented;
        }
        const point_2f vertex{incoming.p1.x, incoming.p1.y};
        const point_2f incoming_offset{
            static_cast<float>(vertex.x + incoming_normal_x * side),
            static_cast<float>(vertex.y + incoming_normal_y * side)};
        const point_2f outgoing_offset{
            static_cast<float>(vertex.x + outgoing_normal_x * side),
            static_cast<float>(vertex.y + outgoing_normal_y * side)};
        const double cross = incoming_unit_x * outgoing_unit_y -
            incoming_unit_y * outgoing_unit_x;
        if (cross == 0.0) {
            append_dash_side_point(output, outgoing_offset);
            continue;
        }
        const bool outer_side = cross * side < 0.0;
        if (outer_side && join == line_join::round) {
            append_dash_side_point(output, incoming_offset);
            append_dash_side_point(output, outgoing_offset, true, vertex);
            continue;
        }
        if (outer_side && join == line_join::bevel) {
            append_dash_side_point(output, incoming_offset);
            append_dash_side_point(output, outgoing_offset);
            continue;
        }
        const double offset_x =
            static_cast<double>(outgoing_offset.x) - incoming_offset.x;
        const double offset_y =
            static_cast<double>(outgoing_offset.y) - incoming_offset.y;
        const double parameter =
            (offset_x * outgoing_unit_y - offset_y * outgoing_unit_x) /
            cross;
        const point_2f intersection{
            static_cast<float>(
                incoming_offset.x + incoming_unit_x * parameter),
            static_cast<float>(
                incoming_offset.y + incoming_unit_y * parameter)};
        if (outer_side && std::hypot(
                static_cast<double>(intersection.x) - vertex.x,
                static_cast<double>(intersection.y) - vertex.y) >
            miter_limit * half_width) {
            if (join == line_join::miter_or_bevel) {
                append_dash_side_point(output, incoming_offset);
                append_dash_side_point(output, outgoing_offset);
                continue;
            }
            const double miter_x =
                static_cast<double>(intersection.x) - vertex.x;
            const double miter_y =
                static_cast<double>(intersection.y) - vertex.y;
            const double miter_length = std::hypot(miter_x, miter_y);
            if (join != line_join::miter || miter_length == 0.0) {
                return not_implemented;
            }
            const double bisector_x = miter_x / miter_length;
            const double bisector_y = miter_y / miter_length;
            const double clip_distance = miter_limit * half_width;
            const auto append_clipped = [&](point_2f offset) {
                const double offset_x =
                    static_cast<double>(offset.x) - vertex.x;
                const double offset_y =
                    static_cast<double>(offset.y) - vertex.y;
                const double projection =
                    offset_x * bisector_x + offset_y * bisector_y;
                const double denominator = miter_length - projection;
                if (denominator <= 0.0) {
                    return false;
                }
                const double amount =
                    (clip_distance - projection) / denominator;
                if (!std::isfinite(amount) || amount < 0.0 || amount > 1.0) {
                    return false;
                }
                append_dash_side_point(output, {
                    static_cast<float>(
                        offset.x + (intersection.x - offset.x) * amount),
                    static_cast<float>(
                        offset.y + (intersection.y - offset.y) * amount)});
                return true;
            };
            if (!append_clipped(incoming_offset) ||
                !append_clipped(outgoing_offset)) {
                return not_implemented;
            }
            continue;
        }
        append_dash_side_point(output, intersection);
    }
    double last_unit_x = 0.0;
    double last_unit_y = 0.0;
    double last_normal_x = 0.0;
    double last_normal_y = 0.0;
    if (!unit_and_normal(
            segments.back(),
            last_unit_x,
            last_unit_y,
            last_normal_x,
            last_normal_y)) {
        return not_implemented;
    }
    const double end_extension = end_cap == cap_style::square
        ? half_width
        : 0.0;
    append_dash_side_point(output, {
        static_cast<float>(
            segments.back().p1.x + last_normal_x * side +
            last_unit_x * end_extension),
        static_cast<float>(
            segments.back().p1.y + last_normal_y * side +
            last_unit_y * end_extension)});
    return com::ok;
}

[[nodiscard]] com::result transform_points_in_place(
    std::vector<point_2f>& points,
    const matrix_3x2_f* transform) noexcept;

struct widened_outline_segment final {
    bool cubic{};
    point_2f control1{};
    point_2f control2{};
    point_2f end{};
};

struct widened_outline final {
    point_2f start{};
    std::vector<widened_outline_segment> segments;
};

void append_outline_line(
    widened_outline& outline,
    point_2f point)
{
    const point_2f current = outline.segments.empty()
        ? outline.start
        : outline.segments.back().end;
    if (!same_point(current, point)) {
        outline.segments.push_back({false, {}, {}, point});
    }
}

[[nodiscard]] com::result append_round_cap_segments(
    widened_outline& outline,
    point_2f endpoint,
    point_2f adjacent,
    point_2f destination,
    double half_width)
{
    constexpr double kappa = 0.5522847498307933984;
    const point_2f start = outline.segments.empty()
        ? outline.start
        : outline.segments.back().end;
    const double outward_x = static_cast<double>(endpoint.x) - adjacent.x;
    const double outward_y = static_cast<double>(endpoint.y) - adjacent.y;
    const double outward_length = std::hypot(outward_x, outward_y);
    const double side_x = static_cast<double>(start.x) - endpoint.x;
    const double side_y = static_cast<double>(start.y) - endpoint.y;
    const double side_length = std::hypot(side_x, side_y);
    if (outward_length == 0.0 || side_length == 0.0) {
        return not_implemented;
    }
    const double unit_outward_x = outward_x / outward_length;
    const double unit_outward_y = outward_y / outward_length;
    const double unit_side_x = side_x / side_length;
    const double unit_side_y = side_y / side_length;
    const point_2f outward{
        static_cast<float>(endpoint.x + unit_outward_x * half_width),
        static_cast<float>(endpoint.y + unit_outward_y * half_width)};
    const double control_distance = half_width * kappa;
    outline.segments.push_back({
        true,
        {
            static_cast<float>(start.x + unit_outward_x * control_distance),
            static_cast<float>(start.y + unit_outward_y * control_distance)},
        {
            static_cast<float>(outward.x + unit_side_x * control_distance),
            static_cast<float>(outward.y + unit_side_y * control_distance)},
        outward});
    outline.segments.push_back({
        true,
        {
            static_cast<float>(outward.x - unit_side_x * control_distance),
            static_cast<float>(outward.y - unit_side_y * control_distance)},
        {
            static_cast<float>(
                destination.x + unit_outward_x * control_distance),
            static_cast<float>(
                destination.y + unit_outward_y * control_distance)},
        destination});
    return com::ok;
}

[[nodiscard]] com::result append_circular_arc_segments(
    widened_outline& outline,
    point_2f center,
    point_2f destination)
{
    const point_2f start = outline.segments.empty()
        ? outline.start
        : outline.segments.back().end;
    const double start_x = static_cast<double>(start.x) - center.x;
    const double start_y = static_cast<double>(start.y) - center.y;
    const double end_x = static_cast<double>(destination.x) - center.x;
    const double end_y = static_cast<double>(destination.y) - center.y;
    const double start_radius = std::hypot(start_x, start_y);
    const double end_radius = std::hypot(end_x, end_y);
    if (start_radius == 0.0 || end_radius == 0.0) {
        return not_implemented;
    }
    const double radius = (start_radius + end_radius) * 0.5;
    const double angle = std::atan2(
        start_x * end_y - start_y * end_x,
        start_x * end_x + start_y * end_y);
    if (!std::isfinite(angle) || angle == 0.0) {
        return not_implemented;
    }
    const std::uint32_t span_count = static_cast<std::uint32_t>(
        std::ceil(std::abs(angle) / (std::numbers::pi / 2.0)));
    if (span_count == 0U || span_count > 2U) {
        return not_implemented;
    }
    const double delta = angle / span_count;
    double current_angle = std::atan2(start_y, start_x);
    point_2f current = start;
    for (std::uint32_t span = 0U; span < span_count; ++span) {
        const double next_angle = current_angle + delta;
        const point_2f next = span + 1U == span_count
            ? destination
            : point_2f{
                static_cast<float>(center.x + radius * std::cos(next_angle)),
                static_cast<float>(center.y + radius * std::sin(next_angle))};
        const double factor = 4.0 / 3.0 * std::tan(delta * 0.25);
        const double current_unit_x =
            (static_cast<double>(current.x) - center.x) / radius;
        const double current_unit_y =
            (static_cast<double>(current.y) - center.y) / radius;
        const double next_unit_x =
            (static_cast<double>(next.x) - center.x) / radius;
        const double next_unit_y =
            (static_cast<double>(next.y) - center.y) / radius;
        outline.segments.push_back({
            true,
            {
                static_cast<float>(
                    current.x - current_unit_y * radius * factor),
                static_cast<float>(
                    current.y + current_unit_x * radius * factor)},
            {
                static_cast<float>(next.x + next_unit_y * radius * factor),
                static_cast<float>(next.y - next_unit_x * radius * factor)},
            next});
        current = next;
        current_angle = next_angle;
    }
    return com::ok;
}

[[nodiscard]] com::result build_joined_dash_outline(
    std::span<const progpu_native_path_segment> segments,
    double half_width,
    line_join join,
    double miter_limit,
    cap_style start_cap,
    cap_style end_cap,
    widened_outline& outline)
{
    dash_side left;
    dash_side right;
    const com::result left_status = append_dash_run_side(
        segments,
        half_width,
        1.0,
        join,
        miter_limit,
        start_cap,
        end_cap,
        left);
    if (com::failed(left_status)) {
        return left_status;
    }
    const com::result right_status = append_dash_run_side(
        segments,
        half_width,
        -1.0,
        join,
        miter_limit,
        start_cap,
        end_cap,
        right);
    if (com::failed(right_status)) {
        return right_status;
    }
    if (left.points.empty() || right.points.empty() ||
        left.edges.size() + 1U != left.points.size() ||
        right.edges.size() + 1U != right.points.size()) {
        return not_implemented;
    }
    outline = {};
    outline.start = left.points.front();
    outline.segments.reserve(
        left.points.size() + right.points.size() + 6U);
    for (std::size_t index = 1U; index < left.points.size(); ++index) {
        if (left.edges[index - 1U].round) {
            const com::result arc_status = append_circular_arc_segments(
                outline,
                left.edges[index - 1U].center,
                left.points[index]);
            if (com::failed(arc_status)) {
                return arc_status;
            }
        } else {
            append_outline_line(outline, left.points[index]);
        }
    }
    const auto& last = segments.back();
    if (end_cap == cap_style::triangle) {
        const double delta_x = static_cast<double>(last.p1.x) - last.p0.x;
        const double delta_y = static_cast<double>(last.p1.y) - last.p0.y;
        const double length = std::hypot(delta_x, delta_y);
        if (length == 0.0) {
            return not_implemented;
        }
        append_outline_line(outline, {
            static_cast<float>(last.p1.x + delta_x / length * half_width),
            static_cast<float>(last.p1.y + delta_y / length * half_width)});
    }
    if (end_cap == cap_style::round) {
        const com::result cap_status = append_round_cap_segments(
            outline,
            {last.p1.x, last.p1.y},
            {last.p0.x, last.p0.y},
            right.points.back(),
            half_width);
        if (com::failed(cap_status)) {
            return cap_status;
        }
    } else {
        append_outline_line(outline, right.points.back());
    }
    for (std::size_t index = right.points.size() - 1U;
         index != 0U;
         --index) {
        const dash_side_edge& edge = right.edges[index - 1U];
        if (edge.round) {
            const com::result arc_status = append_circular_arc_segments(
                outline, edge.center, right.points[index - 1U]);
            if (com::failed(arc_status)) {
                return arc_status;
            }
        } else {
            append_outline_line(outline, right.points[index - 1U]);
        }
    }
    const auto& first = segments.front();
    if (start_cap == cap_style::triangle) {
        const double delta_x = static_cast<double>(first.p1.x) - first.p0.x;
        const double delta_y = static_cast<double>(first.p1.y) - first.p0.y;
        const double length = std::hypot(delta_x, delta_y);
        if (length == 0.0) {
            return not_implemented;
        }
        append_outline_line(outline, {
            static_cast<float>(first.p0.x - delta_x / length * half_width),
            static_cast<float>(first.p0.y - delta_y / length * half_width)});
    }
    if (start_cap == cap_style::round) {
        const com::result cap_status = append_round_cap_segments(
            outline,
            {first.p0.x, first.p0.y},
            {first.p1.x, first.p1.y},
            left.points.front(),
            half_width);
        if (com::failed(cap_status)) {
            return cap_status;
        }
    }
    if (outline.segments.size() < 2U) {
        return not_implemented;
    }
    return com::ok;
}

[[nodiscard]] com::result build_terminal_dash_outline(
    point_2f endpoint,
    point_2f adjacent,
    double half_width,
    cap_style cap,
    bool at_start,
    widened_outline& outline)
{
    const double delta_x = static_cast<double>(endpoint.x) - adjacent.x;
    const double delta_y = static_cast<double>(endpoint.y) - adjacent.y;
    const double length = std::hypot(delta_x, delta_y);
    if (length == 0.0 || cap == cap_style::flat) {
        return not_implemented;
    }
    const double unit_x = delta_x / length;
    const double unit_y = delta_y / length;
    const double normal_x = -unit_y * half_width;
    const double normal_y = unit_x * half_width;
    const double tangent_x = unit_x * half_width;
    const double tangent_y = unit_y * half_width;
    const point_2f positive_normal{
        static_cast<float>(endpoint.x + normal_x),
        static_cast<float>(endpoint.y + normal_y)};
    const point_2f negative_normal{
        static_cast<float>(endpoint.x - normal_x),
        static_cast<float>(endpoint.y - normal_y)};
    const double extension_sign = at_start ? -1.0 : 1.0;
    if (cap == cap_style::round) {
        outline.start = positive_normal;
        const point_2f opposite_adjacent{
            endpoint.x + (endpoint.x - adjacent.x),
            endpoint.y + (endpoint.y - adjacent.y)};
        return append_round_cap_segments(
            outline,
            endpoint,
            at_start ? opposite_adjacent : adjacent,
            negative_normal,
            half_width);
    }
    if (cap == cap_style::triangle) {
        outline.start = positive_normal;
        append_outline_line(outline, negative_normal);
        append_outline_line(outline, {
            static_cast<float>(
                endpoint.x + tangent_x * extension_sign),
            static_cast<float>(
                endpoint.y + tangent_y * extension_sign)});
        return com::ok;
    }
    if (cap != cap_style::square) {
        return com::invalid_argument;
    }
    outline.start = positive_normal;
    append_outline_line(outline, negative_normal);
    append_outline_line(outline, {
        static_cast<float>(
            negative_normal.x + tangent_x * extension_sign),
        static_cast<float>(
            negative_normal.y + tangent_y * extension_sign)});
    append_outline_line(outline, {
        static_cast<float>(
            positive_normal.x + tangent_x * extension_sign),
        static_cast<float>(
            positive_normal.y + tangent_y * extension_sign)});
    return com::ok;
}

[[nodiscard]] com::result transform_widened_outline(
    widened_outline& outline,
    const matrix_3x2_f* transform)
{
    if (transform == nullptr) {
        return com::ok;
    }
    std::vector<point_2f> points;
    points.reserve(1U + outline.segments.size() * 3U);
    points.push_back(outline.start);
    for (const widened_outline_segment& segment : outline.segments) {
        if (segment.cubic) {
            points.push_back(segment.control1);
            points.push_back(segment.control2);
        }
        points.push_back(segment.end);
    }
    const com::result transform_status =
        transform_points_in_place(points, transform);
    if (com::failed(transform_status)) {
        return transform_status;
    }
    std::size_t point_index = 0U;
    outline.start = points[point_index++];
    for (widened_outline_segment& segment : outline.segments) {
        if (segment.cubic) {
            segment.control1 = points[point_index++];
            segment.control2 = points[point_index++];
        }
        segment.end = points[point_index++];
    }
    return point_index == points.size() ? com::ok : failure;
}

[[nodiscard]] com::result emit_joined_dashed_widen(
    std::span<const point_2f> points,
    bool closed,
    float stroke_width,
    stroke_style& style,
    const matrix_3x2_f* transform,
    simplified_geometry_sink& sink)
{
    curve_dash::run_buffer dash_runs;
    const com::result dash_status = create_dashed_polyline_runs(
        points, closed, stroke_width, style, dash_runs);
    if (com::failed(dash_status)) {
        return dash_status;
    }
    std::vector<widened_outline> outlines;
    outlines.reserve(dash_runs.runs.size() + 2U);
    const double half_width = static_cast<double>(stroke_width) * 0.5;
    for (const curve_dash::run& run : dash_runs.runs) {
        if (run.closed) {
            return not_implemented;
        }
        outlines.emplace_back();
        const cap_style start_cap = run.starts_at_source_start
            ? style.GetStartCap()
            : style.GetDashCap();
        const cap_style end_cap = run.ends_at_source_end
            ? style.GetEndCap()
            : style.GetDashCap();
        com::result result = build_joined_dash_outline(
            dash_runs.segments_for(run),
            half_width,
            style.GetLineJoin(),
            style.GetMiterLimit(),
            start_cap,
            end_cap,
            outlines.back());
        if (com::failed(result)) {
            return result;
        }
        result = transform_widened_outline(outlines.back(), transform);
        if (com::failed(result)) {
            return result;
        }
    }
    const auto append_terminal_cap = [&](cap_style cap, bool at_start) {
        outlines.emplace_back();
        com::result result = build_terminal_dash_outline(
            points.back(),
            points[points.size() - 2U],
            half_width,
            cap,
            at_start,
            outlines.back());
        if (com::failed(result)) {
            return result;
        }
        result = transform_widened_outline(outlines.back(), transform);
        if (com::failed(result)) {
            return result;
        }
        return com::ok;
    };
    if (dash_runs.terminal_visible_point) {
        if (style.GetDashCap() != cap_style::flat) {
            const com::result result = append_terminal_cap(
                style.GetDashCap(), true);
            if (com::failed(result)) {
                return result;
            }
        }
        if (style.GetEndCap() != cap_style::flat) {
            const com::result result = append_terminal_cap(
                style.GetEndCap(), false);
            if (com::failed(result)) {
                return result;
            }
        }
    }
    sink.SetFillMode(fill_mode::alternate);
    sink.SetSegmentFlags(path_segment::force_unstroked);
    for (const auto& outline : outlines) {
        sink.BeginFigure(outline.start, figure_begin::filled);
        for (const widened_outline_segment& segment : outline.segments) {
            if (segment.cubic) {
                const bezier_segment bezier{
                    segment.control1,
                    segment.control2,
                    segment.end};
                sink.AddBeziers(&bezier, 1U);
            } else {
                sink.AddLines(&segment.end, 1U);
            }
        }
        sink.EndFigure(figure_end::closed);
    }
    return com::ok;
}

[[nodiscard]] com::result emit_joined_open_solid_widen(
    std::span<const point_2f> points,
    float stroke_width,
    line_join join,
    double miter_limit,
    cap_style start_cap,
    cap_style end_cap,
    const matrix_3x2_f* transform,
    simplified_geometry_sink& sink)
{
    std::vector<progpu_native_path_segment> segments;
    segments.reserve(points.size() - 1U);
    for (std::size_t index = 0U; index + 1U < points.size(); ++index) {
        progpu_native_path_segment segment{};
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        segment.p0 = {points[index].x, points[index].y};
        segment.p1 = {points[index + 1U].x, points[index + 1U].y};
        segments.push_back(segment);
    }
    widened_outline outline;
    com::result result = build_joined_dash_outline(
        segments,
        static_cast<double>(stroke_width) * 0.5,
        join,
        miter_limit,
        start_cap,
        end_cap,
        outline);
    if (com::failed(result)) {
        return result;
    }
    result = transform_widened_outline(outline, transform);
    if (com::failed(result)) {
        return result;
    }
    sink.SetFillMode(fill_mode::alternate);
    sink.SetSegmentFlags(path_segment::force_unstroked);
    sink.BeginFigure(outline.start, figure_begin::filled);
    for (const widened_outline_segment& segment : outline.segments) {
        if (segment.cubic) {
            const bezier_segment bezier{
                segment.control1,
                segment.control2,
                segment.end};
            sink.AddBeziers(&bezier, 1U);
        } else {
            sink.AddLines(&segment.end, 1U);
        }
    }
    sink.EndFigure(figure_end::closed);
    return com::ok;
}

[[nodiscard]] com::result build_default_miter_offset_contour(
    std::span<const point_2f> polygon,
    double offset,
    std::vector<point_2f>& contour) noexcept
{
    try {
        contour.clear();
        contour.reserve(polygon.size());
        constexpr double default_miter_limit = 10.0;
        for (std::size_t index = 0U; index < polygon.size(); ++index) {
            const point_2f previous = polygon[
                (index + polygon.size() - 1U) % polygon.size()];
            const point_2f vertex = polygon[index];
            const point_2f next =
                polygon[(index + 1U) % polygon.size()];
            if (triangle_cross(previous, vertex, next) == 0.0) {
                return not_implemented;
            }
            const double incoming_x =
                static_cast<double>(vertex.x) - previous.x;
            const double incoming_y =
                static_cast<double>(vertex.y) - previous.y;
            const double outgoing_x =
                static_cast<double>(next.x) - vertex.x;
            const double outgoing_y =
                static_cast<double>(next.y) - vertex.y;
            const double incoming_length =
                std::hypot(incoming_x, incoming_y);
            const double outgoing_length =
                std::hypot(outgoing_x, outgoing_y);
            if (incoming_length == 0.0 || outgoing_length == 0.0) {
                return not_implemented;
            }
            const double incoming_unit_x = incoming_x / incoming_length;
            const double incoming_unit_y = incoming_y / incoming_length;
            const double outgoing_unit_x = outgoing_x / outgoing_length;
            const double outgoing_unit_y = outgoing_y / outgoing_length;
            const double denominator =
                incoming_unit_x * outgoing_unit_y -
                incoming_unit_y * outgoing_unit_x;
            if (denominator == 0.0) {
                return not_implemented;
            }
            const point_2f incoming_offset{
                static_cast<float>(vertex.x - incoming_unit_y * offset),
                static_cast<float>(vertex.y + incoming_unit_x * offset)};
            const point_2f outgoing_offset{
                static_cast<float>(vertex.x - outgoing_unit_y * offset),
                static_cast<float>(vertex.y + outgoing_unit_x * offset)};
            const double offset_x =
                static_cast<double>(outgoing_offset.x) - incoming_offset.x;
            const double offset_y =
                static_cast<double>(outgoing_offset.y) - incoming_offset.y;
            const double parameter =
                (offset_x * outgoing_unit_y -
                    offset_y * outgoing_unit_x) /
                denominator;
            const point_2f miter{
                static_cast<float>(
                    incoming_offset.x + incoming_unit_x * parameter),
                static_cast<float>(
                    incoming_offset.y + incoming_unit_y * parameter)};
            const double miter_length = std::hypot(
                static_cast<double>(miter.x) - vertex.x,
                static_cast<double>(miter.y) - vertex.y);
            if (!finite_point(miter) || miter_length >
                default_miter_limit * std::abs(offset)) {
                return not_implemented;
            }
            contour.push_back(miter);
        }
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
}

[[nodiscard]] com::result transform_points_in_place(
    std::vector<point_2f>& points,
    const matrix_3x2_f* transform) noexcept
{
    if (transform == nullptr) {
        return com::ok;
    }
    std::size_t index = 0U;
#if defined(PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_NEON)
    static_assert(sizeof(point_2f) == sizeof(float) * 2U);
    for (; index + 4U <= points.size(); index += 4U) {
        float* destination =
            reinterpret_cast<float*>(points.data() + index);
        const float32x4x2_t source = vld2q_f32(destination);
        float32x4x2_t transformed{};
        transformed.val[0] = vaddq_f32(
            vaddq_f32(
                vmulq_n_f32(source.val[0], transform->m11),
                vmulq_n_f32(source.val[1], transform->m21)),
            vdupq_n_f32(transform->m31));
        transformed.val[1] = vaddq_f32(
            vaddq_f32(
                vmulq_n_f32(source.val[0], transform->m12),
                vmulq_n_f32(source.val[1], transform->m22)),
            vdupq_n_f32(transform->m32));
        vst2q_f32(destination, transformed);
    }
#elif defined(PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_SSE2)
    static_assert(sizeof(point_2f) == sizeof(float) * 2U);
    for (; index + 4U <= points.size(); index += 4U) {
        float* destination =
            reinterpret_cast<float*>(points.data() + index);
        const __m128 first = _mm_loadu_ps(destination);
        const __m128 second = _mm_loadu_ps(destination + 4U);
        const __m128 source_x =
            _mm_shuffle_ps(first, second, _MM_SHUFFLE(2, 0, 2, 0));
        const __m128 source_y =
            _mm_shuffle_ps(first, second, _MM_SHUFFLE(3, 1, 3, 1));
        const __m128 transformed_x = _mm_add_ps(
            _mm_add_ps(
                _mm_mul_ps(source_x, _mm_set1_ps(transform->m11)),
                _mm_mul_ps(source_y, _mm_set1_ps(transform->m21))),
            _mm_set1_ps(transform->m31));
        const __m128 transformed_y = _mm_add_ps(
            _mm_add_ps(
                _mm_mul_ps(source_x, _mm_set1_ps(transform->m12)),
                _mm_mul_ps(source_y, _mm_set1_ps(transform->m22))),
            _mm_set1_ps(transform->m32));
        _mm_storeu_ps(
            destination, _mm_unpacklo_ps(transformed_x, transformed_y));
        _mm_storeu_ps(
            destination + 4U,
            _mm_unpackhi_ps(transformed_x, transformed_y));
    }
#endif
    for (; index < points.size(); ++index) {
        point_2f transformed{};
        const com::result result = transform_point(
            points[index], transform, &transformed);
        if (com::failed(result)) {
            return result;
        }
        points[index] = transformed;
    }
    return std::all_of(
               points.begin(),
               points.end(),
               finite_point)
        ? com::ok
        : com::invalid_argument;
}

[[nodiscard]] polygon_edge_bounds
make_polygon_edge_bounds(std::span<const point_2f> polygon) {
  polygon_edge_bounds result{};
  result.edge_count = polygon.size();
  const std::size_t padded_count = (polygon.size() + 3U) & ~std::size_t{3U};
  result.minimum_x.resize(padded_count, std::numeric_limits<float>::infinity());
  result.minimum_y.resize(padded_count, std::numeric_limits<float>::infinity());
  result.maximum_x.resize(padded_count,
                          -std::numeric_limits<float>::infinity());
  result.maximum_y.resize(padded_count,
                          -std::numeric_limits<float>::infinity());
  for (std::size_t index = 0U; index < polygon.size(); ++index) {
    const point_2f start = polygon[index];
    const point_2f end = polygon[(index + 1U) % polygon.size()];
    result.minimum_x[index] = std::min(start.x, end.x);
    result.minimum_y[index] = std::min(start.y, end.y);
    result.maximum_x[index] = std::max(start.x, end.x);
    result.maximum_y[index] = std::max(start.y, end.y);
  }
  return result;
}

[[nodiscard]] std::uint32_t
polygon_edge_overlap_mask(float minimum_x, float minimum_y, float maximum_x,
                          float maximum_y,
                          const polygon_edge_bounds &candidates,
                          std::size_t candidate_offset) noexcept {
#if defined(PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_NEON)
  const float32x4_t candidate_minimum_x =
      vld1q_f32(candidates.minimum_x.data() + candidate_offset);
  const float32x4_t candidate_minimum_y =
      vld1q_f32(candidates.minimum_y.data() + candidate_offset);
  const float32x4_t candidate_maximum_x =
      vld1q_f32(candidates.maximum_x.data() + candidate_offset);
  const float32x4_t candidate_maximum_y =
      vld1q_f32(candidates.maximum_y.data() + candidate_offset);
  const uint32x4_t overlap = vandq_u32(
      vandq_u32(vcleq_f32(candidate_minimum_x, vdupq_n_f32(maximum_x)),
                vcgeq_f32(candidate_maximum_x, vdupq_n_f32(minimum_x))),
      vandq_u32(vcleq_f32(candidate_minimum_y, vdupq_n_f32(maximum_y)),
                vcgeq_f32(candidate_maximum_y, vdupq_n_f32(minimum_y))));
  return (vgetq_lane_u32(overlap, 0) != 0U ? 1U : 0U) |
         (vgetq_lane_u32(overlap, 1) != 0U ? 2U : 0U) |
         (vgetq_lane_u32(overlap, 2) != 0U ? 4U : 0U) |
         (vgetq_lane_u32(overlap, 3) != 0U ? 8U : 0U);
#elif defined(PROGPU_NATIVE_DIRECT2D_PATH_INTRINSICS_SSE2)
  const __m128 overlap = _mm_and_ps(
      _mm_and_ps(_mm_cmple_ps(_mm_loadu_ps(candidates.minimum_x.data() +
                                           candidate_offset),
                              _mm_set1_ps(maximum_x)),
                 _mm_cmpge_ps(_mm_loadu_ps(candidates.maximum_x.data() +
                                           candidate_offset),
                              _mm_set1_ps(minimum_x))),
      _mm_and_ps(_mm_cmple_ps(_mm_loadu_ps(candidates.minimum_y.data() +
                                           candidate_offset),
                              _mm_set1_ps(maximum_y)),
                 _mm_cmpge_ps(_mm_loadu_ps(candidates.maximum_y.data() +
                                           candidate_offset),
                              _mm_set1_ps(minimum_y))));
  return static_cast<std::uint32_t>(_mm_movemask_ps(overlap));
#else
  std::uint32_t result = 0U;
  for (std::size_t lane = 0U; lane < 4U; ++lane) {
    const std::size_t index = candidate_offset + lane;
    if (candidates.minimum_x[index] <= maximum_x &&
        candidates.maximum_x[index] >= minimum_x &&
        candidates.minimum_y[index] <= maximum_y &&
        candidates.maximum_y[index] >= minimum_y) {
      result |= 1U << lane;
    }
  }
  return result;
#endif
}

[[nodiscard]] double
polygon_coordinate_tolerance(std::span<const point_2f> polygon,
                             point_2f point) noexcept {
  double scale = std::max({1.0, std::abs(static_cast<double>(point.x)),
                           std::abs(static_cast<double>(point.y))});
  for (const point_2f vertex : polygon) {
    scale = std::max(scale, std::max(std::abs(static_cast<double>(vertex.x)),
                                     std::abs(static_cast<double>(vertex.y))));
  }
  return 64.0 * std::numeric_limits<float>::epsilon() * scale;
}

[[nodiscard]] bool same_polygon_boolean_point(point_2f first, point_2f second,
                                              double tolerance) noexcept {
  return std::abs(static_cast<double>(first.x) - second.x) <= tolerance &&
         std::abs(static_cast<double>(first.y) - second.y) <= tolerance;
}

[[nodiscard]] polygon_point_relation
classify_polygon_point(std::span<const point_2f> polygon,
                       point_2f point) noexcept {
  const double coordinate_tolerance =
      polygon_coordinate_tolerance(polygon, point);
  for (std::size_t edge = 0U; edge < polygon.size(); ++edge) {
    const point_2f start = polygon[edge];
    const point_2f end = polygon[(edge + 1U) % polygon.size()];
    const double x = static_cast<double>(end.x) - start.x;
    const double y = static_cast<double>(end.y) - start.y;
    const double length = std::hypot(x, y);
    if (std::abs(triangle_cross(start, end, point)) >
        coordinate_tolerance * std::max(1.0, length)) {
      continue;
    }
    const double point_x = static_cast<double>(point.x) - start.x;
    const double point_y = static_cast<double>(point.y) - start.y;
    const double projection = point_x * x + point_y * y;
    const double squared_length = x * x + y * y;
    const double projection_tolerance =
        coordinate_tolerance * std::max(1.0, length);
    if (projection >= -projection_tolerance &&
        projection <= squared_length + projection_tolerance) {
      return polygon_point_relation::boundary;
    }
  }
  return polygon_contains_point(polygon, point)
             ? polygon_point_relation::inside
             : polygon_point_relation::outside;
}

[[nodiscard]] bool
normalize_simple_polygon(std::vector<point_2f> &polygon) noexcept {
  while (polygon.size() > 1U && same_point(polygon.front(), polygon.back())) {
    polygon.pop_back();
  }
  polygon.erase(std::unique(polygon.begin(), polygon.end(), same_point),
                polygon.end());
  if (polygon.size() < 3U) {
    return false;
  }
  double twice_area = 0.0;
  for (std::size_t index = 0U; index < polygon.size(); ++index) {
    const point_2f current = polygon[index];
    const point_2f next = polygon[(index + 1U) % polygon.size()];
    twice_area += static_cast<double>(current.x) * next.y -
                  static_cast<double>(next.x) * current.y;
  }
  if (!std::isfinite(twice_area) || twice_area == 0.0) {
    return false;
  }
  if (twice_area < 0.0) {
    std::reverse(polygon.begin(), polygon.end());
  }
  for (std::size_t first = 0U; first < polygon.size(); ++first) {
    const std::size_t first_next = (first + 1U) % polygon.size();
    for (std::size_t second = first + 1U; second < polygon.size(); ++second) {
      const std::size_t second_next = (second + 1U) % polygon.size();
      if (first_next == second || second_next == first) {
        continue;
      }
      if (segments_intersect(polygon[first], polygon[first_next],
                             polygon[second], polygon[second_next])) {
        return false;
      }
    }
  }
  return true;
}

[[nodiscard]] point_2f
interpolate_polygon_boolean_point(point_2f start, point_2f end,
                                  double parameter) noexcept {
  return {
      static_cast<float>(static_cast<double>(start.x) +
                         (static_cast<double>(end.x) - start.x) * parameter),
      static_cast<float>(static_cast<double>(start.y) +
                         (static_cast<double>(end.y) - start.y) * parameter)};
}

void append_polygon_boolean_parameter(std::vector<double> &parameters,
                                      double value) {
  value = std::clamp(value, 0.0, 1.0);
  constexpr double parameter_tolerance = 1.0e-10;
  if (std::any_of(parameters.begin(), parameters.end(),
                  [value](double existing) {
                    return std::abs(existing - value) <= parameter_tolerance;
                  })) {
    return;
  }
  parameters.push_back(value);
}

[[nodiscard]] com::result triangulate_simple_polygon(
    std::span<const point_2f> source,
    std::vector<triangle>& triangles) noexcept
{
    try {
        std::vector<point_2f> points;
        points.reserve(source.size());
        for (const point_2f point : source) {
            if (points.empty() || !same_point(points.back(), point)) {
                points.push_back(point);
            }
        }
        if (points.size() > 1U && same_point(points.front(), points.back())) {
            points.pop_back();
        }
        bool changed = true;
        while (changed && points.size() >= 3U) {
            changed = false;
            for (std::size_t index = 0U; index < points.size(); ++index) {
                const point_2f previous = points[
                    (index + points.size() - 1U) % points.size()];
                const point_2f current = points[index];
                const point_2f next = points[(index + 1U) % points.size()];
                if (triangle_cross(previous, current, next) == 0.0) {
                    points.erase(points.begin() +
                        static_cast<std::ptrdiff_t>(index));
                    changed = true;
                    break;
                }
            }
        }
        if (points.size() < 3U) {
            return com::ok;
        }
        double twice_area = 0.0;
        for (std::size_t index = 0U; index < points.size(); ++index) {
            const point_2f current = points[index];
            const point_2f next = points[(index + 1U) % points.size()];
            twice_area += static_cast<double>(current.x) * next.y -
                static_cast<double>(next.x) * current.y;
        }
        if (twice_area == 0.0) {
            return com::ok;
        }
        const bool counter_clockwise = twice_area > 0.0;
        std::vector<std::size_t> remaining(points.size());
        for (std::size_t index = 0U; index < remaining.size(); ++index) {
            remaining[index] = index;
        }
        triangles.reserve(triangles.size() + points.size() - 2U);
        while (remaining.size() > 3U) {
            bool found_ear = false;
            for (std::size_t offset = 0U; offset < remaining.size(); ++offset) {
                const std::size_t previous_index = remaining[
                    (offset + remaining.size() - 1U) % remaining.size()];
                const std::size_t current_index = remaining[offset];
                const std::size_t next_index =
                    remaining[(offset + 1U) % remaining.size()];
                const point_2f previous = points[previous_index];
                const point_2f current = points[current_index];
                const point_2f next = points[next_index];
                const double cross = triangle_cross(previous, current, next);
                if ((counter_clockwise && cross <= 0.0) ||
                    (!counter_clockwise && cross >= 0.0)) {
                    continue;
                }
                bool contains_vertex = false;
                for (const std::size_t candidate : remaining) {
                    if (candidate == previous_index ||
                        candidate == current_index ||
                        candidate == next_index) {
                        continue;
                    }
                    if (point_in_triangle(
                            points[candidate],
                            previous,
                            current,
                            next,
                            counter_clockwise)) {
                        contains_vertex = true;
                        break;
                    }
                }
                if (contains_vertex) {
                    continue;
                }
                triangles.push_back({previous, current, next});
                remaining.erase(remaining.begin() +
                    static_cast<std::ptrdiff_t>(offset));
                found_ear = true;
                break;
            }
            if (!found_ear) {
                // Ear selection is topology-dependent; there are no
                // independent lanes to vectorize. Fail closed for a
                // self-intersecting or numerically ambiguous contour.
                return not_implemented;
            }
        }
        triangles.push_back({
            points[remaining[0U]],
            points[remaining[1U]],
            points[remaining[2U]]});
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return failure;
    }
}

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
        if (!closed()) {
            return wrong_state;
        }
        if (!std::isfinite(stroke_width) || stroke_width < 0.0F ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        bool dashed = false;
        line_join join = line_join::miter;
        double miter_limit = 10.0;
        cap_style start_cap = cap_style::flat;
        cap_style end_cap = cap_style::flat;
        if (style != nullptr) {
            factory* raw_style_factory = nullptr;
            style->GetFactory(&raw_style_factory);
            com::pointer<factory> style_factory;
            style_factory.attach(raw_style_factory);
            if (style_factory.get() != owner_.get()) {
                return wrong_factory;
            }
            dashed = style->GetDashStyle() != dash_style::solid;
            join = style->GetLineJoin();
            miter_limit = style->GetMiterLimit();
            start_cap = style->GetStartCap();
            end_cap = style->GetEndCap();
            if (style->GetLineJoin() > line_join::miter_or_bevel ||
                !std::isfinite(style->GetMiterLimit()) ||
                style->GetMiterLimit() < 1.0F ||
                style->GetStartCap() > cap_style::triangle ||
                style->GetEndCap() > cap_style::triangle ||
                style->GetDashCap() > cap_style::triangle ||
                !std::isfinite(style->GetDashOffset())) {
                return com::invalid_argument;
            }
        }
        if (data_->figures.size() != 1U) {
            return not_implemented;
        }
        const bool closed_figure =
            data_->figures.front().end == figure_end::closed;
        try {
            std::vector<flat_edge> edges;
            const com::result edge_status = collect_flat_edges(
                nullptr, flattening_tolerance, false, edges);
            if (com::failed(edge_status)) {
                return edge_status;
            }
            std::vector<point_2f> polygon;
            for (const auto& edge : edges) {
                if (edge.figure_index != 0U ||
                    edge.flags != path_segment::none ||
                    same_point(edge.start, edge.end)) {
                    if (edge.flags != path_segment::none) {
                        return not_implemented;
                    }
                    continue;
                }
                if (polygon.empty()) {
                    polygon.push_back(edge.start);
                } else if (!same_point(polygon.back(), edge.start)) {
                    return not_implemented;
                }
                polygon.push_back(edge.end);
            }
            if (closed_figure) {
                if (!normalize_simple_polygon(polygon)) {
                    return not_implemented;
                }
            } else {
                polygon.erase(
                    std::unique(polygon.begin(), polygon.end(), same_point),
                    polygon.end());
                if (polygon.size() < 2U) {
                    return not_implemented;
                }
            }
            if (dashed && stroke_width != 0.0F) {
                return dashed_polyline_widened_bounds(
                    polygon,
                    closed_figure,
                    stroke_width,
                    *style,
                    world_transform,
                    *bounds);
            }
            return solid_polyline_widened_bounds(
                polygon,
                closed_figure,
                stroke_width,
                join,
                miter_limit,
                start_cap,
                end_cap,
                world_transform,
                *bounds);
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
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
        if (!closed()) {
            return wrong_state;
        }
        if (!finite_point(point) || !std::isfinite(stroke_width) ||
            stroke_width < 0.0F ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        line_join join = line_join::miter;
        double miter_limit = 10.0;
        cap_style start_cap = cap_style::flat;
        cap_style end_cap = cap_style::flat;
        bool dashed = false;
        if (style != nullptr) {
            factory* raw_style_factory = nullptr;
            style->GetFactory(&raw_style_factory);
            com::pointer<factory> style_factory;
            style_factory.attach(raw_style_factory);
            if (style_factory.get() != owner_.get()) {
                return wrong_factory;
            }
            join = style->GetLineJoin();
            miter_limit = style->GetMiterLimit();
            start_cap = style->GetStartCap();
            end_cap = style->GetEndCap();
            dashed = style->GetDashStyle() != dash_style::solid;
            if (join > line_join::miter_or_bevel ||
                !std::isfinite(miter_limit) || miter_limit < 1.0 ||
                style->GetStartCap() > cap_style::triangle ||
                style->GetEndCap() > cap_style::triangle ||
                style->GetDashCap() > cap_style::triangle ||
                !std::isfinite(style->GetDashOffset())) {
                return com::invalid_argument;
            }
        }
        if (data_->figures.size() != 1U) {
            return not_implemented;
        }
        const bool closed_figure =
            data_->figures.front().end == figure_end::closed;
        point_2f local_point{};
        if (!transform_to_local_point(
                point, world_transform, local_point)) {
            return not_implemented;
        }
        try {
            std::vector<flat_edge> edges;
            const com::result edge_status = collect_flat_edges(
                nullptr, flattening_tolerance, false, edges);
            if (com::failed(edge_status)) {
                return edge_status;
            }
            std::vector<point_2f> polygon;
            for (const auto& edge : edges) {
                if (edge.figure_index != 0U ||
                    edge.flags != path_segment::none ||
                    same_point(edge.start, edge.end)) {
                    if (edge.flags != path_segment::none) {
                        return not_implemented;
                    }
                    continue;
                }
                if (polygon.empty()) {
                    polygon.push_back(edge.start);
                } else if (!same_point(polygon.back(), edge.start)) {
                    return not_implemented;
                }
                polygon.push_back(edge.end);
            }
            if (closed_figure) {
                if (!normalize_simple_polygon(polygon)) {
                    return not_implemented;
                }
            } else {
                polygon.erase(
                    std::unique(polygon.begin(), polygon.end(), same_point),
                    polygon.end());
                if (polygon.size() < 2U) {
                    return not_implemented;
                }
            }
            const float half_width = stroke_width * 0.5F;
            if (half_width == 0.0F) {
                return com::ok;
            }
            if (dashed) {
                return closed_figure
                    ? dashed_polygon_stroke_contains(
                        polygon,
                        local_point,
                        stroke_width,
                        *style,
                        join,
                        miter_limit,
                        *contains)
                    : dashed_open_polyline_stroke_contains(
                        polygon,
                        local_point,
                        stroke_width,
                        *style,
                        join,
                        miter_limit,
                        *contains);
            }
            const polygon_stroke_edges stroke_edges =
                make_polyline_stroke_edges(polygon, closed_figure);
            if (polygon_stroke_body_contains(
                    stroke_edges, local_point, half_width)) {
                *contains = 1;
                return com::ok;
            }
            const double half_width_double = half_width;
            const std::size_t first_join = closed_figure ? 0U : 1U;
            const std::size_t join_end =
                closed_figure ? polygon.size() : polygon.size() - 1U;
            for (std::size_t index = first_join;
                 index < join_end;
                 ++index) {
                const point_2f previous = polygon[closed_figure
                    ? (index + polygon.size() - 1U) % polygon.size()
                    : index - 1U];
                const point_2f vertex = polygon[index];
                const point_2f next = polygon[closed_figure
                    ? (index + 1U) % polygon.size()
                    : index + 1U];
                if (stroke_join_contains(
                        previous,
                        vertex,
                        next,
                        local_point,
                        half_width_double,
                        join,
                        miter_limit)) {
                    *contains = 1;
                    return com::ok;
                }
            }
            if (!closed_figure &&
                (stroke_cap_contains(
                     polygon.front(),
                     polygon[1U],
                     local_point,
                     half_width_double,
                     start_cap) ||
                 stroke_cap_contains(
                     polygon.back(),
                     polygon[polygon.size() - 2U],
                     local_point,
                     half_width_double,
                     end_cap))) {
                *contains = 1;
            }
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
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
        geometry* input,
        const matrix_3x2_f* input_transform,
        float flattening_tolerance,
        geometry_relation* relation) const noexcept override
    {
        if (relation == nullptr) {
            return com::pointer_error;
        }
        *relation = geometry_relation::unknown;
        if (!closed()) {
            return wrong_state;
        }
        if (input == nullptr ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(input_transform)) {
            return com::invalid_argument;
        }
        factory* raw_input_factory = nullptr;
        input->GetFactory(&raw_input_factory);
        com::pointer<factory> input_factory;
        input_factory.attach(raw_input_factory);
        if (input_factory.get() != owner_.get()) {
            return wrong_factory;
        }

        try {
            std::uint32_t source_figure =
                (std::numeric_limits<std::uint32_t>::max)();
            for (std::size_t index = 0U;
                 index < data_->figures.size();
                 ++index) {
                if (data_->figures[index].begin != figure_begin::filled) {
                    continue;
                }
                if (source_figure !=
                    (std::numeric_limits<std::uint32_t>::max)()) {
                    return not_implemented;
                }
                source_figure = static_cast<std::uint32_t>(index);
            }
            if (source_figure ==
                (std::numeric_limits<std::uint32_t>::max)()) {
                return not_implemented;
            }
            std::vector<flat_edge> source_edges;
            com::result result = collect_flat_edges(
                nullptr, flattening_tolerance, true, source_edges);
            if (com::failed(result)) {
                return result;
            }
            std::vector<point_2f> first;
            for (const auto& edge : source_edges) {
                if (edge.figure_index != source_figure ||
                    same_point(edge.start, edge.end)) {
                    continue;
                }
                if (first.empty()) {
                    first.push_back(edge.start);
                } else if (!same_point(first.back(), edge.start)) {
                    return not_implemented;
                }
                first.push_back(edge.end);
            }
            if (!normalize_simple_polygon(first)) {
                return not_implemented;
            }

            auto* raw_input_sink = new (std::nothrow) single_polygon_sink();
            if (raw_input_sink == nullptr) {
                return com::out_of_memory;
            }
            com::pointer<single_polygon_sink> input_sink;
            input_sink.attach(raw_input_sink);
            result = input->Simplify(
                geometry_simplification_option::lines,
                input_transform,
                flattening_tolerance,
                input_sink.get());
            if (com::failed(result)) {
                return result;
            }
            result = raw_input_sink->status();
            if (com::failed(result)) {
                return result;
            }
            std::vector<point_2f> second = raw_input_sink->points();
            if (!normalize_simple_polygon(second)) {
                return not_implemented;
            }

            const polygon_edge_bounds second_bounds =
                make_polygon_edge_bounds(second);
            for (std::size_t first_edge = 0U;
                 first_edge < first.size();
                 ++first_edge) {
                const point_2f first_start = first[first_edge];
                const point_2f first_end =
                    first[(first_edge + 1U) % first.size()];
                const float minimum_x =
                    std::min(first_start.x, first_end.x);
                const float minimum_y =
                    std::min(first_start.y, first_end.y);
                const float maximum_x =
                    std::max(first_start.x, first_end.x);
                const float maximum_y =
                    std::max(first_start.y, first_end.y);
                for (std::size_t second_block = 0U;
                     second_block < second_bounds.minimum_x.size();
                     second_block += 4U) {
                    std::uint32_t mask = polygon_edge_overlap_mask(
                        minimum_x,
                        minimum_y,
                        maximum_x,
                        maximum_y,
                        second_bounds,
                        second_block);
                    while (mask != 0U) {
                        std::uint32_t lane = 0U;
                        while ((mask & (1U << lane)) == 0U) {
                            ++lane;
                        }
                        mask &= mask - 1U;
                        const std::size_t second_edge = second_block + lane;
                        if (second_edge < second_bounds.edge_count &&
                            segments_intersect(
                                first_start,
                                first_end,
                                second[second_edge],
                                second[(second_edge + 1U) % second.size()])) {
                            *relation = geometry_relation::overlap;
                            return com::ok;
                        }
                    }
                }
            }

            bool first_inside = false;
            bool first_outside = false;
            bool first_boundary = false;
            for (const point_2f point : first) {
                const polygon_point_relation point_relation =
                    classify_polygon_point(second, point);
                first_inside |= point_relation == polygon_point_relation::inside;
                first_outside |= point_relation == polygon_point_relation::outside;
                first_boundary |= point_relation == polygon_point_relation::boundary;
            }
            bool second_inside = false;
            bool second_outside = false;
            bool second_boundary = false;
            for (const point_2f point : second) {
                const polygon_point_relation point_relation =
                    classify_polygon_point(first, point);
                second_inside |= point_relation == polygon_point_relation::inside;
                second_outside |= point_relation == polygon_point_relation::outside;
                second_boundary |= point_relation == polygon_point_relation::boundary;
            }
            if (first_boundary && !first_inside && !first_outside &&
                second_boundary && !second_inside && !second_outside) {
                *relation = geometry_relation::is_contained;
                return com::ok;
            }
            if (first_boundary || second_boundary) {
                *relation = geometry_relation::overlap;
                return com::ok;
            }
            if ((first_inside && first_outside) ||
                (second_inside && second_outside)) {
                return not_implemented;
            }
            if (first_inside) {
                *relation = geometry_relation::is_contained;
            } else if (second_inside) {
                *relation = geometry_relation::contains;
            } else {
                *relation = geometry_relation::disjoint;
            }
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
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
        const matrix_3x2_f* world_transform,
        float flattening_tolerance,
        tessellation_sink* sink) const noexcept override
    {
        if (sink == nullptr) {
            return com::pointer_error;
        }
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
            std::vector<std::vector<point_2f>> polygons(
                data_->figures.size());
            for (const auto& edge : edges) {
                if (data_->figures[edge.figure_index].begin !=
                    figure_begin::filled) {
                    continue;
                }
                auto& polygon = polygons[edge.figure_index];
                if (polygon.empty()) {
                    polygon.push_back(edge.start);
                }
                polygon.push_back(edge.end);
            }
            polygons.erase(
                std::remove_if(
                    polygons.begin(),
                    polygons.end(),
                    [](const std::vector<point_2f>& polygon) {
                        return polygon.size() < 4U;
                    }),
                polygons.end());
            for (std::size_t first = 0U; first < polygons.size(); ++first) {
                const auto first_polygon = std::span(polygons[first]);
                const std::size_t first_edge_count =
                    same_point(first_polygon.front(), first_polygon.back())
                    ? first_polygon.size() - 1U
                    : first_polygon.size();
                for (std::size_t left = 0U;
                     left < first_edge_count;
                     ++left) {
                    const std::size_t left_next =
                        (left + 1U) % first_edge_count;
                    for (std::size_t right = left + 1U;
                         right < first_edge_count;
                         ++right) {
                        const std::size_t right_next =
                            (right + 1U) % first_edge_count;
                        if (left_next == right || right_next == left) {
                            continue;
                        }
                        if (segments_intersect(
                                first_polygon[left],
                                first_polygon[left_next],
                                first_polygon[right],
                                first_polygon[right_next])) {
                            return not_implemented;
                        }
                    }
                }
                for (std::size_t second = first + 1U;
                     second < polygons.size();
                     ++second) {
                    const auto second_polygon = std::span(polygons[second]);
                    const std::size_t second_edge_count = same_point(
                            second_polygon.front(), second_polygon.back())
                        ? second_polygon.size() - 1U
                        : second_polygon.size();
                    for (std::size_t left = 0U;
                         left < first_edge_count;
                         ++left) {
                        for (std::size_t right = 0U;
                             right < second_edge_count;
                             ++right) {
                            if (segments_intersect(
                                    first_polygon[left],
                                    first_polygon[(left + 1U) %
                                        first_edge_count],
                                    second_polygon[right],
                                    second_polygon[(right + 1U) %
                                        second_edge_count])) {
                                return not_implemented;
                            }
                        }
                    }
                    if (polygon_contains_point(
                            first_polygon.first(first_edge_count),
                            second_polygon.front()) ||
                        polygon_contains_point(
                            second_polygon.first(second_edge_count),
                            first_polygon.front())) {
                        // Nested contours require a hole-aware triangulator.
                        // Preserve exact fill-mode semantics by failing closed.
                        return not_implemented;
                    }
                }
            }
            std::vector<triangle> triangles;
            for (const auto& polygon : polygons) {
                const com::result result = triangulate_simple_polygon(
                    polygon, triangles);
                if (com::failed(result)) {
                    return result;
                }
            }
            if (triangles.size() >
                (std::numeric_limits<std::uint32_t>::max)()) {
                return com::out_of_memory;
            }
            if (!triangles.empty()) {
                sink->AddTriangles(
                    triangles.data(),
                    static_cast<std::uint32_t>(triangles.size()));
            }
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
    }

  com::result PROGPU_NATIVE_COM_CALL CombineWithGeometry(
      geometry *input, combine_mode mode, const matrix_3x2_f *input_transform,
      float flattening_tolerance,
      simplified_geometry_sink *sink) const noexcept override {
    if (sink == nullptr) {
      return com::pointer_error;
    }
    if (!closed()) {
      return wrong_state;
    }
    if (input == nullptr ||
        (mode != combine_mode::union_value && mode != combine_mode::intersect &&
         mode != combine_mode::xor_value && mode != combine_mode::exclude) ||
        !valid_tolerance(flattening_tolerance) ||
        !core::valid_transform(input_transform)) {
      return com::invalid_argument;
    }
    factory *raw_input_factory = nullptr;
    input->GetFactory(&raw_input_factory);
    com::pointer<factory> input_factory;
    input_factory.attach(raw_input_factory);
    if (input_factory.get() != owner_.get()) {
      return wrong_factory;
    }

    try {
      std::uint32_t source_figure = (std::numeric_limits<std::uint32_t>::max)();
      for (std::size_t index = 0U; index < data_->figures.size(); ++index) {
        if (data_->figures[index].begin != figure_begin::filled) {
          continue;
        }
        if (source_figure != (std::numeric_limits<std::uint32_t>::max)()) {
          return not_implemented;
        }
        source_figure = static_cast<std::uint32_t>(index);
      }
      if (source_figure == (std::numeric_limits<std::uint32_t>::max)()) {
        return not_implemented;
      }
      std::vector<flat_edge> source_edges;
      com::result result =
          collect_flat_edges(nullptr, flattening_tolerance, true, source_edges);
      if (com::failed(result)) {
        return result;
      }
      std::vector<point_2f> first;
      for (const auto &edge : source_edges) {
        if (edge.figure_index != source_figure ||
            same_point(edge.start, edge.end)) {
          continue;
        }
        if (first.empty()) {
          first.push_back(edge.start);
        } else if (!same_point(first.back(), edge.start)) {
          return not_implemented;
        }
        first.push_back(edge.end);
      }
      if (!normalize_simple_polygon(first)) {
        return not_implemented;
      }

      auto *raw_input_sink = new (std::nothrow) single_polygon_sink();
      if (raw_input_sink == nullptr) {
        return com::out_of_memory;
      }
      com::pointer<single_polygon_sink> input_sink;
      input_sink.attach(raw_input_sink);
      result = input->Simplify(geometry_simplification_option::lines,
                               input_transform, flattening_tolerance,
                               input_sink.get());
      if (com::failed(result)) {
        return result;
      }
      result = raw_input_sink->status();
      if (com::failed(result)) {
        return result;
      }
      std::vector<point_2f> second = raw_input_sink->points();
      if (!normalize_simple_polygon(second)) {
        return not_implemented;
      }

      constexpr double intersection_tolerance = 1.0e-10;
      constexpr std::size_t maximum_boundary_segments = 1U << 20U;
      std::vector<std::vector<double>> first_parameters(first.size());
      std::vector<std::vector<double>> second_parameters(second.size());
      for (auto &parameters : first_parameters) {
        parameters = {0.0, 1.0};
      }
      for (auto &parameters : second_parameters) {
        parameters = {0.0, 1.0};
      }
      const polygon_edge_bounds second_edge_bounds =
          make_polygon_edge_bounds(second);
      for (std::size_t first_edge = 0U; first_edge < first.size();
           ++first_edge) {
        const point_2f first_start = first[first_edge];
        const point_2f first_end = first[(first_edge + 1U) % first.size()];
        const double first_x = static_cast<double>(first_end.x) - first_start.x;
        const double first_y = static_cast<double>(first_end.y) - first_start.y;
        const float first_minimum_x = std::min(first_start.x, first_end.x);
        const float first_minimum_y = std::min(first_start.y, first_end.y);
        const float first_maximum_x = std::max(first_start.x, first_end.x);
        const float first_maximum_y = std::max(first_start.y, first_end.y);
        for (std::size_t second_block = 0U;
             second_block < second_edge_bounds.minimum_x.size();
             second_block += 4U) {
          std::uint32_t overlap_mask = polygon_edge_overlap_mask(
              first_minimum_x, first_minimum_y, first_maximum_x,
              first_maximum_y, second_edge_bounds, second_block);
          while (overlap_mask != 0U) {
            std::uint32_t lane = 0U;
            while ((overlap_mask & (1U << lane)) == 0U) {
              ++lane;
            }
            overlap_mask &= overlap_mask - 1U;
            const std::size_t second_edge = second_block + lane;
            if (second_edge >= second_edge_bounds.edge_count) {
              continue;
            }
            const point_2f second_start = second[second_edge];
            const point_2f second_end =
                second[(second_edge + 1U) % second.size()];
            const double second_x =
                static_cast<double>(second_end.x) - second_start.x;
            const double second_y =
                static_cast<double>(second_end.y) - second_start.y;
            const double offset_x =
                static_cast<double>(second_start.x) - first_start.x;
            const double offset_y =
                static_cast<double>(second_start.y) - first_start.y;
            const double denominator = first_x * second_y - first_y * second_x;
            if (denominator == 0.0) {
              if (offset_x * first_y - offset_y * first_x != 0.0) {
                continue;
              }
              const bool use_x = std::abs(first_x) >= std::abs(first_y);
              const double first_axis_start =
                  use_x ? first_start.x : first_start.y;
              const double first_axis_end = use_x ? first_end.x : first_end.y;
              const double second_axis_start =
                  use_x ? second_start.x : second_start.y;
              const double second_axis_end =
                  use_x ? second_end.x : second_end.y;
              const double overlap =
                  std::min(std::max(first_axis_start, first_axis_end),
                           std::max(second_axis_start, second_axis_end)) -
                  std::max(std::min(first_axis_start, first_axis_end),
                           std::min(second_axis_start, second_axis_end));
              if (overlap <= 0.0) {
                continue;
              }
              const double first_axis_delta = first_axis_end - first_axis_start;
              const double second_axis_delta =
                  second_axis_end - second_axis_start;
              const auto append_if_on_edge = [](auto &parameters,
                                                double value) {
                if (value < -intersection_tolerance ||
                    value > 1.0 + intersection_tolerance) {
                  return;
                }
                append_polygon_boolean_parameter(parameters, value);
              };
              append_if_on_edge(first_parameters[first_edge],
                                (second_axis_start - first_axis_start) /
                                    first_axis_delta);
              append_if_on_edge(first_parameters[first_edge],
                                (second_axis_end - first_axis_start) /
                                    first_axis_delta);
              append_if_on_edge(second_parameters[second_edge],
                                (first_axis_start - second_axis_start) /
                                    second_axis_delta);
              append_if_on_edge(second_parameters[second_edge],
                                (first_axis_end - second_axis_start) /
                                    second_axis_delta);
              continue;
            }
            const double first_parameter =
                (offset_x * second_y - offset_y * second_x) / denominator;
            const double second_parameter =
                (offset_x * first_y - offset_y * first_x) / denominator;
            if (first_parameter < -intersection_tolerance ||
                first_parameter > 1.0 + intersection_tolerance ||
                second_parameter < -intersection_tolerance ||
                second_parameter > 1.0 + intersection_tolerance) {
              continue;
            }
            append_polygon_boolean_parameter(first_parameters[first_edge],
                                             first_parameter);
            append_polygon_boolean_parameter(second_parameters[second_edge],
                                             second_parameter);
          }
        }
      }
      for (auto &parameters : first_parameters) {
        std::sort(parameters.begin(), parameters.end());
      }
      for (auto &parameters : second_parameters) {
        std::sort(parameters.begin(), parameters.end());
      }

      const auto evaluate_mode = [mode](bool in_first,
                                        bool in_second) noexcept {
        switch (mode) {
        case combine_mode::union_value:
          return in_first || in_second;
        case combine_mode::intersect:
          return in_first && in_second;
        case combine_mode::xor_value:
          return in_first != in_second;
        case combine_mode::exclude:
          return in_first && !in_second;
        }
        return false;
      };
      const auto classify_other_sides = [](std::span<const point_2f> other,
                                           point_2f source_start,
                                           point_2f source_end,
                                           point_2f midpoint,
                                           polygon_point_relation relation,
                                           bool &inside_left,
                                           bool &inside_right) {
        if (relation != polygon_point_relation::boundary) {
          inside_left = relation == polygon_point_relation::inside;
          inside_right = inside_left;
          return true;
        }
        const double source_x =
            static_cast<double>(source_end.x) - source_start.x;
        const double source_y =
            static_cast<double>(source_end.y) - source_start.y;
        const double tolerance = polygon_coordinate_tolerance(other, midpoint);
        for (std::size_t edge = 0U; edge < other.size(); ++edge) {
          const point_2f other_start = other[edge];
          const point_2f other_end = other[(edge + 1U) % other.size()];
          const double other_x =
              static_cast<double>(other_end.x) - other_start.x;
          const double other_y =
              static_cast<double>(other_end.y) - other_start.y;
          const double length = std::hypot(other_x, other_y);
          if (std::abs(triangle_cross(other_start, other_end, midpoint)) >
              tolerance * std::max(1.0, length)) {
            continue;
          }
          const double midpoint_x =
              static_cast<double>(midpoint.x) - other_start.x;
          const double midpoint_y =
              static_cast<double>(midpoint.y) - other_start.y;
          const double projection = midpoint_x * other_x + midpoint_y * other_y;
          const double squared_length = other_x * other_x + other_y * other_y;
          const double projection_tolerance = tolerance * std::max(1.0, length);
          if (projection < -projection_tolerance ||
              projection > squared_length + projection_tolerance) {
            continue;
          }
          const double direction = source_x * other_x + source_y * other_y;
          if (direction == 0.0) {
            continue;
          }
          inside_left = direction > 0.0;
          inside_right = !inside_left;
          return true;
        }
        return false;
      };

      std::vector<polygon_boolean_segment> segments;
      const auto append_boundary_segment = [&](point_2f start, point_2f end) {
        const double tolerance =
            std::max(polygon_coordinate_tolerance(first, start),
                     polygon_coordinate_tolerance(second, start));
        for (const auto &segment : segments) {
          if (same_polygon_boolean_point(segment.start, start, tolerance) &&
              same_polygon_boolean_point(segment.end, end, tolerance)) {
            return true;
          }
        }
        if (segments.size() == maximum_boundary_segments) {
          return false;
        }
        segments.push_back({start, end, false});
        return true;
      };
      const auto append_segments = [&](const std::vector<point_2f> &source,
                                       const std::vector<point_2f> &other,
                                       const auto &parameters,
                                       bool first_operand) {
        for (std::size_t edge = 0U; edge < source.size(); ++edge) {
          const point_2f edge_start = source[edge];
          const point_2f edge_end = source[(edge + 1U) % source.size()];
          for (std::size_t part = 0U; part + 1U < parameters[edge].size();
               ++part) {
            const double start_parameter = parameters[edge][part];
            const double end_parameter = parameters[edge][part + 1U];
            if (end_parameter - start_parameter <= intersection_tolerance) {
              continue;
            }
            const point_2f start = interpolate_polygon_boolean_point(
                edge_start, edge_end, start_parameter);
            const point_2f end = interpolate_polygon_boolean_point(
                edge_start, edge_end, end_parameter);
            const point_2f midpoint = interpolate_polygon_boolean_point(
                edge_start, edge_end, (start_parameter + end_parameter) * 0.5);
            bool other_inside_left = false;
            bool other_inside_right = false;
            if (!classify_other_sides(other, start, end, midpoint,
                                      classify_polygon_point(other, midpoint),
                                      other_inside_left, other_inside_right)) {
              return false;
            }
            const bool first_inside_left =
                first_operand ? true : other_inside_left;
            const bool first_inside_right =
                first_operand ? false : other_inside_right;
            const bool second_inside_left =
                first_operand ? other_inside_left : true;
            const bool second_inside_right =
                first_operand ? other_inside_right : false;
            const bool result_inside_left =
                evaluate_mode(first_inside_left, second_inside_left);
            const bool result_inside_right =
                evaluate_mode(first_inside_right, second_inside_right);
            if (result_inside_left == result_inside_right) {
              continue;
            }
            if (!append_boundary_segment(result_inside_left ? start : end,
                                         result_inside_left ? end : start)) {
              return false;
            }
          }
        }
        return true;
      };
      if (!append_segments(first, second, first_parameters, true) ||
          !append_segments(second, first, second_parameters, false)) {
        return not_implemented;
      }

      std::vector<std::vector<point_2f>> contours;
      constexpr double pi = 3.1415926535897932384626433832795;
      for (std::size_t first_segment = 0U; first_segment < segments.size();
           ++first_segment) {
        if (segments[first_segment].used) {
          continue;
        }
        std::vector<point_2f> points;
        points.push_back(segments[first_segment].start);
        polygon_boolean_segment *current = &segments[first_segment];
        current->used = true;
        const double point_tolerance =
            std::max(polygon_coordinate_tolerance(first, current->start),
                     polygon_coordinate_tolerance(second, current->start));
        while (!same_polygon_boolean_point(current->end, points.front(),
                                           point_tolerance)) {
          if (points.size() == maximum_boundary_segments) {
            return not_implemented;
          }
          points.push_back(current->end);
          polygon_boolean_segment *next = nullptr;
          double best_angle = std::numeric_limits<double>::infinity();
          const double incoming_x =
              static_cast<double>(current->end.x) - current->start.x;
          const double incoming_y =
              static_cast<double>(current->end.y) - current->start.y;
          for (auto &candidate : segments) {
            if (candidate.used ||
                !same_polygon_boolean_point(candidate.start, current->end,
                                            point_tolerance)) {
              continue;
            }
            const double outgoing_x =
                static_cast<double>(candidate.end.x) - candidate.start.x;
            const double outgoing_y =
                static_cast<double>(candidate.end.y) - candidate.start.y;
            double angle =
                std::atan2(incoming_x * outgoing_y - incoming_y * outgoing_x,
                           incoming_x * outgoing_x + incoming_y * outgoing_y);
            if (angle <= 0.0) {
              angle += 2.0 * pi;
            }
            if (angle < best_angle) {
              best_angle = angle;
              next = &candidate;
            }
          }
          if (next == nullptr) {
            return not_implemented;
          }
          next->used = true;
          current = next;
        }
        if (points.size() < 3U) {
          return not_implemented;
        }
        contours.push_back(std::move(points));
      }

      sink->SetFillMode(fill_mode::alternate);
      sink->SetSegmentFlags(path_segment::force_unstroked);
      for (const auto &contour : contours) {
        if (contour.size() - 1U > (std::numeric_limits<std::uint32_t>::max)()) {
          return com::out_of_memory;
        }
        sink->BeginFigure(contour.front(), figure_begin::filled);
        sink->AddLines(contour.data() + 1U,
                       static_cast<std::uint32_t>(contour.size() - 1U));
        sink->AddLines(contour.data(), 1U);
        sink->EndFigure(figure_end::closed);
      }
      return com::ok;
    } catch (const std::bad_alloc &) {
      return com::out_of_memory;
    } catch (...) {
      return failure;
    }
  }

    com::result PROGPU_NATIVE_COM_CALL Outline(
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
            std::uint32_t filled_figure =
                (std::numeric_limits<std::uint32_t>::max)();
            for (std::size_t index = 0U;
                 index < data_->figures.size();
                 ++index) {
                if (data_->figures[index].begin != figure_begin::filled) {
                    continue;
                }
                if (filled_figure !=
                    (std::numeric_limits<std::uint32_t>::max)()) {
                    // Multiple contours need fill-rule-aware union and hole
                    // removal before the result can be fill invariant.
                    return not_implemented;
                }
                filled_figure = static_cast<std::uint32_t>(index);
            }
            sink->SetFillMode(fill_mode::alternate);
            if (filled_figure ==
                (std::numeric_limits<std::uint32_t>::max)()) {
                return com::ok;
            }

            std::vector<point_2f> points;
            for (const auto& edge : edges) {
                if (edge.figure_index != filled_figure ||
                    same_point(edge.start, edge.end)) {
                    continue;
                }
                if (points.empty()) {
                    points.push_back(edge.start);
                } else if (!same_point(points.back(), edge.start)) {
                    return not_implemented;
                }
                if (!same_point(points.back(), edge.end)) {
                    points.push_back(edge.end);
                }
            }
            if (points.size() > 1U &&
                same_point(points.front(), points.back())) {
                points.pop_back();
            }
            if (points.size() < 3U) {
                return com::ok;
            }

            for (std::size_t left = 0U; left < points.size(); ++left) {
                const std::size_t left_next =
                    (left + 1U) % points.size();
                for (std::size_t right = left + 1U;
                     right < points.size();
                     ++right) {
                    const std::size_t right_next =
                        (right + 1U) % points.size();
                    if (left_next == right || right_next == left) {
                        continue;
                    }
                    if (segments_intersect(
                            points[left],
                            points[left_next],
                            points[right],
                            points[right_next])) {
                        return not_implemented;
                    }
                }
            }

            double twice_area = 0.0;
            for (std::size_t index = 0U; index < points.size(); ++index) {
                const point_2f current = points[index];
                const point_2f next = points[(index + 1U) % points.size()];
                twice_area += static_cast<double>(current.x) * next.y -
                    static_cast<double>(next.x) * current.y;
            }
            if (!std::isfinite(twice_area)) {
                return com::invalid_argument;
            }
            if (twice_area == 0.0) {
                return com::ok;
            }
            if (twice_area < 0.0) {
                std::reverse(points.begin(), points.end());
            }
            if (points.size() - 1U >
                (std::numeric_limits<std::uint32_t>::max)()) {
                return com::out_of_memory;
            }

            sink->SetSegmentFlags(path_segment::none);
            sink->BeginFigure(points.front(), figure_begin::filled);
            sink->AddLines(
                points.data() + 1U,
                static_cast<std::uint32_t>(points.size() - 1U));
            sink->AddLines(points.data(), 1U);
            sink->EndFigure(figure_end::closed);
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
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
        float stroke_width,
        stroke_style* style,
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
        if (!std::isfinite(stroke_width) || stroke_width < 0.0F ||
            !valid_tolerance(flattening_tolerance) ||
            !core::valid_transform(world_transform)) {
            return com::invalid_argument;
        }
        if (stroke_width == 0.0F) {
            return not_implemented;
        }
        bool dashed = false;
        line_join join = line_join::miter;
        double miter_limit = 10.0;
        cap_style start_cap = cap_style::flat;
        cap_style end_cap = cap_style::flat;
        if (style != nullptr) {
            factory* raw_style_factory = nullptr;
            style->GetFactory(&raw_style_factory);
            com::pointer<factory> style_factory;
            style_factory.attach(raw_style_factory);
            if (style_factory.get() != owner_.get()) {
                return wrong_factory;
            }
            dashed = style->GetDashStyle() != dash_style::solid;
            join = style->GetLineJoin();
            miter_limit = style->GetMiterLimit();
            start_cap = style->GetStartCap();
            end_cap = style->GetEndCap();
            if (style->GetLineJoin() > line_join::miter_or_bevel ||
                !std::isfinite(style->GetMiterLimit()) ||
                style->GetMiterLimit() < 1.0F ||
                style->GetStartCap() > cap_style::triangle ||
                style->GetEndCap() > cap_style::triangle ||
                style->GetDashCap() > cap_style::triangle ||
                !std::isfinite(style->GetDashOffset())) {
                return not_implemented;
            }
        }
        if (data_->figures.size() != 1U) {
            return not_implemented;
        }
        const bool closed_figure =
            data_->figures.front().end == figure_end::closed;
        if (closed_figure && style != nullptr && !dashed) {
            return not_implemented;
        }
        try {
            std::vector<flat_edge> edges;
            const com::result edge_status = collect_flat_edges(
                nullptr, flattening_tolerance, false, edges);
            if (com::failed(edge_status)) {
                return edge_status;
            }
            std::vector<point_2f> polygon;
            for (const auto& edge : edges) {
                if (edge.figure_index != 0U ||
                    edge.flags != path_segment::none ||
                    same_point(edge.start, edge.end)) {
                    if (edge.flags != path_segment::none) {
                        return not_implemented;
                    }
                    continue;
                }
                if (polygon.empty()) {
                    polygon.push_back(edge.start);
                } else if (!same_point(polygon.back(), edge.start)) {
                    return not_implemented;
                }
                polygon.push_back(edge.end);
            }
            if (closed_figure) {
                if (!normalize_simple_polygon(polygon)) {
                    return not_implemented;
                }
            } else {
                polygon.erase(
                    std::unique(polygon.begin(), polygon.end(), same_point),
                    polygon.end());
                if (polygon.size() < 2U) {
                    return not_implemented;
                }
            }
            if (dashed) {
                return emit_joined_dashed_widen(
                    polygon,
                    closed_figure,
                    stroke_width,
                    *style,
                    world_transform,
                    *sink);
            }
            if (!closed_figure) {
                return emit_joined_open_solid_widen(
                    polygon,
                    stroke_width,
                    join,
                    miter_limit,
                    start_cap,
                    end_cap,
                    world_transform,
                    *sink);
            }
            const double half_width =
                static_cast<double>(stroke_width) * 0.5;
            std::vector<point_2f> outer;
            std::vector<point_2f> inner;
            com::result result = build_default_miter_offset_contour(
                polygon, -half_width, outer);
            if (com::failed(result)) {
                return result;
            }
            result = build_default_miter_offset_contour(
                polygon, half_width, inner);
            if (com::failed(result)) {
                return result;
            }
            if (!normalize_simple_polygon(outer) ||
                !normalize_simple_polygon(inner)) {
                return not_implemented;
            }
            for (const point_2f point : inner) {
                if (classify_polygon_point(outer, point) ==
                    polygon_point_relation::outside) {
                    return not_implemented;
                }
            }
            result = transform_points_in_place(outer, world_transform);
            if (com::failed(result)) {
                return result;
            }
            result = transform_points_in_place(inner, world_transform);
            if (com::failed(result)) {
                return result;
            }
            if (outer.size() >
                    (std::numeric_limits<std::uint32_t>::max)() ||
                inner.size() >
                    (std::numeric_limits<std::uint32_t>::max)()) {
                return com::out_of_memory;
            }

            sink->SetFillMode(fill_mode::alternate);
            sink->SetSegmentFlags(path_segment::force_unstroked);
            sink->BeginFigure(outer.front(), figure_begin::filled);
            sink->AddLines(
                outer.data() + 1U,
                static_cast<std::uint32_t>(outer.size() - 1U));
            sink->EndFigure(figure_end::closed);
            sink->BeginFigure(inner.front(), figure_begin::filled);
            sink->AddLines(
                inner.data() + 1U,
                static_cast<std::uint32_t>(inner.size() - 1U));
            sink->EndFigure(figure_end::closed);
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
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
                    path_segment flags) {
                    edges.push_back(
                        {start, end, segment_index, figure_index, flags});
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
