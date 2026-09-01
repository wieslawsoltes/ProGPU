#pragma once

#include "progpu_native_direct2d_compat.hpp"

namespace progpu::native::direct2d::compat::detail {

[[nodiscard]] com::result create_stroke_style(
    factory* owner,
    const stroke_style_properties* properties,
    const float* dashes,
    std::uint32_t dash_count,
    stroke_style** value) noexcept;

} // namespace progpu::native::direct2d::compat::detail
