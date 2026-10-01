#pragma once

#include "progpu_native.h"

namespace progpu::native::semantic {

// Ordinary Text leaves source RGB multiplication to the blend unit. Preserve
// that arithmetic for materials and straight textures, while original
// premultiplied texture samples and R8 coverage retain their One blend path.
constexpr bool glyph_paint_premultiplied_output(
    const progpu_native_scene_glyph_paint& paint, bool alpha_mask_target) noexcept {
    return alpha_mask_target ||
        (paint.kind == PROGPU_NATIVE_SCENE_GLYPH_PAINT_TEXTURE &&
            (paint.flags & PROGPU_NATIVE_SCENE_GLYPH_PAINT_PREMULTIPLIED) != 0U);
}

} // namespace progpu::native::semantic
