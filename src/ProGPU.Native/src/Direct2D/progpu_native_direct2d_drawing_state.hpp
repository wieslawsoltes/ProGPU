#pragma once

#include "progpu_native_direct2d_compat.hpp"

namespace progpu::native::direct2d::compat::detail {

[[nodiscard]] com::result create_drawing_state_block(
    factory* owner,
    const drawing_state_description* description,
    rendering_parameters* text_rendering_parameters,
    drawing_state_block** value) noexcept;

[[nodiscard]] com::result create_drawing_state_block1(
    factory* owner,
    const drawing_state_description1* description,
    rendering_parameters* text_rendering_parameters,
    drawing_state_block1** value) noexcept;

} // namespace progpu::native::direct2d::compat::detail
