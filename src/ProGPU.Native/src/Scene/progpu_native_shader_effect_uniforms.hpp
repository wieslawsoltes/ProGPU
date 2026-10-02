#pragma once

#include "progpu_native.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <iterator>

namespace progpu::native::shader_effect {

// The renderer has proved a complete integral positive-axis capture and an
// identity final composite basis. Its UV-to-device linear basis is exactly
// diag(physical_width, physical_height); DPI is already in those dimensions.
// Translation/viewport origin contributes no vector displacement. This is not
// an inverse for arbitrary source matrices or a fractional/cropped-frame repair.
// Time/space O(128), fixed uniform storage; no shader expression is evaluated.
inline bool prepare_constants(const progpu_native_scene_shader_effect& source,
    std::uint32_t derivative_register, std::uint32_t physical_width,
    std::uint32_t physical_height, std::array<float, 128U>& output) noexcept {
    if (derivative_register != PROGPU_NATIVE_SCENE_NO_INDEX &&
        (derivative_register >= 32U || physical_width == 0U || physical_height == 0U ||
         physical_width > (1U << 24U) || physical_height > (1U << 24U))) return false;
    std::array<float, 128U> candidate{};
    std::copy(std::begin(source.constants), std::end(source.constants), candidate.begin());
    if (derivative_register != PROGPU_NATIVE_SCENE_NO_INDEX) {
        const auto offset = derivative_register * 4U;
        candidate[offset] = 1.0F / static_cast<float>(physical_width);
        candidate[offset + 1U] = 0.0F;
        candidate[offset + 2U] = 0.0F;
        candidate[offset + 3U] = 1.0F / static_cast<float>(physical_height);
    }
    output = candidate;
    return true;
}
} // namespace progpu::native::shader_effect
