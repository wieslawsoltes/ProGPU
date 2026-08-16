#pragma once

#include "progpu_native_text.hpp"

#include <span>

namespace progpu::native::text::detail {

void apply_legacy_kern(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef) noexcept;

} // namespace progpu::native::text::detail
