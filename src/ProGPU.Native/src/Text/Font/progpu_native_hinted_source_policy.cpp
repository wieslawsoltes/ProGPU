#include "progpu_native_hinted_source_policy.hpp"

#include <cmath>
#include <limits>

namespace progpu::native::text {

bool resolve_hinted_source_device(const hinted_source_style& source,
    hinted_source_device_selection& result) noexcept {
    if (!std::isfinite(source.em_size) || source.em_size <= 0.0 ||
        !std::isfinite(source.pixels_per_dip) || source.pixels_per_dip <= 0.0 ||
        (source.advance_policy != hinted_source_advance_policy::unchanged &&
         source.advance_policy != hinted_source_advance_policy::physical_ties_to_even)) return false;
    const double physical_em = source.em_size * source.pixels_per_dip;
    if (!std::isfinite(physical_em) || physical_em <= 0.0) return false;
    double physical_units = 0.0;
    switch (source.em_policy) {
    case hinted_source_em_policy::exact_26_6:
        physical_units = physical_em * 64.0;
        if (!std::isfinite(physical_units) || std::trunc(physical_units) != physical_units) return false;
        break;
    case hinted_source_em_policy::nearest_half_up:
    case hinted_source_em_policy::nearest_ties_to_even: {
        // No dependence on the process floating-point rounding mode. Resolve
        // midpoint choice before multiplication; no fractional 26.6 rounding
        // can move a source value across an integer-em midpoint.
        double integral = std::floor(physical_em);
        const double fraction = physical_em - integral;
        if (fraction > 0.5 || (fraction == 0.5 &&
            (source.em_policy == hinted_source_em_policy::nearest_half_up ||
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
    } else if (policy != hinted_source_advance_policy::unchanged) {
        return false;
    }
    result = candidate;
    return true;
}

} // namespace progpu::native::text
