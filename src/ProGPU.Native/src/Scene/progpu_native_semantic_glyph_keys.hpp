#pragma once

#include "progpu_native.h"

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace progpu::native::semantic {

static_assert(sizeof(progpu_native_glyph_outline_key) == 16U);
static_assert(sizeof(progpu_native_scene_glyph_key) == 24U);
static_assert(offsetof(progpu_native_scene_glyph_key, raster_scale) == 16U);
static_assert(sizeof(progpu_native_registered_glyph_outline) == 48U);

// A glyph resource that names outlines registered with the engine instead of
// carrying them.
inline bool is_keyed_glyph_resource(
    const progpu_native_scene_resource& resource) noexcept {
    return resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN &&
        (resource.flags & PROGPU_NATIVE_SCENE_GLYPH_OUTLINE_KEYS) != 0U;
}

inline bool is_valid_glyph_key(
    const progpu_native_scene_glyph_key& key) noexcept {
    return std::isfinite(key.raster_scale) && key.raster_scale > 0.0F &&
        !std::signbit(key.subpixel_x) &&
        (key.subpixel_x == 0.0F || key.subpixel_x == 0.25F ||
            key.subpixel_x == 0.5F || key.subpixel_x == 0.75F);
}

// The stream rules of a keyed glyph resource: a non-empty table of valid keys
// and nothing else. Whether the engine holds the outlines is not a property of
// the stream; a frame that names an unknown key fails when it is rendered.
inline bool validate_keyed_glyph_resource(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset) noexcept {
    error_offset = resource.payload_offset;
    if (bytes == nullptr || !is_keyed_glyph_resource(resource) ||
        (resource.flags & PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS) != 0U ||
        resource.payload_size == 0U || resource.auxiliary_size != 0U ||
        resource.payload_size % sizeof(progpu_native_scene_glyph_key) != 0U) {
        return false;
    }
    const auto count = resource.payload_size / sizeof(progpu_native_scene_glyph_key);
    for (std::uint32_t index = 0U; index < count; ++index) {
        progpu_native_scene_glyph_key key{};
        error_offset = resource.payload_offset +
            index * static_cast<std::uint32_t>(sizeof(key));
        std::memcpy(&key, bytes + error_offset, sizeof(key));
        if (!is_valid_glyph_key(key)) {
            return false;
        }
    }
    return true;
}

} // namespace progpu::native::semantic
