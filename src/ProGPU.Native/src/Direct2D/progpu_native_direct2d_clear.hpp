#pragma once

#include "progpu_native_scene_builder.hpp"

namespace progpu::native::direct2d {

// The caller has proved an aliased-only clip stack and validated straight RGBA.
// Bounds already belong to the captured target frame, not the current source
// transform. Use the ordinary retained SRC layer/compositor; transparent Clear
// must replace previous pixels rather than disappear as a source-over no-op.
// O(1) appended records; no readback, target mutation or resource-owner transfer.
inline bool append_clipped_clear(
    semantic_scene_builder& builder,
    const progpu_native_image_rect& bounds,
    const progpu_native_color& color,
    bool& recorded) noexcept
{
    recorded = false;
    if (bounds.width == 0.0F || bounds.height == 0.0F) return true;
    const progpu_native_scene_layer layer{
        sizeof(progpu_native_scene_layer), PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS,
        bounds, 1.0F, PROGPU_NATIVE_BLEND_SRC,
        PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
        0U, 0U, 0U, 0U};
    std::uint32_t brush = PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu_native_analytic_primitive primitive{};
    primitive.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
    primitive.flags = PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED;
    primitive.x = bounds.x;
    primitive.y = bounds.y;
    primitive.width = bounds.width;
    primitive.height = bounds.height;
    primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
    primitive.transform = semantic_scene_builder::identity_transform();
    if (!builder.add_solid_brush(color, 1.0F, brush) ||
        !builder.push_layer(layer) ||
        !builder.draw_analytic(std::span(&primitive, 1U), std::span(&brush, 1U), bounds) ||
        !builder.pop_layer()) return false;
    recorded = true;
    return true;
}

} // namespace progpu::native::direct2d
