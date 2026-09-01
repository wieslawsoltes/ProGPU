#pragma once

#include "progpu_native_direct2d_compat.hpp"

namespace progpu::native::direct2d::compat::detail {

[[nodiscard]] com::result create_path_geometry(
    factory* owner,
    path_geometry** value) noexcept;

} // namespace progpu::native::direct2d::compat::detail
