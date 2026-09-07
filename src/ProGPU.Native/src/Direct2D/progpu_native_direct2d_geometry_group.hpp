#pragma once

#include "progpu_native_direct2d_compat.hpp"

namespace progpu::native::direct2d::compat::detail {

[[nodiscard]] com::result create_geometry_group(
    factory* owner,
    fill_mode mode,
    geometry** geometries,
    std::uint32_t geometry_count,
    geometry_group** value) noexcept;

} // namespace progpu::native::direct2d::compat::detail
