#include "progpu_native_text.hpp"

// Direct native port of ProGPU.Text.TtfFont.DecodeContourToFigure. It writes
// the existing renderer path ABI instead of materializing managed figures.
namespace progpu::native::text {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool validate_contours(
    std::span<const std::uint16_t> contour_end_points,
    std::span<const sfnt_outline_point> points) noexcept {
    if (contour_end_points.empty()) {
        return points.empty();
    }
    std::uint16_t previous = 0U;
    for (std::size_t index = 0U;
        index < contour_end_points.size();
        ++index) {
        const auto end = contour_end_points[index];
        if ((index > 0U && end <= previous) || end >= points.size()) {
            return false;
        }
        previous = end;
    }
    return static_cast<std::size_t>(contour_end_points.back()) + 1U ==
        points.size();
}

std::uint32_t count_contour_segments(
    std::span<const sfnt_outline_point> points) noexcept {
    if (points.size() < 2U) {
        return 0U;
    }
    const auto count = static_cast<std::uint32_t>(points.size());
    std::uint32_t index = points.front().on_curve() ? 1U : 0U;
    std::uint32_t processed = 0U;
    std::uint32_t segments = 0U;
    while (processed < count) {
        const auto current = index % count;
        if (points[current].on_curve()) {
            ++index;
            ++processed;
        } else {
            const auto next = (index + 1U) % count;
            if (points[next].on_curve()) {
                index += 2U;
                processed += 2U;
            } else {
                ++index;
                ++processed;
            }
        }
        ++segments;
    }
    return segments;
}

progpu_native_point to_native_point(const sfnt_outline_point& point) noexcept {
    return progpu_native_point{
        static_cast<float>(point.x),
        static_cast<float>(point.y)};
}

progpu_native_point midpoint(
    const sfnt_outline_point& left,
    const sfnt_outline_point& right) noexcept {
    return progpu_native_point{
        (static_cast<float>(left.x) + static_cast<float>(right.x)) * 0.5F,
        (static_cast<float>(left.y) + static_cast<float>(right.y)) * 0.5F};
}

} // namespace

bool sfnt_simple_glyph_path::try_get_segment_count(
    std::span<const std::uint16_t> contour_end_points,
    std::span<const sfnt_outline_point> points,
    std::uint32_t& result,
    font_error* error) noexcept {
    result = 0U;
    set_error(error, font_error::none);
    if (!validate_contours(contour_end_points, points)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t start = 0U;
    for (const auto end : contour_end_points) {
        const auto count = static_cast<std::size_t>(end) + 1U - start;
        result += count_contour_segments(points.subspan(start, count));
        start += count;
    }
    return true;
}

bool sfnt_simple_glyph_path::try_write_segments(
    std::span<const std::uint16_t> contour_end_points,
    std::span<const sfnt_outline_point> points,
    std::span<progpu_native_path_segment> segments,
    std::uint32_t& written,
    font_error* error) noexcept {
    written = 0U;
    std::uint32_t required = 0U;
    if (!try_get_segment_count(
            contour_end_points, points, required, error)) {
        return false;
    }
    if (segments.size() < required) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    std::size_t contour_start = 0U;
    for (const auto contour_end : contour_end_points) {
        const auto count = static_cast<std::uint32_t>(
            static_cast<std::size_t>(contour_end) + 1U - contour_start);
        const auto contour = points.subspan(contour_start, count);
        contour_start += count;
        if (count < 2U) {
            continue;
        }

        std::uint32_t index = 0U;
        progpu_native_point current{};
        if (contour.front().on_curve()) {
            current = to_native_point(contour.front());
            index = 1U;
        } else if (contour.back().on_curve()) {
            current = to_native_point(contour.back());
        } else {
            current = midpoint(contour.front(), contour.back());
        }

        std::uint32_t processed = 0U;
        while (processed < count) {
            const auto current_index = index % count;
            const auto& point = contour[current_index];
            if (point.on_curve()) {
                const auto end = to_native_point(point);
                segments[written++] = progpu_native_path_segment{
                    current,
                    end,
                    {},
                    {},
                    PROGPU_NATIVE_PATH_SEGMENT_LINE,
                    0U,
                    0U,
                    0U};
                current = end;
                ++index;
                ++processed;
                continue;
            }

            const auto next_index = (index + 1U) % count;
            const auto& next = contour[next_index];
            const auto control = to_native_point(point);
            progpu_native_point end{};
            if (next.on_curve()) {
                end = to_native_point(next);
                index += 2U;
                processed += 2U;
            } else {
                end = midpoint(point, next);
                ++index;
                ++processed;
            }
            segments[written++] = progpu_native_path_segment{
                current,
                control,
                end,
                {},
                PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC,
                0U,
                0U,
                0U};
            current = end;
        }
    }
    return true;
}

} // namespace progpu::native::text
