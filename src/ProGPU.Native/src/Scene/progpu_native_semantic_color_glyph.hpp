#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>

struct progpu_native_engine;
struct semantic_glyph_page;

namespace progpu::native::semantic {

bool validate_color_glyph_resource(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset) noexcept;

bool is_color_glyph_resource(
    const progpu_native_scene_resource& resource) noexcept;

bool prepare_color_glyph_atlas(
    progpu_native_engine& engine,
    semantic_glyph_page& page,
    std::uint64_t scene_hash,
    std::uint64_t& upload_bytes) noexcept;

} // namespace progpu::native::semantic
