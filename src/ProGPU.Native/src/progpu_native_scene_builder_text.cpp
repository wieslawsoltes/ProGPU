#include "progpu_native_scene_builder_internal.hpp"

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Bulk shaped-run bridge from progpu_native_text into the existing semantic
// glyph command. Conversion storage is caller-owned and no managed callback,
// per-glyph interop call, or intermediate retained resource is introduced.

namespace progpu::native {
namespace {

bool finite(float value) noexcept {
    return std::isfinite(value);
}

bool valid_options(const shaped_text_scene_options& options) noexcept {
    return finite(options.basis_x.x) && finite(options.basis_x.y) &&
        finite(options.basis_y.x) && finite(options.basis_y.y) &&
        finite(options.color.r) && finite(options.color.g) &&
        finite(options.color.b) && finite(options.color.a) &&
        finite(options.atlas_to_logical_scale) &&
        options.atlas_to_logical_scale > 0.0F &&
        finite(options.bold_offset) && finite(options.italic_skew);
}

} // namespace

bool semantic_scene_builder::draw_shaped_text_run(
    std::uint32_t glyph_resource_index,
    std::span<const text::shaping_glyph> shaped_glyphs,
    std::span<const text::positioned_text_glyph> positioned_glyphs,
    std::span<progpu_native_positioned_glyph> conversion_scratch,
    const shaped_text_scene_options& options,
    progpu_native_image_rect bounds,
    std::span<const std::uint32_t> glyph_to_outline,
    std::uint32_t state_resource_index,
    std::uint32_t text_style_index) noexcept {
    if (positioned_glyphs.empty() ||
        conversion_scratch.size() < positioned_glyphs.size() ||
        !valid_options(options)) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }

    for (std::size_t index = 0U; index < positioned_glyphs.size(); ++index) {
        const auto& positioned = positioned_glyphs[index];
        if ((positioned.glyph_index !=
                std::numeric_limits<std::uint32_t>::max() &&
                positioned.glyph_index >= shaped_glyphs.size()) ||
            !finite(positioned.x) || !finite(positioned.y)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
        if (positioned.glyph_index !=
                std::numeric_limits<std::uint32_t>::max() &&
            positioned.glyph_id !=
                shaped_glyphs[positioned.glyph_index].glyph_id) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
        const std::uint32_t glyph_id = positioned.glyph_id;
        if (!glyph_to_outline.empty() && glyph_id >= glyph_to_outline.size()) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
        const std::uint32_t outline_index = glyph_to_outline.empty()
            ? glyph_id
            : glyph_to_outline[glyph_id];
        conversion_scratch[index] = progpu_native_positioned_glyph{
            outline_index,
            0U,
            progpu_native_point{positioned.x, positioned.y},
            options.basis_x,
            options.basis_y,
            options.color,
            options.atlas_to_logical_scale,
            options.bold_offset,
            options.italic_skew,
            0.0F};
    }

    return draw_glyph_run(
        glyph_resource_index,
        conversion_scratch.first(positioned_glyphs.size()),
        bounds,
        state_resource_index,
        text_style_index);
}

} // namespace progpu::native
