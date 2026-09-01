#pragma once

#include "progpu_native_direct2d_compat.hpp"

namespace progpu::native::direct2d::compat::detail {

[[nodiscard]] com::result create_drawing_state_block(
    factory* owner,
    const drawing_state_description* description,
    com::unknown* text_rendering_parameters,
    drawing_state_block** value) noexcept;

} // namespace progpu::native::direct2d::compat::detail
