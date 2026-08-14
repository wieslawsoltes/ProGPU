#include "progpu_native_cff_type2_internal.hpp"

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

// Direct native port provenance: ProGPU-owned CFF OutlineBuilder at checkpoint
// 281a9078. Closed figures are written directly to the shared renderer segment
// ABI. Preflight is O(B + S), writing is O(B + S), and both use fixed storage.
namespace progpu::native::text {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

namespace detail {
namespace {

bool finite_coordinate(double value) noexcept {
    return std::isfinite(value) &&
        value >= -static_cast<double>(std::numeric_limits<float>::max()) &&
        value <= static_cast<double>(std::numeric_limits<float>::max());
}

progpu_native_point point(double x, double y) noexcept {
    return {static_cast<float>(x), static_cast<float>(y)};
}

} // namespace

cff_path_writer::cff_path_writer(
    std::span<progpu_native_path_segment> segments,
    bool count_only) noexcept
    : segments_(segments), count_only_(count_only) {
}

bool cff_path_writer::move_to(double x, double y) noexcept {
    if (!finite_coordinate(x) || !finite_coordinate(y) ||
        !close_figure()) {
        valid_ = false;
        return false;
    }
    start_x_ = current_x_ = x;
    start_y_ = current_y_ = y;
    figure_active_ = true;
    return true;
}

bool cff_path_writer::line_to(
    double x0,
    double y0,
    double x1,
    double y1) noexcept {
    if (!finite_coordinate(x0) || !finite_coordinate(y0) ||
        !finite_coordinate(x1) || !finite_coordinate(y1) ||
        !begin_if_needed(x0, y0) ||
        !emit(progpu_native_path_segment{
            point(x0, y0),
            point(x1, y1),
            {},
            {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE,
            0U,
            0U,
            0U})) {
        valid_ = false;
        return false;
    }
    current_x_ = x1;
    current_y_ = y1;
    return true;
}

bool cff_path_writer::curve_to(
    double x0,
    double y0,
    double x1,
    double y1,
    double x2,
    double y2,
    double x3,
    double y3) noexcept {
    if (!finite_coordinate(x0) || !finite_coordinate(y0) ||
        !finite_coordinate(x1) || !finite_coordinate(y1) ||
        !finite_coordinate(x2) || !finite_coordinate(y2) ||
        !finite_coordinate(x3) || !finite_coordinate(y3) ||
        !begin_if_needed(x0, y0) ||
        !emit(progpu_native_path_segment{
            point(x0, y0),
            point(x1, y1),
            point(x2, y2),
            point(x3, y3),
            PROGPU_NATIVE_PATH_SEGMENT_CUBIC,
            0U,
            0U,
            0U})) {
        valid_ = false;
        return false;
    }
    current_x_ = x3;
    current_y_ = y3;
    return true;
}

bool cff_path_writer::end_glyph() noexcept {
    return close_figure();
}

std::uint32_t cff_path_writer::count() const noexcept {
    return count_;
}

bool cff_path_writer::valid() const noexcept {
    return valid_;
}

bool cff_path_writer::close_figure() noexcept {
    if (!figure_active_) {
        return valid_;
    }
    if ((current_x_ != start_x_ || current_y_ != start_y_) &&
        !emit(progpu_native_path_segment{
            point(current_x_, current_y_),
            point(start_x_, start_y_),
            {},
            {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE,
            0U,
            0U,
            0U})) {
        valid_ = false;
        return false;
    }
    figure_active_ = false;
    return valid_;
}

bool cff_path_writer::emit(progpu_native_path_segment segment) noexcept {
    if (!valid_ || count_ == std::numeric_limits<std::uint32_t>::max()) {
        valid_ = false;
        return false;
    }
    if (!count_only_) {
        if (count_ >= segments_.size()) {
            valid_ = false;
            return false;
        }
        segments_[count_] = segment;
    }
    ++count_;
    return true;
}

bool cff_path_writer::begin_if_needed(double x, double y) noexcept {
    if (figure_active_) {
        return true;
    }
    start_x_ = current_x_ = x;
    start_y_ = current_y_ = y;
    figure_active_ = true;
    return true;
}

bool try_evaluate_cff1_outline(
    sfnt_cff1_font_view font,
    std::uint32_t glyph_index,
    std::span<progpu_native_path_segment> segments,
    bool count_only,
    std::uint32_t& written,
    font_error* error) noexcept {
    written = 0U;
    if (glyph_index >= font.char_strings.count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::span<const std::byte> char_string{};
    sfnt_cff_index_view local_subroutines{};
    if (!sfnt_cff_data::try_get_index_item(
            font.char_strings, glyph_index, char_string, error) ||
        !sfnt_cff_data::try_get_local_subroutines(
            font, glyph_index, local_subroutines, error)) {
        return false;
    }
    std::array<double, 513U> operands{};
    std::array<double, 32U> transient{};
    cff_path_writer writer{segments, count_only};
    cff_type2_evaluator evaluator{
        writer,
        font.global_subroutines,
        local_subroutines,
        operands,
        transient,
        glyph_index + 1U};
    if (!evaluator.try_evaluate(char_string)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    written = writer.count();
    set_error(error, font_error::none);
    return true;
}

} // namespace detail

bool sfnt_cff_data::try_get_outline_requirements(
    sfnt_cff1_font_view font,
    std::uint32_t glyph_index,
    sfnt_cff1_outline_requirements& result,
    font_error* error) noexcept {
    result = {};
    return detail::try_evaluate_cff1_outline(
        font,
        glyph_index,
        {},
        true,
        result.path_segment_count,
        error);
}

bool sfnt_cff_data::try_decode_outline(
    sfnt_cff1_font_view font,
    std::uint32_t glyph_index,
    std::span<progpu_native_path_segment> segments,
    std::uint32_t& written,
    font_error* error) noexcept {
    written = 0U;
    sfnt_cff1_outline_requirements requirements{};
    if (!try_get_outline_requirements(
            font, glyph_index, requirements, error)) {
        return false;
    }
    if (segments.size() < requirements.path_segment_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    return detail::try_evaluate_cff1_outline(
        font,
        glyph_index,
        segments,
        false,
        written,
        error);
}

} // namespace progpu::native::text
