#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>

namespace progpu::native::mil {

// Original-owned arithmetic over explicit source metadata. Bounds remain in
// the source's float edge frame; receiving-window DPI is never an input.
struct cache_raster_frame final {
    std::uint32_t width{}, height{};
    float scale_x{}, scale_y{}, offset_x{}, offset_y{};
};

inline bool cache_raster_axis(float first, float last, double selected_scale,
    float primary_scale, std::uint32_t limit, std::uint32_t& pixels,
    float& scale, float& offset) noexcept {
    if (!std::isfinite(first) || !std::isfinite(last) || last < first ||
        !std::isfinite(selected_scale) || selected_scale < 0.0 || !std::isfinite(primary_scale) ||
        primary_scale <= 0.0F || limit == 0U) return false;
    volatile float extent = last - first;
    if (!std::isfinite(extent)) return false;
    if (extent == 0.0F || selected_scale == 0.0) {
        pixels = 0U; scale = offset = 0.0F;
        return true;
    }
    // The mathematical source rule is integer round-out after a relative
    // binary32 near-integer test, not ceil(q). Each marked intermediate owns
    // its rounding boundary. See docs/native-cache-sampler-raster.md.
    volatile double scaled_extent = static_cast<double>(extent) * selected_scale;
    const double requested = scaled_extent * static_cast<double>(primary_scale);
    if (!std::isfinite(requested) || requested < 0.0 ||
        requested > static_cast<double>(UINT32_MAX)) return false;
    const auto integer = static_cast<std::uint32_t>(requested);
    const float projected = static_cast<float>(requested);
    volatile float difference = static_cast<float>(integer) - projected;
    volatile float relative = difference / (projected == 0.0F ? 1.0F : projected);
    const bool increment = std::abs(relative) >= 10.0F * std::numeric_limits<float>::epsilon();
    if (increment && integer == UINT32_MAX) return false;
    const std::uint32_t rounded = integer + static_cast<std::uint32_t>(increment);
    if (rounded == 0U) {
        pixels = 0U; scale = offset = 0.0F;
        return true;
    }
    double effective_primary = primary_scale;
    if (rounded > limit) effective_primary *= static_cast<double>(limit) / rounded;
    const float effective_scale = static_cast<float>(selected_scale * effective_primary);
    volatile float translation = -first * effective_scale;
    if (!std::isfinite(effective_scale) || effective_scale <= 0.0F || !std::isfinite(translation)) return false;
    pixels = std::min(rounded, limit);
    scale = effective_scale; offset = translation;
    return true;
}

// O(1), allocation-free, atomic publication. Zero dimensions describe an
// absent original texture, not missing ownership or a fabricated positive box.
inline bool make_cache_raster_frame(double x, double y, double width, double height,
    double selected_scale, float primary_x, float primary_y,
    std::uint32_t maximum_width, std::uint32_t maximum_height,
    cache_raster_frame& output) noexcept {
    if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(width) ||
        !std::isfinite(height) || width < 0.0 || height < 0.0 ||
        !std::isfinite(selected_scale) || selected_scale < 0.0 ||
        !std::isfinite(primary_x) || !std::isfinite(primary_y) || primary_x <= 0.0F ||
        primary_y <= 0.0F || maximum_width == 0U || maximum_height == 0U) return false;
    cache_raster_frame candidate{};
    const float left = static_cast<float>(x), top = static_cast<float>(y);
    const float right = static_cast<float>(x + width), bottom = static_cast<float>(y + height);
    if (!std::isfinite(left) || !std::isfinite(top) || !std::isfinite(right) ||
        !std::isfinite(bottom) || right < left || bottom < top) return false;
    // A float rectangle is globally empty before either nonempty axis is
    // multiplied by cache/DPI scale. Do not overflow the other axis first.
    if (left == right || top == bottom || selected_scale == 0.0) {
        output = candidate;
        return true;
    }
    if (!cache_raster_axis(left, right,
            selected_scale, primary_x, maximum_width, candidate.width, candidate.scale_x, candidate.offset_x) ||
        !cache_raster_axis(top, bottom,
            selected_scale, primary_y, maximum_height, candidate.height, candidate.scale_y, candidate.offset_y)) return false;
    output = candidate;
    return true;
}

} // namespace progpu::native::mil
