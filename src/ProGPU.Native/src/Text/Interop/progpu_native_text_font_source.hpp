#pragma once

#include "progpu_native.h"
#include "../Font/progpu_native_font_source.hpp"
#include "../Font/progpu_native_hinted_font.hpp"

#include <memory>

namespace progpu::native::text {
// Internal context selection, borrowed under the same existing context use lease
// as shaping. The returned owner retains original bytes/index independently of
// fallback-vector growth or subsequent context destruction. Not a C ABI export.
std::shared_ptr<const owned_font_source> select_context_font_source(
    progpu_native_text_context* context, std::uint32_t font_index) noexcept;
bool capture_context_hinted(progpu_native_text_context* context, std::uint32_t font_index,
    const hinted_font_configuration& configuration, std::span<const std::uint32_t> glyph_indices,
    std::shared_ptr<const hinted_glyph_batch>& result, hinted_font_error& error) noexcept;
} // namespace progpu::native::text
