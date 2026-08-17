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

bool try_measure_positioned_text_lines(
    std::span<const positioned_text_line> lines,
    float maximum_width,
    text_layout_metrics& result,
    font_error* error) noexcept {
    result = {};
    if (!valid_maximum_width(maximum_width)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    for (const auto& line : lines) {
        const float bottom = line.baseline_y + line.height;
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
    }
    result.measured_width = maximum_width > 0.0F
        ? maximum_width
        : result.content_width;
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
