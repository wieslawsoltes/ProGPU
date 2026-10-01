#pragma once

#include "progpu_native_mil.hpp"
#include "../Text/Interop/progpu_native_hinted_paragraph_glyph_frame.hpp"

namespace progpu::native::mil {

// Explicit private source bridge. The indices address original positioned
// occurrences, including no-ink ones, in the same retained paragraph. Font
// bytes/face and source em are verified against the glyph run's existing SFNT
// sideband. Existing source bounds must contain every selected hinted ink box;
// they are never fabricated or widened, since they own spatial-brush mapping.
// Only the identity linear basis is admitted; no source mode changes.
struct hinted_glyph_binding_access final {
    static status bind(channel& target, std::uint32_t handle,
        std::shared_ptr<const text::hinted_paragraph_glyph_resource> resource,
        std::span<const std::uint32_t> positioned_indices, std::uint32_t font_index,
        float dpi_scale, progpu_native_point logical_origin,
        progpu_native_affine_2d basis = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}) noexcept;
};

} // namespace progpu::native::mil
