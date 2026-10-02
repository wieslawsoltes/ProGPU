#include "progpu_native_hinted_source_policy.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

namespace progpu::native::text {
namespace {
double ties_to_even(double value) noexcept {
    // Explicit source Math.Round semantics, independent of the ambient fenv.
    // The sign of a zero result is retained as in the original API.
    if (std::abs(value) >= 0x1p52) return value;
    double integral = std::floor(value);
    const double fraction = value - integral;
    if (fraction > 0.5 || (fraction == 0.5 && std::fmod(integral, 2.0) != 0.0)) integral += 1.0;
    return integral == 0.0 ? std::copysign(0.0, value) : integral;
}

bool source_ideal_projection(double device_units, double source_dpi, bool advance, double& result) noexcept {
    const auto capture_dpi = static_cast<float>(source_dpi);
    if (!std::isfinite(capture_dpi) || capture_dpi <= 0.0F) return false;
    // Original GetGdiCompatibleGlyphPlacements returns float DIP geometry.
    // Conversion to the source ideal-unit integer is a separate boundary from
    // the final physical Display rounding; do not combine these operations.
    const auto dip = static_cast<float>((device_units / 64.0) / static_cast<double>(capture_dpi));
    const double scaled = static_cast<double>(dip) * 300.0;
    if (!std::isfinite(scaled)) return false;
    const double integral = advance ? ties_to_even(scaled) : std::trunc(scaled);
    if (integral < INT32_MIN || integral > INT32_MAX) return false;
    const auto ideal = static_cast<std::int32_t>(integral);
    const double unrounded = static_cast<double>(ideal) * (1.0 / 300.0);
    const double physical = unrounded * source_dpi;
    if (!std::isfinite(physical)) return false;
    double projected = ties_to_even(physical) / source_dpi;
    if (ideal > 0) projected = std::max(projected, 1.0 / 300.0);
    if (!std::isfinite(projected) || std::abs(projected) > std::numeric_limits<float>::max()) return false;
    result = projected;
    return true;
}
} // namespace

bool resolve_hinted_source_device(const hinted_source_style& source,
    hinted_source_device_selection& result) noexcept {
    if (!std::isfinite(source.em_size) || source.em_size <= 0.0 ||
        !std::isfinite(source.pixels_per_dip) || source.pixels_per_dip <= 0.0 ||
        (source.advance_policy != hinted_source_advance_policy::unchanged &&
         source.advance_policy != hinted_source_advance_policy::physical_ties_to_even &&
         source.advance_policy != hinted_source_advance_policy::source_ideal_units) ||
        (source.offset_policy != hinted_source_offset_policy::unchanged &&
         source.offset_policy != hinted_source_offset_policy::source_ideal_units)) return false;
    const double physical_em = source.em_policy == hinted_source_em_policy::float_capture_nearest_half_up
        ? static_cast<double>(static_cast<float>(source.em_size) * static_cast<float>(source.pixels_per_dip))
        : source.em_size * source.pixels_per_dip;
    if (!std::isfinite(physical_em) || physical_em <= 0.0) return false;
    double physical_units = 0.0;
    switch (source.em_policy) {
    case hinted_source_em_policy::exact_26_6:
        physical_units = physical_em * 64.0;
        if (!std::isfinite(physical_units) || std::trunc(physical_units) != physical_units) return false;
        break;
    case hinted_source_em_policy::nearest_half_up:
    case hinted_source_em_policy::float_capture_nearest_half_up:
    case hinted_source_em_policy::nearest_ties_to_even: {
        // No dependence on the process floating-point rounding mode. Resolve
        // midpoint choice before multiplication; no fractional 26.6 rounding
        // can move a source value across an integer-em midpoint.
        double integral = std::floor(physical_em);
        const double fraction = physical_em - integral;
        if (fraction > 0.5 || (fraction == 0.5 &&
            (source.em_policy != hinted_source_em_policy::nearest_ties_to_even ||
             std::fmod(integral, 2.0) != 0.0))) integral += 1.0;
        physical_units = integral * 64.0;
        break;
    }
    default:
        return false;
    }
    if (!std::isfinite(physical_units) || physical_units < 1.0 ||
        physical_units > static_cast<double>(std::numeric_limits<std::int32_t>::max())) return false;
    const auto units_per_pixel = static_cast<float>(1.0 / source.pixels_per_dip);
    if (!std::isfinite(units_per_pixel) || units_per_pixel <= 0.0F ||
        !std::isfinite(units_per_pixel / 64.0F) || units_per_pixel / 64.0F <= 0.0F) return false;
    result = {static_cast<std::uint32_t>(physical_units), units_per_pixel};
    return true;
}

bool project_hinted_source_advance(const shaping_glyph& original,
    hinted_source_advance_policy policy, shaping_glyph& result) noexcept {
    auto candidate = original;
    if (policy == hinted_source_advance_policy::physical_ties_to_even) {
        // Signed wide magnitude also represents INT32_MIN. Round the physical
        // 26.6 advance once, after all original GPOS/legacy-kern positioning.
        const auto value = static_cast<std::int64_t>(original.advance_x);
        const auto magnitude = value < 0 ? -value : value;
        auto pixels = magnitude / 64;
        const auto remainder = magnitude % 64;
        if (remainder > 32 || (remainder == 32 && (pixels & 1) != 0)) ++pixels;
        const auto rounded = (value < 0 ? -pixels : pixels) * 64;
        if (rounded < std::numeric_limits<std::int32_t>::min() ||
            rounded > std::numeric_limits<std::int32_t>::max()) return false;
        candidate.advance_x = static_cast<std::int32_t>(rounded);
    } else if (policy != hinted_source_advance_policy::unchanged && policy != hinted_source_advance_policy::source_ideal_units) {
        return false;
    }
    result = candidate;
    return true;
}

bool project_hinted_source_geometry(const shaping_glyph& glyph, double dpi, text_source_glyph_metrics& result) noexcept {
    if (!std::isfinite(dpi) || dpi <= 0.0) return false;
    double projected[4]{};
#if defined(__aarch64__) || defined(_M_ARM64)
    const double advances[2]{static_cast<double>(glyph.advance_x), static_cast<double>(glyph.advance_y)};
    const double offsets[2]{static_cast<double>(glyph.offset_x), static_cast<double>(glyph.offset_y)};
    vst1q_f64(projected, vdivq_f64(vmulq_n_f64(vld1q_f64(advances), 1.0 / 64.0), vdupq_n_f64(dpi)));
    vst1q_f64(projected + 2U, vdivq_f64(vmulq_n_f64(vld1q_f64(offsets), 1.0 / 64.0), vdupq_n_f64(dpi)));
#elif defined(__SSE2__) || defined(_M_X64)
    const auto divisor = _mm_set1_pd(dpi), scale = _mm_set1_pd(1.0 / 64.0);
    _mm_storeu_pd(projected, _mm_div_pd(_mm_mul_pd(_mm_set_pd(glyph.advance_y, glyph.advance_x), scale), divisor));
    _mm_storeu_pd(projected + 2U, _mm_div_pd(_mm_mul_pd(_mm_set_pd(glyph.offset_y, glyph.offset_x), scale), divisor));
#else
    projected[0] = (static_cast<double>(glyph.advance_x) / 64.0) / dpi;
    projected[1] = (static_cast<double>(glyph.advance_y) / 64.0) / dpi;
    projected[2] = (static_cast<double>(glyph.offset_x) / 64.0) / dpi;
    projected[3] = (static_cast<double>(glyph.offset_y) / 64.0) / dpi;
#endif
    for (const double value : projected)
        if (!std::isfinite(value) || std::abs(value) > std::numeric_limits<float>::max()) return false;
    result = {projected[0], projected[1], projected[2], projected[3]};
    return true;
}

bool project_hinted_source_geometry(const shaping_glyph& glyph, const hinted_source_style& source,
    text_source_glyph_metrics& result) noexcept {
    text_source_glyph_metrics projected{};
    if (!project_hinted_source_geometry(glyph, source.pixels_per_dip, projected)) return false;
    if (source.advance_policy == hinted_source_advance_policy::source_ideal_units) {
        if (!source_ideal_projection(glyph.advance_x, source.pixels_per_dip, true, projected.advance_x) ||
            !source_ideal_projection(-static_cast<double>(glyph.advance_y), source.pixels_per_dip, true, projected.advance_y)) return false;
        projected.advance_y = -projected.advance_y;
    } else if (source.advance_policy != hinted_source_advance_policy::unchanged &&
        source.advance_policy != hinted_source_advance_policy::physical_ties_to_even) return false;
    if (source.offset_policy == hinted_source_offset_policy::source_ideal_units) {
        if (!source_ideal_projection(glyph.offset_x, source.pixels_per_dip, false, projected.offset_x) ||
            !source_ideal_projection(-static_cast<double>(glyph.offset_y), source.pixels_per_dip, false, projected.offset_y)) return false;
        projected.offset_y = -projected.offset_y;
    } else if (source.offset_policy != hinted_source_offset_policy::unchanged) return false;
    result = projected;
    return true;
}

} // namespace progpu::native::text
