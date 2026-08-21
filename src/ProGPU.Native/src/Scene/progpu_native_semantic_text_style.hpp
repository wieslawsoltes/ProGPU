#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <vector>

namespace progpu::native::semantic {

struct semantic_text_style_page final {
    std::uint64_t scene_hash = 0U;
    bool cache_valid = false;
    std::vector<progpu_native_scene_text_style> styles;
    std::vector<std::uint32_t> command_style_indices;
};

bool validate_text_style_table(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset) noexcept;

bool validate_styled_glyph_draw(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_command& command,
    std::uint32_t& error_offset) noexcept;

bool try_get_glyph_payload(
    const std::byte* bytes,
    const progpu_native_scene_command& command,
    std::uint32_t& payload_offset,
    std::uint32_t& glyph_count) noexcept;

bool compile_text_style_page(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint64_t scene_hash,
    semantic_text_style_page& page) noexcept;

bool try_get_command_text_style_index(
    const semantic_text_style_page& page,
    std::uint32_t command_index,
    std::uint32_t& style_index) noexcept;

} // namespace progpu::native::semantic
