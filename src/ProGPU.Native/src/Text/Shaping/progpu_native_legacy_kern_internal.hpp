#pragma once

#include "progpu_native_text.hpp"

#include <span>

namespace progpu::native::text::detail {

struct gpos_device_frame;

void apply_legacy_kern(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef) noexcept;

// Borrows the retained frame synchronously. A failed matched pair publishes no
// metrics or dependency flags; earlier successful pairs remain applied.
bool try_apply_device_legacy_kern(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef,
    const gpos_device_frame& frame,
    font_error* error = nullptr) noexcept;

} // namespace progpu::native::text::detail
