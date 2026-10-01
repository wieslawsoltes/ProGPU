#pragma once

#include "progpu_native_direct2d_compat.hpp"

namespace progpu::native::direct2d::compat::detail {

[[nodiscard]] com::result create_scene_render_target(
    factory* owner,
    const scene_render_target_properties* properties,
    render_target** value) noexcept;

[[nodiscard]] com::result create_formatted_scene_render_target(
    factory* owner,
    const scene_render_target_properties* properties,
    const pixel_format* format,
    render_target** value) noexcept;

} // namespace progpu::native::direct2d::compat::detail
