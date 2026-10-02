#pragma once

#include "progpu_native_semantic_validation.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <span>

namespace progpu::native::semantic {

inline bool valid_rgb_glyph_descriptor(const progpu_native_scene_rgb_glyph_draw& draw) noexcept {
    return draw.struct_size == sizeof(draw) && draw.glyph_count != 0U && draw.glyph_count <= 65536U &&
        draw.filter_model == PROGPU_NATIVE_RGB_GLYPH_FULL_PIXEL_BOX_8X8 && draw.pixel_geometry <= 2U &&
        draw.gamma == 1.0F && draw.enhanced_contrast == 0.0F && draw.cleartype_level == 1.0F &&
        std::isfinite(draw.dpi_scale) && draw.dpi_scale > 0.0F && draw.reserved0 == 0U && draw.reserved1 == 0U;
}

inline bool valid_rgb_glyph_tile(const progpu_native_scene_rgb_glyph_tile& tile,
    std::size_t outline_count) noexcept {
    if (tile.outline_index >= outline_count || tile.reserved != 0U ||
        tile.width == 0U || tile.height == 0U || tile.width > 4096U || tile.height > 4096U ||
        tile.target_x < -4096 || tile.target_y < -4096 || tile.target_x > 4096 || tile.target_y > 4096 ||
        !std::isfinite(tile.x_start) || !std::isfinite(tile.y_start) ||
        !std::isfinite(tile.scale) || tile.scale <= 0.0F || !std::isfinite(tile.subpixel_x)) return false;
    for (const float value : {tile.foreground.r, tile.foreground.g, tile.foreground.b, tile.foreground.a})
        if (!std::isfinite(value) || value < 0.0F || value > 1.0F) return false;
    // Validate the actual original shader arithmetic, not only finite operands.
    const std::array sample_extents{
        ((tile.x_start - 1.0F / 3.0F) + 0.0625F - tile.subpixel_x) / tile.scale,
        ((tile.x_start + static_cast<float>(tile.width - 1U) + 1.0F / 3.0F) +
            0.9375F - tile.subpixel_x) / tile.scale,
        -(tile.y_start + 0.0625F) / tile.scale,
        -(tile.y_start + static_cast<float>(tile.height - 1U) + 0.9375F) / tile.scale};
    for (const float value : sample_extents) if (!std::isfinite(value)) return false;
    return true;
}

// Called after generic scene range/record validation. Byte loads preserve the
// wire's alignment independence; no borrowed descriptor outlives this call.
// O(G + O + S) time, O(1) scratch for tiles, outlines and original segments.
inline bool validate_rgb_glyph_draw(const std::byte* bytes,
    const progpu_native_scene_header& header, const progpu_native_scene_command& command,
    std::uint32_t& error_offset) noexcept {
    error_offset = command.payload_offset;
    if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_RGB_GLYPH_RUN ||
        command.resource_index >= header.resource_count || command.payload_size < sizeof(progpu_native_scene_rgb_glyph_draw))
        return false;
    progpu_native_scene_rgb_glyph_draw draw{};
    std::memcpy(&draw, bytes + command.payload_offset, sizeof(draw));
    if (!valid_rgb_glyph_descriptor(draw) || command.payload_size != sizeof(draw) +
        std::uint64_t{draw.glyph_count} * sizeof(progpu_native_scene_rgb_glyph_tile)) return false;
    progpu_native_scene_resource resource{};
    std::memcpy(&resource, bytes + header.resource_offset +
        std::uint64_t{command.resource_index} * header.resource_stride, sizeof(resource));
    if (resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN ||
        (resource.flags & PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS) != 0U || resource.payload_size == 0U ||
        resource.payload_size % sizeof(progpu_native_scene_glyph_outline) != 0U || resource.auxiliary_size == 0U ||
        resource.auxiliary_size % sizeof(progpu_native_path_segment) != 0U) return false;
    const auto outlines = resource.payload_size / sizeof(progpu_native_scene_glyph_outline);
    const auto segments = resource.auxiliary_size / sizeof(progpu_native_path_segment);
    if (outlines > 65536U || segments > 1048576U) return false;
    std::uint64_t covered_end = 0U;
    for (std::size_t index = 0U; index < outlines; ++index) {
        progpu_native_scene_glyph_outline outline{};
        const auto offset = resource.payload_offset + index * sizeof(outline);
        std::memcpy(&outline, bytes + offset, sizeof(outline));
        if (outline.segment_offset > covered_end || !is_valid_semantic_glyph_outline(outline, segments)) {
            error_offset = static_cast<std::uint32_t>(offset);
            return false;
        }
        covered_end = std::max(covered_end, outline.segment_offset + outline.segment_count);
    }
    if (covered_end != segments) return false;
    for (std::size_t index = 0U; index < segments; ++index) {
        progpu_native_path_segment segment{};
        const auto offset = resource.auxiliary_offset + index * sizeof(segment);
        std::memcpy(&segment, bytes + offset, sizeof(segment));
        if (!is_valid_semantic_segment(segment, false)) {
            error_offset = static_cast<std::uint32_t>(offset);
            return false;
        }
    }
    std::uint64_t pixels = 0U;
    for (std::uint32_t index = 0U; index < draw.glyph_count; ++index) {
        progpu_native_scene_rgb_glyph_tile tile{};
        const auto offset = command.payload_offset + sizeof(draw) + std::size_t{index} * sizeof(tile);
        std::memcpy(&tile, bytes + offset, sizeof(tile));
        pixels += std::uint64_t{tile.width} * tile.height;
        if (!valid_rgb_glyph_tile(tile, outlines) || pixels > 4096U * 4096U) {
            error_offset = static_cast<std::uint32_t>(offset);
            return false;
        }
    }
    return true;
}

static_assert(sizeof(progpu_native_scene_rgb_glyph_draw) == 40U);
static_assert(sizeof(progpu_native_scene_rgb_glyph_tile) == 56U);

} // namespace progpu::native::semantic
