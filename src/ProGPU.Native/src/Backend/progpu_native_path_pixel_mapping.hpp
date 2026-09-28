#pragma once
#include "progpu_native.h"
#include <array>
#include <cmath>

namespace progpu::native {
// Original ProGPU.Scene/PathAtlasPixelMapping.cs contract, shared by native
// ordinary paths and retained clips. Fixed four-corner work; no allocation.
inline bool exact_path_pixel_mapping(
    const std::array<progpu_native_point, 4U>& positions,
    const std::array<progpu_native_point, 4U>& atlas) noexcept {
    const auto integer_point = [](const progpu_native_point& p) {
        return std::abs(p.x) <= 8388608.0F && std::abs(p.y) <= 8388608.0F &&
            std::trunc(p.x) == p.x && std::trunc(p.y) == p.y;
    };
    const float dx = atlas[0].x - positions[0].x;
    const float dy = atlas[0].y - positions[0].y;
    for (std::size_t i = 0U; i < 4U; ++i) {
        if (!integer_point(positions[i]) || !integer_point(atlas[i]) ||
            atlas[i].x - positions[i].x != dx ||
            atlas[i].y - positions[i].y != dy) return false;
    }
    return true;
}
}
