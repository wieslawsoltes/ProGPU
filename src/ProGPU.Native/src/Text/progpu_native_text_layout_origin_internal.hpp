#pragma once

#include "progpu_native_text.hpp"

// Original ProGPU paragraph-writer arithmetic. Positioning and retained
// interaction metadata share this pure origin policy, without glyph ink.
namespace progpu::native::text::detail {
inline float text_line_alignment_shift(const text_layout_options& options,
    float line_width) noexcept {
    if (options.maximum_width <= line_width) return 0.0F;
    switch (options.alignment) {
        case text_alignment::center: return (options.maximum_width - line_width) * 0.5F;
        case text_alignment::right: return options.maximum_width - line_width;
        case text_alignment::left:
        case text_alignment::justify: return 0.0F;
    }
    return 0.0F;
}

inline float text_line_pen_origin(bool leading_sign, float sign_width,
    bool right_to_left_justified, float trailing_width) noexcept {
    return leading_sign ? sign_width : right_to_left_justified ? -trailing_width : 0.0F;
}
} // namespace progpu::native::text::detail
