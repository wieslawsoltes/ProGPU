#pragma once

#include "progpu_native_direct2d_compat.hpp"

namespace progpu::native::direct2d::compat::detail {

[[nodiscard]] com::result create_rounded_rectangle_geometry(
    factory* owner,
    const rounded_rectangle* value,
    rounded_rectangle_geometry** geometry_value) noexcept;

} // namespace progpu::native::direct2d::compat::detail
