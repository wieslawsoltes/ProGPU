#include "progpu_native_path_measure.hpp"

#include <algorithm>
#include <bit>
#include <cmath>
#include <limits>
#include <utility>

namespace progpu::native::geometry {
namespace {

// Ported from ProGPU's managed SKPathMeasure (src/SkiaSharp/SKPathMeasure.cs):
// the contour, sample and location model, the tangent and slicing rules. The
// subdivision follows the curvature tests of Skia's contour measure instead of
// the managed fixed step count, so lengths agree with Skia-based backends.

struct vec2 final {
    double x;
    double y;
};

[[nodiscard]] constexpr vec2 operator+(vec2 left, vec2 right) noexcept
{
    return {left.x + right.x, left.y + right.y};
}

[[nodiscard]] constexpr vec2 operator-(vec2 left, vec2 right) noexcept
{
    return {left.x - right.x, left.y - right.y};
}

[[nodiscard]] constexpr vec2 operator*(vec2 value, double scale) noexcept
{
    return {value.x * scale, value.y * scale};
}

[[nodiscard]] constexpr bool operator==(vec2 left, vec2 right) noexcept
{
    return left.x == right.x && left.y == right.y;
}

[[nodiscard]] constexpr vec2 lerp(vec2 from, vec2 to, double t) noexcept
{
    return {from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t};
}

[[nodiscard]] constexpr vec2 midpoint(vec2 from, vec2 to) noexcept
{
    return {(from.x + to.x) * 0.5, (from.y + to.y) * 0.5};
}

[[nodiscard]] double distance_between(vec2 from, vec2 to) noexcept
{
    return std::hypot(to.x - from.x, to.y - from.y);
}

[[nodiscard]] constexpr vec2 to_vec(progpu_native_point point) noexcept
{
    return {point.x, point.y};
}

[[nodiscard]] constexpr progpu_native_point to_point(vec2 value) noexcept
{
    return {static_cast<float>(value.x), static_cast<float>(value.y)};
}

[[nodiscard]] bool finite_point(progpu_native_point point) noexcept
{
    return std::isfinite(point.x) && std::isfinite(point.y);
}

// A homogeneous point of a rational curve: (x w, y w, w).
struct hpoint final {
    double x;
    double y;
    double z;
};

[[nodiscard]] constexpr hpoint hlerp(hpoint from, hpoint to, double t) noexcept
{
    return {from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t, from.z + (to.z - from.z) * t};
}

[[nodiscard]] constexpr hpoint lift(vec2 point, double weight) noexcept
{
    return {point.x * weight, point.y * weight, weight};
}

[[nodiscard]] constexpr vec2 project(hpoint point) noexcept
{
    return {point.x / point.z, point.y / point.z};
}

// One canonical segment, decoded.
struct curve final {
    std::uint32_t kind;
    vec2 p[4];
    double w1;
    double w2;
    double theta;
    double sweep;
    double rotation;
};

[[nodiscard]] curve decode(const progpu_native_path_segment& segment) noexcept
{
    curve value{};
    value.kind = segment.kind;
    value.p[0] = to_vec(segment.p0);
    value.p[1] = to_vec(segment.p1);
    value.p[2] = to_vec(segment.p2);
    value.p[3] = to_vec(segment.p3);
    value.w1 = 1.0;
    value.w2 = 1.0;
    switch (segment.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_ARC:
        value.theta = std::bit_cast<float>(segment.pad0);
        value.sweep = std::bit_cast<float>(segment.pad1);
        value.rotation = std::bit_cast<float>(segment.pad2);
        break;
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC:
        value.w1 = std::bit_cast<float>(segment.pad0);
        break;
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC:
        value.w1 = std::bit_cast<float>(segment.pad0);
        value.w2 = std::bit_cast<float>(segment.pad1);
        break;
    default:
        break;
    }
    return value;
}

[[nodiscard]] bool valid_segment(const progpu_native_path_segment& segment) noexcept
{
    const auto weight = [](std::uint32_t bits) {
        const float value = std::bit_cast<float>(bits);
        return std::isfinite(value) && value > 0.0F;
    };
    const auto finite_bits = [](std::uint32_t bits) { return std::isfinite(std::bit_cast<float>(bits)); };
    if (!finite_point(segment.p0) || !finite_point(segment.p1)) {
        return false;
    }
    switch (segment.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        return true;
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
        return finite_point(segment.p2);
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC:
        return finite_point(segment.p2) && finite_point(segment.p3);
    case PROGPU_NATIVE_PATH_SEGMENT_ARC:
        return finite_point(segment.p2) && finite_point(segment.p3) && segment.p3.x >= 0.0F &&
            segment.p3.y >= 0.0F && finite_bits(segment.pad0) && finite_bits(segment.pad1) &&
            finite_bits(segment.pad2);
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC:
        return finite_point(segment.p2) && weight(segment.pad0);
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC:
        return finite_point(segment.p2) && finite_point(segment.p3) && weight(segment.pad0) &&
            weight(segment.pad1);
    default:
        return false;
    }
}

[[nodiscard]] vec2 end_point(const curve& value) noexcept
{
    switch (value.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC:
        return value.p[2];
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC:
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC:
        return value.p[3];
    default:
        return value.p[1];
    }
}

[[nodiscard]] vec2 arc_point(const curve& value, double angle) noexcept
{
    const double ex = value.p[3].x * std::cos(angle);
    const double ey = value.p[3].y * std::sin(angle);
    const double cosine = std::cos(value.rotation);
    const double sine = std::sin(value.rotation);
    return {value.p[2].x + ex * cosine - ey * sine, value.p[2].y + ex * sine + ey * cosine};
}

[[nodiscard]] vec2 evaluate(const curve& value, double t) noexcept
{
    t = std::clamp(t, 0.0, 1.0);
    const double u = 1.0 - t;
    const vec2* p = value.p;
    switch (value.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        return t >= 1.0 ? p[1] : lerp(p[0], p[1], t);
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
        return p[0] * (u * u) + p[1] * (2.0 * u * t) + p[2] * (t * t);
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC:
        return p[0] * (u * u * u) + p[1] * (3.0 * u * u * t) + p[2] * (3.0 * u * t * t) + p[3] * (t * t * t);
    case PROGPU_NATIVE_PATH_SEGMENT_ARC:
        // The stored endpoints are exact; the interior follows the parameters.
        if (t <= 0.0) {
            return p[0];
        }
        if (t >= 1.0) {
            return p[1];
        }
        return arc_point(value, value.theta + t * value.sweep);
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC: {
        const double b0 = u * u;
        const double b1 = 2.0 * value.w1 * u * t;
        const double b2 = t * t;
        const double denominator = b0 + b1 + b2;
        return (p[0] * b0 + p[1] * b1 + p[2] * b2) * (1.0 / denominator);
    }
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC: {
        const double b0 = u * u * u;
        const double b1 = 3.0 * value.w1 * u * u * t;
        const double b2 = 3.0 * value.w2 * u * t * t;
        const double b3 = t * t * t;
        const double denominator = b0 + b1 + b2 + b3;
        return (p[0] * b0 + p[1] * b1 + p[2] * b2 + p[3] * b3) * (1.0 / denominator);
    }
    default:
        return p[0];
    }
}

// A vector in the direction of the curve at t (not normalized), with the
// endpoint fallbacks of the managed measure for coincident control points.
[[nodiscard]] vec2 direction(const curve& value, double t) noexcept
{
    t = std::clamp(t, 0.0, 1.0);
    const double u = 1.0 - t;
    const vec2* p = value.p;
    switch (value.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        return p[1] - p[0];
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
        if ((t == 0.0 && p[0] == p[1]) || (t == 1.0 && p[1] == p[2])) {
            return p[2] - p[0];
        }
        return ((p[1] - p[0]) * u + (p[2] - p[1]) * t) * 2.0;
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC: {
        if ((t == 0.0 && p[0] == p[1]) || (t == 1.0 && p[2] == p[3])) {
            vec2 tangent = t == 0.0 ? p[2] - p[0] : p[3] - p[1];
            if (tangent.x == 0.0 && tangent.y == 0.0) {
                tangent = p[3] - p[0];
            }
            return tangent;
        }
        return (p[1] - p[0]) * (3.0 * u * u) + (p[2] - p[1]) * (6.0 * u * t) + (p[3] - p[2]) * (3.0 * t * t);
    }
    case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
        const double angle = value.theta + t * value.sweep;
        const double ex = -value.p[3].x * std::sin(angle) * value.sweep;
        const double ey = value.p[3].y * std::cos(angle) * value.sweep;
        const double cosine = std::cos(value.rotation);
        const double sine = std::sin(value.rotation);
        return {ex * cosine - ey * sine, ex * sine + ey * cosine};
    }
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC: {
        if ((t == 0.0 && p[0] == p[1]) || (t == 1.0 && p[1] == p[2])) {
            return p[2] - p[0];
        }
        // The derivative of the conic up to a positive factor.
        const vec2 p20 = p[2] - p[0];
        const vec2 c = (p[1] - p[0]) * value.w1;
        const vec2 a = p20 * value.w1 - p20;
        const vec2 b = p20 - c - c;
        return (a * t + b) * t + c;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC: {
        // Quotient rule on N(t) / D(t) of the weighted Bernstein form.
        const double w[4] = {1.0, value.w1, value.w2, 1.0};
        const double basis[4] = {u * u * u, 3.0 * u * u * t, 3.0 * u * t * t, t * t * t};
        const double derived[4] = {-3.0 * u * u, 3.0 * u * u - 6.0 * u * t, 6.0 * u * t - 3.0 * t * t, 3.0 * t * t};
        vec2 n{};
        vec2 dn{};
        double d = 0.0;
        double dd = 0.0;
        for (int index = 0; index < 4; ++index) {
            n = n + p[index] * (w[index] * basis[index]);
            dn = dn + p[index] * (w[index] * derived[index]);
            d += w[index] * basis[index];
            dd += w[index] * derived[index];
        }
        vec2 tangent = dn * d - n * dd;
        if (tangent.x == 0.0 && tangent.y == 0.0) {
            tangent = t <= 0.5 ? p[2] - p[0] : p[3] - p[1];
            if (tangent.x == 0.0 && tangent.y == 0.0) {
                tangent = p[3] - p[0];
            }
        }
        return tangent;
    }
    default:
        return {};
    }
}

[[nodiscard]] bool exceeds(vec2 point, vec2 reference, double tolerance) noexcept
{
    return std::max(std::abs(point.x - reference.x), std::abs(point.y - reference.y)) > tolerance;
}

// The smallest parameter span that is subdivided: Skia's 30-bit t values stop at
// a span of 1024 units.
constexpr double minimum_span = 1.0 / 1048576.0;

class sampler final {
public:
    sampler(std::vector<double>& distances, std::vector<double>& parameters, double tolerance) noexcept
        : distances_(distances), parameters_(parameters), tolerance_(tolerance)
    {
    }

    double line(vec2 from, vec2 to, double distance)
    {
        return chord(from, to, 1.0, distance);
    }

    double quadratic(vec2 p0, vec2 p1, vec2 p2, double t0, double t1, double distance)
    {
        const double dx = p1.x * 0.5 - (p0.x + p2.x) * 0.25;
        const double dy = p1.y * 0.5 - (p0.y + p2.y) * 0.25;
        if (t1 - t0 >= minimum_span && std::max(std::abs(dx), std::abs(dy)) > tolerance_) {
            const vec2 p01 = midpoint(p0, p1);
            const vec2 p12 = midpoint(p1, p2);
            const vec2 middle = midpoint(p01, p12);
            const double half = (t0 + t1) * 0.5;
            distance = quadratic(p0, p01, middle, t0, half, distance);
            return quadratic(middle, p12, p2, half, t1, distance);
        }
        return chord(p0, p2, t1, distance);
    }

    double cubic(vec2 p0, vec2 p1, vec2 p2, vec2 p3, double t0, double t1, double distance)
    {
        if (t1 - t0 >= minimum_span &&
            (exceeds(p1, lerp(p0, p3, 1.0 / 3.0), tolerance_) || exceeds(p2, lerp(p0, p3, 2.0 / 3.0), tolerance_))) {
            const vec2 p01 = midpoint(p0, p1);
            const vec2 p12 = midpoint(p1, p2);
            const vec2 p23 = midpoint(p2, p3);
            const vec2 p012 = midpoint(p01, p12);
            const vec2 p123 = midpoint(p12, p23);
            const vec2 middle = midpoint(p012, p123);
            const double half = (t0 + t1) * 0.5;
            distance = cubic(p0, p01, p012, middle, t0, half, distance);
            return cubic(middle, p123, p23, p3, half, t1, distance);
        }
        return chord(p0, p3, t1, distance);
    }

    // Conics and arcs: the point at the middle of the span against the middle of
    // its chord (Skia's conic test); rational cubics: the points at a third and
    // two thirds against the chord, which also sees inflections.
    double evaluated(const curve& value, double t0, vec2 from, double t1, vec2 to, double distance)
    {
        const double half = (t0 + t1) * 0.5;
        const vec2 middle = evaluate(value, half);
        bool curvy = exceeds(middle, midpoint(from, to), tolerance_);
        if (!curvy && value.kind == PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC) {
            const double span = t1 - t0;
            curvy = exceeds(evaluate(value, t0 + span / 3.0), lerp(from, to, 1.0 / 3.0), tolerance_) ||
                exceeds(evaluate(value, t0 + span * 2.0 / 3.0), lerp(from, to, 2.0 / 3.0), tolerance_);
        }
        if (t1 - t0 >= minimum_span && curvy) {
            distance = evaluated(value, t0, from, half, middle, distance);
            return evaluated(value, half, middle, t1, to, distance);
        }
        return chord(from, to, t1, distance);
    }

private:
    double chord(vec2 from, vec2 to, double t, double distance)
    {
        const double next = distance + distance_between(from, to);
        if (next > distance) {
            distances_.push_back(next);
            parameters_.push_back(t);
        }
        return next;
    }

    std::vector<double>& distances_;
    std::vector<double>& parameters_;
    double tolerance_;
};

// The part of a segment between two parameters, in the same kind.
[[nodiscard]] progpu_native_path_segment slice(const progpu_native_path_segment& source, double from, double to)
{
    const curve value = decode(source);
    progpu_native_path_segment result{};
    result.kind = source.kind;
    const vec2* p = value.p;
    switch (source.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        result.p0 = to_point(evaluate(value, from));
        result.p1 = to_point(evaluate(value, to));
        break;
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC: {
        // Split at `to`, keep the left part, then split it at from / to and keep
        // the right part.
        vec2 q[3] = {p[0], lerp(p[0], p[1], to), evaluate(value, to)};
        if (to < 1.0 && from > 0.0) {
            const double relative = from / to;
            const vec2 left = lerp(q[0], q[1], relative);
            const vec2 right = lerp(q[1], q[2], relative);
            q[0] = lerp(left, right, relative);
            q[1] = right;
        } else if (to >= 1.0) {
            const vec2 left = lerp(p[0], p[1], from);
            const vec2 right = lerp(p[1], p[2], from);
            q[0] = from > 0.0 ? lerp(left, right, from) : p[0];
            q[1] = from > 0.0 ? right : p[1];
            q[2] = p[2];
        }
        result.p0 = to_point(q[0]);
        result.p1 = to_point(q[1]);
        result.p2 = to_point(q[2]);
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC: {
        const auto split_left = [](const vec2 (&c)[4], double t, vec2 (&out)[4]) {
            const vec2 p01 = lerp(c[0], c[1], t);
            const vec2 p12 = lerp(c[1], c[2], t);
            const vec2 p23 = lerp(c[2], c[3], t);
            const vec2 p012 = lerp(p01, p12, t);
            const vec2 p123 = lerp(p12, p23, t);
            out[0] = c[0];
            out[1] = p01;
            out[2] = p012;
            out[3] = lerp(p012, p123, t);
        };
        const auto split_right = [](const vec2 (&c)[4], double t, vec2 (&out)[4]) {
            const vec2 p01 = lerp(c[0], c[1], t);
            const vec2 p12 = lerp(c[1], c[2], t);
            const vec2 p23 = lerp(c[2], c[3], t);
            const vec2 p012 = lerp(p01, p12, t);
            const vec2 p123 = lerp(p12, p23, t);
            out[0] = lerp(p012, p123, t);
            out[1] = p123;
            out[2] = p23;
            out[3] = c[3];
        };
        vec2 left[4] = {p[0], p[1], p[2], p[3]};
        if (to < 1.0) {
            const vec2 whole[4] = {p[0], p[1], p[2], p[3]};
            split_left(whole, to, left);
        }
        vec2 piece[4] = {left[0], left[1], left[2], left[3]};
        if (from > 0.0) {
            split_right(left, to < 1.0 ? from / to : from, piece);
        }
        if (to >= 1.0) {
            piece[3] = p[3];
        }
        result.p0 = to_point(piece[0]);
        result.p1 = to_point(piece[1]);
        result.p2 = to_point(piece[2]);
        result.p3 = to_point(piece[3]);
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_ARC:
        result.p0 = to_point(evaluate(value, from));
        result.p1 = to_point(evaluate(value, to));
        result.p2 = source.p2;
        result.p3 = source.p3;
        result.pad0 = std::bit_cast<std::uint32_t>(static_cast<float>(value.theta + from * value.sweep));
        result.pad1 = std::bit_cast<std::uint32_t>(static_cast<float>((to - from) * value.sweep));
        result.pad2 = source.pad2;
        break;
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC: {
        // Homogeneous de Casteljau, then the standard form (end weights one).
        const hpoint h[3] = {lift(p[0], 1.0), lift(p[1], value.w1), lift(p[2], 1.0)};
        const auto split = [](const hpoint (&c)[3], double t, hpoint (&left)[3], hpoint (&right)[3]) {
            const hpoint p01 = hlerp(c[0], c[1], t);
            const hpoint p12 = hlerp(c[1], c[2], t);
            const hpoint p012 = hlerp(p01, p12, t);
            left[0] = c[0];
            left[1] = p01;
            left[2] = p012;
            right[0] = p012;
            right[1] = p12;
            right[2] = c[2];
        };
        hpoint left[3] = {h[0], h[1], h[2]};
        hpoint unused[3];
        if (to < 1.0) {
            split(h, to, left, unused);
        }
        hpoint piece[3] = {left[0], left[1], left[2]};
        if (from > 0.0) {
            split(left, to < 1.0 ? from / to : from, unused, piece);
        }
        const double weight = piece[1].z / std::sqrt(piece[0].z * piece[2].z);
        result.p0 = to_point(from > 0.0 ? project(piece[0]) : p[0]);
        result.p1 = to_point(project(piece[1]));
        result.p2 = to_point(to < 1.0 ? project(piece[2]) : p[2]);
        result.pad0 = std::bit_cast<std::uint32_t>(static_cast<float>(weight));
        break;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC: {
        const hpoint h[4] = {lift(p[0], 1.0), lift(p[1], value.w1), lift(p[2], value.w2), lift(p[3], 1.0)};
        const auto split = [](const hpoint (&c)[4], double t, hpoint (&left)[4], hpoint (&right)[4]) {
            const hpoint p01 = hlerp(c[0], c[1], t);
            const hpoint p12 = hlerp(c[1], c[2], t);
            const hpoint p23 = hlerp(c[2], c[3], t);
            const hpoint p012 = hlerp(p01, p12, t);
            const hpoint p123 = hlerp(p12, p23, t);
            const hpoint p0123 = hlerp(p012, p123, t);
            left[0] = c[0];
            left[1] = p01;
            left[2] = p012;
            left[3] = p0123;
            right[0] = p0123;
            right[1] = p123;
            right[2] = p23;
            right[3] = c[3];
        };
        hpoint left[4] = {h[0], h[1], h[2], h[3]};
        hpoint unused[4];
        if (to < 1.0) {
            split(h, to, left, unused);
        }
        hpoint piece[4] = {left[0], left[1], left[2], left[3]};
        if (from > 0.0) {
            split(left, to < 1.0 ? from / to : from, unused, piece);
        }
        // Reparameterize to end weights one: w_i' = z_i / (z0^(3-i)/3 * z3^(i/3)).
        const double a = std::cbrt(piece[0].z);
        const double b = std::cbrt(piece[3].z);
        const double w1 = piece[1].z / (a * a * b);
        const double w2 = piece[2].z / (a * b * b);
        result.p0 = to_point(from > 0.0 ? project(piece[0]) : p[0]);
        result.p1 = to_point(project(piece[1]));
        result.p2 = to_point(project(piece[2]));
        result.p3 = to_point(to < 1.0 ? project(piece[3]) : p[3]);
        result.pad0 = std::bit_cast<std::uint32_t>(static_cast<float>(w1));
        result.pad1 = std::bit_cast<std::uint32_t>(static_cast<float>(w2));
        break;
    }
    default:
        break;
    }
    return result;
}

} // namespace

progpu_native_status path_measure::build(
    std::span<const progpu_native_geometry_query_figure> figures,
    std::span<const progpu_native_path_segment> segments,
    bool force_closed, float resolution_scale)
{
    constexpr std::size_t limit = std::size_t{1} << 20U;
    if (!std::isfinite(resolution_scale) || resolution_scale <= 0.0F || figures.size() > limit ||
        segments.size() > limit) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::size_t next = 0;
    for (const progpu_native_geometry_query_figure& figure : figures) {
        if (figure.first_segment != next || figure.segment_count > segments.size() - next ||
            (figure.flags & ~3U) != 0U || !finite_point(figure.start)) {
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }
        next += figure.segment_count;
    }
    if (next != segments.size()) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    for (const progpu_native_path_segment& segment : segments) {
        if (!valid_segment(segment)) {
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }
    }

    const double tolerance = 0.5 / static_cast<double>(resolution_scale);
    std::vector<progpu_native_path_segment> owned;
    std::vector<sample> measured;
    std::vector<contour> contours;
    owned.reserve(segments.size() + figures.size());
    std::vector<double> distances;
    std::vector<double> parameters;
    sampler measure(distances, parameters, tolerance);
    for (const progpu_native_geometry_query_figure& figure : figures) {
        contour value{};
        value.first_segment = static_cast<std::uint32_t>(owned.size());
        value.first_sample = static_cast<std::uint32_t>(measured.size());
        value.closed = force_closed || (figure.flags & 1U) != 0U;
        const vec2 start = to_vec(figure.start);
        vec2 current = start;
        double distance = 0.0;
        const auto add = [&](const progpu_native_path_segment& segment) {
            const auto index = static_cast<std::uint32_t>(owned.size());
            owned.push_back(segment);
            const curve decoded = decode(segment);
            distances.clear();
            parameters.clear();
            switch (segment.kind) {
            case PROGPU_NATIVE_PATH_SEGMENT_LINE:
                distance = measure.line(decoded.p[0], decoded.p[1], distance);
                break;
            case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
                distance = measure.quadratic(decoded.p[0], decoded.p[1], decoded.p[2], 0.0, 1.0, distance);
                break;
            case PROGPU_NATIVE_PATH_SEGMENT_CUBIC:
                distance = measure.cubic(decoded.p[0], decoded.p[1], decoded.p[2], decoded.p[3], 0.0, 1.0, distance);
                break;
            default:
                distance = measure.evaluated(decoded, 0.0, decoded.p[0], 1.0, end_point(decoded), distance);
                break;
            }
            for (std::size_t sample_index = 0; sample_index < distances.size(); ++sample_index) {
                measured.push_back({distances[sample_index], parameters[sample_index], index});
            }
            current = end_point(decoded);
        };
        for (std::uint32_t index = 0; index < figure.segment_count; ++index) {
            add(segments[figure.first_segment + index]);
        }
        if (value.closed && !(current == start)) {
            progpu_native_path_segment closing{};
            closing.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
            closing.p0 = to_point(current);
            closing.p1 = figure.start;
            add(closing);
        }
        value.segment_count = static_cast<std::uint32_t>(owned.size()) - value.first_segment;
        value.sample_count = static_cast<std::uint32_t>(measured.size()) - value.first_sample;
        value.length = distance;
        if (value.sample_count != 0U && std::isfinite(distance) && distance > 0.0) {
            contours.push_back(value);
        } else {
            // A contour without length is not one (as in Skia's contour iteration).
            owned.resize(value.first_segment);
            measured.resize(value.first_sample);
        }
    }
    segments_ = std::move(owned);
    samples_ = std::move(measured);
    contours_ = std::move(contours);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

float path_measure::length(std::uint32_t contour) const noexcept
{
    return contour < contours_.size() ? static_cast<float>(contours_[contour].length) : 0.0F;
}

bool path_measure::closed(std::uint32_t contour) const noexcept
{
    return contour < contours_.size() && contours_[contour].closed;
}

std::uint32_t path_measure::locate(const contour& value, double distance, double& t) const noexcept
{
    const sample* first = samples_.data() + value.first_sample;
    const sample* last = first + value.sample_count;
    const sample* found = std::lower_bound(first, last, distance,
        [](const sample& item, double target) { return item.distance < target; });
    if (found == last) {
        found = last - 1;
    }
    double start_distance = 0.0;
    double start_t = 0.0;
    if (found != first) {
        start_distance = found[-1].distance;
        if (found[-1].segment == found->segment) {
            start_t = found[-1].t;
        }
    }
    const double span = found->distance - start_distance;
    const double amount = span > 0.0 ? (distance - start_distance) / span : 0.0;
    t = std::clamp(start_t + (found->t - start_t) * amount, 0.0, 1.0);
    return found->segment;
}

bool path_measure::position_tangent(std::uint32_t contour, float distance,
    progpu_native_point* position, progpu_native_point* tangent) const noexcept
{
    if (contour >= contours_.size() || std::isnan(distance)) {
        return false;
    }
    const struct contour& value = contours_[contour];
    double t = 0.0;
    const std::uint32_t index = locate(value, std::clamp(static_cast<double>(distance), 0.0, value.length), t);
    const curve decoded = decode(segments_[index]);
    if (position != nullptr) {
        *position = to_point(evaluate(decoded, t));
    }
    if (tangent != nullptr) {
        const vec2 vector = direction(decoded, t);
        const double length = std::hypot(vector.x, vector.y);
        *tangent = length > 0.0 && std::isfinite(length) ? to_point(vector * (1.0 / length))
                                                          : progpu_native_point{0.0F, 0.0F};
    }
    return true;
}

bool path_measure::segment(std::uint32_t contour, float start_distance,
    float stop_distance, std::vector<progpu_native_path_segment>& output) const
{
    if (contour >= contours_.size() || std::isnan(start_distance) || std::isnan(stop_distance)) {
        return false;
    }
    const struct contour& value = contours_[contour];
    const double start = std::max(static_cast<double>(start_distance), 0.0);
    const double stop = std::min(static_cast<double>(stop_distance), value.length);
    if (!(start <= stop)) {
        return false;
    }
    double start_t = 0.0;
    double stop_t = 0.0;
    const std::uint32_t first = locate(value, start, start_t);
    const std::uint32_t last = locate(value, stop, stop_t);
    const std::size_t initial = output.size();
    if (first == last) {
        if (stop_t > start_t) {
            output.push_back(slice(segments_[first], start_t, stop_t));
        }
    } else {
        if (start_t < 1.0) {
            output.push_back(slice(segments_[first], start_t, 1.0));
        }
        // The segments in between that have length, whole.
        std::uint32_t previous = first;
        for (std::uint32_t index = value.first_sample; index < value.first_sample + value.sample_count; ++index) {
            const std::uint32_t segment_index = samples_[index].segment;
            if (segment_index <= previous || segment_index >= last) {
                continue;
            }
            output.push_back(segments_[segment_index]);
            previous = segment_index;
        }
        if (stop_t > 0.0) {
            output.push_back(slice(segments_[last], 0.0, stop_t));
        }
    }
    if (output.size() == initial) {
        // An empty piece is a zero-length line at its point, which a stroke caps.
        progpu_native_point point{};
        static_cast<void>(position_tangent(contour, static_cast<float>(start), &point, nullptr));
        progpu_native_path_segment line{};
        line.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        line.p0 = point;
        line.p1 = point;
        output.push_back(line);
    }
    return true;
}

} // namespace progpu::native::geometry
