#include "progpu_native_text.hpp"

#include <cstddef>

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.InferUntouchedDeltas/InterpolateDelta at checkpoint
// 62f04811, rewritten as a bounded allocation-free circular contour scan.

namespace progpu::native::text {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

float interpolate_delta(
    float target,
    float first,
    float second,
    float delta_first,
    float delta_second) noexcept {
    if (first == second) {
        return target <= first
            ? (delta_first < delta_second ? delta_first : delta_second)
            : (delta_first > delta_second ? delta_first : delta_second);
    }
    if (first > second) {
        const auto coordinate = first;
        first = second;
        second = coordinate;
        const auto delta = delta_first;
        delta_first = delta_second;
        delta_second = delta;
    }
    if (target <= first) {
        return delta_first;
    }
    if (target >= second) {
        return delta_second;
    }
    const auto ratio = (target - first) / (second - first);
    return delta_first + ratio * (delta_second - delta_first);
}

std::size_t next_point(
    std::size_t point,
    std::size_t start,
    std::size_t end) noexcept {
    return point == end ? start : point + 1U;
}

void interpolate_contour_axis(
    std::span<const progpu_native_point> original_points,
    std::span<float> deltas,
    std::span<const std::uint8_t> touched,
    std::size_t contour_start,
    std::size_t contour_end,
    bool use_x) noexcept {
    std::size_t first_touched = contour_start;
    while (first_touched <= contour_end && touched[first_touched] == 0U) {
        ++first_touched;
    }
    if (first_touched > contour_end) {
        return;
    }
    auto second_touched = next_point(
        first_touched, contour_start, contour_end);
    while (second_touched != first_touched && touched[second_touched] == 0U) {
        second_touched = next_point(
            second_touched, contour_start, contour_end);
    }
    if (second_touched == first_touched) {
        for (auto point = contour_start; point <= contour_end; ++point) {
            deltas[point] = deltas[first_touched];
        }
        return;
    }

    auto current_touched = first_touched;
    do {
        auto following_touched = next_point(
            current_touched, contour_start, contour_end);
        while (touched[following_touched] == 0U) {
            following_touched = next_point(
                following_touched, contour_start, contour_end);
        }
        auto point = next_point(
            current_touched, contour_start, contour_end);
        while (point != following_touched) {
            const auto coordinate = use_x
                ? original_points[point].x
                : original_points[point].y;
            const auto first = use_x
                ? original_points[current_touched].x
                : original_points[current_touched].y;
            const auto second = use_x
                ? original_points[following_touched].x
                : original_points[following_touched].y;
            deltas[point] = interpolate_delta(
                coordinate,
                first,
                second,
                deltas[current_touched],
                deltas[following_touched]);
            point = next_point(point, contour_start, contour_end);
        }
        current_touched = following_touched;
    } while (current_touched != first_touched);
}

} // namespace

bool sfnt_gvar_deltas::try_infer_untouched(
    std::span<const progpu_native_point> original_points,
    std::span<const std::uint16_t> contour_end_points,
    std::span<float> x_deltas,
    std::span<float> y_deltas,
    std::span<const std::uint8_t> touched,
    font_error* error) noexcept {
    set_error(error, font_error::none);
    if (x_deltas.size() < original_points.size() ||
        y_deltas.size() < original_points.size() ||
        touched.size() < original_points.size()) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::size_t contour_start = 0U;
    for (const auto contour_end_value : contour_end_points) {
        const auto contour_end = static_cast<std::size_t>(contour_end_value);
        if (contour_end < contour_start || contour_end >= original_points.size()) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        contour_start = contour_end + 1U;
    }
    if ((!original_points.empty() &&
            (contour_end_points.empty() ||
                contour_start != original_points.size())) ||
        (original_points.empty() && !contour_end_points.empty())) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }

    contour_start = 0U;
    for (const auto contour_end_value : contour_end_points) {
        const auto contour_end = static_cast<std::size_t>(contour_end_value);
        interpolate_contour_axis(
            original_points,
            x_deltas,
            touched,
            contour_start,
            contour_end,
            true);
        interpolate_contour_axis(
            original_points,
            y_deltas,
            touched,
            contour_start,
            contour_end,
            false);
        contour_start = contour_end + 1U;
    }
    return true;
}

} // namespace progpu::native::text
