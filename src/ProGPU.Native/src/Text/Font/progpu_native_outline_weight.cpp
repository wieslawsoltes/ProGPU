#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>

// Outline weight: a glyph outline grows by a box, each of its edges moving
// outwards by the side of the box its normal faces. A stem gains the left and
// the right growth, a bar the bottom and the top one, and a slanted stroke
// gains both, which is what makes this the weight of a rasterizer that widens
// glyphs by a fixed amount along each axis. The outline stays an outline: the
// result is rasterized, cached and transformed like any other, and its bytes
// are its raster identity.
//
// Algorithm: the sum of the filled outline and the box, traced on the outline
// itself (the convolution of the two boundaries).
// 1. The fill direction is the sign of the area of all contours.
// 2. Every curve is cut where it turns in x or in y, so each piece travels in
//    one quadrant and its outward normal faces one corner of the box.
// 3. Each piece moves by the corner its normal faces. A piece along an axis
//    faces a whole side of the box: its start takes the missing coordinate
//    from the piece before it and its end from the piece after it, which
//    keeps right angles exact.
// 4. Where two pieces that follow each other moved differently, the gap is
//    closed along the boundary of the box, through the corner between the two
//    when they face opposite corners. At a joint that turns into the fill the
//    closing lines overlap the grown fill; the fill rule of glyphs is the
//    winding number, so the overlap changes nothing.
// Time complexity: O(segments). Space complexity: O(segments) for the pieces.

namespace progpu::native::text {
namespace {

struct vec2 {
    double x = 0.0;
    double y = 0.0;
};

vec2 of(progpu_native_point point) noexcept { return {point.x, point.y}; }

progpu_native_point to_point(vec2 value) noexcept {
    return {static_cast<float>(value.x), static_cast<float>(value.y)};
}

bool same(progpu_native_point a, progpu_native_point b) noexcept {
    return a.x == b.x && a.y == b.y;
}

std::uint32_t control_count(std::uint32_t kind) noexcept {
    switch (kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE: return 2U;
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC: return 3U;
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC: return 4U;
    default: return 0U;
    }
}

const progpu_native_point& control(
    const progpu_native_path_segment& segment, std::uint32_t index) noexcept {
    switch (index) {
    case 0U: return segment.p0;
    case 1U: return segment.p1;
    case 2U: return segment.p2;
    default: return segment.p3;
    }
}

progpu_native_point end_of(const progpu_native_path_segment& segment) noexcept {
    return control(segment, control_count(segment.kind) - 1U);
}

double signed_area(std::span<const progpu_native_path_segment> segments) noexcept {
    double area = 0.0;
    for (const auto& segment : segments) {
        const auto count = control_count(segment.kind);
        for (std::uint32_t index = 0U; index + 1U < count; ++index) {
            const auto a = of(control(segment, index));
            const auto b = of(control(segment, index + 1U));
            area += a.x * b.y - b.x * a.y;
        }
    }
    return area * 0.5;
}

int sign_of(double value) noexcept { return value > 0.0 ? 1 : (value < 0.0 ? -1 : 0); }

// A piece of a contour that travels in one quadrant: up to four controls, and
// the signs of its outward normal (zero for a piece along that axis).
struct piece {
    std::array<vec2, 4> controls{};
    std::uint32_t count = 0U;
    int normal_x = 0;
    int normal_y = 0;
    // The signs its start and its end move by: the normal, with a zero
    // replaced by the sign of the neighbour on that side.
    int start_x = 0;
    int start_y = 0;
    int end_x = 0;
    int end_y = 0;

    const vec2& first() const noexcept { return controls[0]; }
    const vec2& last() const noexcept { return controls[count - 1U]; }
};

vec2 lerp(vec2 a, vec2 b, double t) noexcept {
    return {a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t};
}

// The parameters inside (0, 1) where one coordinate of a curve turns.
void append_turns(
    double c0, double c1, double c2, double c3, std::uint32_t count,
    std::array<double, 4>& turns, std::uint32_t& turn_count) noexcept {
    constexpr double edge = 1e-6;
    const auto add = [&](double t) {
        if (std::isfinite(t) && t > edge && t < 1.0 - edge && turn_count < turns.size()) {
            turns[turn_count++] = t;
        }
    };
    if (count == 3U) {
        const double denominator = c0 - 2.0 * c1 + c2;
        if (denominator != 0.0) add((c0 - c1) / denominator);
        return;
    }
    if (count != 4U) return;
    // The derivative of a cubic, a t^2 + b t + c.
    const double a = (c3 - c2) - 2.0 * (c2 - c1) + (c1 - c0);
    const double b = 2.0 * ((c2 - c1) - (c1 - c0));
    const double c = c1 - c0;
    if (a == 0.0) {
        if (b != 0.0) add(-c / b);
        return;
    }
    const double discriminant = b * b - 4.0 * a * c;
    if (discriminant <= 0.0) return;
    const double root = std::sqrt(discriminant);
    // The stable pair of roots.
    const double q = -0.5 * (b + (b < 0.0 ? -root : root));
    add(q / a);
    if (q != 0.0) add(c / q);
}

// Cuts `controls` at t: the part before stays in `head`, the part after in
// `controls`.
void cut(std::array<vec2, 4>& controls, std::uint32_t count, double t, std::array<vec2, 4>& head) noexcept {
    if (count == 3U) {
        const vec2 a = lerp(controls[0], controls[1], t);
        const vec2 b = lerp(controls[1], controls[2], t);
        const vec2 middle = lerp(a, b, t);
        head = {controls[0], a, middle, {}};
        controls = {middle, b, controls[2], {}};
        return;
    }
    const vec2 a = lerp(controls[0], controls[1], t);
    const vec2 b = lerp(controls[1], controls[2], t);
    const vec2 c = lerp(controls[2], controls[3], t);
    const vec2 ab = lerp(a, b, t);
    const vec2 bc = lerp(b, c, t);
    const vec2 middle = lerp(ab, bc, t);
    head = {controls[0], a, ab, middle};
    controls = {middle, bc, c, controls[3]};
}

void append_piece(
    std::vector<piece>& pieces, const std::array<vec2, 4>& controls, std::uint32_t count, double side) {
    piece value{};
    value.controls = controls;
    value.count = count;
    const double dx = value.last().x - value.first().x;
    const double dy = value.last().y - value.first().y;
    // A piece without a chord has no direction: it would only add a joint.
    if (dx == 0.0 && dy == 0.0) return;
    // The outward normal is the direction of travel turned to the outside.
    value.normal_x = sign_of(dy * side);
    value.normal_y = sign_of(-dx * side);
    pieces.push_back(value);
}

void append_pieces(std::vector<piece>& pieces, const progpu_native_path_segment& segment, double side) {
    const auto count = control_count(segment.kind);
    std::array<vec2, 4> controls{};
    for (std::uint32_t index = 0U; index < count; ++index) controls[index] = of(control(segment, index));
    std::array<double, 4> turns{};
    std::uint32_t turn_count = 0U;
    append_turns(controls[0].x, controls[1].x, controls[2].x, controls[3].x, count, turns, turn_count);
    append_turns(controls[0].y, controls[1].y, controls[2].y, controls[3].y, count, turns, turn_count);
    std::sort(turns.begin(), turns.begin() + turn_count);
    double done = 0.0;
    for (std::uint32_t index = 0U; index < turn_count; ++index) {
        const double t = turns[index];
        if (t - done < 1e-6) continue;
        std::array<vec2, 4> head{};
        cut(controls, count, (t - done) / (1.0 - done), head);
        append_piece(pieces, head, count, side);
        done = t;
    }
    append_piece(pieces, controls, count, side);
}

// Gives every piece along an axis the sign its neighbours move by on that
// axis: the start from the pieces before it, the end from the pieces after.
void resolve_ends(std::span<piece> pieces) noexcept {
    const std::size_t count = pieces.size();
    int before_x = 0;
    int before_y = 0;
    // Twice around, so the first pieces see the last ones.
    for (std::size_t step = 0U; step < count * 2U; ++step) {
        auto& value = pieces[step % count];
        value.start_x = value.normal_x != 0 ? value.normal_x : before_x;
        value.start_y = value.normal_y != 0 ? value.normal_y : before_y;
        if (value.normal_x != 0) before_x = value.normal_x;
        if (value.normal_y != 0) before_y = value.normal_y;
    }
    int after_x = 0;
    int after_y = 0;
    for (std::size_t step = count * 2U; step > 0U; --step) {
        auto& value = pieces[(step - 1U) % count];
        value.end_x = value.normal_x != 0 ? value.normal_x : after_x;
        value.end_y = value.normal_y != 0 ? value.normal_y : after_y;
        if (value.normal_x != 0) after_x = value.normal_x;
        if (value.normal_y != 0) after_y = value.normal_y;
    }
}

struct box {
    double left = 0.0;
    double bottom = 0.0;
    double right = 0.0;
    double top = 0.0;

    // The corner the signs face; a zero sign stays on the axis.
    vec2 corner(int x, int y) const noexcept {
        return {x > 0 ? right : (x < 0 ? -left : 0.0), y > 0 ? top : (y < 0 ? -bottom : 0.0)};
    }
};

vec2 add(vec2 a, vec2 b) noexcept { return {a.x + b.x, a.y + b.y}; }

// The direction a piece leaves its start in, or arrives at its end in: the
// first leg with a length from that side.
vec2 tangent(const piece& value, bool at_end) noexcept {
    if (at_end) {
        for (std::uint32_t index = value.count - 1U; index > 0U; --index) {
            const vec2 d{value.last().x - value.controls[index - 1U].x, value.last().y - value.controls[index - 1U].y};
            if (d.x != 0.0 || d.y != 0.0) return d;
        }
        return {};
    }
    for (std::uint32_t index = 1U; index < value.count; ++index) {
        const vec2 d{value.controls[index].x - value.first().x, value.controls[index].y - value.first().y};
        if (d.x != 0.0 || d.y != 0.0) return d;
    }
    return {};
}

void append_line(std::vector<progpu_native_path_segment>& destination, vec2 from, vec2 to) {
    const auto a = to_point(from);
    const auto b = to_point(to);
    if (same(a, b)) return;
    progpu_native_path_segment segment{};
    segment.p0 = a;
    segment.p1 = b;
    segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    destination.push_back(segment);
}

void append_moved(
    std::vector<progpu_native_path_segment>& destination, const piece& value, const box& growth) {
    const vec2 start = growth.corner(value.start_x, value.start_y);
    const vec2 end = growth.corner(value.end_x, value.end_y);
    if (value.count == 2U || start.x != end.x || start.y != end.y) {
        // A line, or a piece along an axis whose ends moved apart along it.
        append_line(destination, add(value.first(), start), add(value.last(), end));
        return;
    }
    progpu_native_path_segment segment{};
    segment.kind = value.count == 3U ? PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC : PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
    segment.p0 = to_point(add(value.controls[0], start));
    segment.p1 = to_point(add(value.controls[1], start));
    segment.p2 = to_point(add(value.controls[2], start));
    if (value.count == 4U) segment.p3 = to_point(add(value.controls[3], start));
    destination.push_back(segment);
}

// Closes the gap between a piece and the one after it, along the box.
void append_joint(
    std::vector<progpu_native_path_segment>& destination,
    const piece& from, const piece& to, const box& growth) {
    const vec2 joint = from.last();
    const vec2 a = growth.corner(from.end_x, from.end_y);
    const vec2 b = growth.corner(to.start_x, to.start_y);
    if (from.end_x != to.start_x && from.end_y != to.start_y &&
        from.end_x != 0 && from.end_y != 0) {
        // Opposite corners: round the box in the direction the outline turns.
        const vec2 in = tangent(from, true);
        const vec2 out = tangent(to, false);
        const double turn = in.x * out.y - in.y * out.x;
        const int x = turn >= 0.0 ? -from.end_y : from.end_y;
        const int y = turn >= 0.0 ? from.end_x : -from.end_x;
        const vec2 between = growth.corner(x, y);
        append_line(destination, add(joint, a), add(joint, between));
        append_line(destination, add(joint, between), add(joint, b));
        return;
    }
    append_line(destination, add(joint, a), add(joint, b));
}

void embolden_contour(
    std::span<const progpu_native_path_segment> contour,
    double side,
    const box& growth,
    std::vector<piece>& pieces,
    std::vector<progpu_native_path_segment>& destination) {
    pieces.clear();
    for (const auto& segment : contour) append_pieces(pieces, segment, side);
    if (pieces.empty()) return;
    resolve_ends(pieces);
    const bool closed = same(end_of(contour.back()), contour.front().p0);
    const std::size_t first = destination.size();
    for (std::size_t index = 0U; index < pieces.size(); ++index) {
        append_moved(destination, pieces[index], growth);
        if (index + 1U < pieces.size()) {
            append_joint(destination, pieces[index], pieces[index + 1U], growth);
        } else if (closed) {
            append_joint(destination, pieces[index], pieces[0], growth);
        }
    }
    // The contour ends where it began, to the bit: a rounding step between the
    // two would open it.
    if (closed && destination.size() > first) {
        auto& last = destination.back();
        const auto start = destination[first].p0;
        switch (last.kind) {
        case PROGPU_NATIVE_PATH_SEGMENT_LINE: last.p1 = start; break;
        case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC: last.p2 = start; break;
        default: last.p3 = start; break;
        }
    }
}

} // namespace

bool embolden_outline(
    std::span<const progpu_native_path_segment> source,
    const outline_growth& growth,
    std::vector<progpu_native_path_segment>& destination) {
    destination.clear();
    if (source.empty()) return false;
    if (!std::isfinite(growth.left) || !std::isfinite(growth.bottom) ||
        !std::isfinite(growth.right) || !std::isfinite(growth.top)) {
        return false;
    }
    if (growth.left == 0.0F && growth.bottom == 0.0F && growth.right == 0.0F && growth.top == 0.0F) {
        return false;
    }
    for (const auto& segment : source) {
        if (control_count(segment.kind) == 0U) return false;
    }
    const double area = signed_area(source);
    if (area == 0.0 || !std::isfinite(area)) return false;
    // Y up: a positive area is a counter-clockwise outer contour, whose
    // outside is to the right of the direction of travel.
    const double side = area > 0.0 ? 1.0 : -1.0;
    const box sides{growth.left, growth.bottom, growth.right, growth.top};
    std::vector<piece> pieces;
    destination.reserve(source.size() + source.size() / 2U + 8U);
    std::size_t start = 0U;
    while (start < source.size()) {
        std::size_t end = start + 1U;
        while (end < source.size() && same(end_of(source[end - 1U]), source[end].p0)) ++end;
        embolden_contour(source.subspan(start, end - start), side, sides, pieces, destination);
        start = end;
    }
    if (destination.empty()) return false;
    return true;
}

} // namespace progpu::native::text
