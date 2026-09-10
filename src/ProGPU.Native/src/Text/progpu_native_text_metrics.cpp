#include "progpu_native_text.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#include <span>

// Direct fixed-work-per-line/column port of ProGPU-owned TextLayout
// ContentSize and MeasuredSize publication.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool valid_maximum_width(float value) noexcept {
    return std::isfinite(value) && value >= 0.0F;
}

} // namespace

static bool measure_text_lines(
    std::span<const positioned_text_line> lines,
    float maximum_width,
    text_layout_metrics& result,
    font_error* error, bool measured) noexcept {
    result = {};
    if (!valid_maximum_width(maximum_width)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    double top = 0.0;
    for (const auto& line : lines) {
        const float bottom = measured ? static_cast<float>(top + line.height) : line.baseline_y + line.height;
        if (!std::isfinite(line.width) || line.width < 0.0F ||
            !std::isfinite(line.baseline_y) ||
            !std::isfinite(line.height) || line.height < 0.0F ||
            !std::isfinite(bottom)) {
            result = {};
            set_error(error, font_error::invalid_argument);
            return false;
        }
        result.content_width = std::max(result.content_width, line.width);
        result.content_height = std::max(result.content_height, bottom);
        top += line.height;
    }
    result.measured_width = maximum_width > 0.0F
        ? maximum_width
        : result.content_width;
    result.measured_height = result.content_height;
    set_error(error, font_error::none);
    return true;
}

bool try_measure_positioned_text_lines(std::span<const positioned_text_line> lines,
    float maximum_width, text_layout_metrics& result, font_error* error) noexcept {
    return measure_text_lines(lines, maximum_width, result, error, false);
}

bool try_measure_measured_text_lines(std::span<const positioned_text_line> lines,
    float maximum_width, text_layout_metrics& result, font_error* error) noexcept {
    return measure_text_lines(lines, maximum_width, result, error, true);
}

bool try_measure_fragment_text_lines(std::span<const positioned_text_line> lines,
    std::span<const text_fragment_placement> placements, float maximum_width,
    text_layout_metrics& result, font_error* error) noexcept {
    result = {};
    const auto invalid = [&] { result = {}; set_error(error, font_error::invalid_argument); return false; };
    if (!valid_maximum_width(maximum_width) || placements.size() != lines.size()) return invalid();
    int row_direction = 0;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const auto line = lines[i]; const auto p = placements[i];
        const double right = static_cast<double>(p.left) + line.width;
        const double bottom = static_cast<double>(p.top) + line.height;
        if (!std::isfinite(p.left) || p.left < 0 || !std::isfinite(p.top) || p.top < 0 ||
            !std::isfinite(p.width) || p.width <= 0 || !std::isfinite(p.left + p.width) ||
            !std::isfinite(line.width) || line.width < 0 ||
            !std::isfinite(line.height) || line.height < 0 || !std::isfinite(line.baseline_y) ||
            !std::isfinite(right) || right > std::numeric_limits<float>::max() ||
            !std::isfinite(bottom) || bottom > std::numeric_limits<float>::max() ||
            line.baseline_y < static_cast<float>(p.top) ||
            line.baseline_y > static_cast<float>(bottom)) return invalid();
        if (i == 0U) { if (p.row_index != 0U) return invalid(); }
        else {
            const auto previous = placements[i - 1U];
            if (p.row_index == previous.row_index) {
                if (p.top != previous.top || line.height != lines[i - 1U].height ||
                    line.baseline_y != lines[i - 1U].baseline_y) return invalid();
                const int order = p.left >= previous.left + previous.width ? 1 :
                    p.left + p.width <= previous.left ? -1 : 0;
                if (order == 0 || (row_direction != 0 && order != row_direction)) return invalid();
                row_direction = order;
            } else {
                if (p.row_index != previous.row_index + 1U || p.top < previous.top + lines[i - 1U].height) return invalid();
                row_direction = 0;
            }
        }
        result.content_width = std::max(result.content_width, static_cast<float>(right));
        result.content_height = std::max(result.content_height, static_cast<float>(bottom));
    }
    result.measured_width = maximum_width > 0 ? maximum_width : result.content_width;
    result.measured_height = result.content_height;
    set_error(error, font_error::none);
    return true;
}

bool try_measure_positioned_text_columns(
    std::span<const positioned_text_column> columns,
    float maximum_width,
    text_layout_metrics& result,
    font_error* error) noexcept {
    result = {};
    if (!valid_maximum_width(maximum_width)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    float minimum_x = std::numeric_limits<float>::infinity();
    float maximum_x = -std::numeric_limits<float>::infinity();
    for (const auto& column : columns) {
        const float right = column.x + column.width;
        if (!std::isfinite(column.height) || column.height < 0.0F ||
            !std::isfinite(column.x) || !std::isfinite(column.width) ||
            column.width < 0.0F || !std::isfinite(right)) {
            result = {};
            set_error(error, font_error::invalid_argument);
            return false;
        }
        minimum_x = std::min(minimum_x, column.x);
        maximum_x = std::max(maximum_x, right);
        result.content_height = std::max(
            result.content_height, column.height);
    }
    if (!columns.empty()) {
        result.content_width = std::max(0.0F, maximum_x - minimum_x);
    }
    result.measured_width = maximum_width > 0.0F
        ? maximum_width
        : result.content_width;
    result.measured_height = result.content_height;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
