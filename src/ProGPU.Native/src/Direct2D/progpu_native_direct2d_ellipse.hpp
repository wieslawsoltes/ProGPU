#pragma once

#include "progpu_native_direct2d_compat.hpp"

namespace progpu::native::direct2d::compat::detail {

[[nodiscard]] com::result create_ellipse_geometry(
    factory* owner,
    const ellipse* value,
    ellipse_geometry** geometry_value) noexcept;

} // namespace progpu::native::direct2d::compat::detail
