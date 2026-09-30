#pragma once

#include "progpu_native_hinted_font.hpp"
#include "../Shaping/progpu_native_open_type_gpos_internal.hpp"

namespace progpu::native::text {
struct hinted_gpos_frame_result final {
    hinted_projection_error error = hinted_projection_error::none;
    detail::gpos_device_frame frame{};
};

// Synchronous borrowed view: caller retains this exact batch and font view for
// the complete positioning pass. Does not admit shaping/source Display policy.
hinted_gpos_frame_result bind_hinted_gpos_frame(const hinted_glyph_batch& batch,
    const sfnt_font_view& font, hinted_projection_policy policy = hinted_projection_policy::automatic) noexcept;
} // namespace progpu::native::text
