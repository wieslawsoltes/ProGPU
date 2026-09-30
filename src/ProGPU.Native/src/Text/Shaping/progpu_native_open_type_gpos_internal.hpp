#pragma once

#include "progpu_native_text.hpp"

#include <cstdint>
#include <array>
#include <span>

namespace progpu::native::text::detail {

struct gpos_metric_vector final { std::int32_t x = 0, y = 0; };
enum class gpos_arithmetic_path : std::uint32_t { intrinsic_simd, scalar_reference };
// Synchronously borrowed native generation. No managed callback or font/GPU
// work is permitted in projection/point access; the source retains its lease.
struct gpos_device_frame final {
    const sfnt_font_view* font = nullptr;
    const void* owner = nullptr;
    std::uint16_t pixels_per_em_x = 0U;
    std::uint16_t pixels_per_em_y = 0U;
    // Same instance as retained outlines; borrowed for the synchronous run.
    std::span<const std::int16_t> normalized_coordinates{};
    gpos_arithmetic_path arithmetic_path = gpos_arithmetic_path::intrinsic_simd;
    bool (*project_design)(const void*, const std::array<gpos_metric_vector, 2>&,
        std::array<gpos_metric_vector, 2>&) noexcept = nullptr;
    bool (*contour_point)(const void*, std::size_t, std::uint32_t, std::uint16_t, gpos_metric_vector&) noexcept = nullptr;
};

// Composition leaves the original public options' layout and ABI unchanged.
struct gpos_execution_options final {
    open_type_gpos_apply_options base{};
    const gpos_device_frame* device = nullptr;
};

enum class gpos_apply_result : std::uint8_t {
    no_match,
    applied,
    invalid_argument,
    malformed
};

inline void mark_gpos_dependency(
    std::span<shaping_glyph> glyphs,
    std::size_t first,
    std::size_t last) noexcept {
    if (first > last || last >= glyphs.size() || first == last) {
        return;
    }
    std::int32_t minimum_cluster = glyphs[first].cluster;
    for (std::size_t index = first + 1U; index <= last; ++index) {
        if (glyphs[index].cluster < minimum_cluster) {
            minimum_cluster = glyphs[index].cluster;
        }
    }
    constexpr auto dependency_flags =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    for (std::size_t index = first; index <= last; ++index) {
        if (glyphs[index].cluster == minimum_cluster) {
            continue;
        }
        glyphs[index].flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(glyphs[index].flags) |
            dependency_flags);
    }
}

gpos_apply_result apply_gpos_lookup_at(
    const open_type_layout_table_view&,
    std::uint16_t,
    std::span<shaping_glyph>,
    std::size_t,
    const gpos_execution_options&,
    std::uint32_t) noexcept;

gpos_apply_result apply_gpos_context_subtable(
    const open_type_layout_table_view& gpos,
    std::span<const std::byte> table,
    std::uint16_t type,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const gpos_execution_options& options,
    std::uint32_t depth) noexcept;

bool try_apply_gpos_lookup(const open_type_layout_table_view&, std::uint16_t,
    std::span<shaping_glyph>, const gpos_execution_options&, bool&, font_error*) noexcept;
bool try_apply_gpos_lookup_at(const open_type_layout_table_view&, std::uint16_t,
    std::span<shaping_glyph>, std::uint32_t, const gpos_execution_options&, bool&, font_error*) noexcept;

bool try_apply_device_gpos_lookup(const open_type_layout_table_view&, std::uint16_t,
    std::span<shaping_glyph>, const open_type_gpos_apply_options&, const gpos_device_frame&,
    bool&, font_error* = nullptr) noexcept;
bool try_apply_device_gpos_lookup_at(const open_type_layout_table_view&, std::uint16_t,
    std::span<shaping_glyph>, std::uint32_t, const open_type_gpos_apply_options&, const gpos_device_frame&,
    bool&, font_error* = nullptr) noexcept;

bool try_resolve_device_gpos_attachments(std::span<shaping_glyph>, std::span<const shaping_attachment>,
    shaping_direction, std::span<std::uint8_t>, font_error* = nullptr) noexcept;

} // namespace progpu::native::text::detail
