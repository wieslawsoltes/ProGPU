#include "progpu_native_text.hpp"
#include "progpu_native_svg_path_internal.hpp"

#include <algorithm>
#include <bit>
#include <charconv>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <numbers>
#include <string_view>

// Direct native port provenance: ProGPU-owned PathGeometry.Parse and
// PathAtlas.CompileFillPath plus ArcSegmentGeometry.TryGetArcCenter/bounds at
// checkpoint 8bf3cd44. This unit preserves their observable SVG command,
// closure, resolved-arc, fill-rule, and bounds contracts without retaining a
// managed geometry graph. Parsing is O(B + S) for B bytes and S output
// segments, with O(1) internal storage and exact caller-owned output.
namespace progpu::native::text {
namespace {

using svg_path_detail::angle_within_sweep;
using svg_path_detail::equal;
using svg_path_detail::evaluate_arc;
using svg_path_detail::finite;
using svg_path_detail::point;
using svg_path_detail::resolve_arc;

progpu_native_point native(point value) noexcept {
    return {value.x, value.y};
}

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool ascii_letter(char value) noexcept {
    return (value >= 'A' && value <= 'Z') ||
        (value >= 'a' && value <= 'z');
}

char upper_ascii(char value) noexcept {
    return value >= 'a' && value <= 'z'
        ? static_cast<char>(value - ('a' - 'A'))
        : value;
}

bool relative_command(char value) noexcept {
    return value >= 'a' && value <= 'z';
}

void skip_separators(std::string_view text, std::size_t& index) noexcept {
    while (index < text.size()) {
        const char value = text[index];
        if (value != ',' && value != ' ' && value != '\t' &&
            value != '\r' && value != '\n' && value != '\f') {
            break;
        }
        ++index;
    }
}

bool read_number(
    std::string_view text,
    std::size_t& index,
    float& result) noexcept {
    skip_separators(text, index);
    if (index >= text.size() || ascii_letter(text[index])) {
        return false;
    }
    const char* first = text.data() + index;
    const char* last = text.data() + text.size();
    float value = 0.0F;
    const auto conversion = std::from_chars(
        first, last, value, std::chars_format::general);
    if (conversion.ec != std::errc{} || conversion.ptr == first ||
        !std::isfinite(value)) {
        return false;
    }
    index = static_cast<std::size_t>(conversion.ptr - text.data());
    result = value;
    return true;
}

bool read_point(
    std::string_view text,
    std::size_t& index,
    point& result) noexcept {
    return read_number(text, index, result.x) &&
        read_number(text, index, result.y);
}

struct path_writer final {
    std::span<progpu_native_path_segment> output{};
    std::size_t count = 0U;
    point minimum{
        std::numeric_limits<float>::max(),
        std::numeric_limits<float>::max()};
    point maximum{
        std::numeric_limits<float>::lowest(),
        std::numeric_limits<float>::lowest()};
    std::uint32_t fill_rule = PROGPU_NATIVE_FILL_RULE_NON_ZERO;
    bool write = false;
    bool failed = false;

    void include(point value) noexcept {
        if (!finite(value)) {
            failed = true;
            return;
        }
        minimum.x = std::min(minimum.x, value.x);
        minimum.y = std::min(minimum.y, value.y);
        maximum.x = std::max(maximum.x, value.x);
        maximum.y = std::max(maximum.y, value.y);
    }

    void append(const progpu_native_path_segment& segment) noexcept {
        if (count == std::numeric_limits<std::size_t>::max()) {
            failed = true;
            return;
        }
        if (write) {
            if (count >= output.size()) {
                failed = true;
                return;
            }
            output[count] = segment;
        }
        ++count;
    }

    void line(point start, point end) noexcept {
        progpu_native_path_segment segment{};
        segment.p0 = native(start);
        segment.p1 = native(end);
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        append(segment);
        include(start);
        include(end);
    }

    void quadratic(point start, point control, point end) noexcept {
        progpu_native_path_segment segment{};
        segment.p0 = native(start);
        segment.p1 = native(control);
        segment.p2 = native(end);
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC;
        append(segment);
        include(start);
        include(control);
        include(end);
    }

    void cubic(
        point start,
        point control1,
        point control2,
        point end) noexcept {
        progpu_native_path_segment segment{};
        segment.p0 = native(start);
        segment.p1 = native(control1);
        segment.p2 = native(control2);
        segment.p3 = native(end);
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
        append(segment);
        include(start);
        include(control1);
        include(control2);
        include(end);
    }

    void arc(
        point start,
        point end,
        point radii,
        float rotation,
        bool large_arc,
        bool clockwise) noexcept {
        point center{};
        float theta1 = 0.0F;
        float delta_theta = 0.0F;
        float radius_x = 0.0F;
        float radius_y = 0.0F;
        if (!resolve_arc(start, end, radii, rotation, large_arc, clockwise,
                center, theta1, delta_theta, radius_x, radius_y)) {
            if (!equal(start, end)) {
                line(start, end);
            } else {
                include(end);
            }
            return;
        }
        progpu_native_path_segment segment{};
        segment.p0 = native(start);
        segment.p1 = native(end);
        segment.p2 = native(center);
        segment.p3 = {radius_x, radius_y};
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
        segment.pad0 = std::bit_cast<std::uint32_t>(theta1);
        segment.pad1 = std::bit_cast<std::uint32_t>(delta_theta);
        segment.pad2 = std::bit_cast<std::uint32_t>(
            rotation * std::numbers::pi_v<float> / 180.0F);
        append(segment);
        include(start);
        include(end);

        const float phi = rotation * std::numbers::pi_v<float> / 180.0F;
        const float cosine_phi = std::cos(phi);
        const float sine_phi = std::sin(phi);
        const float x_extrema = std::atan2(
            -radius_y * sine_phi, radius_x * cosine_phi);
        const float y_extrema = std::atan2(
            radius_y * cosine_phi, radius_x * sine_phi);
        const float extrema[4]{
            x_extrema,
            x_extrema + std::numbers::pi_v<float>,
            y_extrema,
            y_extrema + std::numbers::pi_v<float>};
        for (const float theta : extrema) {
            if (angle_within_sweep(theta, theta1, delta_theta)) {
                include(evaluate_arc(
                    center, radius_x, radius_y, rotation, theta));
            }
        }
    }
};

bool parse_path(
    std::string_view text,
    path_writer& writer) noexcept {
    std::size_t index = 0U;
    point current{};
    point figure_start{};
    point last_control{};
    bool has_figure = false;
    bool figure_has_segments = false;
    char last_command = '\0';

    const auto close_figure = [&]() noexcept {
        if (has_figure && figure_has_segments && !equal(current, figure_start)) {
            writer.line(current, figure_start);
        }
        if (has_figure) {
            current = figure_start;
        }
        figure_has_segments = false;
    };

    skip_separators(text, index);
    while (index < text.size()) {
        char command = last_command;
        const char previous_command = last_command;
        if (ascii_letter(text[index])) {
            command = text[index++];
            last_command = command;
        } else if (command == '\0') {
            return false;
        }
        const bool relative = relative_command(command);
        switch (upper_ascii(command)) {
        case 'F': {
            float value = 0.0F;
            if (!read_number(text, index, value)) {
                return false;
            }
            writer.fill_rule = value == 0.0F
                ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
                : PROGPU_NATIVE_FILL_RULE_NON_ZERO;
            last_command = '\0';
            break;
        }
        case 'M': {
            point value{};
            if (!read_point(text, index, value)) {
                return false;
            }
            if (relative) {
                value = value + current;
            }
            close_figure();
            current = value;
            figure_start = value;
            has_figure = true;
            writer.include(value);
            last_command = relative ? 'l' : 'L';
            break;
        }
        case 'L': {
            point value{};
            if (!read_point(text, index, value)) {
                return false;
            }
            if (relative) {
                value = value + current;
            }
            if (!has_figure) {
                figure_start = current;
                has_figure = true;
            }
            writer.line(current, value);
            current = value;
            figure_has_segments = true;
            break;
        }
        case 'H': {
            float value = 0.0F;
            if (!read_number(text, index, value)) {
                return false;
            }
            if (relative) {
                value += current.x;
            }
            const point end{value, current.y};
            if (!has_figure) {
                figure_start = current;
                has_figure = true;
            }
            writer.line(current, end);
            current = end;
            figure_has_segments = true;
            break;
        }
        case 'V': {
            float value = 0.0F;
            if (!read_number(text, index, value)) {
                return false;
            }
            if (relative) {
                value += current.y;
            }
            const point end{current.x, value};
            if (!has_figure) {
                figure_start = current;
                has_figure = true;
            }
            writer.line(current, end);
            current = end;
            figure_has_segments = true;
            break;
        }
        case 'Q': {
            point control{};
            point end{};
            if (!read_point(text, index, control) ||
                !read_point(text, index, end)) {
                return false;
            }
            if (relative) {
                control = control + current;
                end = end + current;
            }
            if (!has_figure) {
                figure_start = current;
                has_figure = true;
            }
            writer.quadratic(current, control, end);
            last_control = control;
            current = end;
            figure_has_segments = true;
            break;
        }
        case 'T': {
            point end{};
            if (!read_point(text, index, end)) {
                return false;
            }
            if (relative) {
                end = end + current;
            }
            const char previous = upper_ascii(previous_command);
            const point control = previous == 'Q' || previous == 'T'
                ? current * 2.0F - last_control
                : current;
            if (!has_figure) {
                figure_start = current;
                has_figure = true;
            }
            writer.quadratic(current, control, end);
            last_control = control;
            current = end;
            figure_has_segments = true;
            break;
        }
        case 'C': {
            point control1{};
            point control2{};
            point end{};
            if (!read_point(text, index, control1) ||
                !read_point(text, index, control2) ||
                !read_point(text, index, end)) {
                return false;
            }
            if (relative) {
                control1 = control1 + current;
                control2 = control2 + current;
                end = end + current;
            }
            if (!has_figure) {
                figure_start = current;
                has_figure = true;
            }
            writer.cubic(current, control1, control2, end);
            last_control = control2;
            current = end;
            figure_has_segments = true;
            break;
        }
        case 'S': {
            point control2{};
            point end{};
            if (!read_point(text, index, control2) ||
                !read_point(text, index, end)) {
                return false;
            }
            if (relative) {
                control2 = control2 + current;
                end = end + current;
            }
            const char previous = upper_ascii(previous_command);
            const point control1 = previous == 'C' || previous == 'S'
                ? current * 2.0F - last_control
                : current;
            if (!has_figure) {
                figure_start = current;
                has_figure = true;
            }
            writer.cubic(current, control1, control2, end);
            last_control = control2;
            current = end;
            figure_has_segments = true;
            break;
        }
        case 'A': {
            float radius_x = 0.0F;
            float radius_y = 0.0F;
            float rotation = 0.0F;
            float large_arc = 0.0F;
            float sweep = 0.0F;
            point end{};
            if (!read_number(text, index, radius_x) ||
                !read_number(text, index, radius_y) ||
                !read_number(text, index, rotation) ||
                !read_number(text, index, large_arc) ||
                !read_number(text, index, sweep) ||
                !read_point(text, index, end)) {
                return false;
            }
            if (relative) {
                end = end + current;
            }
            if (!has_figure) {
                figure_start = current;
                has_figure = true;
            }
            writer.arc(current, end, {radius_x, radius_y}, rotation,
                large_arc != 0.0F, sweep != 0.0F);
            current = end;
            figure_has_segments = true;
            break;
        }
        case 'Z':
            close_figure();
            last_command = '\0';
            break;
        default:
            return false;
        }
        if (writer.failed) {
            return false;
        }
        skip_separators(text, index);
    }
    close_figure();
    return !writer.failed;
}

bool compile_requirements(
    std::string_view path_data,
    svg_path_requirements& result) noexcept {
    result = {};
    if (path_data.empty()) {
        return true;
    }
    path_writer writer{};
    if (!parse_path(path_data, writer)) {
        return false;
    }
    result.segment_count = writer.count;
    result.fill_rule = writer.fill_rule;
    if (writer.count != 0U) {
        result.minimum_x = writer.minimum.x;
        result.minimum_y = writer.minimum.y;
        result.maximum_x = writer.maximum.x;
        result.maximum_y = writer.maximum.y;
    }
    return true;
}

} // namespace

bool try_get_svg_path_requirements(
    std::string_view path_data,
    svg_path_requirements& result,
    font_error* error) noexcept {
    set_error(error, font_error::none);
    if (!compile_requirements(path_data, result)) {
        result = {};
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    return true;
}

bool try_decode_svg_path(
    std::string_view path_data,
    std::span<progpu_native_path_segment> segments,
    svg_path_requirements& result,
    font_error* error) noexcept {
    set_error(error, font_error::none);
    svg_path_requirements required{};
    if (!compile_requirements(path_data, required)) {
        result = {};
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    if (segments.size() < required.segment_count) {
        result = required;
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    path_writer writer{segments, 0U, {}, {}, required.fill_rule, true, false};
    writer.minimum = {
        std::numeric_limits<float>::max(),
        std::numeric_limits<float>::max()};
    writer.maximum = {
        std::numeric_limits<float>::lowest(),
        std::numeric_limits<float>::lowest()};
    if (!parse_path(path_data, writer) || writer.count != required.segment_count) {
        result = {};
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    result = required;
    return true;
}

} // namespace progpu::native::text
