#ifndef PROGPU_NATIVE_ARABIC_FALLBACK_INTERNAL_HPP
#define PROGPU_NATIVE_ARABIC_FALLBACK_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

struct arabic_fallback_options final {
    bool initial = false;
    bool medial = false;
    bool final = false;
    bool isolated = false;
    bool required_ligatures = false;
    bool track_fallback_marks = false;
};

bool try_apply_arabic_fallback(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    arabic_fallback_options options,
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
